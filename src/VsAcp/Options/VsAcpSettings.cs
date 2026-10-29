using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Utilities.UnifiedSettings;
using Serilog;
using VsAcp.Core.Services;

namespace VsAcp.Options;

/// <summary>
/// Tools &gt; Options &gt; ACP Agent. The page is declared statically in <c>UnifiedSettings/registration.json</c>
/// (registered by <c>[ProvideSettingsManifest]</c> on <see cref="VsAcpPackage"/>), which is how VS's own
/// components do it; a VSSDK <c>DialogPage</c> would only get a link to the legacy dialog in the new Settings UI.
/// Monikers and defaults here must match that file.
/// </summary>
internal static class VsAcpSettings
{
    public const string PresetMoniker = "acpAgent.preset";
    public const string AgentCommandMoniker = "acpAgent.agentCommand";
    public const string AgentArgumentsMoniker = "acpAgent.agentArguments";
    public const string EnableChatDevToolsMoniker = "acpAgent.enableChatDevTools";
    public const string EnableFileLoggingMoniker = "acpAgent.enableFileLogging";
    public const string EnableIdeToolsMoniker = "acpAgent.enableIdeTools";
    public const string CursorEnvironmentMoniker = "acpAgent.cursorEnvironment";
    public const string ClaudeCodeEnvironmentMoniker = "acpAgent.claudeCodeEnvironment";
    public const string CustomEnvironmentMoniker = "acpAgent.customEnvironment";

    public static readonly string[] AllMonikers =
    {
        PresetMoniker, AgentCommandMoniker, AgentArgumentsMoniker, EnableChatDevToolsMoniker, EnableFileLoggingMoniker,
        EnableIdeToolsMoniker, CursorEnvironmentMoniker, ClaudeCodeEnvironmentMoniker, CustomEnvironmentMoniker,
    };

    /// <summary>
    /// Turns a settings read into the effective agent command line. <paramref name="reader"/> may be null
    /// (Unified Settings unavailable), which yields the declared defaults. A preset cannot populate the
    /// command fields, so they are blank by default and <see cref="AgentPresets.Resolve"/> fills each blank one
    /// from the preset; a value typed in a field overrides the preset's.
    /// </summary>
    internal static AgentSettings Read(ISettingsReader? reader)
    {
        var (preset, command, arguments) = AgentPresets.Resolve(
            ReadValue(reader, PresetMoniker, AgentPresets.Cursor),
            ReadValue(reader, AgentCommandMoniker, ""),
            ReadValue(reader, AgentArgumentsMoniker, ""));
        return new AgentSettings(preset, command, arguments,
            ReadValue(reader, EnableChatDevToolsMoniker, false),
            ReadValue(reader, EnableFileLoggingMoniker, true),
            ReadValue(reader, EnableIdeToolsMoniker, false),
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                [AgentPresets.Cursor] = ReadEnvironment(reader, CursorEnvironmentMoniker),
                [AgentPresets.ClaudeCode] = ReadEnvironment(reader, ClaudeCodeEnvironmentMoniker),
                [AgentPresets.Custom] = ReadEnvironment(reader, CustomEnvironmentMoniker),
            });
    }

    /// <summary>A preset's list: one object per row with a "name" and a "value" (the shape registration.json declares).</summary>
    static Dictionary<string, string> ReadEnvironment(ISettingsReader? reader, string moniker)
    {
        if (reader is null)
            return new Dictionary<string, string>();
        try
        {
            var result = reader.GetArray<IReadOnlyDictionary<string, object>>(moniker);
            if (result.Outcome != SettingRetrievalOutcome.Success)
            {
                Log.Warning("[Settings] Could not read {Moniker}: {Outcome} {Message}", moniker, result.Outcome, result.Message);
                return new Dictionary<string, string>();
            }
            return AgentEnvironment.Build(result.Value.Select(row =>
                new KeyValuePair<string?, string?>(TextOf(row, "name"), TextOf(row, "value"))));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Settings] Could not read {Moniker}", moniker);
            return new Dictionary<string, string>();
        }
    }

    static string? TextOf(IReadOnlyDictionary<string, object> row, string key) =>
        row.TryGetValue(key, out var value) ? value?.ToString() : null;

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

/// <summary>
/// Effective values of the ACP Agent settings at one point in time. <paramref name="Environment"/> holds every
/// preset's variables, keyed by preset: a saved chat takes its own preset's, not the selected one's.
/// </summary>
internal sealed record AgentSettings(string Preset, string AgentCommand, string AgentArguments, bool EnableChatDevTools,
    bool EnableFileLogging, bool EnableIdeTools, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? Environment = null)
{
    /// <summary>The extra environment variables for an agent started from <paramref name="preset"/>; none for an unknown or missing one.</summary>
    public IReadOnlyDictionary<string, string> EnvironmentOf(string? preset) =>
        preset is not null && Environment is not null && Environment.TryGetValue(preset, out var variables)
            ? variables
            : new Dictionary<string, string>();
}

/// <summary>
/// Service identity for Unified Settings' <see cref="ISettingsManager"/>. The documented service type
/// (<c>SVsUnifiedSettingsManager</c>) ships only in an internal interop assembly, so it is re-declared here
/// by the same GUID - <c>GetServiceAsync</c> resolves services by type GUID.
/// </summary>
[Guid("e3684f31-344e-42ea-9047-b620fdc7ac25")]
internal interface SVsUnifiedSettingsManager
{
}
