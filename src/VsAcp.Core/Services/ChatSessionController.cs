using VsAcp.Core.Abstractions;
using VsAcp.Core.Models;

namespace VsAcp.Core.Services;

/// <summary>
/// The non-UI half of one chat tab: connects agent output to the <see cref="ChatSession"/>, runs turns (with the
/// title, session-id and resync bookkeeping around them), persists, restores from the store, and reverts. The
/// view-model owns the bindable state and delegates here, and the headless tests drive the very same class, so
/// the two cannot drift apart.
/// </summary>
/// <remarks>
/// All mutations of the <see cref="ChatSession"/> go through one <see cref="SerialDispatcher"/> over the
/// <c>dispatch</c> delegate, which is how the host puts them on the thread the session is read from (the UI thread
/// in Visual Studio; a lock in tests). They therefore apply in the order they happened, whichever thread raised them;
/// the host never mutates the session itself.
/// </remarks>
public sealed class ChatSessionController : IDisposable
{
    private readonly ChatSession _session;
    private readonly IChatService? _chatService;
    private readonly ICheckpointService? _checkpointService;
    private readonly IPermissionBroker? _permissionBroker;
    private readonly IElicitationBroker? _elicitationBroker;
    private readonly SerialDispatcher _queue;
    private readonly IAcpSessionLifecycle? _lifecycle;
    private readonly string? _agentCommand;
    private readonly string? _agentArgs;
    private readonly Func<string, string> _nextId;
    private readonly ChatTurnRunner _runner;

    private SessionPersistenceWriter? _writer;
    private ChatSessionPersister? _persister;

    // The user renamed the chat: nothing automatic may change the title any more (persisted as SessionEntry.TitleLocked).
    private volatile bool _titleLocked;
    // The agent has named the chat, so the placeholder taken from the first message must not replace its title.
    private volatile bool _agentTitled;

    /// <param name="lifecycle">The ACP session lifecycle, for persisting the agent session id and reacting to a failed restore.</param>
    /// <param name="agentCommand">Recorded with the agent session id, so a later reopen knows which agent it belongs to.</param>
    /// <param name="agentArgs">See <paramref name="agentCommand"/>.</param>
    /// <param name="nextId">Maps an element-id prefix to an id unique within the session; defaults to a per-controller counter.</param>
    public ChatSessionController(
        ChatSession session,
        IChatService? chatService,
        ICheckpointService? checkpointService,
        IPermissionBroker? permissionBroker,
        IElicitationBroker? elicitationBroker,
        Action<Action> dispatch,
        IAcpSessionLifecycle? lifecycle = null,
        string? agentCommand = null,
        string? agentArgs = null,
        Func<string, string>? nextId = null)
    {
        _session = session;
        _chatService = chatService;
        _checkpointService = checkpointService;
        _permissionBroker = permissionBroker;
        _elicitationBroker = elicitationBroker;
        _queue = new SerialDispatcher(dispatch);
        _lifecycle = lifecycle;
        _agentCommand = agentCommand;
        _agentArgs = agentArgs;

        if (nextId is null)
        {
            var prefix = Guid.NewGuid().ToString("N")[..8];
            var counter = 0;
            nextId = p => $"{p}-{prefix}-{Interlocked.Increment(ref counter)}";
        }
        _nextId = nextId;
        _runner = new ChatTurnRunner(chatService, checkpointService, session, _nextId, _queue.Post);

        if (_lifecycle is not null)
            _lifecycle.SessionRestoreFailed += OnSessionRestoreFailed;
        if (_chatService is not null)
            _chatService.TitleChanged += OnAgentTitle;
    }

    /// <summary>
    /// Raised (through the dispatch delegate) when the chat's title changes: the placeholder taken from the first
    /// message, the agent's own title for the conversation, or the user's rename.
    /// </summary>
    public event Action<string>? TitleChanged;

    /// <summary>The store this tab persists to, once <see cref="AttachStore"/> has been called.</summary>
    public ISessionStore? Store { get; private set; }

    /// <summary>The persisted id of this tab's session, once <see cref="AttachStore"/> has been called.</summary>
    public string? SessionId { get; private set; }

    /// <summary>Raised (on the writer's thread) when a queued write fails; the failure is also logged.</summary>
    public event Action<string, Exception>? PersistFailed;

    // ---- agent output -> session ----

    public void OnStepStarted(OutputItem item) => _queue.Post(() => _session.StartStep(item));

