using VsAcp.Core.Abstractions;

namespace VsAcp.Core.Models;

/// <summary>
/// A single in-memory chat message, owned by <see cref="ChatSession"/>. This is the authoritative
/// model; the WebView DOM and the persisted JSONL are projections of it.
///
/// Mutable fields use <c>internal set</c> so only code inside <c>VsAcp.Core</c> (i.e.
/// <see cref="ChatSession"/> methods) can update them. External assemblies (the VS extension, tests)
/// see a read-only view and must go through <c>ChatSession</c> to trigger mutations, which ensures
/// the change events that drive projection and persistence are always raised.
/// </summary>
public sealed class ChatMessage
{
    public required string Id { get; init; }
    public required int Ordinal { get; init; }
    public required ChatItemType Type { get; init; }

    public string Content { get; internal set; } = "";
    public string? ToolName { get; internal set; }
    public string Title { get; internal set; } = "";
    public string? Body { get; internal set; }
    public OutputBodyMode BodyMode { get; internal set; } = OutputBodyMode.Markdown;
    public string? Language { get; internal set; }
    public string ExpanderTitle { get; internal set; } = "";
    public OutputItemStatus Status { get; internal set; } = OutputItemStatus.Pending;
    public bool IsStreaming { get; internal set; }

    /// <summary>Workspace snapshot id; populated only on user messages (drives "Revert to here").</summary>
    public string? CheckpointId { get; init; }

    /// <summary>Agent-assigned message id (ACP <c>messageId</c>) on assistant/thinking messages, when provided.</summary>
    public string? AgentMessageId { get; init; }

    /// <summary>
    /// Inline image attachments as data URIs. Persisted so they survive reload and can be restored
    /// into the input on "Revert to here".
    /// </summary>
    public IReadOnlyList<string>? ImageDataUris { get; init; }

    /// <summary>File attachment names shown on a user message. Display-only.</summary>
    public IReadOnlyList<string>? FileNames { get; init; }

    /// <summary>
    /// Full paths of files attached (dropped) on a user message. Persisted so "Revert to here" can
    /// restore them into the input for resubmission. File names for display are derived from these.
    /// </summary>
    public IReadOnlyList<string>? FilePaths { get; init; }

    /// <summary>Folder attachment names shown on a user message. Display-only.</summary>
    public IReadOnlyList<string>? FolderNames { get; init; }

    /// <summary>
    /// Full paths of folders attached (dropped) on a user message: a one-off reference sent to the agent for
    /// this message only (unlike the shared extra-folders list, it grants no session-wide access). Persisted
    /// so "Revert to here" can restore them into the input for resubmission.
    /// </summary>
    public IReadOnlyList<string>? FolderPaths { get; init; }

    /// <summary>Code snippets pasted as file + line-range references on a user message.</summary>
    public IReadOnlyList<CodeSelection>? Selections { get; init; }

    /// <summary>On a completed tool step: the files the agent reported the call changed (see <see cref="OutputItem.ChangedFiles"/>).</summary>
    public IReadOnlyList<string>? ChangedFiles { get; internal set; }

    /// <summary>
    /// On a user message: its turn's tool steps record <see cref="ChangedFiles"/>. False on messages saved before
    /// that was recorded; a revert reaching one cannot tell the agent's files apart and restores everything.
    /// </summary>
    public bool TracksChangedFiles { get; init; }

    /// <summary>
    /// Transient messages (errors, cancellation notices) are shown in the UI but never persisted,
    /// so they do not survive reload. Ordinary turn content has this false.
    /// </summary>
    public bool IsTransient { get; init; }

    /// <summary>
    /// Reconstructs a <see cref="ChatMessage"/> from its persisted form. The canonical reverse of
    /// <see cref="Services.ChatSessionPersister"/>.<c>ToPersisted</c>. Lives here (inside
    /// <c>VsAcp.Core</c>) so it can set the <c>internal set</c> mutable fields directly.
    /// </summary>
    public static ChatMessage FromPersisted(PersistedMessage msg, string? fallbackId = null)
    {
        var type = Enum.TryParse<ChatItemType>(msg.ItemType, ignoreCase: true, out var t)
            ? t : ChatItemType.Assistant;
        var id = string.IsNullOrEmpty(msg.Id) ? (fallbackId ?? msg.Id) : msg.Id;

        return new ChatMessage
        {
            Id = id!,
            Ordinal = msg.Ordinal,
            Type = type,
            Content = msg.Content,
            ToolName = msg.ToolName,
            Title = msg.Title ?? "",
            Body = msg.Body,
            BodyMode = Enum.TryParse<OutputBodyMode>(msg.BodyMode ?? "Markdown", ignoreCase: true, out var bm)
                ? bm : OutputBodyMode.Markdown,
            Language = msg.Language,
            ExpanderTitle = msg.ExpanderTitle ?? "",
            Status = Enum.TryParse<OutputItemStatus>(msg.StatusText, ignoreCase: true, out var st)
                ? st : OutputItemStatus.Success,
            IsStreaming = false,
            CheckpointId = msg.CheckpointId,
            AgentMessageId = msg.AgentMessageId,
            FilePaths = msg.FilePaths,
            FileNames = msg.FilePaths?.Select(System.IO.Path.GetFileName).ToList(),
            FolderPaths = msg.FolderPaths,
            FolderNames = msg.FolderPaths?.Select(p => new FolderAttachment { FolderPath = p }.FolderName).ToList(),
            ImageDataUris = msg.ImageDataUris,
            Selections = msg.Selections,
            ChangedFiles = msg.ChangedFiles,
            TracksChangedFiles = msg.TracksChangedFiles
        };
    }
}
