namespace VsAcp.Core.Models;

public class SessionEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "New Session";

    /// <summary>
    /// The user renamed the chat by hand, so neither the agent's title updates nor the placeholder derived from the
    /// first message may replace <see cref="Title"/>.
    /// </summary>
    public bool TitleLocked { get; set; }

    public int Ordinal { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime LastActivityUtc { get; set; }

    /// <summary>
    /// The ACP agent session ID assigned by the backend.
    /// Stored so we can call LoadSession on restore.
    /// </summary>
    public string? AcpSessionId { get; set; }

    /// <summary>
    /// The agent command (executable) used when this session was created.
    /// Persisted so that restoring the session always starts the same backend,
    /// even if the user later changes the global agent settings.
    /// </summary>
    public string? AgentCommand { get; set; }

    /// <summary>
    /// The agent arguments used when this session was created (space-separated).
    /// </summary>
    public string? AgentArgs { get; set; }

}