    public void OnStepUpdated(OutputItem item) => _queue.Post(() =>
    {
        // A reply is saved when it completes, so text arriving later is shown but never saved: an ordering bug.
        if (!string.IsNullOrEmpty(item.Delta)
            && _session.Find(item.Id) is { IsStreaming: false, Type: ChatItemType.Assistant or ChatItemType.Thinking })
            Serilog.Log.Warning("[ChatSession] Text for {Id} arrived after it was completed; the saved copy lacks it", item.Id);
        _session.ApplyStepUpdate(item);
    });

    public void OnStepCompleted(OutputItem item) => _queue.Post(() => _session.CompleteStep(item));

    /// <summary>A transient line in the transcript (never persisted), in order with the agent's output.</summary>
    public void AddNotice(string id, string content)
        => _queue.Post(() => _session.AddTransient(id, ChatItemType.Assistant, content));

    /// <summary>Clears the transcript and the agent's history (the Clear command).</summary>
    public void ClearHistory()
    {
        _chatService?.ClearHistory();
        _queue.Post(_session.Clear);
    }

    // ---- turns ----

    /// <summary>
    /// Runs one turn and the bookkeeping around it: generates the chat title in the background on the first
    /// message, persists the agent session id after a completed turn, and finally asks the session to resync its
    /// view so anything dropped during the turn is corrected. Cancellation and errors become transient messages
    /// (see <see cref="ChatTurnRunner"/>); this does not throw for them.
    /// </summary>
    public async Task<TurnResult> SendAsync(TurnSubmission submission, CancellationToken cancellationToken)
    {
        // The first user message (before the runner adds it) names the chat.
        if (_chatService is not null && _session.UserMessageCount == 0)
            GenerateTitleInBackground(submission.Message);

        var canCheckpoint = _checkpointService is not null && SessionId is not null;

        try
        {
            var result = await _runner.RunAsync(
                submission.Message, submission.Images, submission.ImageDataUris, submission.FileNames,
                submission.FilePaths, submission.DroppedFilePaths, submission.FolderNames, submission.FolderPaths,
                submission.Selections, canCheckpoint, cancellationToken);

            if (result == TurnResult.Completed)
                PersistAcpSessionId();

            return result;
        }
        finally
        {
            // Self-heal: reconcile the view against the session source of truth. Both ends are logged, so a view
            // that never got its end-of-turn sync shows whether the request was queued and whether it ran.
            Serilog.Log.Debug("[ChatSession] Turn over; end-of-turn resync queued");
            _queue.Post(() =>
            {
                Serilog.Log.Debug("[ChatSession] End-of-turn resync running");
                _session.RequestResync();
            });
        }
    }

    // The placeholder keeps the tab from being blank until the agent names the conversation.
    private void GenerateTitleInBackground(string message)
    {
        var chat = _chatService!;
        _ = Task.Run(async () =>
        {
            try
            {
                var title = await chat.GenerateTitleAsync(message);
                if (!_agentTitled)
                    ApplyTitle(title);
            }
            catch
            {
                // best effort
            }
        });
    }

    private void OnAgentTitle(string title)
    {
        _agentTitled = true;
        ApplyTitle(title);
    }

    /// <summary>
    /// Renames the chat by hand and locks the title, so later agent titles do not replace it. A blank name is ignored.
    /// </summary>
    public void Rename(string title)
    {
        title = title.Trim();
        if (title.Length == 0) return;

        _titleLocked = true;
        EnqueueSessionIndexMutation($"rename {SessionId}", entry =>
        {
            entry.Title = title;
            entry.TitleLocked = true;
        });
        _queue.Post(() => TitleChanged?.Invoke(title));
    }

    private void ApplyTitle(string title)
    {
        if (_titleLocked) return;

        // The stored flag is checked too: it is the source of truth until RestoreAsync has read it.
        EnqueueSessionIndexMutation($"title update for {SessionId}", entry =>
        {
            if (!entry.TitleLocked) entry.Title = title;
        });
        _queue.Post(() => TitleChanged?.Invoke(title));
    }

    private void PersistAcpSessionId()
    {
        if (_lifecycle is null) return;
        var acpId = _lifecycle.SerializeHistory();
        if (string.IsNullOrEmpty(acpId)) return;

        var agentCommand = _agentCommand;
        var agentArgs = _agentArgs;

        EnqueueSessionIndexMutation($"acp session id for {SessionId}", entry =>
        {
            if (entry.AcpSessionId == acpId && entry.AgentCommand is not null) return;
            entry.AcpSessionId = acpId;
            entry.AgentCommand = agentCommand;
            entry.AgentArgs = agentArgs;
        });
    }

