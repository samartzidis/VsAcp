using VsAcp.Core.Abstractions;
using VsAcp.Core.Models;

namespace VsAcp.Core.Services;

public enum TurnResult
{
    /// <summary>No chat service is connected; a notice was shown and nothing was sent.</summary>
    NoService,
    /// <summary>The agent turn completed normally.</summary>
    Completed,
    /// <summary>The user cancelled the turn.</summary>
    Cancelled,
    /// <summary>The turn failed with an error (notice shown).</summary>
    Errored
}

/// <summary>
/// Executes a single chat turn: snapshot checkpoint, add the user message, stream the agent reply,
/// and surface cancellation/errors as transient messages. Owns no view state - the view-model
/// keeps observable/command state and reacts to the returned <see cref="TurnResult"/>.
/// </summary>
public sealed class ChatTurnRunner
{
    private readonly IChatService? _chatService;
    private readonly ICheckpointService? _checkpointService;
    private readonly ChatSession _session;
    private readonly Func<string, string> _nextId;
    private readonly Action<Action> _post;

    /// <param name="nextId">Maps an element-id prefix to a session-unique id (shared counter with the view-model).</param>
    /// <param name="post">Applies a session change in order with the agent's output (the controller's queue), so a
    /// "stopped" or error line can never land above output that was still waiting to be applied.</param>
    public ChatTurnRunner(IChatService? chatService, ICheckpointService? checkpointService,
        ChatSession session, Func<string, string> nextId, Action<Action> post)
    {
        _chatService = chatService;
        _checkpointService = checkpointService;
        _session = session;
        _nextId = nextId;
        _post = post;
    }

    public async Task<TurnResult> RunAsync(
        string message,
        IReadOnlyList<ImageAttachment>? images,
        IReadOnlyList<string>? imageDataUris,
        IReadOnlyList<string>? fileNames,
        IReadOnlyList<string>? filePaths,
        IReadOnlyList<string>? droppedFilePaths,
        IReadOnlyList<string>? folderNames,
        IReadOnlyList<string>? folderPaths,
        IReadOnlyList<CodeSelection>? selections,
        bool canCreateCheckpoint,
        CancellationToken cancellationToken)
    {
        // Snapshot workspace before the agent turn begins (best-effort - null if it fails). Skipped
        // when no project folder is set: the fallback working dir (%USERPROFILE%) is too large to scan.
        string? checkpointId = null;
        if (canCreateCheckpoint && _checkpointService is not null)
        {
            try { checkpointId = await _checkpointService.CreateCheckpointAsync(); }
            catch { /* best effort - do not block the send */ }
        }

        // AddUser raises MessageAdded -> projector renders, persister writes. Added after the
        // checkpoint so the message carries its CheckpointId.
        var userId = _nextId("user");
        _post(() => _session.AddUser(userId, message, checkpointId, imageDataUris, fileNames, droppedFilePaths, folderNames, folderPaths, selections));

        if (_chatService is null)
        {
            AddNotice(_nextId("user-err"), "_AI service not connected yet. This will be wired up in a future update._");
            return TurnResult.NoService;
        }

        try
        {
            await foreach (var _ in _chatService.SendMessageAsync(message, images, filePaths, folderPaths, selections, cancellationToken))
            {
                // Output is handled by listener callbacks.
            }
            return TurnResult.Completed;
        }
        catch (OperationCanceledException)
        {
            AddNotice(_nextId("cancel"), "_Processing stopped._");
            return TurnResult.Cancelled;
        }
        catch (Exception ex)
        {
            AddNotice(_nextId("err"), $"**Error:** {ex.Message}");
            return TurnResult.Errored;
        }
    }

    private void AddNotice(string id, string content)
        => _post(() => _session.AddTransient(id, ChatItemType.Assistant, content));
}
