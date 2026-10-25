using VsAcp.Core.Abstractions;

namespace VsAcp.Core.Services;

/// <summary>Decisions about pending file changes (the Keep / Undo list) that do not need Visual Studio.</summary>
public static class DiffKindRules
{
    /// <summary>
    /// What a file's pending change is after the agent reports another edit to it. The original stays the one
    /// from the start of the turn, so: a created file stays created however often it is edited; a created file
    /// that is then deleted is no change at all (null); anything else that is deleted is deleted; and a
    /// deleted file that comes back is a modification (or nothing, when its content is the original again).
    /// </summary>
    public static DiffKind? Merge(DiffKind? existing, DiffKind incoming)
    {
        if (existing is null) return incoming;
        if (incoming == DiffKind.Deleted) return existing == DiffKind.Added ? null : DiffKind.Deleted;
        return existing == DiffKind.Added ? DiffKind.Added : DiffKind.Modified;
    }

    /// <summary>
    /// Where a file's pending change counts as coming from after another report. Once the agent has reported
    /// the file it stays the agent's, so a later scan (a build touching it again) cannot move it out of the main list.
    /// </summary>
    public static DiffSource MergeSource(DiffSource? existing, DiffSource incoming)
        => existing == DiffSource.Agent ? DiffSource.Agent : incoming;

    /// <summary>
    /// What a pending change is when the file on disk is looked at again (after a revert rewrote files, say):
    /// compares <paramref name="currentContent"/> (null = the file is gone) with how the file started
    /// (<paramref name="existedAtStart"/>, <paramref name="originalContent"/>). Null means nothing is pending any more.
    /// </summary>
    public static DiffKind? Classify(bool existedAtStart, string originalContent, string? currentContent)
    {
        if (currentContent is null) return existedAtStart ? DiffKind.Deleted : null;
        if (!existedAtStart) return DiffKind.Added;
        return string.Equals(originalContent, currentContent, StringComparison.Ordinal) ? null : DiffKind.Modified;
    }

    /// <summary>
    /// Undoes a pending change on disk: a created file is deleted, anything else gets its original content back
    /// (a deleted file is recreated, with its folder if that is gone too). Throws on I/O errors.
    /// </summary>
    public static void RestoreOriginal(DiffKind kind, string path, string originalContent)
    {
        if (kind == DiffKind.Added)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, originalContent);
    }
}
