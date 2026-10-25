using System.Text.RegularExpressions;

namespace VsAcp.Core.Services;

/// <summary>An existing file to open and the line to put the caret on (0 = no line given).</summary>
public sealed record ResolvedFileLink(string FilePath, int Line);

/// <summary>
/// Turns a path as an agent wrote it into a file that exists. Agents cite paths in many shapes: with a
/// <c>:42</c> or <c>:42-51</c> suffix, as Git-Bash paths (<c>/c/src/a.cs</c>), with forward slashes, relative to
/// the solution, or relative to some subfolder of it.
/// </summary>
public static class FileLinkResolver
{
    private static readonly string[] SkippedFolders = { ".git", ".vs", "bin", "obj", "node_modules", ".idea" };

    /// <summary>
    /// Resolves <paramref name="rawPath"/> to an existing file, or null when there is none. A relative path is
    /// resolved against <paramref name="solutionDirectory"/>; failing that, the single file under it whose path
    /// ends with the given relative path is used (ambiguous matches are rejected). Touches the disk, so call it
    /// off the UI thread.
    /// </summary>
    public static ResolvedFileLink? Resolve(string rawPath, string? solutionDirectory)
    {
        // Optional :line suffix (e.g. "file.cs:42" or "file.cs:42-51"); the first number is the line to show.
        var line = 0;
        var lineMatch = Regex.Match(rawPath, @":(\d+)(?:-\d+)?$");
        var filePath = lineMatch.Success ? rawPath.Substring(0, lineMatch.Index) : rawPath;
        if (lineMatch.Success)
            int.TryParse(lineMatch.Groups[1].Value, out line);

        // MSYS / Git-Bash style paths ("/c/foo/bar") to Windows form ("c:\foo\bar").
        var msysMatch = Regex.Match(filePath, @"^/([A-Za-z])/");
        if (msysMatch.Success)
            filePath = msysMatch.Groups[1].Value + ":/" + filePath.Substring(3);

        filePath = filePath.Replace('/', '\\');

        var relativePath = filePath;
        var isRooted = Path.IsPathRooted(filePath);

        if (!isRooted && solutionDirectory is not null)
            filePath = Path.GetFullPath(Path.Combine(solutionDirectory, filePath));

        // Agents often cite paths relative to a subfolder (e.g. "VsAcp.Core/README.md" for src\VsAcp.Core\README.md).
        if (!File.Exists(filePath) && !isRooted && solutionDirectory is not null)
        {
            var found = FindUniqueFileBySuffix(solutionDirectory, relativePath);
            if (found is not null)
                filePath = found;
        }

        return File.Exists(filePath) ? new ResolvedFileLink(filePath, line) : null;
    }

    /// <summary>
    /// Returns the single file under <paramref name="root"/> whose path ends with
    /// <paramref name="relativePath"/>, or null when there is none or the match is ambiguous. Build output and
    /// tool folders are not searched.
    /// </summary>
    public static string? FindUniqueFileBySuffix(string root, string relativePath)
    {
        var suffix = relativePath.TrimStart('.', '\\');
        if (suffix.Length == 0) return null;

        string? found = null;
        var pending = new Stack<string>();
        pending.Push(root);
        try
        {
            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                foreach (var file in Directory.EnumerateFiles(dir, Path.GetFileName(suffix)))
                {
                    if (!file.EndsWith("\\" + suffix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (found is not null) return null;
                    found = file;
                }
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (!SkippedFolders.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
                        pending.Push(sub);
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[OpenFile] Suffix search failed under {Root}", root);
            return null;
        }
        return found;
    }
}
