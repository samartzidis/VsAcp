using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VsAcp.Core.Abstractions;
using VsAcp.Core.Acp;
using VsAcp.Core.Configuration;
using VsAcp.Core.DependencyInjection;
using VsAcp.Core.Models;
using VsAcp.Core.Services;

namespace VsAcp.Core.Tests.Support;

/// <summary>
/// One chat tab without Visual Studio: the real pipeline the view-model wires up (agent process, ACP
/// connection, adapter, <see cref="ChatSession"/>, persistence, shadow-git checkpoints, revert) in a temp
/// workspace, talking to the scriptable <c>VsAcp.FakeAgent</c> process. Only the UI thread hop is
/// replaced, by a lock.
/// </summary>
public sealed class ChatHarness : IDisposable
{
    /// <summary>Checkpoint ids have one-second resolution; spacing sends keeps their order deterministic.</summary>
    private const int CheckpointSpacingMs = 1100;

    private readonly TempDirectory _dir;
    private readonly ServiceProvider _provider;
    private readonly ChatSession _session;
    private readonly object _gate;
    private readonly ChatSessionController _controller;
    private readonly SimulatedUiThread? _ui;

    private ChatHarness(TempDirectory dir, ServiceProvider provider, ChatSession session, object gate,
        SessionEntry entry, TestLog log, ForwardingListener listener, SimulatedUiThread? ui)
    {
        _ui = ui;
        _dir = dir;
        _provider = provider;
        _session = session;
        _gate = gate;
        Entry = entry;
        Log = log;

        Service = provider.GetRequiredService<AcpSessionService>();
        Store = provider.GetRequiredService<ISessionStore>();
        Checkpoints = provider.GetRequiredService<ICheckpointService>();
        Diffs = provider.GetRequiredService<IDiffBroker>();
        Permissions = provider.GetRequiredService<IPermissionBroker>();

        // The same controller the view-model uses; the only difference is that "the UI thread" is a lock,
        // or, when a simulated UI thread is given, its inline-or-queue Dispatch (the view-model's rule).
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<VsAcpOptions>>().Value;
        Action<Action> dispatch = ui is null
            ? action => { lock (gate) action(); }
            : action => ui.Dispatch(() => { lock (gate) action(); });
        _controller = new ChatSessionController(session, Service, Checkpoints, Permissions,
            provider.GetService<IElicitationBroker>(), dispatch,
            lifecycle: Service, agentCommand: options.AgentCommand, agentArgs: string.Join(" ", options.AgentArgs),
            agentPreset: options.AgentPreset);
        _controller.PersistFailed += (what, ex) => log.Add($"PERSIST FAILED {what}: {ex}");
        _controller.TitleChanged += title => Title = title;
        _controller.AttachStore(Store, entry.Id);
        listener.Target = _controller;
    }

    /// <summary>The chat's latest title (placeholder, agent title or rename), or null before the first one.</summary>
    public string? Title { get; private set; }

    /// <summary>The controller under test (the one the view-model uses in Visual Studio).</summary>
    public ChatSessionController Controller => _controller;

    /// <summary>The in-memory transcript itself, for tests that subscribe to its events.</summary>
    public ChatSession Session => _session;

    /// <summary>The persisted index entry for this chat, as the store has it now (after pending writes land).</summary>
    public async Task<SessionEntry> IndexEntryAsync()
    {
        await FlushAsync();
        return (await Store.GetSessionIndexAsync()).Single(e => e.Id == Entry.Id);
    }

    public string Workspace => _dir.Path;
    public string AgentLogPath { get; private init; } = "";
    public TestLog Log { get; }
    /// <summary>The live options instance; changing it affects the next agent launch.</summary>
    public VsAcpOptions Options => _provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<VsAcpOptions>>().Value;
    public SessionEntry Entry { get; }
    public AcpSessionService Service { get; }
    public ISessionStore Store { get; }
    public ICheckpointService Checkpoints { get; }
    /// <summary>Where the pending-file-change reports land (the Visual Studio side subscribes to it).</summary>
    public IDiffBroker Diffs { get; }
    public IPermissionBroker Permissions { get; }

