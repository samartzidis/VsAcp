namespace VsAcp.Core.Abstractions;

public enum OutputItemStatus
{
    Pending,
    Success,
    Error,
    Info,
    Warning
}

public enum OutputBodyMode
{
    Markdown,
    Html,
    PlainText
}

public record OutputItem
{
    public required string Id { get; init; }
    public required string ToolName { get; init; }
    public required string Title { get; set; }
    public OutputItemStatus Status { get; set; } = OutputItemStatus.Pending;
    public OutputBodyMode BodyMode { get; set; } = OutputBodyMode.Markdown;
    public string? Body { get; set; }
    public string? Language { get; set; }
    public string? Delta { get; set; }

    /// <summary>Agent-assigned message id (ACP <c>messageId</c>) for streamed message/thought chunks, when provided.</summary>
    public string? AgentMessageId { get; init; }

    /// <summary>
    /// On a completed tool call: the absolute paths of the files the agent reported the call changed (empty if
    /// none). Persisted with the step; "Revert to here" restores these files by default. Null on anything else.
    /// </summary>
    public IReadOnlyList<string>? ChangedFiles { get; set; }
}

public interface IOutputListener
{
    /// <summary>
    /// Called when a new step begins. The item starts with Pending status.
    /// </summary>
    void OnStepStarted(OutputItem item);

    /// <summary>
    /// Called when a step's body or status is updated (e.g. streaming text).
    /// </summary>
    void OnStepUpdated(OutputItem item);

    /// <summary>
    /// Called when a step completes (status should be Success or Error).
    /// </summary>
    void OnStepCompleted(OutputItem item);
}
