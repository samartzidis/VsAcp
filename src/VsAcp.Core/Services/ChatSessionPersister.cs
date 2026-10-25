using VsAcp.Core.Abstractions;
using VsAcp.Core.Models;

namespace VsAcp.Core.Services;

/// <summary>
/// Persists <see cref="ChatSession"/> changes by mapping <see cref="ChatMessage"/> to
/// <see cref="PersistedMessage"/> and enqueuing them on the serialized
/// <see cref="SessionPersistenceWriter"/>. Single place the message-to-disk mapping lives.
///
/// User messages persist on add (their final form is known immediately); agent steps persist on
/// completion (after streaming). Transient messages (errors, cancellation) are never persisted.
/// Restored messages arrive via <see cref="ChatSession.Restored"/>, which this does not subscribe
/// to, so reload does not re-write history.
/// </summary>
public sealed class ChatSessionPersister : IDisposable
{
    private readonly ChatSession _session;
    private readonly SessionPersistenceWriter _writer;

    public ChatSessionPersister(ChatSession session, SessionPersistenceWriter writer)
    {
        _session = session;
        _writer = writer;
        _session.MessageAdded += OnMessageAdded;
        _session.MessageCompleted += OnMessageCompleted;
    }

    private void OnMessageAdded(ChatMessage m)
    {
        if (m.IsTransient) return;
        // User messages are complete on arrival; steps persist when they finish.
        if (m.Type == ChatItemType.User)
            _writer.Enqueue(ToPersisted(m));
    }

    private void OnMessageCompleted(string id)
    {
        var m = _session.Find(id);
        if (m is null || m.IsTransient) return;
        _writer.Enqueue(ToPersisted(m));
    }

    private static PersistedMessage ToPersisted(ChatMessage m) => new()
    {
        Id = m.Id,
        Ordinal = m.Ordinal,
        ItemType = m.Type.ToString(),
        Content = m.Content,
        ToolName = m.ToolName,
        Title = m.Title,
        Body = m.Body,
        BodyMode = m.BodyMode.ToString(),
        Language = m.Language,
        ExpanderTitle = m.ExpanderTitle,
        StatusText = m.Status.ToString(),
        CreatedUtc = DateTime.UtcNow,
        CheckpointId = m.CheckpointId,
        AgentMessageId = m.AgentMessageId,
        FilePaths = m.FilePaths?.ToList(),
        FolderPaths = m.FolderPaths?.ToList(),
        ImageDataUris = m.ImageDataUris?.ToList(),
        Selections = m.Selections?.ToList(),
        ChangedFiles = m.ChangedFiles?.ToList(),
        TracksChangedFiles = m.TracksChangedFiles
    };

    public void Dispose()
    {
        _session.MessageAdded -= OnMessageAdded;
        _session.MessageCompleted -= OnMessageCompleted;
    }
}
