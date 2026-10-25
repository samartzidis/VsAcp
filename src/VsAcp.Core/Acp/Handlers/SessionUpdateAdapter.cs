using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using VsAcp.Core.Abstractions;
using AcpProtocol;

namespace VsAcp.Core.Acp.Handlers;

/// <summary>
/// Whether the adapter projects incoming session notifications to the UI (<see cref="Live"/>)
/// or drops them (<see cref="Replay"/>, used during session/load history replay).
/// </summary>
public enum AdapterMode
{
    Live,
    Replay
}

public sealed class SessionUpdateAdapter
{
    readonly IOutputListener _outputListener;
    readonly ToolCallAccumulator _accumulator;
    readonly ToolCallDiffNotifier _diffNotifier;
    readonly ILogger<SessionUpdateAdapter> _logger;
    readonly HashSet<string> _knownToolCalls = new();
    // Tool calls already reported to the listener as completed, so a call that arrives finished and is then
    // followed by a trailing completed update is completed (and persisted) once, not twice.
    readonly HashSet<string> _finishedToolCalls = new();

    // The files each tool call reported changing; the completed step carries them (see OutputItem.ChangedFiles).
    readonly ToolCallChangedFiles _changedFiles = new();
    // Per-process token ensures step IDs never collide with IDs from a previously-saved session
    // that are restored into the same ChatSession on reload.
    readonly string _idPrefix = Guid.NewGuid().ToString("N")[..8];
    int _stepCounter;

    string? _thinkingItemId;
    string? _messageItemId;
    // Agent messageId of the open thinking/message item; a different id on the next chunk starts a new item.
    string? _thinkingAgentId;
    string? _messageAgentId;

    public Channel<string>? TextChannel { get; set; }

    /// <summary>
    /// Output mode for incoming session notifications.
    ///
    /// Agent-replay assumption: on <c>session/load</c> the Cursor agent replays the prior
    /// conversation as a burst of live <c>session/update</c> notifications. Our UI is already
    /// populated from the local store, so those replayed notifications would duplicate messages.
    /// While in <see cref="AdapterMode.Replay"/> the adapter drops all notifications. The mode is
    /// reset to <see cref="AdapterMode.Live"/> in <see cref="Reset"/> (start of every prompt), so
    /// late-arriving replay notifications are dropped regardless of timing, with no fixed delay.
    /// </summary>
    public AdapterMode Mode { get; set; } = AdapterMode.Live;

    CancellationToken _promptCancellationToken;

    public event Action<string>? ModeUpdated;
    public event Action<SessionConfigOption[]>? ConfigOptionsUpdated;
    public event Action<AvailableCommand[]>? CommandsUpdated;
    public event Action<UsageUpdate>? UsageUpdated;

    /// <summary>The agent named (or renamed) the session via <c>session_info_update</c>; never blank.</summary>
    public event Action<string>? TitleUpdated;

    public SessionUpdateAdapter(IOutputListener outputListener, ToolCallAccumulator accumulator, ToolCallDiffNotifier diffNotifier, ILogger<SessionUpdateAdapter> logger)
    {
        _outputListener = outputListener;
        _accumulator = accumulator;
        _diffNotifier = diffNotifier;
        _logger = logger;
    }

    public void Reset(CancellationToken promptCancellationToken = default)
    {
        Mode = AdapterMode.Live;
        _promptCancellationToken = promptCancellationToken;
        _thinkingItemId = null;
        _messageItemId = null;
        _thinkingAgentId = null;
        _messageAgentId = null;
        _knownToolCalls.Clear();
        _finishedToolCalls.Clear();
        _changedFiles.Reset();
        _diffNotifier.Reset();
        _accumulator.Reset();
    }

    public void Complete()
    {
        CompleteThinkingItem();
        CompleteMessageItem();

        // Last look at what the turn did to the workspace (catches whatever no tool call reported).
        _diffNotifier.RequestScan();
    }

