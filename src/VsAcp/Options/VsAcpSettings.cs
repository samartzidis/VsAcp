using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Utilities.UnifiedSettings;

namespace VsAcp.Options;

/// <summary>
/// Tools &gt; Options &gt; ACP Agent. The page is declared statically in <c>UnifiedSettings/registration.json</c>
/// (registered by <c>[ProvideSettingsManifest]</c> on <see cref="VsAcpPackage"/>), which is how VS's own
/// components do it; a VSSDK <c>DialogPage</c> would only get a link to the legacy dialog in the new Settings UI.
/// Monikers and defaults here must match that file.
/// </summary>
internal static class VsAcpSettings
{
    public const string PresetCursor = "cursor";
    public const string PresetClaudeCode = "claudeCode";
    public const string PresetCustom = "custom";

    public const string PresetMoniker = "acpAgent.preset";
    public const string AgentCommandMoniker = "acpAgent.agentCommand";
    public const string AgentArgumentsMoniker = "acpAgent.agentArguments";
    public const string EnableChatDevToolsMoniker = "acpAgent.enableChatDevTools";
    public const string EnableFileLoggingMoniker = "acpAgent.enableFileLogging";

    public static readonly string[] AllMonikers =
    {
        PresetMoniker, AgentCommandMoniker, AgentArgumentsMoniker, EnableChatDevToolsMoniker, EnableFileLoggingMoniker,
    };

    const string CursorAgentCommand = "agent";
    const string CursorAgentArguments = "acp";
    const string ClaudeCodeCommand = "npx";
    const string ClaudeCodeArguments = "@agentclientprotocol/claude-agent-acp --yes";

    /// <summary>
    /// Turns a settings read into the effective agent command line. <paramref name="reader"/> may be null
    /// (Unified Settings unavailable), which yields the declared defaults. A preset cannot populate the
    /// command fields, so it is resolved here and the fields only appear for "Custom".
    /// </summary>
    internal static AgentSettings Read(ISettingsReader? reader)
    {
        var preset = ReadValue(reader, PresetMoniker, PresetCursor);
        var (command, arguments) = preset switch
        {
            PresetClaudeCode => (ClaudeCodeCommand, ClaudeCodeArguments),
            PresetCustom => (
                ReadValue(reader, AgentCommandMoniker, CursorAgentCommand),
                ReadValue(reader, AgentArgumentsMoniker, CursorAgentArguments)),
            _ => (CursorAgentCommand, CursorAgentArguments),
        };
        return new AgentSettings(preset, command, arguments,
            ReadValue(reader, EnableChatDevToolsMoniker, false),
            ReadValue(reader, EnableFileLoggingMoniker, true));
    }

    static T ReadValue<T>(ISettingsReader? reader, string moniker, T fallback) where T : notnull
    {
        if (reader is null)
            return fallback;
        try
        {
            var result = reader.GetValue<T>(moniker);
            return result.Outcome == SettingRetrievalOutcome.Success && result.Value is not null ? result.Value : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}

/// <summary>Effective values of the ACP Agent settings at one point in time.</summary>
internal sealed record AgentSettings(string Preset, string AgentCommand, string AgentArguments, bool EnableChatDevTools, bool EnableFileLogging);

/// <summary>
/// Service identity for Unified Settings' <see cref="ISettingsManager"/>. The documented service type
/// (<c>SVsUnifiedSettingsManager</c>) ships only in an internal interop assembly, so it is re-declared here
/// by the same GUID - <c>GetServiceAsync</c> resolves services by type GUID.
/// </summary>
[Guid("e3684f31-344e-42ea-9047-b620fdc7ac25")]
internal interface SVsUnifiedSettingsManager
{
}
