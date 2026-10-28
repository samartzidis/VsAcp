namespace VsAcp.Core.Services;

/// <summary>
/// The agent presets of Tools &gt; Options &gt; ACP Agent (values of <c>acpAgent.preset</c>; they must match
/// <c>UnifiedSettings/registration.json</c>), the command line each built-in preset runs, and the logo the chat page shows.
/// </summary>
public static class AgentPresets
{
    public const string Cursor = "cursor";
    public const string ClaudeCode = "claudeCode";
    public const string Custom = "custom";

    public const string CursorCommand = "agent";
    public const string CursorArguments = "acp";
    public const string ClaudeCodeCommand = "npx";
    public const string ClaudeCodeArguments = "@agentclientprotocol/claude-agent-acp --yes";

    /// <summary>The command line a built-in preset runs; null for <see cref="Custom"/> and unknown values.</summary>
    public static (string Command, string Arguments)? CommandLine(string? preset) => preset switch
    {
        Cursor => (CursorCommand, CursorArguments),
        ClaudeCode => (ClaudeCodeCommand, ClaudeCodeArguments),
        _ => null,
    };

    /// <summary>
    /// The logo shown for a chat started from <paramref name="preset"/>: "claude", "cursor", or null. Only the built-in
    /// presets have one; a custom command never does, whatever it runs.
    /// </summary>
    public static string? Logo(string? preset) => preset switch
    {
        ClaudeCode => "claude",
        Cursor => "cursor",
        _ => null,
    };
}
