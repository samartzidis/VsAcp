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
    /// The effective preset and command line from the saved preset and the optional command / arguments fields.
    /// Each field is resolved on its own: blank (or whitespace) takes the preset's value, anything else replaces it.
    /// <see cref="Custom"/> has no values of its own, so its blank fields stay blank (a blank command is reported when
    /// the agent starts). An unknown preset reads as Cursor.
    /// </summary>
    public static (string Preset, string Command, string Arguments) Resolve(string? preset, string? command, string? arguments)
    {
        if (preset != Custom && CommandLine(preset) is null)
            preset = Cursor;
        var (presetCommand, presetArguments) = CommandLine(preset) ?? ("", "");
        return (preset!,
            string.IsNullOrWhiteSpace(command) ? presetCommand : command!.Trim(),
            string.IsNullOrWhiteSpace(arguments) ? presetArguments : arguments!.Trim());
    }

    /// <summary>
    /// The logo shown for a chat started from <paramref name="preset"/>: "claude", "cursor", "custom" (the pink
    /// invader, whatever the command runs) or null for a chat saved without a preset.
    /// </summary>
    public static string? Logo(string? preset) => preset switch
    {
        ClaudeCode => "claude",
        Cursor => "cursor",
        Custom => "custom",
        _ => null,
    };
}
