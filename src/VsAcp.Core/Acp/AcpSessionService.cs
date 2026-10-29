using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VsAcp.Core.Abstractions;
using VsAcp.Core.Acp.Handlers;
using VsAcp.Core.Models;
using AcpProtocol;
using VsAcp.Core.Configuration;
using VsAcp.Core.IdeTools;

namespace VsAcp.Core.Acp;

public sealed class AcpSessionService : IChatService, IAcpSessionLifecycle, IDisposable
{
    readonly AgentProcessHost _processHost;
    readonly AcpClientHandler _clientHandler;
    readonly SessionUpdateAdapter _sessionUpdateAdapter;
    readonly IOptions<VsAcpOptions> _options;
    readonly ILogger<AcpSessionService> _logger;
    readonly IIdeToolsEndpoint? _ideTools;

    ClientSideConnection? _connection;
    // _connection finished its initialize handshake, so a new session can be opened on it without a restart.
    bool _connectionReady;
    string? _sessionId;
    readonly SemaphoreSlim _initLock = new(1, 1);
    SessionConfigOption[] _configOptions = [];
    SessionMode[] _availableModes = [];
    AvailableCommand[] _availableCommands = [];
    string? _currentModeId;
    UsageUpdate? _usage;
    AgentCapabilities? _agentCapabilities;
    string[]? _activeAdditionalDirectories;
    // The IDE tools were withheld from this agent process because it takes no HTTP MCP servers (logged once).
    bool _ideToolsWithheldLogged;

    // Context block injected into the next user prompt after a revert. We can't
    // inject it as a separate silent turn because Cursor's agent filters out
    // meta/orchestration turns from its session memory across subsequent turns.
    // Prepending to a real user turn ensures the agent treats it as part of
    // genuine user input that it cannot drop.
    string? _pendingContextPrefix;

    // The prompt request of the current (or last) turn, and the check started by Stop that kills an agent which
    // never ends the stopped turn; the next message waits for that check so it reaches a working agent.
    Task? _promptTask;
    Task? _settling;

    public AcpSessionService(
        AgentProcessHost processHost,
        AcpClientHandler clientHandler,
        SessionUpdateAdapter sessionUpdateAdapter,
        IOptions<VsAcpOptions> options,
        ILogger<AcpSessionService> logger,
        IIdeToolsEndpoint? ideTools = null)
    {
        _processHost = processHost;
        _clientHandler = clientHandler;
        _sessionUpdateAdapter = sessionUpdateAdapter;
        _options = options;
        _ideTools = ideTools;

        _sessionUpdateAdapter.ModeUpdated += newModeId =>
        {
            _currentModeId = newModeId;
            ModesChanged?.Invoke();
        };
        _sessionUpdateAdapter.ConfigOptionsUpdated += configOptions =>
        {
            ApplyConfigOptions(configOptions);
        };
        _sessionUpdateAdapter.CommandsUpdated += commands =>
        {
            _availableCommands = commands;
            CommandsChanged?.Invoke();
        };
        _sessionUpdateAdapter.UsageUpdated += usage =>
        {
            _usage = usage;
            UsageChanged?.Invoke();
        };
        _sessionUpdateAdapter.TitleUpdated += title =>
        {
            if (CanFork) title = WithoutForkSuffix(title);
            if (title.Length > 0) TitleChanged?.Invoke(title);
        };
        _logger = logger;
    }

