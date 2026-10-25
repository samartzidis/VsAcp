namespace VsAcp.Core.Abstractions;

/// <summary>
/// Kind of a chat message in a session transcript.
/// </summary>
public enum ChatItemType
{
    User,
    Assistant,
    ToolStep,
    Thinking,

    /// <summary>Live agent notice (ACP <c>notice</c>). Transient: shown, never persisted or replayed into a revert prefix.</summary>
    Notice,

    /// <summary>Agent context compaction (ACP <c>compaction_update</c>). Persisted; the summary is the expandable body.</summary>
    Compaction
}
