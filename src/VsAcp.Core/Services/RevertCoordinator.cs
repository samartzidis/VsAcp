using VsAcp.Core.Abstractions;
using VsAcp.Core.Models;

namespace VsAcp.Core.Services;

/// <summary>What a revert would change on disk, split into the agent's files and everything else.</summary>
public sealed class RevertPreview
{
    /// <summary>
    /// The files the agent reported changing in the reverted turns, as restoring just those would change them.
    /// When <see cref="AgentFilesKnown"/> is false (history saved before this was recorded): every change.
    /// </summary>
    public required RestorePreview Agent { get; init; }

    /// <summary>
    /// Everything else a full restore would change: the user's own edits, other chats, shell changes the agent
    /// did not report, build output. Left alone unless the user asks for it. Empty when the agent's files are unknown.
    /// </summary>
    public required RestorePreview Other { get; init; }

    public bool AgentFilesKnown { get; init; }

    public bool HasChanges => Agent.HasChanges || Other.HasChanges;
}

/// <summary>
/// Orchestrates the data/agent side of "Revert to here": restore the workspace to a checkpoint,
/// truncate persisted history by creation-order ordinal, reset the agent session, and prime the
/// prior-conversation context prefix. The caller owns UI concerns (busy state, clearing/reloading
/// the session, notifying the host to reload editors).
/// <para>
/// The restore covers the files the agent reported changing in the discarded turns (the tool steps'
/// <see cref="PersistedMessage.ChangedFiles"/>); everything else is restored only when the user asks for it.
/// </para>
/// </summary>
public sealed class RevertCoordinator
{
    private readonly ICheckpointService _checkpointService;
    private readonly IChatService? _chatService;

    private static readonly RestorePreview NoChanges = new() { Modified = [], Recreated = [], Deleted = [] };

    public RevertCoordinator(ICheckpointService checkpointService, IChatService? chatService)
    {
        _checkpointService = checkpointService;
        _chatService = chatService;
    }

