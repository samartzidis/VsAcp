namespace VsAcp.Core.Configuration;

public class VsAcpOptions
{
    public string WorkingDirectory { get; set; } = Environment.CurrentDirectory;

    /// <summary>
    /// True when <see cref="WorkingDirectory"/> is a real project/solution directory.
    /// False when it is the UserProfile fallback (no solution open), in which case
    /// project-local <c>.vsacp</c> storage is suppressed and AppData is used instead.
    /// </summary>
    public bool HasProjectDirectory { get; set; }

    /// <summary>
    /// Project directories outside <see cref="WorkingDirectory"/> that the agent should also have
    /// access to (ACP <c>additionalDirectories</c>). Only sent when the agent advertises support.
    /// </summary>
    public string[] AdditionalDirectories { get; set; } = [];

    /// <summary>
    /// Executable to launch as the ACP agent process. Defaults to "agent.cmd".
    /// </summary>
    public string AgentCommand { get; set; } = "agent.cmd";

    /// <summary>
    /// Command-line arguments for the agent process.
    /// </summary>
    public string[] AgentArgs { get; set; } = ["acp"];

    /// <summary>
    /// Additional environment variables passed to the agent process.
    /// </summary>
    public Dictionary<string, string> AgentEnv { get; set; } = new();

    /// <summary>
    /// How long a stopped turn may take to end after session/cancel before the agent is treated as wedged and
    /// its process tree is killed (the next message then starts a fresh agent).
    /// </summary>
    public TimeSpan CancelGracePeriod { get; set; } = TimeSpan.FromSeconds(5);
}
