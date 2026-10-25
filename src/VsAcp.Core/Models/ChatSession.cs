using VsAcp.Core.Abstractions;

namespace VsAcp.Core.Models;

/// <summary>
/// Single in-memory source of truth for a chat session's messages. Owns message identity and
/// creation-order (<see cref="ChatMessage.Ordinal"/>) assignment, and the mapping from agent
/// <see cref="OutputItem"/> steps to messages.
///
/// All mutations raise change events; a projector renders them to the WebView and a persister
/// writes them to disk. Callers mutate only through the methods here (never the events), so a
/// mutation can never silently bypass projection or persistence. Not thread-safe: all access is
/// expected on the UI dispatcher thread (the sequence counter is Interlocked only to guard the
/// off-thread restore seed).
/// </summary>
public sealed class ChatSession
{
    private readonly List<ChatMessage> _messages = new();
    private readonly Dictionary<string, ChatMessage> _byId = new();

    // Monotonic creation-order counter. -1 means the next value is 0.
    private int _lastSequence = -1;

    /// <summary>Raised when a new message is added (user, step, or transient).</summary>
    public event Action<ChatMessage>? MessageAdded;
    /// <summary>Raised when a streaming message's content grows.</summary>
    public event Action<string, string>? MessageContentUpdated;
    /// <summary>Raised when a message's status/title (expander) changes.</summary>
    public event Action<string, OutputItemStatus, string>? MessageStatusUpdated;
    /// <summary>Raised when a message's body block is (re)set.</summary>
    public event Action<string, string, OutputBodyMode, string?>? MessageBodySet;
    /// <summary>Raised when a streaming message finishes.</summary>
    public event Action<string>? MessageCompleted;
    /// <summary>Raised when the session is emptied.</summary>
    public event Action? Cleared;
    /// <summary>Raised once after a bulk restore, carrying the restored snapshot in order.</summary>
    public event Action<IReadOnlyList<ChatMessage>>? Restored;
    /// <summary>
    /// Raised to ask observers to reconcile their projection against the full message list. Used as
    /// an end-of-turn self-heal: if a live update was dropped (e.g. a WebView script lost a teardown
    /// race), re-projecting this source-of-truth snapshot converges the view without a full reload.
    /// </summary>
    public event Action<IReadOnlyList<ChatMessage>>? ResyncRequested;

    public IReadOnlyList<ChatMessage> Messages => _messages;

    public int UserMessageCount
    {
        get
        {
            var n = 0;
            foreach (var m in _messages)
                if (m.Type == ChatItemType.User) n++;
            return n;
        }
    }

    public int NextSequence() => System.Threading.Interlocked.Increment(ref _lastSequence);

    public ChatMessage? Find(string id) => _byId.TryGetValue(id, out var m) ? m : null;

    public bool TryGet(string id, out ChatMessage message)
    {
        var found = _byId.TryGetValue(id, out var m);
        message = m!;
        return found;
    }

    /// <summary>Adds a user message (lowest ordinal in its turn) and raises <see cref="MessageAdded"/>.</summary>
    public ChatMessage AddUser(string id, string content, string? checkpointId,
        IReadOnlyList<string>? imageDataUris = null, IReadOnlyList<string>? fileNames = null,
        IReadOnlyList<string>? filePaths = null, IReadOnlyList<string>? folderNames = null,
        IReadOnlyList<string>? folderPaths = null, IReadOnlyList<CodeSelection>? selections = null)
        => AddAndRaise(new ChatMessage
        {
            Id = id,
            Ordinal = NextSequence(),
            Type = ChatItemType.User,
            Content = content,
            Title = "You",
            Status = OutputItemStatus.Success,
            IsStreaming = false,
            CheckpointId = checkpointId,
            ImageDataUris = imageDataUris,
            FileNames = fileNames,
            FilePaths = filePaths,
            FolderNames = folderNames,
            FolderPaths = folderPaths,
            Selections = selections,
            TracksChangedFiles = true
        });

    /// <summary>
    /// Creates and adds a message for a newly started agent step, mapping the
    /// <see cref="OutputItem"/> to message fields (type, streaming, expander title), and raises
    /// <see cref="MessageAdded"/>.
    /// </summary>
    public ChatMessage StartStep(OutputItem item)
    {
        var (type, streaming) = ClassifyStep(item.ToolName);
        var expanderTitle = type == ChatItemType.Thinking ? "Thinking..." : item.Title;
        return AddAndRaise(new ChatMessage
        {
            Id = item.Id,
            Ordinal = NextSequence(),
            Type = type,
            ToolName = item.ToolName,
            Title = item.Title,
            Status = item.Status,
            IsStreaming = streaming,
            ExpanderTitle = expanderTitle,
            Body = item.Body,
            BodyMode = item.BodyMode,
            AgentMessageId = item.AgentMessageId,
            // Notices are live events, not history: shown but never persisted.
            IsTransient = type == ChatItemType.Notice
        });
    }