    void CompleteThinkingItem()
    {
        if (_thinkingItemId is null) return;

        _outputListener.OnStepCompleted(new OutputItem
        {
            Id = _thinkingItemId,
            ToolName = "Thinking",
            Title = "Thought",
            Status = OutputItemStatus.Success,
        });
        _thinkingItemId = null;
        _thinkingAgentId = null;
    }

    void CompleteMessageItem()
    {
        if (_messageItemId is null) return;

        _outputListener.OnStepCompleted(new OutputItem
        {
            Id = _messageItemId,
            ToolName = "AI",
            Title = "Response",
            Status = OutputItemStatus.Success,
        });
        _messageItemId = null;
        _messageAgentId = null;
    }

    readonly ConcurrentDictionary<string, bool> _closedSessions = new();

    /// <summary>
    /// Drops every later update of <paramref name="sessionId"/>: a revert closed it while the agent process lives
    /// on, and a late title or usage update from it must not land on the chat's new session.
    /// </summary>
    public void IgnoreSession(string sessionId) => _closedSessions[sessionId] = true;

    public ValueTask HandleAsync(SessionNotification notification, CancellationToken cancellationToken)
    {
        if (_closedSessions.ContainsKey(notification.SessionId))
        {
            _logger.LogDebug("Dropping {UpdateType} of closed session {SessionId}", notification.Update?.GetType().Name, notification.SessionId);
            return default;
        }

        var update = notification.Update;

        // Usage is session state, not turn content: the final usage_update of a replayed history is
        // the session's current context usage, so it is applied even in Replay mode.
        if (update is SessionUpdateUsageUpdate usage)
        {
            _logger.LogDebug("Usage update: {Used}/{Size} tokens, cost={Cost}", usage.Used, usage.Size, usage.Cost?.Amount);
            UsageUpdated?.Invoke(new UsageUpdate { Used = usage.Used, Size = usage.Size, Cost = usage.Cost });
            return default;
        }

        // Notices, compactions and title updates are session events that can arrive outside a prompt (or
        // after a cancel), so a cancelled prompt must not leave a compaction stuck "in progress". They are
        // still dropped during session/load replay: the local store already holds the history.
        if (Mode != AdapterMode.Replay)
        {
            switch (update)
            {
                case SessionUpdateNotice notice:
                    HandleNotice(notice);
                    return default;
                case SessionUpdateCompactionUpdate compaction:
                    HandleCompactionUpdate(compaction);
                    return default;
                case SessionUpdateCompactionSummaryChunk chunk:
                    HandleCompactionSummaryChunk(chunk);
                    return default;
                case SessionUpdateSessionInfoUpdate info:
                    HandleSessionInfo(info);
                    return default;
            }
        }

        if (_promptCancellationToken.IsCancellationRequested || Mode == AdapterMode.Replay)
            return default;

        _logger.LogDebug("Session update received: {UpdateType}", update.GetType().Name);

        switch (update)
        {
            case SessionUpdateAgentMessageChunk msg:
                HandleAgentMessageChunk(msg);
                break;

            case SessionUpdateAgentThoughtChunk thought:
                HandleAgentThoughtChunk(thought);
                break;

            case SessionUpdateToolCall toolCall:
                HandleToolCall(toolCall);
                break;

            case SessionUpdateToolCallUpdate toolCallUpdate:
                HandleToolCallUpdate(toolCallUpdate);
                break;

            case SessionUpdatePlan plan:
                HandlePlan(plan);
                break;

            case SessionUpdateCurrentModeUpdate modeUpdate:
                ModeUpdated?.Invoke(modeUpdate.CurrentModeId);
                break;

            case SessionUpdateConfigOptionUpdate configUpdate:
                ConfigOptionsUpdated?.Invoke(configUpdate.ConfigOptions);
                break;

            case SessionUpdateUserMessageChunk:
                break;

            case SessionUpdateAvailableCommandsUpdate cmdsUpdate:
                _logger.LogDebug("Available commands updated: {Commands}",
                    string.Join(", ", cmdsUpdate.AvailableCommands.Select(c => c.Name)));
                CommandsUpdated?.Invoke(cmdsUpdate.AvailableCommands);
                break;

            case SessionUpdateUnknown unknown:
                _logger.LogWarning("Unknown session update type: {UpdateType}", unknown.RawType);
                break;

            default:
                _logger.LogWarning("Unhandled session update type: {UpdateType}", update.GetType().Name);
                break;
        }

        return default;
    }

