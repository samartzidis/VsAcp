using System.Text.Json;

namespace VsAcp.Bridge;

// C# side of the page <-> host contract. Keep in sync with Web/src/bridge/protocol.ts: property names
// are serialised camelCase and message types are the strings used there.

internal static class BridgeJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static string Serialize(object message) => JsonSerializer.Serialize(message, Options);
}

internal sealed record ChoiceDto(string Id, string Name);

internal sealed record SlashCommandDto(string Name, string Description);

internal sealed record ImageChipDto(string Id, string FileName);

internal sealed record FileChipDto(string Id, string FileName, string FilePath);

/// <summary>A folder dropped on the composer: a one-off reference for this message only, distinct from the shared extra-folders chip.</summary>
internal sealed record FolderChipDto(string Id, string FolderName, string FolderPath);

internal sealed record SelectionChipDto(string Id, string Label, string FilePath);

internal sealed record ActiveContextDto(string FileName, string FilePath, bool Enabled);

internal sealed record UsageDto(string Text, string? Tooltip);

/// <summary>
/// One file with a pending change: <c>Kind</c> is "modified", "added" or "deleted"; plus its added / removed line counts.
/// <c>Source</c> is "agent" (reported by the agent) or "scan" (found on disk, listed under "Other changes").
/// </summary>
internal sealed record PendingFileDto(string FileName, string FilePath, string DisplayPath, string Kind, int Added, int Removed, string Source);

/// <summary>One shared extra folder: <c>Source</c> is "auto" (found in the solution) or "manual" (added by the user).</summary>
internal sealed record FolderDto(string Path, string DisplayName, string Source);

/// <summary>The extra folders shared with the agent. <c>Supported</c> is null until the agent has said whether it uses them; <c>CanEdit</c> is false when there is nowhere to save changes.</summary>
internal sealed record FoldersDto(IReadOnlyList<FolderDto> Items, bool? Supported, bool CanEdit);

/// <summary>
/// Everything the composer shows except the text being typed. Sent whole whenever any of it changes.
/// <c>ActiveFolders</c> is the list this tab's agent session was started with (null until the session
/// exists); it can differ from the configured <c>Folders</c>.
/// <c>Agent</c> is "claude" or "cursor" when the tab's agent was started from that built-in preset (its logo is shown),
/// null for a custom command.
/// </summary>
internal sealed record ComposerStateDto(
    bool Enabled,
    string Placeholder,
    bool CanSend,
    bool Busy,
    bool Reverting,
    IReadOnlyList<ImageChipDto> Images,
    IReadOnlyList<FileChipDto> Files,
    IReadOnlyList<FolderChipDto> AttachedFolders,
    IReadOnlyList<SelectionChipDto> Selections,
    ActiveContextDto? ActiveContext,
    IReadOnlyList<SlashCommandDto> Commands,
    IReadOnlyList<ChoiceDto> Modes,
    string? ModeId,
    IReadOnlyList<ChoiceDto> Models,
    string? ModelId,
    bool AutoApprove,
    UsageDto? Usage,
    IReadOnlyList<PendingFileDto> PendingFiles,
    FoldersDto? Folders,
    IReadOnlyList<FolderDto>? ActiveFolders,
    string? Agent);
