namespace VsAcp.Core.Configuration;

/// <summary>
/// Resolves on-disk paths for VsAcp data inside a project-local <c>.vsacp</c> folder.
/// Callers are responsible for only invoking these methods when a local project directory is available.
/// </summary>
public static class VsAcpPaths
{
    /// <summary>Returns the directory where session data (index + JSONL) should be stored.</summary>
    internal static string SessionsPath(string projectDirectory) =>
        Path.Combine(projectDirectory, ".vs", "vsacp", "sessions");

    /// <summary>Returns the path for the shadow git bare repo used for checkpoints.</summary>
    internal static string CheckpointRepoPath(string projectDirectory) =>
        Path.Combine(projectDirectory, ".vs", "vsacp", "checkpoints", "shadow.git");

    /// <summary>Returns the file holding the user's extra-folder choices for a solution.</summary>
    public static string ExtraFoldersPath(string projectDirectory) =>
        Path.Combine(projectDirectory, ".vs", "vsacp", "folders.json");

    /// <summary>Returns true when <paramref name="path"/> is a UNC path or resides on a network drive.</summary>
    public static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return false;
        try { return new DriveInfo(root).DriveType == DriveType.Network; }
        catch { return false; }
    }
}
