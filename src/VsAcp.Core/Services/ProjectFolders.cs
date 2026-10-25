namespace VsAcp.Core.Services;

/// <summary>Which project folders to share with the agent beyond the solution folder (ACP additionalDirectories).</summary>
public static class ProjectFolders
{
    /// <summary>
    /// The folders in <paramref name="projectDirectories"/> that exist and live outside
    /// <paramref name="solutionDirectory"/>, without a trailing separator and without duplicates (ignoring case).
    /// A folder whose name merely starts like the solution folder's (<c>C:\src\app2</c> next to <c>C:\src\app</c>)
    /// counts as outside.
    /// </summary>
    public static string[] Outside(
        string solutionDirectory,
        IEnumerable<string?> projectDirectories,
        Func<string, bool>? directoryExists = null)
    {
        directoryExists ??= Directory.Exists;

        var root = Normalize(solutionDirectory);
        if (root is null) return Array.Empty<string>();

        var result = new List<string>();
        foreach (var projectDirectory in projectDirectories)
        {
            if (projectDirectory is null) continue;

            var dir = Normalize(projectDirectory);
            if (dir is null || dir.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                continue;

            var trimmed = dir.TrimEnd(Path.DirectorySeparatorChar);
            if (directoryExists(trimmed) && !result.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                result.Add(trimmed);
        }
        return result.ToArray();
    }

    /// <summary>
    /// <paramref name="path"/> relative to <paramref name="rootDirectory"/> with forward slashes (for display),
    /// or the path unchanged when it is not inside the root or either one is unusable.
    /// </summary>
    public static string RelativeTo(string rootDirectory, string path)
    {
        var root = Normalize(rootDirectory);
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return path; }

        if (root is null || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || full.Length == root.Length)
            return path;
        return full.Substring(root.Length).Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>Full path with a single trailing separator, so prefix checks match whole segments; null if it is not a usable path.</summary>
    public static string? Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar; }
        catch { return null; }
    }
}