    /// <summary>
    /// Creates a harness whose agent plays <paramref name="scenario"/> (any object serialisable to the
    /// scenario format documented in <c>VsAcp.FakeAgent/Program.cs</c>; null = answer every prompt with "OK").
    /// </summary>
    /// <param name="uiThread">Runs turns and applies agent output on a simulated UI thread, so the ordering of
    /// queued updates against the end of the turn can be exercised (default: a lock, everything in order).</param>
    /// <param name="sessionId">A chat already in the store to be this tab's (default: a new one). With
    /// <c>configure</c> pointing <see cref="VsAcpOptions.WorkingDirectory"/> at another harness's
    /// <see cref="Workspace"/>, a second tab on the same workspace and store.</param>
    /// <param name="services">Adds to the tab's container what the host adds (e.g. its IDE tools endpoint).</param>
    public static async Task<ChatHarness> CreateAsync(object? scenario = null, Action<VsAcpOptions>? configure = null,
        SimulatedUiThread? uiThread = null, string? sessionId = null, Action<IServiceCollection>? services = null)
    {
        // The workspace is snapshotted and restored, so the agent's scenario and log live beside it.
        var dir = new TempDirectory("workspace");
        var scenarioPath = Path.Combine(dir.Root, "scenario.json");
        var logPath = Path.Combine(dir.Root, "agent.log");
        if (scenario is not null)
            File.WriteAllText(scenarioPath, JsonSerializer.Serialize(scenario));

        var agentExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FakeAgent", "VsAcp.FakeAgent.exe");
        if (!File.Exists(agentExe))
            throw new FileNotFoundException("The fake agent was not copied next to the tests.", agentExe);

        var log = new TestLog();
        var gate = new object();
        var session = new ChatSession();

        var container = new ServiceCollection();
        container.AddVsAcpServices(options =>
        {
            options.WorkingDirectory = dir.Path;
            options.HasProjectDirectory = true;
            options.AgentCommand = agentExe;
            options.AgentArgs = [];
            options.AgentEnv = new Dictionary<string, string>
            {
                ["FAKE_AGENT_SCENARIO"] = scenarioPath,
                ["FAKE_AGENT_LOG"] = logPath
            };
            configure?.Invoke(options);
        });
        container.AddSingleton(log);
        container.AddSingleton(typeof(ILogger<>), typeof(TestLogger<>));
        var listener = new ForwardingListener();
        container.AddSingleton<IOutputListener>(listener);
        services?.Invoke(container);

        var provider = container.BuildServiceProvider();
        try
        {
            var store = provider.GetRequiredService<ISessionStore>();
            await store.EnsureWorkspaceAsync();
            var entry = sessionId is null
                ? await store.CreateSessionAsync("test")
                : (await store.GetSessionIndexAsync()).Single(e => e.Id == sessionId);
            return new ChatHarness(dir, provider, session, gate, entry, log, listener, uiThread) { AgentLogPath = logPath };
        }
        catch
        {
            provider.Dispose();
            dir.Dispose();
            throw;
        }
    }

    // ---- workspace files ----

    public void WriteFile(string relativePath, string content) => _dir.Write(relativePath, content);
    public string ReadFile(string relativePath) => _dir.Read(relativePath);
    public bool FileExists(string relativePath) => _dir.Exists(relativePath);
    public string PathOf(string relativePath) => _dir.Combine(relativePath);

    // ---- chat ----

    /// <summary>Snapshot of the in-memory transcript (what the page would show).</summary>
    public IReadOnlyList<ChatMessage> Messages
    {
        get { lock (_gate) return _session.Messages.ToArray(); }
    }

    public Task<TurnResult> SendAsync(string text, CancellationToken cancellationToken = default)
        => SendAsync(new TurnSubmission(text), cancellationToken);

    public async Task<TurnResult> SendAsync(TurnSubmission submission, CancellationToken cancellationToken = default)
    {
        await Task.Delay(CheckpointSpacingMs, cancellationToken);
        // like the send command: started on the UI thread, so the awaits in the turn resume there
        return _ui is null
            ? await _controller.SendAsync(submission, cancellationToken)
            : await _ui.RunAsync(() => _controller.SendAsync(submission, cancellationToken));
    }

    /// <summary>
    /// <see cref="SendAsync"/> that fails the test instead of hanging when the turn never finishes (an agent that
    /// died mid-turn must not leave the chat busy forever).
    /// </summary>
    public async Task<TurnResult> SendWithinAsync(string text, int timeoutMs = 20000)
    {
        var sending = SendAsync(text);
        if (await Task.WhenAny(sending, Task.Delay(timeoutMs)) != sending)
            throw new TimeoutException($"The turn did not finish within {timeoutMs} ms.\n{Log}");
        return await sending;
    }

    /// <summary>Drains <see cref="AcpSessionService.SendMessageAsync"/> directly, for tests that need attachments.</summary>
    public async Task SendRawAsync(string text, IReadOnlyList<ImageAttachment>? images = null,
        IReadOnlyList<string>? filePaths = null, IReadOnlyList<string>? folderPaths = null, IReadOnlyList<CodeSelection>? selections = null)
    {
        await foreach (var _ in Service.SendMessageAsync(text, images, filePaths, folderPaths, selections))
        {
        }
    }

    /// <summary>What the Stop button does: cancel the turn, tell the agent, release anything waiting on the user.</summary>
    public void Stop(CancellationTokenSource turn) => _controller.Stop(turn);