    public async IAsyncEnumerable<string> SendMessageAsync(
        string userMessage,
        IReadOnlyList<ImageAttachment>? images = null,
        IReadOnlyList<string>? filePaths = null,
        IReadOnlyList<string>? folderPaths = null,
        IReadOnlyList<CodeSelection>? selections = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("SendMessageAsync: begin (sessionId={SessionId}, processRunning={Running})",
            _sessionId, _processHost.IsRunning);

        if (_settling is { } settling)
        {
            _settling = null;
            await settling;
        }

        await EnsureInitializedAsync(cancellationToken);

        _logger.LogInformation("SendMessageAsync: initialized (sessionId={SessionId})", _sessionId);

        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _sessionUpdateAdapter.TextChannel = channel;
        _sessionUpdateAdapter.Reset(cancellationToken);

        var promptTask = Task.Run(async () =>
        {
            try
            {
                // Stopped before the prompt went out: nothing reaches the agent (the reader below ends the turn).
                if (cancellationToken.IsCancellationRequested)
                    return;

                // If a revert primed prior-conversation context, prepend it to the user's message so it travels
                // in the same turn the agent cannot filter out. Taken only here, where the prompt really goes
                // out, so a send stopped earlier (e.g. while the agent was starting) leaves it for the next one.
                var effectiveUserMessage = userMessage;
                if (Interlocked.Exchange(ref _pendingContextPrefix, null) is { } prefix)
                {
                    _logger.LogInformation("SendMessageAsync: prepending pending context prefix ({Length} chars) to user message",
                        prefix.Length);
                    effectiveUserMessage = prefix + userMessage;
                }

                var promptBlocks = BuildPromptBlocks(effectiveUserMessage, images, filePaths, folderPaths, selections,
                    _agentCapabilities?.PromptCapabilities?.EmbeddedContext ?? false);

                _logger.LogInformation("SendMessageAsync: calling PromptAsync");
                // Not cancellable: Stop ends the turn through session/cancel, and KillIfStopIgnoredAsync needs this
                // task to stay pending until the agent really answers.
                await _connection!.PromptAsync(new PromptRequest
                {
                    SessionId = _sessionId!,
                    Prompt = promptBlocks
                }, CancellationToken.None);
                _logger.LogInformation("SendMessageAsync: PromptAsync returned");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SendMessageAsync: PromptAsync failed");
                throw;
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, cancellationToken);
        _promptTask = promptTask;

        try
        {
            await foreach (var text in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return text;
            }

            _logger.LogInformation("SendMessageAsync: channel drained, awaiting promptTask");
            await promptTask;

            _logger.LogInformation("SendMessageAsync: complete");
        }
        finally
        {
            // Always finalize open Thinking/AI items - including on cancel (Stop) or error, where
            // the drain above throws before reaching here. Otherwise the "Thinking..." expander
            // from the interrupted turn stays open forever. Complete() does not check the prompt
            // token, so it closes the items regardless of why the turn ended.
            _sessionUpdateAdapter.Complete();
            _sessionUpdateAdapter.TextChannel = null;
        }
    }

    public Task<string> GenerateTitleAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        var words = userMessage.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var title = string.Join(" ", words.Take(6));
        if (words.Length > 6) title += "...";
        return Task.FromResult(title);
    }

    public void Cancel()
    {
        if (_connection is null || _sessionId is null)
            return;

        _logger.LogInformation("Sending session/cancel for sessionId={SessionId}", _sessionId);

        _ = Task.Run(async () =>
        {
            try
            {
                await _connection.CancelAsync(new CancelNotification { SessionId = _sessionId });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "session/cancel notification failed");
            }
        });

        if (_promptTask is { IsCompleted: false } prompt)
            _settling = KillIfStopIgnoredAsync(prompt);
    }

    // A wedged agent (or a launcher whose agent died while it keeps the pipe open) never answers the prompt, and
    // the process still counts as running, so every later message would hang too. Restarting it is the only way out.
    async Task KillIfStopIgnoredAsync(Task prompt)
    {
        var grace = _options.Value.CancelGracePeriod;
        if (await Task.WhenAny(prompt, Task.Delay(grace)).ConfigureAwait(false) == prompt)
            return;

        _logger.LogWarning("The agent did not end the stopped turn within {Grace}; killing it so the next message starts a fresh one", grace);
        _processHost.Kill();
    }

    public void ClearHistory()
    {
        _logger.LogInformation("Clearing ACP session");
        _connection?.Dispose();
        _connection = null;
        _connectionReady = false;
        ForgetSession();
        _processHost.Kill();
    }