    /// <summary>
    /// The agent started a fresh session for a chat that already has history: forget the stale agent session
    /// id and tell the user the agent does not remember what is shown above.
    /// </summary>
    private void OnSessionRestoreFailed()
    {
        EnqueueSessionIndexMutation($"clear acp session id for {SessionId}", entry => entry.AcpSessionId = null);

        _queue.Post(() =>
        {
            const string content = "> **Agent session could not be restored.** "
                + "The agent has no memory of prior conversation - messages above are shown for reference only.";
            _session.AddTransient(_nextId("restore-warn"), ChatItemType.Assistant, content);
        });
    }

    // ---- persistence ----

    /// <summary>
    /// Starts persisting this tab's session to <paramref name="store"/> under <paramref name="sessionId"/>.
    /// Replaces any earlier attachment.
    /// </summary>
    public void AttachStore(ISessionStore store, string sessionId)
    {
        Store = store;
        SessionId = sessionId;

        _writer?.Dispose();
        _writer = new SessionPersistenceWriter(store, sessionId);
        _writer.PersistFailed += (what, ex) =>
        {
            Serilog.Log.Warning(ex, "Failed to persist session data: {What}", what);
            PersistFailed?.Invoke(what, ex);
        };

        _persister?.Dispose();
        _persister = new ChatSessionPersister(_session, _writer);
    }

    /// <summary>
    /// Queues a session-index read-modify-write behind the message writes, so it lands in order. The
    /// session id is captured now; does nothing before <see cref="AttachStore"/>.
    /// </summary>
    public void EnqueueSessionIndexMutation(string description, Action<SessionEntry> apply)
    {
        var sessionId = SessionId;
        if (_writer is null || sessionId is null) return;

        _writer.EnqueueIndexMutation(async store =>
        {
            var index = await store.GetSessionIndexAsync();
            var entry = index.FirstOrDefault(e => e.Id == sessionId);
            if (entry is not null)
            {
                apply(entry);
                entry.LastActivityUtc = DateTime.UtcNow;
                await store.UpdateSessionAsync(entry);
            }
        }, description);
    }

    /// <summary>
    /// Completes when every session update raised so far has been applied and everything that queued for
    /// persistence has been written: first the session queue drains (a reply's final save is queued there behind its
    /// last chunks), then a marker goes through the writer, an ordered queue, behind those writes. Gives up quietly
    /// after <paramref name="timeoutMs"/> rather than blocking the caller on a stuck UI thread or store.
    /// </summary>
    public async Task FlushAsync(int timeoutMs = 5000)
    {
        if (_writer is null) return;

        var deadline = Task.Delay(timeoutMs);
        var applied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Post(() => applied.TrySetResult(true));
        if (await Task.WhenAny(applied.Task, deadline) != applied.Task)
        {
            Serilog.Log.Warning("Timed out waiting for session updates to be applied");
            return;
        }

        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _writer.EnqueueIndexMutation(_ => { done.TrySetResult(true); return Task.CompletedTask; }, "flush marker");
        if (await Task.WhenAny(done.Task, deadline) != done.Task)
            Serilog.Log.Warning("Timed out waiting for session persistence to drain");
    }