    /// <summary>
    /// "Revert to here" on the <paramref name="userMessageIndex"/>-th user message. By default with "Also revert
    /// other changes" ticked (every file restored); <paramref name="includeOther"/> false restores only the agent's.
    /// </summary>
    public Task<PersistedMessage?> RevertToUserMessageAsync(int userMessageIndex, bool includeOther = true)
        => _controller.RevertAsync(CheckpointOfUserMessage(userMessageIndex), includeOther);

    /// <summary>"Revert to here" with a raw checkpoint id, e.g. one this chat does not own (a misrouted click).</summary>
    public Task<PersistedMessage?> RevertToCheckpointAsync(string checkpointId, bool includeOther = false)
        => _controller.RevertAsync(checkpointId, includeOther);

    /// <summary>What the revert confirmation would list for the <paramref name="userMessageIndex"/>-th user message.</summary>
    public Task<RevertPreview> PreviewRevertAsync(int userMessageIndex)
        => _controller.PreviewRevertAsync(CheckpointOfUserMessage(userMessageIndex))!;

    private string CheckpointOfUserMessage(int userMessageIndex)
    {
        lock (_gate)
            return _session.Messages.Where(m => m.Type == ChatItemType.User).ElementAt(userMessageIndex).CheckpointId!;
    }

    /// <summary>
    /// What a tab opening a saved chat does (<c>ChatSessionViewModel.EnablePersistence</c>): loads the transcript
    /// from the store and takes the agent session the store holds for the chat, to resume at the first send.
    /// </summary>
    public async Task OpenStoredChatAsync()
    {
        await _controller.RestoreAsync();
        if (await _controller.ReadAcpSessionIdAsync() is { } acpSessionId)
            Service.RestoreHistory(acpSessionId);
    }

    /// <summary>Waits until everything queued for persistence so far has been written.</summary>
    public Task FlushAsync() => _controller.FlushAsync();

    public async Task<IReadOnlyList<PersistedMessage>> PersistedAsync()
    {
        await FlushAsync();
        return await Store.GetMessagesAsync(Entry.Id);
    }

    public async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for: {what}\n{Log}");
            await Task.Delay(25);
        }
    }

    // ---- what the agent saw ----

    public IReadOnlyList<JsonObject> AgentLog()
    {
        if (!File.Exists(AgentLogPath)) return [];

        using var stream = new FileStream(AgentLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var entries = new List<JsonObject>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length > 0 && JsonNode.Parse(line) is JsonObject entry)
                entries.Add(entry);
        }
        return entries;
    }

    /// <summary>The params of every request or notification of this method that reached the agent, in order.</summary>
    public IReadOnlyList<JsonObject> Received(string method) =>
        AgentLog()
            .Where(e => (string?)e["dir"] == "in" && (string?)e["name"] == method)
            .Select(e => e["payload"] as JsonObject ?? new JsonObject())
            .ToList();

    /// <summary>The text blocks of every <c>session/prompt</c> the agent received, joined like the agent does.</summary>
    public IReadOnlyList<string> PromptTexts() =>
        Received("session/prompt")
            .Select(p => string.Join("\n", (p["prompt"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Where(b => (string?)b["type"] == "text")
                .Select(b => (string?)b["text"])))
            .ToList();

    /// <summary>Events the agent recorded about its own behaviour (e.g. <c>permissionResult</c>).</summary>
    public IReadOnlyList<JsonObject> AgentEvents(string name) =>
        AgentLog()
            .Where(e => (string?)e["dir"] == "event" && (string?)e["name"] == name)
            .Select(e => e["payload"] as JsonObject ?? new JsonObject())
            .ToList();

    /// <summary>Process ids of every agent process started so far, oldest first.</summary>
    public IReadOnlyList<int> AgentProcessIds() =>
        AgentLog()
            .Where(e => (string?)e["dir"] == "event" && (string?)e["name"] == "process")
            .Select(e => (int)e["payload"]!)
            .ToList();

    public static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static void Kill(int processId)
    {
        using var process = Process.GetProcessById(processId);
        process.Kill();
        process.WaitForExit(5000);
    }

    public void Dispose()
    {
        _controller.Dispose();
        Service.Dispose();
        _provider.Dispose();
        _ui?.Dispose();
        _dir.Dispose();
    }

    /// <summary>
    /// What <c>OutputListener</c> is in the extension: registered with the agent services before the controller
    /// exists, and forwarding to it once it does.
    /// </summary>
    private sealed class ForwardingListener : IOutputListener
    {
        public ChatSessionController? Target { get; set; }

        public void OnStepStarted(OutputItem item) => Target?.OnStepStarted(item);
        public void OnStepUpdated(OutputItem item) => Target?.OnStepUpdated(item);
        public void OnStepCompleted(OutputItem item) => Target?.OnStepCompleted(item);
    }
}