    public void ResetSession()
    {
        var connection = _connection;
        if (connection is null || !_connectionReady || !_processHost.IsRunning)
        {
            ClearHistory();
            return;
        }

        var oldSessionId = _sessionId;
        _logger.LogInformation("Dropping ACP session {SessionId}; the agent process stays for the next session", oldSessionId);
        if (oldSessionId is not null)
        {
            _sessionUpdateAdapter.IgnoreSession(oldSessionId);
            if (_agentCapabilities?.SessionCapabilities?.Close is not null)
                _ = CloseSessionAsync(connection, oldSessionId);
        }
        ForgetSession();
    }

    async Task CloseSessionAsync(ClientSideConnection connection, string sessionId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await connection.CloseSessionAsync(new CloseSessionRequest { SessionId = sessionId }, timeout.Token).ConfigureAwait(false);
            _logger.LogInformation("Closed ACP session {SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("session/close for {SessionId} failed: {Error}", sessionId, ex.Message);
        }
    }

    // sessionCapabilities.fork is unstable, so the stable types leave it in the extension data.
    bool CanFork => _agentCapabilities?.SessionCapabilities?.ExtensionData?.ContainsKey("fork") == true;

    // The Claude Agent SDK names a fork "<source title> (fork)" ("Forked session (fork)" when the source has none), and
    // claude-agent-acp publishes that stored title, after a later load too. A revert's fork is the same chat, so the
    // suffix (one per fork in a chain) is dropped and the placeholder ignored.
    static string WithoutForkSuffix(string title)
    {
        const string suffix = " (fork)";
        while (title.EndsWith(suffix, StringComparison.Ordinal))
            title = title.Substring(0, title.Length - suffix.Length).TrimEnd();
        return title == "Forked session" ? "" : title;
    }

    public async Task<string?> TryForkSessionAsync(string agentMessageId, CancellationToken cancellationToken = default)
    {
        // Behind the init lock: a session still being loaded (a tab just reopened) is forked once it is there.
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (await ForkAsync(agentMessageId, cancellationToken) is not (var connection, var sourceId, var response, var additionalDirectories))
                return null;

            _sessionUpdateAdapter.IgnoreSession(sourceId);
            if (_agentCapabilities?.SessionCapabilities?.Close is not null)
                _ = CloseSessionAsync(connection, sourceId);
            ForgetSession();

            _sessionId = response.SessionId;
            SetActiveAdditionalDirectories(additionalDirectories ?? []);
            ApplyConfigOptions(response.ConfigOptions);
            ApplyModeState(response.Modes);
            _logger.LogInformation("ACP session {SessionId} forked from {SourceId}", _sessionId, sourceId);
            return _sessionId;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<string?> TryForkSessionForOtherChatAsync(string agentMessageId, CancellationToken cancellationToken = default)
    {
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (await ForkAsync(agentMessageId, cancellationToken) is not (var connection, var sourceId, var response, _))
                return null;

            // The copy belongs to the other chat: nothing it sends here is shown, and once it is closed the other
            // chat's agent process is the only one holding it (awaited, so that one loads it after the close).
            var forkId = response.SessionId;
            _sessionUpdateAdapter.IgnoreSession(forkId);
            if (_agentCapabilities?.SessionCapabilities?.Close is not null)
                await CloseSessionAsync(connection, forkId);
            _logger.LogInformation("ACP session {SessionId} forked from {SourceId} for another chat", forkId, sourceId);
            return forkId;
        }
        finally
        {
            _initLock.Release();
        }
    }