    void HandleAgentMessageChunk(SessionUpdateAgentMessageChunk msg)
    {
        var text = ExtractText(msg.Content);
        if (text is null) return;

        TextChannel?.Writer.TryWrite(text);

        if (IsNewAgentMessage(_messageItemId, _messageAgentId, msg.MessageId))
            CompleteMessageItem();

        if (_messageItemId is null)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            _messageItemId = NextId();
            _messageAgentId = msg.MessageId;
            _outputListener.OnStepStarted(new OutputItem
            {
                Id = _messageItemId,
                ToolName = "AI",
                Title = "Response",
                Status = OutputItemStatus.Pending,
                AgentMessageId = msg.MessageId,
            });
        }
        else
        {
            _messageAgentId ??= msg.MessageId;
        }

        _outputListener.OnStepUpdated(new OutputItem
        {
            Id = _messageItemId,
            ToolName = "AI",
            Title = "Response",
            Status = OutputItemStatus.Pending,
            Delta = text
        });
    }

    void HandleAgentThoughtChunk(SessionUpdateAgentThoughtChunk thought)
    {
        var text = ExtractText(thought.Content);
        if (text is null) return;

        if (IsNewAgentMessage(_thinkingItemId, _thinkingAgentId, thought.MessageId))
            CompleteThinkingItem();

        if (_thinkingItemId is null)
        {
            _thinkingItemId = NextId();
            _thinkingAgentId = thought.MessageId;
            _outputListener.OnStepStarted(new OutputItem
            {
                Id = _thinkingItemId,
                ToolName = "Thinking",
                Title = "Thinking",
                Status = OutputItemStatus.Pending,
                AgentMessageId = thought.MessageId,
            });
        }
        else
        {
            _thinkingAgentId ??= thought.MessageId;
        }

        _outputListener.OnStepUpdated(new OutputItem
        {
            Id = _thinkingItemId,
            ToolName = "Thinking",
            Title = "Thinking",
            Status = OutputItemStatus.Pending,
            Delta = text
        });
    }

    /// <summary>
    /// True when a chunk carries an agent messageId that differs from the one the open item was
    /// started with, i.e. the agent began a new message without an intervening tool call.
    /// </summary>
    static bool IsNewAgentMessage(string? openItemId, string? openAgentId, string? chunkAgentId)
        => openItemId is not null && openAgentId is not null && chunkAgentId is not null && chunkAgentId != openAgentId;

    void HandleToolCall(SessionUpdateToolCall toolCall)
    {
        CompleteMessageItem();

        _logger.LogInformation("Tool call: {ToolCallId} '{Title}' name={Name} kind={Kind} status={Status} contentItems={ContentCount} locations={LocationCount}",
            toolCall.ToolCallId, toolCall.Title, toolCall.Name, toolCall.Kind, toolCall.Status,
            toolCall.Content?.Length ?? 0, toolCall.Locations?.Length ?? 0);
        LogToolCallDetail("tool_call", toolCall.ToolCallId, toolCall.RawInput, toolCall.RawOutput,
            toolCall.Locations, toolCall.Content);

        var status = toolCall.Status switch
        {
            ToolCallStatus.Completed => OutputItemStatus.Success,
            ToolCallStatus.Failed => OutputItemStatus.Error,
            _ => OutputItemStatus.Pending
        };

        var displayTitle = _accumulator.ResolveCallTitle(toolCall.ToolCallId, toolCall.Title, toolCall.Name, toolCall.Kind, toolCall.Locations, toolCall.RawInput);

        var item = new OutputItem
        {
            Id = toolCall.ToolCallId,
            ToolName = displayTitle,
            Title = displayTitle,
            Status = status
        };

        if (_accumulator.PopulateBody(item, toolCall.ToolCallId, toolCall.Content, toolCall.RawInput, toolCall.RawOutput, toolCall.Locations))
            _logger.LogInformation("Tool call {ToolCallId} contains DiffToolCallContent", toolCall.ToolCallId);

        var shellChanged = ClaudeCodeMeta.ShellChangedFiles(toolCall.Meta);
        _changedFiles.Record(toolCall.ToolCallId, toolCall.Kind, toolCall.Content, toolCall.Locations, shellChanged);
        if (status != OutputItemStatus.Pending)
            item.ChangedFiles = _changedFiles.Of(toolCall.ToolCallId, status == OutputItemStatus.Success);

        if (_knownToolCalls.Add(toolCall.ToolCallId))
        {
            _outputListener.OnStepStarted(item);
        }
        else
        {
            _outputListener.OnStepUpdated(item);
        }

        // A call can arrive already finished, with no later update to complete it; without this it would
        // never be completed, and so never persisted.
        if (status != OutputItemStatus.Pending && _finishedToolCalls.Add(toolCall.ToolCallId))
            _outputListener.OnStepCompleted(item);

        _diffNotifier.RecordToolCall(toolCall.ToolCallId, toolCall.Kind, toolCall.Locations);
        _diffNotifier.RecordChangedFiles(shellChanged);
        if (status == OutputItemStatus.Success)
        {
            _diffNotifier.FireIfNeeded(toolCall.Content);
            _diffNotifier.FireDeletedIfFinished(toolCall.ToolCallId);
            _diffNotifier.RequestScanAfterToolCall(toolCall.ToolCallId);
        }
    }

    void HandleToolCallUpdate(SessionUpdateToolCallUpdate update)
    {
        _logger.LogInformation("Tool call update: {ToolCallId} '{Title}' name={Name} kind={Kind} status={Status} contentItems={ContentCount} locations={LocationCount}",
            update.ToolCallId, update.Title, update.Name, update.Kind, update.Status,
            update.Content?.Length ?? 0, update.Locations?.Length ?? 0);
        LogToolCallDetail("tool_call_update", update.ToolCallId, update.RawInput, update.RawOutput,
            update.Locations, update.Content);

        var isCompleted = update.Status is ToolCallStatus.Completed or ToolCallStatus.Failed;
        var status = update.Status switch
        {
            ToolCallStatus.Completed => OutputItemStatus.Success,
            ToolCallStatus.Failed => OutputItemStatus.Error,
            _ => OutputItemStatus.Pending
        };

        var displayTitle = _accumulator.ResolveUpdateTitle(update.ToolCallId, update.Title, update.Name, update.Kind, update.Locations, update.RawInput);

        var item = new OutputItem
        {
            Id = update.ToolCallId,
            ToolName = displayTitle,
            Title = displayTitle,
            Status = status
        };

        if (_accumulator.PopulateBody(item, update.ToolCallId, update.Content, update.RawInput, update.RawOutput, update.Locations))
            _logger.LogInformation("Tool call update {ToolCallId} contains DiffToolCallContent", update.ToolCallId);

        // Fire inline diff adornment whenever diff content is present, regardless of
        // completion status. With claude-agent-acp the diff arrives in an intermediate
        // tool_call_update (status=null) before the permission is answered, so waiting
        // for completion means the adornment never appears during the permission prompt.
        if (update.Content is not null)
            _diffNotifier.FireIfNeeded(update.Content);

        _diffNotifier.RecordToolCall(update.ToolCallId, update.Kind, update.Locations);
        // claude-agent-acp says which files a shell command changed; the scan this completion starts lists them as
        // the agent's, and the completed step keeps them for "Revert to here".
        var shellChanged = ClaudeCodeMeta.ShellChangedFiles(update.Meta);
        _diffNotifier.RecordChangedFiles(shellChanged);
        _changedFiles.Record(update.ToolCallId, update.Kind, update.Content, update.Locations, shellChanged);
        if (isCompleted)
            item.ChangedFiles = _changedFiles.Of(update.ToolCallId, status == OutputItemStatus.Success);
        if (update.Status == ToolCallStatus.Completed)
        {
            _diffNotifier.FireDeletedIfFinished(update.ToolCallId);
            _diffNotifier.RequestScanAfterToolCall(update.ToolCallId);
        }

        if (isCompleted && _finishedToolCalls.Add(update.ToolCallId))
        {
            _logger.LogInformation("Tool call completed: {ToolCallId} status={Status}", update.ToolCallId, update.Status);
            _outputListener.OnStepCompleted(item);
        }
        else
        {
            _outputListener.OnStepUpdated(item);
        }
    }

    void LogToolCallDetail(string eventType, string toolCallId,
        JsonElement? rawInput, JsonElement? rawOutput,
        ToolCallLocation[]? locations, ToolCallContent[]? content)
    {
        if (rawInput is JsonElement ri && ri.ValueKind != JsonValueKind.Undefined)
            _logger.LogDebug("  [{EventType}] {ToolCallId} RawInput: {RawInput}", eventType, toolCallId, ri.ToString());
        if (rawOutput is JsonElement ro && ro.ValueKind != JsonValueKind.Undefined)
            _logger.LogDebug("  [{EventType}] {ToolCallId} RawOutput: {RawOutput}", eventType, toolCallId,
                ro.ToString().Length > 500 ? ro.ToString().Substring(0, 500) + "...(truncated)" : ro.ToString());
        if (locations is { Length: > 0 })
        {
            foreach (var loc in locations)
                _logger.LogDebug("  [{EventType}] {ToolCallId} Location: {Path}:{Line}", eventType, toolCallId, loc.Path, loc.Line);
        }
        if (content is { Length: > 0 })
        {
            foreach (var c in content)
                _logger.LogDebug("  [{EventType}] {ToolCallId} Content type: {ContentType}", eventType, toolCallId, c.GetType().Name);
        }
    }

    void HandlePlan(SessionUpdatePlan plan)
    {
        var body = string.Join("\n", plan.Entries.Select(e =>
            $"- [{(e.Status == PlanEntryStatus.Completed ? "x" : " ")}] {e.Content}"));

        var id = NextId();
        _outputListener.OnStepStarted(new OutputItem
        {
            Id = id,
            ToolName = "Plan",
            Title = "Plan",
            Status = OutputItemStatus.Info,
            Body = body,
            BodyMode = OutputBodyMode.Markdown
        });
    }

    void HandleSessionInfo(SessionUpdateSessionInfoUpdate info)
    {
        // Updates are partial, and ACP lets an agent clear the title with null; a tab with no name is
        // worse than the one it already has, so only a real title is passed on.
        var title = info.Title?.Trim();
        if (string.IsNullOrEmpty(title)) return;

        _logger.LogInformation("Session title from agent: {Title}", title);
        TitleUpdated?.Invoke(title!);
    }

    void HandleNotice(SessionUpdateNotice notice)
    {
        _logger.LogInformation("Notice ({Severity}): {Title}", notice.Severity, notice.Title);

        // Plain text only: the page renders title and description as text, never as markdown/HTML.
        _outputListener.OnStepStarted(new OutputItem
        {
            Id = NextId(),
            ToolName = "Notice",
            Title = notice.Title,
            Status = notice.Severity switch
            {
                NoticeSeverity.Error => OutputItemStatus.Error,
                NoticeSeverity.Warning => OutputItemStatus.Warning,
                _ => OutputItemStatus.Info
            },
            Body = string.IsNullOrWhiteSpace(notice.Description) ? null : notice.Description,
            BodyMode = OutputBodyMode.PlainText
        });
    }

    /// <summary>One agent context compaction as shown in the chat; the ACP id is agent-owned and only unique per session.</summary>
    sealed class CompactionState
    {
        public required string ItemId { get; init; }
        public StringBuilder Summary { get; } = new();
        public bool Done { get; set; }
    }

    readonly Dictionary<string, CompactionState> _compactions = new();

    /// <summary>Clears compaction bookkeeping when the chat history is cleared (revert, new session).</summary>
    public void ResetCompactions() => _compactions.Clear();

    void HandleCompactionUpdate(SessionUpdateCompactionUpdate update)
    {
        _logger.LogInformation("Compaction {CompactionId}: {Status}", update.CompactionId, update.Status);

        var state = GetOrStartCompaction(update.CompactionId, out var created);
        if (state.Done)
        {
            _logger.LogDebug("Compaction {CompactionId} already finished; ignoring {Status}", update.CompactionId, update.Status);
            return;
        }

        // Patch semantics: an omitted or null summary leaves the stored one unchanged (the two cannot be
        // told apart after deserialization), a non-empty array replaces it, [] clears it.
        if (update.Summary is not null)
        {
            state.Summary.Clear();
            foreach (var block in update.Summary)
            {
                var text = ExtractText(block);
                if (string.IsNullOrEmpty(text)) continue;
                if (state.Summary.Length > 0) state.Summary.Append("\n\n");
                state.Summary.Append(text);
            }
        }

        var terminal = update.Status != CompactionStatus.InProgress;
        var item = BuildCompactionItem(state, update.Status, update.Error);

        if (created)
            _outputListener.OnStepStarted(item);
        else
            _outputListener.OnStepUpdated(item);

        if (terminal)
        {
            state.Done = true;
            _outputListener.OnStepCompleted(item);
        }
    }

    void HandleCompactionSummaryChunk(SessionUpdateCompactionSummaryChunk chunk)
    {
        var state = GetOrStartCompaction(chunk.CompactionId, out var created);
        if (state.Done)
        {
            _logger.LogDebug("Compaction {CompactionId} already finished; ignoring a summary chunk", chunk.CompactionId);
            return;
        }

        var text = ExtractText(chunk.Content);
        if (text is not null)
            state.Summary.Append(text);

        var item = BuildCompactionItem(state, CompactionStatus.InProgress, null);
        if (created)
            _outputListener.OnStepStarted(item);
        else
            _outputListener.OnStepUpdated(item);
    }

    CompactionState GetOrStartCompaction(string compactionId, out bool created)
    {
        if (_compactions.TryGetValue(compactionId, out var existing))
        {
            created = false;
            return existing;
        }

        created = true;
        var state = new CompactionState { ItemId = $"acp-compaction-{_idPrefix}-{compactionId}" };
        _compactions[compactionId] = state;
        return state;
    }

    static OutputItem BuildCompactionItem(CompactionState state, CompactionStatus status, string? error)
    {
        var body = state.Summary.ToString();
        if (status == CompactionStatus.Failed && !string.IsNullOrWhiteSpace(error))
            body = (body.Length > 0 ? body + "\n\n" : "") + "**Error:** " + error;

        return new OutputItem
        {
            Id = state.ItemId,
            ToolName = "Compaction",
            Title = status switch
            {
                CompactionStatus.Completed => "Context compacted",
                CompactionStatus.Failed => "Context compaction failed",
                CompactionStatus.Cancelled => "Context compaction cancelled",
                _ => "Compacting context..."
            },
            Status = status switch
            {
                CompactionStatus.Completed => OutputItemStatus.Success,
                CompactionStatus.Failed => OutputItemStatus.Error,
                CompactionStatus.Cancelled => OutputItemStatus.Info,
                _ => OutputItemStatus.Pending
            },
            Body = body.Length > 0 ? body : null,
            BodyMode = OutputBodyMode.Markdown
        };
    }

    string NextId() => $"acp-step-{_idPrefix}-{Interlocked.Increment(ref _stepCounter)}";

    static string? ExtractText(ContentBlock content)
    {
        return content is ContentBlockText text ? text.Text : null;
    }
}