    /// <summary>
    /// The files the agent reported changing in the turns a revert to <paramref name="checkpointId"/> discards
    /// (the target message's own turn and everything after it). Null when that is unknown: the target is not in
    /// <paramref name="messages"/>, or one of the discarded user messages was saved before tool steps recorded
    /// their files, in which case the revert restores everything as it always did.
    /// </summary>
    public static IReadOnlyCollection<string>? AgentChangedFiles(IReadOnlyList<PersistedMessage> messages, string checkpointId)
    {
        var target = messages.FirstOrDefault(m => m.CheckpointId == checkpointId);
        if (target is null) return null;

        var discarded = messages.Where(m => m.Ordinal >= target.Ordinal).ToList();
        if (discarded.Any(m => m.ItemType == nameof(ChatItemType.User) && !m.TracksChangedFiles)) return null;

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var message in discarded)
            if (message.ChangedFiles is not null) files.UnionWith(message.ChangedFiles);
        return files;
    }

    /// <summary>What <see cref="RevertAsync"/> would change on disk, by group, without changing anything.</summary>
    public async Task<RevertPreview> PreviewAsync(string checkpointId, ISessionStore? store, string? sid)
    {
        var agentFiles = store is not null && sid is not null
            ? AgentChangedFiles(await store.GetMessagesAsync(sid), checkpointId)
            : null;
        var preview = await _checkpointService.PreviewRestoreAsync(checkpointId, agentFiles);
        return new RevertPreview { Agent = preview, Other = preview.Excluded ?? NoChanges, AgentFilesKnown = agentFiles is not null };
    }

    /// <summary>
    /// Reverts the workspace and history to the given checkpoint. The workspace restore covers the agent's
    /// files only, unless <paramref name="includeOther"/> (or the agent's files are unknown, see
    /// <see cref="AgentChangedFiles"/>), which restores every file. Returns the reverted user message
    /// (the one that owns the checkpoint) so the caller can restore its text and file attachments into
    /// the input for resubmission, or null when it cannot be determined.
    /// </summary>
    public async Task<PersistedMessage?> RevertAsync(string checkpointId, ISessionStore? store, string? sid, bool includeOther)
    {
        IReadOnlyList<PersistedMessage> allMessages = store is not null && sid is not null
            ? await store.GetMessagesAsync(sid)
            : [];

        // A Revert button belongs to a message of its own chat, so an unknown checkpoint is a misrouted request
        // (another tab's click). Going ahead would restore every file and truncate this chat to nothing.
        if (store is not null && sid is not null && allMessages.All(m => m.CheckpointId != checkpointId))
        {
            Serilog.Log.Warning("[Revert] Checkpoint {CheckpointId} is no message of chat {SessionId}; nothing reverted", checkpointId, sid);
            throw new InvalidOperationException($"Checkpoint {checkpointId} is not a message of this chat; nothing was reverted.");
        }

        // 1. Restore workspace files: the agent's, or everything.
        var onlyPaths = includeOther ? null : AgentChangedFiles(allMessages, checkpointId);
        Serilog.Log.Information("[Revert] Restoring {Scope}", onlyPaths is null
            ? (includeOther ? "every file (the user asked)" : "every file (the agent's files are unknown)")
            : $"the agent's {onlyPaths.Count} file(s)");
        await _checkpointService.RestoreAsync(checkpointId, onlyPaths);

        // 2. Find the ordinal of the user message that owns this checkpoint, and the messages to keep.
        int keepUpToOrdinal = -1;
        PersistedMessage? target = null;
        List<PersistedMessage> messagesToKeep = [];
        // Checkpoints owned by the history being discarded (the target's own checkpoint is kept: it is
        // the restore point and a resend after the revert builds on it).
        List<string> discardedCheckpointIds = [];
        if (store is not null && sid is not null)
        {
            target = allMessages.FirstOrDefault(m => m.CheckpointId == checkpointId);

            Serilog.Log.Information("[Revert] checkpointId={CheckpointId} totalMessages={Total} targetFound={Found} targetOrdinal={Ordinal}",
                checkpointId, allMessages.Count, target is not null, target?.Ordinal);

            if (target is not null)
            {
                keepUpToOrdinal = target.Ordinal - 1;
                discardedCheckpointIds = allMessages
                    .Where(m => m.Ordinal > target.Ordinal && m.CheckpointId is not null)
                    .Select(m => m.CheckpointId!)
                    .ToList();
            }

            messagesToKeep = allMessages
                .Where(m => m.Ordinal <= keepUpToOrdinal)
                .OrderBy(m => m.Ordinal)
                .ToList();

            Serilog.Log.Information("[Revert] keepUpToOrdinal={KeepUpTo} messagesToKeep={Count}", keepUpToOrdinal, messagesToKeep.Count);

            // 3. Truncate persisted messages
            await store.TruncateMessagesAsync(sid, keepUpToOrdinal);
        }

        // 4. Drop this session's later checkpoints. Only those: the shadow repo is shared by every chat
        // tab of the workspace, so "everything newer than the target" would delete other tabs' restore points.
        await _checkpointService.DeleteCheckpointsAsync(discardedCheckpointIds);

        // 5. Drop the old agent session (the agent process stays for the next one) and erase the persisted
        // ACP session ID so that on the next VS load, EnablePersistence gets null and never calls
        // session/load with the old ID (which would replay the full pre-revert history from Cursor's backend).
        _chatService?.ResetSession();
        if (store is not null && sid is not null)
        {
            var index = await store.GetSessionIndexAsync();
            var entry = index.FirstOrDefault(e => e.Id == sid);
            if (entry is not null && entry.AcpSessionId is not null)
            {
                entry.AcpSessionId = null;
                await store.UpdateSessionAsync(entry);
                Serilog.Log.Information("[Revert] Cleared persisted AcpSessionId for session {SessionId}", sid);
            }
        }

        // 5b. Prime the prior-conversation context: a prefix block transparently prepended to the
        // user's first real message after the revert (no new ACP session here - that happens lazily
        // on the first SendMessageAsync, so the context rides a genuine user turn Cursor cannot strip).
        Serilog.Log.Information("[Revert] chatServiceNull={Null} messagesToKeepCount={Count} - will{Skip} prime context prefix",
            _chatService is null, messagesToKeep.Count, (_chatService is null || messagesToKeep.Count == 0) ? " SKIP" : "");
        if (_chatService is not null && messagesToKeep.Count > 0)
        {
            try
            {
                await _chatService.InitializeWithContextAsync(messagesToKeep);
                Serilog.Log.Information("[Revert] Context prefix primed");
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[Revert] Context prefix priming failed - agent will start with no prior context");
            }
        }

        return target;
    }
}
