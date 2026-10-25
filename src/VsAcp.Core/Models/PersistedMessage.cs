namespace VsAcp.Core.Models;

public class PersistedMessage
{
    /// <summary>
    /// Stable, unique-within-session message id (the same id used for the WebView element).
    /// Persisted so id-keyed operations (copy, revert, future per-message actions) stay stable
    /// across reload instead of being regenerated on restore.
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// Creation-order sequence assigned by the caller. -1 means "unassigned" - the store
    /// falls back to append position. Used for both display ordering on restore and as the
    /// revert truncation key, so it must reflect creation order, not completion/append order.
    /// </summary>
    public int Ordinal { get; set; } = -1;
    public string ItemType { get; set; } = "";
    public string Content { get; set; } = "";
    public string? ToolName { get; set; }
    public string? Title { get; set; }
    public string? Body { get; set; }
    public string? BodyMode { get; set; }
    public string? Language { get; set; }
    public string? ExpanderTitle { get; set; }
    public string StatusText { get; set; } = "Success";
    public DateTime CreatedUtc { get; set; }

    /// <summary>
    /// Agent-assigned message id (ACP <c>messageId</c>) for assistant/thinking messages, when the
    /// agent provided one. Links our message to the agent's own message identity.
    /// </summary>
    public string? AgentMessageId { get; set; }

    /// <summary>
    /// ID of the workspace snapshot taken before this user message was sent.
    /// Populated only on user messages. Used to restore workspace state on revert.
    /// </summary>
    public string? CheckpointId { get; set; }

    /// <summary>
    /// Full paths of files attached (dropped) on a user message. Populated only on user messages.
    /// Used to restore the attachments into the input on "Revert to here".
    /// </summary>
    public List<string>? FilePaths { get; set; }

    /// <summary>
    /// Full paths of folders attached (dropped) on a user message. Populated only on user messages. A
    /// one-off reference for that message only (unlike the shared extra-folders list, which grants the agent
    /// session-wide access via <c>additionalDirectories</c>). Used to restore the attachments into the input
    /// on "Revert to here".
    /// </summary>
    public List<string>? FolderPaths { get; set; }

    /// <summary>
    /// Inline image attachments as data URIs (<c>data:{mime};base64,{data}</c>). Populated only on
    /// user messages. Persisted so images survive reload and can be restored on "Revert to here".
    /// </summary>
    public List<string>? ImageDataUris { get; set; }

    /// <summary>
    /// Code snippets pasted from an editor as file + line-range references. Populated only on user
    /// messages. Persisted so they survive reload and can be restored on "Revert to here".
    /// </summary>
    public List<Abstractions.CodeSelection>? Selections { get; set; }

    /// <summary>
    /// Absolute paths of the files the agent reported this tool call changed. Populated only on tool steps.
    /// "Revert to here" restores these by default and leaves every other file alone.
    /// </summary>
    public List<string>? ChangedFiles { get; set; }

    /// <summary>
    /// True on user messages whose turn records <see cref="ChangedFiles"/> on its tool steps; false (absent) on
    /// messages saved before that, which a revert treats as "the agent's files are unknown: restore everything".
    /// </summary>
    public bool TracksChangedFiles { get; set; }
}