    /// <summary>Adds a transient (non-persisted) message such as an error or cancellation notice.</summary>
    public ChatMessage AddTransient(string id, ChatItemType type, string content)
        => AddAndRaise(new ChatMessage
        {
            Id = id,
            Ordinal = NextSequence(),
            Type = type,
            Content = content,
            Status = OutputItemStatus.Success,
            IsStreaming = false,
            IsTransient = true
        });

    /// <summary>
    /// Applies a streaming update for an in-flight step, raising the matching change events.
    /// Owns the OutputItem-to-message mapping that previously lived in the view-model.
    /// </summary>
    public void ApplyStepUpdate(OutputItem item)
    {
        if (!TryGet(item.Id, out var msg)) return;

        if (!string.IsNullOrEmpty(item.Delta))
        {
            msg.Content += item.Delta;
            MessageContentUpdated?.Invoke(item.Id, msg.Content);
        }

        if (!string.IsNullOrEmpty(item.Title) && item.Title != msg.Title)
        {
            msg.Title = item.Title;
            msg.ToolName = item.ToolName;
            msg.ExpanderTitle = item.Title;
            MessageStatusUpdated?.Invoke(item.Id, item.Status, item.Title);
        }

        if (!string.IsNullOrEmpty(item.Body) && item.ToolName != "AI")
        {
            msg.Body = item.Body;
            msg.BodyMode = item.BodyMode;
            msg.Language = item.Language;
            MessageBodySet?.Invoke(item.Id, item.Body!, item.BodyMode, item.Language);
        }
    }

    /// <summary>Finalizes an in-flight step, raising status/body/completed events.</summary>
    public void CompleteStep(OutputItem item)
    {
        if (!TryGet(item.Id, out var msg)) return;

        msg.Status = item.Status;
        msg.IsStreaming = false;
        if (item.ChangedFiles is not null) msg.ChangedFiles = item.ChangedFiles;

        MessageStatusUpdated?.Invoke(item.Id, item.Status,
            item.ToolName == "Thinking" ? item.Title : msg.ExpanderTitle);

        if (item.ToolName == "Thinking")
        {
            msg.ExpanderTitle = item.Title;
        }
        else if (!string.IsNullOrEmpty(item.Body) && item.ToolName != "AI")
        {
            msg.Body = item.Body;
            msg.BodyMode = item.BodyMode;
            msg.Language = item.Language;
            MessageBodySet?.Invoke(item.Id, item.Body!, item.BodyMode, item.Language);
        }

        MessageCompleted?.Invoke(item.Id);
    }

    /// <summary>Classifies an agent step's tool name into a message type and whether it streams.</summary>
    public static (ChatItemType Type, bool Streaming) ClassifyStep(string? toolName)
    {
        var isAi = toolName == "AI";
        var isThinking = toolName == "Thinking";
        var isAgent = toolName == "Agent";
        var type = isAi ? ChatItemType.Assistant
                 : isThinking ? ChatItemType.Thinking
                 : toolName == "Notice" ? ChatItemType.Notice
                 : toolName == "Compaction" ? ChatItemType.Compaction
                 : ChatItemType.ToolStep;
        return (type, isAi || isAgent || isThinking);
    }

    public void Clear()
    {
        _messages.Clear();
        _byId.Clear();
        System.Threading.Interlocked.Exchange(ref _lastSequence, -1);
        Cleared?.Invoke();
    }

    /// <summary>
    /// Bulk-loads restored messages (already in creation order), advances the sequence counter past
    /// the highest restored ordinal, and raises a single <see cref="Restored"/> with the snapshot.
    /// </summary>
    public void RestoreRange(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            Add(message);
            var current = System.Threading.Volatile.Read(ref _lastSequence);
            if (message.Ordinal > current)
                System.Threading.Interlocked.Exchange(ref _lastSequence, message.Ordinal);
        }
        if (_messages.Count > 0)
            Restored?.Invoke(_messages);
    }

    /// <summary>
    /// Asks observers (projector) to reconcile against the current message list. Idempotent and
    /// cheap when nothing diverged; intended to run at end of turn as a self-heal.
    /// </summary>
    public void RequestResync() => ResyncRequested?.Invoke(_messages);

    private ChatMessage AddAndRaise(ChatMessage message)
    {
        Add(message);
        MessageAdded?.Invoke(message);
        return message;
    }

    private ChatMessage Add(ChatMessage message)
    {
        _messages.Add(message);
        _byId[message.Id] = message;
        return message;
    }
}
