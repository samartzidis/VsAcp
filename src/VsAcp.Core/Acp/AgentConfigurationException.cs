namespace VsAcp.Core.Acp;

/// <summary>
/// Thrown when the agent process cannot be started due to a configuration problem,
/// such as a missing or invalid <c>AgentCommand</c> setting.
/// </summary>
public sealed class AgentConfigurationException : InvalidOperationException
{
    public AgentConfigurationException(string message) : base(message) { }
    public AgentConfigurationException(string message, Exception inner) : base(message, inner) { }
}