    /// <summary>
    /// Loads the persisted messages into the session. Best effort: a store failure leaves the session as it is.
    /// </summary>
    public async Task RestoreAsync()
    {
        var store = Store;
        var sessionId = SessionId;
        if (store is null || sessionId is null) return;

        try
        {
            var entry = (await store.GetSessionIndexAsync()).FirstOrDefault(e => e.Id == sessionId);
            if (entry?.TitleLocked == true) _titleLocked = true;

            // GetMessagesAsync already returns creation-order (Ordinal) sorted; the store appends
            // in completion order so on-disk order can differ.
            var ordered = await store.GetMessagesAsync(sessionId);
            if (ordered.Count == 0) return;

            _queue.Post(() =>
            {
                var idx = 0;
                var restored = ordered
                    .Select(msg => ChatMessage.FromPersisted(msg, fallbackId: $"restore-{idx++}"))
                    .ToList();
                _session.RestoreRange(restored);
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Restoring session messages failed");
        }
    }

    /// <summary>
    /// The agent session id stored for this chat now, to resume when its tab opens: turns, reverts and failed restores
    /// change it after the host read its chat list. Null when there is none, or before <see cref="AttachStore"/>.
    /// </summary>
    public async Task<string?> ReadAcpSessionIdAsync()
    {
        var store = Store;
        var sessionId = SessionId;
        if (store is null || sessionId is null) return null;

        await FlushAsync();
        return (await store.GetSessionIndexAsync()).FirstOrDefault(e => e.Id == sessionId)?.AcpSessionId;
    }

    // ---- revert ----

    /// <summary>
    /// What <see cref="RevertAsync"/> would change on disk, as the agent's files and everything else (flushes
    /// pending writes first: the agent's files come from the stored tool steps). Null without a checkpoint service.
    /// </summary>
    public Task<RevertPreview>? PreviewRevertAsync(string checkpointId) =>
        _checkpointService is null ? null : PreviewRevertCoreAsync(_checkpointService, checkpointId);

    private async Task<RevertPreview> PreviewRevertCoreAsync(ICheckpointService checkpointService, string checkpointId)
    {
        await FlushAsync();
        return await new RevertCoordinator(checkpointService, _chatService).PreviewAsync(checkpointId, Store, SessionId);
    }

    /// <summary>
    /// Reverts the workspace and history to the snapshot taken before the user message that owns
    /// <paramref name="checkpointId"/>: pending writes are flushed first, then files are restored (the agent's,
    /// or every file with <paramref name="includeOther"/>), history is truncated and the agent session reset
    /// (see <see cref="RevertCoordinator"/>), and finally the session is cleared and reloaded from the truncated
    /// store. Returns the reverted user message so the caller can put its text and attachments back into the
    /// input, or null when it cannot be determined.
    /// </summary>
    public async Task<PersistedMessage?> RevertAsync(string checkpointId, bool includeOther)
    {
        if (_checkpointService is null)
            throw new InvalidOperationException("Reverting needs a checkpoint service.");

        // The coordinator reads the store to decide what to keep, so the latest messages must be in it.
        await FlushAsync();

        var reverted = await new RevertCoordinator(_checkpointService, _chatService)
            .RevertAsync(checkpointId, Store, SessionId, includeOther);

        _queue.Post(_session.Clear);
        await RestoreAsync();
        return reverted;
    }

    // ---- fork ----

    /// <summary>
    /// "Fork from here" on the message <paramref name="messageId"/>: copies this chat up to it into a new chat in the
    /// same store, flushing pending writes first (see <see cref="ForkCoordinator"/>). The caller opens the new chat and
    /// calls <see cref="PrimeForkedChatAsync"/> on its controller.
    /// </summary>
    public async Task<ForkResult> ForkAsync(string messageId)
    {
        var store = Store;
        var sessionId = SessionId;
        if (store is null || sessionId is null)
            throw new InvalidOperationException("Forking needs a saved chat.");

        await FlushAsync();
        return await new ForkCoordinator(_checkpointService, _chatService).ForkAsync(store, sessionId, messageId);
    }

    /// <summary>
    /// In the new chat of a fork: gives its agent, with the first message, what it does not have yet. A forked agent
    /// session already holds the conversation, so only a note that the files were not rolled back; otherwise the
    /// copied conversation as text.
    /// </summary>
    public Task PrimeForkedChatAsync(ForkResult fork)
    {
        if (_chatService is null) return Task.CompletedTask;
        if (!fork.AgentForked)
            return _chatService.InitializeWithContextAsync(fork.Messages, PriorContextOrigin.Fork);

        _chatService.PrimeForkNote();
        return Task.CompletedTask;
    }

    // ---- stop ----

    /// <summary>
    /// What Stop does to the agent side: tells the agent to cancel its turn, releases anything waiting on the
    /// user (permission and elicitation prompts, so their handlers answer "cancelled"), and cancels the turn's
    /// token. The caller dismisses its own prompt UI.
    /// </summary>
    public void Stop(CancellationTokenSource? turn)
    {
        _chatService?.Cancel();
        _permissionBroker?.CancelAll();
        _elicitationBroker?.CancelAll();

        try
        {
            turn?.Cancel();
        }
        catch
        {
            // Best effort - the token may already be disposed.
        }
    }

    public void Dispose()
    {
        if (_lifecycle is not null)
            _lifecycle.SessionRestoreFailed -= OnSessionRestoreFailed;
        if (_chatService is not null)
            _chatService.TitleChanged -= OnAgentTitle;

        _persister?.Dispose();
        _persister = null;
        _writer?.Dispose();
        _writer = null;
    }
}
