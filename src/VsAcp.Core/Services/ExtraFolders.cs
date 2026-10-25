using VsAcp.Core.Configuration;

namespace VsAcp.Core.Services;

/// <summary>Where a shared extra folder came from: found in the VS solution, or added by the user.</summary>
public enum ExtraFolderSource
{
    Auto,
    Manual
}

/// <summary>One folder shared with the agent beyond the solution folder (ACP additionalDirectories).</summary>
public sealed record ExtraFolder(string Path, ExtraFolderSource Source);

/// <summary>The user's choices for a solution: folders added by hand, and auto-detected folders turned off.</summary>
public sealed class ExtraFoldersData
{
    public string[] Added { get; set; } = [];
    public string[] Removed { get; set; } = [];
}

/// <summary>Pure rules for the extra-folder list: how the auto-detected and user-chosen folders combine, and which paths may be added.</summary>
public static class ExtraFolders
{
    /// <summary>
    /// The folders to share: the auto-detected ones that are not in <paramref name="removed"/>, then the user-added ones.
    /// Every entry exists, is outside <paramref name="solutionDirectory"/>, has no trailing separator and appears once
    /// (ignoring case; a folder that is both auto-detected and added counts as auto).
    /// </summary>
    public static IReadOnlyList<ExtraFolder> Merge(
        string solutionDirectory,
        IEnumerable<string?> auto,
        IEnumerable<string?> added,
        IEnumerable<string?> removed,
        Func<string, bool>? directoryExists = null)
    {
        var removedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in removed)
        {
            var key = Key(r);
            if (key is not null) removedKeys.Add(key);
        }

        var result = new List<ExtraFolder>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in ProjectFolders.Outside(solutionDirectory, auto, directoryExists))
            if (!removedKeys.Contains(dir) && seen.Add(dir))
                result.Add(new ExtraFolder(dir, ExtraFolderSource.Auto));

        foreach (var dir in ProjectFolders.Outside(solutionDirectory, added, directoryExists))
            if (seen.Add(dir))
                result.Add(new ExtraFolder(dir, ExtraFolderSource.Manual));

        return result;
    }

    /// <summary>
    /// Null when <paramref name="path"/> may be shared as an extra folder, else a short reason for the user.
    /// Rejects relative paths, missing folders, network folders, drive roots, the user profile folder and
    /// folders already under the solution folder.
    /// </summary>
    public static string? Validate(
        string? path,
        string solutionDirectory,
        Func<string, bool>? directoryExists = null,
        Func<string, bool>? isNetworkPath = null,
        string? userProfile = null)
    {
        directoryExists ??= Directory.Exists;
        isNetworkPath ??= VsAcpPaths.IsNetworkPath;
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (string.IsNullOrWhiteSpace(path)) return "No folder was chosen.";

        string full;
        try
        {
            if (!System.IO.Path.IsPathRooted(path)) return "The folder path must be absolute.";
            full = System.IO.Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return "The folder path is not valid.";
        }

        var normalized = ProjectFolders.Normalize(full)!;
        var trimmed = normalized.TrimEnd(System.IO.Path.DirectorySeparatorChar);

        if (!directoryExists(trimmed)) return "The folder does not exist.";
        if (isNetworkPath(trimmed)) return "Network folders cannot be shared.";

        var root = System.IO.Path.GetPathRoot(normalized);
        if (root is not null && string.Equals(ProjectFolders.Normalize(root), normalized, StringComparison.OrdinalIgnoreCase))
            return "A whole drive cannot be shared; choose a folder.";

        if (!string.IsNullOrWhiteSpace(userProfile)
            && string.Equals(ProjectFolders.Normalize(userProfile), normalized, StringComparison.OrdinalIgnoreCase))
            return "The user profile folder is too broad to share.";

        var solutionRoot = ProjectFolders.Normalize(solutionDirectory);
        if (solutionRoot is not null && normalized.StartsWith(solutionRoot, StringComparison.OrdinalIgnoreCase))
            return "The folder is already part of the solution folder.";

        return null;
    }

    /// <summary>
    /// Splits dropped paths into the folders among them (each once, ignoring case and a trailing separator)
    /// and the number of entries that are not folders (files, or paths that no longer exist).
    /// </summary>
    public static (IReadOnlyList<string> Folders, int Skipped) FoldersAmong(
        IEnumerable<string> paths, Func<string, bool>? directoryExists = null)
    {
        directoryExists ??= Directory.Exists;
        var folders = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        foreach (var path in paths)
        {
            var key = Key(path);
            if (string.IsNullOrWhiteSpace(path) || key is null || !directoryExists(path)) { skipped++; continue; }
            if (seen.Add(key)) folders.Add(path);
        }
        return (folders, skipped);
    }

    /// <summary>The folder without a trailing separator, for comparisons; null if it is not a usable path.</summary>
    internal static string? Key(string? path)
    {
        if (path is null) return null;
        return ProjectFolders.Normalize(path)?.TrimEnd(System.IO.Path.DirectorySeparatorChar);
    }
}