    // session/fork of the current session, behind _initLock. Null when the agent cannot fork or the fork failed.
    async Task<(ClientSideConnection Connection, string SourceId, ForkSessionResponse Response, string[]? AdditionalDirectories)?> ForkAsync(
        string agentMessageId, CancellationToken cancellationToken)
    {
        var connection = _connection;
        var sourceId = _sessionId;
        if (connection is null || !_connectionReady || !_processHost.IsRunning || sourceId is null || !CanFork)
            return null;

        var additionalDirectories = ResolveAdditionalDirectories(_agentCapabilities!);
        var request = new ForkSessionRequest
        {
            SessionId = sourceId,
            Cwd = _options.Value.WorkingDirectory,
            McpServers = McpServersFor(_agentCapabilities!),
            AdditionalDirectories = additionalDirectories,
            // The fork point: claude-agent-acp reads it from JetBrains AIR's extension, as an agent_message_chunk
            // messageId, and copies the session up to and including that message. Without it, the whole session.
            Meta = JsonSerializer.SerializeToElement(new { jetbrains = new { air = new { fork = new { version = 1, messageId = agentMessageId } } } })
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Value.NewSessionOnRunningAgentTimeout);
        try
        {
            _logger.LogInformation("Forking ACP session {SessionId} at message {MessageId}", sourceId, agentMessageId);
            return (connection, sourceId, await connection.ForkSessionAsync(request, timeout.Token), additionalDirectories);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // E.g. the message is from an earlier agent session (before a revert the agent could not fork).
            _logger.LogWarning("session/fork of {SessionId} at {MessageId} failed: {Error}", sourceId, agentMessageId, ex.Message);
            return null;
        }
    }

    void ForgetSession()
    {
        _sessionId = null;
        _pendingContextPrefix = null;
        // A new agent session may reuse compaction ids; stale finished entries would swallow them.
        _sessionUpdateAdapter.ResetCompactions();

        if (_usage is not null)
        {
            _usage = null;
            UsageChanged?.Invoke();
        }
    }

    public void PrimeForkNote()
    {
        _pendingContextPrefix = "[This conversation was forked from another chat at this point. The workspace files were not rolled back with it: they are as the other chat and I have left them, so they may differ from what earlier messages describe. Read a file again before relying on what an earlier message says about it.]"
            + Environment.NewLine + Environment.NewLine + "My next request:" + Environment.NewLine;
        _logger.LogInformation("PrimeForkNote: primed the forked-chat note");
    }

    public Task InitializeWithContextAsync(IReadOnlyList<PersistedMessage> messages, PriorContextOrigin origin = PriorContextOrigin.Revert,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0)
        {
            _pendingContextPrefix = null;
            return Task.CompletedTask;
        }

        var sb = new StringBuilder();
        sb.AppendLine(origin == PriorContextOrigin.Fork
            ? "[Continuing a conversation forked from another chat. Everything below - my messages, your responses, your tool calls and their results, and your reasoning - already happened between us before this point. The workspace files were NOT rolled back to this point: they are as the other chat and I have left them, so they may differ from what is described below; read a file again before relying on it. Treat this block as our established shared memory: do not re-execute any actions or repeat file edits described below. After this prior-context block, my actual next request follows.]"
            : "[Continuing from a prior conversation in this same chat. Everything below - my messages, your responses, your tool calls and their results, and your reasoning - already happened between us before this point. The workspace files have been restored to the state they were in at this point in the conversation, so any file edits you previously made and that are referenced below have been UNDONE on disk. Treat this block as our established shared memory: do not re-execute any actions or repeat file edits described below. After this prior-context block, my actual next request follows.]");
        sb.AppendLine();
        sb.AppendLine("<prior_conversation_context>");

        int included = 0;
        foreach (var msg in messages)
        {
            switch (msg.ItemType)
            {
                case "User":
                    if (!string.IsNullOrWhiteSpace(msg.Content) || msg.Selections is { Count: > 0 })
                    {
                        sb.AppendLine($"[User]: {msg.Content}");
                        if (msg.Selections is not null)
                        {
                            foreach (var s in msg.Selections)
                                sb.AppendLine(FormatSelection(s));
                        }
                        included++;
                    }
                    break;

                case "Assistant":
                    if (!string.IsNullOrWhiteSpace(msg.Content))
                    {
                        sb.AppendLine($"[Assistant]: {msg.Content}");
                        included++;
                    }
                    break;

                case "Thinking":
                    if (!string.IsNullOrWhiteSpace(msg.Content))
                    {
                        sb.AppendLine($"[Assistant thinking]: {msg.Content}");
                        included++;
                    }
                    break;

                case "ToolStep":
                    var toolLabel = !string.IsNullOrWhiteSpace(msg.Title) ? msg.Title
                                  : !string.IsNullOrWhiteSpace(msg.ToolName) ? msg.ToolName
                                  : "tool";
                    sb.Append($"[Tool call - {toolLabel}");
                    if (!string.IsNullOrWhiteSpace(msg.StatusText) && msg.StatusText != "Success")
                        sb.Append($" ({msg.StatusText})");
                    sb.AppendLine("]");
                    if (!string.IsNullOrWhiteSpace(msg.Body))
                        sb.AppendLine(msg.Body);
                    included++;
                    break;

                // Notices are transient and never persisted; a compaction summary describes the agent's old
                // session, while this prefix already carries the full visible history.
                case "Notice":
                case "Compaction":
                    break;
            }
        }

        sb.AppendLine("</prior_conversation_context>");
        sb.AppendLine();
        sb.AppendLine("My next request:");

        var prefix = sb.ToString();
        _logger.LogInformation("InitializeWithContextAsync: primed pending context prefix ({Length} chars, {Included}/{Total} messages included)",
            prefix.Length, included, messages.Count);
        _pendingContextPrefix = prefix;

        return Task.CompletedTask;
    }

    public string SerializeHistory()
    {
        return _sessionId ?? string.Empty;
    }

    public void RestoreHistory(string serializedHistory)
    {
        if (!string.IsNullOrEmpty(serializedHistory))
            _sessionId = serializedHistory;
    }

    public async Task LoadSessionAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
    }

    public decimal? GetSessionCost() => _usage?.Cost is { } cost ? (decimal)cost.Amount : null;

    // --- Context usage (tokens / cost) ---

    public UsageUpdate? Usage => _usage;
    public event Action? UsageChanged;

    public event Action<string>? TitleChanged;

    // --- Available commands (slash commands) ---

    public IReadOnlyList<AvailableCommand> AvailableCommands => _availableCommands;
    public event Action? CommandsChanged;

    // --- Config options (model selection, etc.) ---

    public IReadOnlyList<SessionConfigOption> ConfigOptions => _configOptions;
    public event Action? ConfigOptionsChanged;

    public async Task SetConfigOptionAsync(string configId, string value, CancellationToken cancellationToken = default)
    {
        if (_connection is null || _sessionId is null) return;

        var response = await _connection.SetSessionConfigOptionAsync(new SetSessionConfigOptionRequest
        {
            SessionId = _sessionId,
            ConfigId = configId,
            Value = JsonSerializer.SerializeToElement(value)
        }, cancellationToken);

        ApplyConfigOptions(response.ConfigOptions);
        _logger.LogInformation("Config option {ConfigId} set to {Value}", configId, value);
    }

    void ApplyConfigOptions(SessionConfigOption[]? configOptions)
    {
        if (configOptions is null) return;
        _configOptions = configOptions;
        _logger.LogInformation("Config options available: {Count}", _configOptions.Length);
        ConfigOptionsChanged?.Invoke();
    }

    // --- Mode selection ---

    public IReadOnlyList<SessionMode> AvailableModes => _availableModes;
    public string? CurrentModeId => _currentModeId;
    public event Action? ModesChanged;

    public event Action? SessionRestoreFailed;

    // --- Agent-side session deletion ---

    public bool CanDeleteAgentSession =>
        _connection is not null && _sessionId is not null && _agentCapabilities?.SessionCapabilities?.Delete is not null;

    public bool? SupportsAdditionalDirectories =>
        _agentCapabilities is null ? null : _agentCapabilities.SessionCapabilities?.AdditionalDirectories is not null;

    public IReadOnlyList<string>? ActiveAdditionalDirectories => _activeAdditionalDirectories;

    public event Action? ActiveAdditionalDirectoriesChanged;

    void SetActiveAdditionalDirectories(string[]? sent)
    {
        _activeAdditionalDirectories = sent is null ? null : [.. sent];
        ActiveAdditionalDirectoriesChanged?.Invoke();
    }

    public async Task DeleteAgentSessionAsync(CancellationToken cancellationToken = default)
    {
        if (!CanDeleteAgentSession) return;

        var sessionId = _sessionId!;
        _logger.LogInformation("Deleting agent session {SessionId}", sessionId);
        await _connection!.DeleteSessionAsync(new DeleteSessionRequest { SessionId = sessionId }, cancellationToken);
        _sessionId = null;
        SetActiveAdditionalDirectories(null);
        _logger.LogInformation("Agent session {SessionId} deleted", sessionId);
    }

    public async Task SetModeAsync(string modeId, CancellationToken cancellationToken = default)
    {
        if (_connection is null || _sessionId is null) return;

        await _connection.SetSessionModeAsync(new SetSessionModeRequest
        {
            SessionId = _sessionId,
            ModeId = modeId
        }, cancellationToken);

        _currentModeId = modeId;
        _logger.LogInformation("Mode changed to {ModeId}", modeId);
    }

    void ApplyModeState(SessionModeState? modes)
    {
        if (modes is null) return;
        _availableModes = modes.AvailableModes ?? [];
        _currentModeId = modes.CurrentModeId;
        _logger.LogInformation("Modes available: {Count}, current: {Current}",
            _availableModes.Length, _currentModeId);
        ModesChanged?.Invoke();
    }

    async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        // Fast path without lock
        if (_connection is not null && _sessionId is not null && _processHost.IsRunning)
            return;

        // Lock guards concurrent first-time init: StartConnection (LoadSessionAsync via Task.Run)
        // and a user-triggered SendMessageAsync can both reach EnsureInitializedAsync before
        // _sessionId is set. Without the lock the second caller would Kill() the process the
        // first caller is mid-handshake with.
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            // Re-check after acquiring the lock - another caller may have initialized
            if (_connection is not null && _sessionId is not null && _processHost.IsRunning)
                return;

            // The agent is still up with no session (a revert dropped it): a new session on the same connection
            // avoids a cold start (npx can take 10+ s). A wedged agent falls back to the restart below.
            if (_connection is not null && _connectionReady && _processHost.IsRunning
                && await TryNewSessionOnRunningAgentAsync(cancellationToken))
                return;

            _connection?.Dispose();
            _connectionReady = false;
            _processHost.Kill();

            _processHost.Start();

            _connection = new ClientSideConnection(
                _ => _clientHandler,
                _processHost.StandardOutput!,
                _processHost.StandardInput!,
                _logger);

            _connection.ErrorOccurred += msg => _logger.LogError("ACP endpoint error: {Error}", msg);
            _connection.Open();

            _logger.LogInformation("Initializing ACP connection");

            var initResponse = await _connection.InitializeAsync(new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new Implementation
                {
                    Name = "vs-agentic",
                    Version = "1.0.0"
                },
                ClientCapabilities = new ClientCapabilities
                {
                    Fs = new FileSystemCapabilities
                    {
                        ReadTextFile = true,
                        WriteTextFile = true
                    },
                    Terminal = true,
                    // Notices are shown as a line in the chat; compactions as an expandable summary entry
                    // (SessionUpdateAdapter). Agents only send either when advertised here.
                    Session = new ClientSessionCapabilities
                    {
                        Notices = new NoticeCapabilities(),
                        Compaction = new CompactionCapabilities()
                    },
                    // Form and URL elicitations are rendered in the chat banner (ElicitationHandler).
                    Elicitation = new ElicitationCapabilities
                    {
                        Form = new ElicitationFormCapabilities(),
                        Url = new ElicitationUrlCapabilities()
                    }
                }
            }, cancellationToken);

            _agentCapabilities = initResponse.AgentCapabilities;
            _ideToolsWithheldLogged = false;
            _connectionReady = true;
            _logger.LogInformation("Agent initialized: {Name} v{Version}",
                initResponse.AgentInfo?.Name, initResponse.AgentInfo?.Version);

            try
            {
                var capsJson = System.Text.Json.JsonSerializer.Serialize(
                    initResponse.AgentCapabilities,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                _logger.LogInformation("Agent capabilities:\n{Capabilities}", capsJson);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to serialize agent capabilities: {Error}", ex.Message);
            }

            var restoredSessionId = _sessionId;
            var additionalDirectories = ResolveAdditionalDirectories(initResponse.AgentCapabilities);

            if (restoredSessionId is not null && initResponse.AgentCapabilities.LoadSession)
            {
                try
                {
                    _logger.LogInformation(">>> LoadSession BEGIN for sessionId={SessionId}", restoredSessionId);
                    // Enter replay mode: the agent replays prior session history as live
                    // notifications, but our UI is already populated from the local store. The
                    // adapter drops them until SessionUpdateAdapter.Reset() restores Live mode at
                    // the start of the user's next real prompt (see AdapterMode docs).
                    _sessionUpdateAdapter.Mode = AdapterMode.Replay;
                    var loadRequest = new LoadSessionRequest
                    {
                        SessionId = restoredSessionId,
                        Cwd = _options.Value.WorkingDirectory,
                        McpServers = McpServersFor(initResponse.AgentCapabilities)
                    };
                    if (additionalDirectories is not null)
                        loadRequest = loadRequest with { AdditionalDirectories = additionalDirectories };
                    var loadResponse = await _connection.LoadSessionAsync(loadRequest, cancellationToken);
                    _logger.LogInformation(">>> LoadSession END for sessionId={SessionId}", restoredSessionId);
                    SetActiveAdditionalDirectories(additionalDirectories ?? []);
                    ApplyConfigOptions(loadResponse.ConfigOptions);
                    ApplyModeState(loadResponse.Modes);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    _sessionUpdateAdapter.Mode = AdapterMode.Live;
                    _logger.LogWarning("LoadSession failed, creating new session: {Error}", ex.Message);
                    _sessionId = null;
                    SessionRestoreFailed?.Invoke();
                }
            }
            else if (restoredSessionId is not null)
            {
                // The agent cannot load sessions (after a crash, or for a chat reopened later), so it starts
                // fresh and does not know the conversation the user still sees. Say so, as for a failed load.
                _logger.LogWarning("Agent cannot load sessions; starting a new session for retained sessionId={SessionId}", restoredSessionId);
                _sessionId = null;
                SessionRestoreFailed?.Invoke();
            }

            await NewSessionAsync(additionalDirectories, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped mid-handshake (a cold npx start can take many seconds). Drop the half-made connection so the
            // next message starts the agent again; a retained session id is then loaded as usual.
            _logger.LogInformation("Agent start-up cancelled");
            _sessionUpdateAdapter.Mode = AdapterMode.Live;
            _connection?.Dispose();
            _connection = null;
            _connectionReady = false;
            throw;
        }
        finally
        {
            _initLock.Release();
        }
    }

    async Task<bool> TryNewSessionOnRunningAgentAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Value.NewSessionOnRunningAgentTimeout);
        try
        {
            _logger.LogInformation("Opening a new session on the running agent");
            await NewSessionAsync(ResolveAdditionalDirectories(_agentCapabilities!), timeout.Token);
            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("The running agent did not open a new session ({Error}); restarting it", ex.Message);
            return false;
        }
    }

    async Task NewSessionAsync(string[]? additionalDirectories, CancellationToken cancellationToken)
    {
        var newRequest = new NewSessionRequest
        {
            Cwd = _options.Value.WorkingDirectory,
            McpServers = McpServersFor(_agentCapabilities!)
        };
        if (additionalDirectories is not null)
            newRequest = newRequest with { AdditionalDirectories = additionalDirectories };
        var sessionResponse = await _connection!.NewSessionAsync(newRequest, cancellationToken);

        _sessionId = sessionResponse.SessionId;
        SetActiveAdditionalDirectories(additionalDirectories ?? []);
        _logger.LogInformation("NewSession response - modes={Modes}",
            sessionResponse.Modes is not null ? $"{sessionResponse.Modes.AvailableModes?.Length} modes, current={sessionResponse.Modes.CurrentModeId}" : "null");
        ApplyConfigOptions(sessionResponse.ConfigOptions);
        ApplyModeState(sessionResponse.Modes);
        _logger.LogInformation("ACP session created: {SessionId}", _sessionId);
    }

    /// <summary>
    /// The MCP servers to offer at session setup: the IDE tools server (ADR-061) when the user turned it on and the
    /// agent advertises <c>mcpCapabilities.http</c>; otherwise none.
    /// </summary>
    McpServer[] McpServersFor(AgentCapabilities capabilities)
    {
        if (_ideTools?.Describe() is not { } server)
            return [];

        if (!capabilities.McpCapabilities.Http)
        {
            if (!_ideToolsWithheldLogged)
                _logger.LogInformation("[IdeTools] The agent takes no HTTP MCP servers; IDE tools not offered");
            _ideToolsWithheldLogged = true;
            return [];
        }

        return [server];
    }

    /// <summary>
    /// Project folders outside the working directory to share with the agent, or null when there are
    /// none or the agent did not advertise <c>sessionCapabilities.additionalDirectories</c> (the
    /// field is then omitted from the request entirely).
    /// </summary>
    string[]? ResolveAdditionalDirectories(AgentCapabilities capabilities)
    {
        var dirs = _options.Value.AdditionalDirectories;
        if (dirs.Length == 0) return null;

        if (capabilities.SessionCapabilities?.AdditionalDirectories is null)
        {
            _logger.LogInformation("Agent does not support additionalDirectories; {Count} project folder(s) outside the working directory will not be shared", dirs.Length);
            return null;
        }

        _logger.LogInformation("Additional directories: {Dirs}", string.Join(", ", dirs));
        return dirs;
    }

    /// <summary>Text form of a pasted snippet, used when the agent cannot take embedded resources and in the post-revert context.</summary>
    static string FormatSelection(CodeSelection s) =>
        $"<code_selection path=\"{s.FilePath}\" lines=\"{s.StartLine}-{s.EndLine}\">\n{s.Text}\n</code_selection>";

    static ContentBlock[] BuildPromptBlocks(string text, IReadOnlyList<ImageAttachment>? images, IReadOnlyList<string>? filePaths,
        IReadOnlyList<string>? folderPaths, IReadOnlyList<CodeSelection>? selections, bool embeddedContext)
    {
        var blocks = new List<ContentBlock> { new ContentBlockText { Text = text } };

        if (selections is { Count: > 0 })
        {
            foreach (var s in selections)
            {
                if (embeddedContext)
                {
                    blocks.Add(new ContentBlockResource
                    {
                        Resource = new TextResourceContents
                        {
                            Uri = $"{new Uri(s.FilePath).AbsoluteUri}#L{s.StartLine}-{s.EndLine}",
                            MimeType = "text/plain",
                            Text = s.Text
                        }
                    });
                }
                else
                {
                    blocks.Add(new ContentBlockText { Text = FormatSelection(s) });
                }
            }
        }

        if (images is { Count: > 0 })
        {
            foreach (var img in images)
            {
                blocks.Add(new ContentBlockImage
                {
                    Data = Convert.ToBase64String(img.Data),
                    MimeType = img.MimeType
                });
            }
        }

        if (filePaths is { Count: > 0 })
        {
            foreach (var path in filePaths)
            {
                var uri = new Uri(path).AbsoluteUri;
                blocks.Add(new ContentBlockResourceLink
                {
                    Uri = uri,
                    Name = System.IO.Path.GetFileName(path),
                    MimeType = "text/plain"
                });
            }
        }

        if (folderPaths is { Count: > 0 })
        {
            foreach (var path in folderPaths)
            {
                // Trailing separator so the resulting file:// URI ends in "/", marking it as a directory.
                var dirPath = path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
                    + System.IO.Path.DirectorySeparatorChar;
                var uri = new Uri(dirPath).AbsoluteUri;
                blocks.Add(new ContentBlockResourceLink
                {
                    Uri = uri,
                    Name = new FolderAttachment { FolderPath = path }.FolderName
                });
            }
        }

        return blocks.ToArray();
    }

    public void Dispose()
    {
        // AcpSessionService is registered under multiple service types; MS DI may call
        // Dispose() once per registration. Null _connection out to prevent double-dispose.
        var conn = _connection;
        _connection = null;
        conn?.Dispose();
        _processHost.Dispose();
        _initLock.Dispose();
    }
}
