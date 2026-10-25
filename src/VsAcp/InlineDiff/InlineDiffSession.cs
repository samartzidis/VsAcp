using System.Collections.Generic;
using VsAcp.Core.Abstractions;
using VsAcp.Core.Services;

namespace VsAcp.InlineDiff;

/// <summary>
/// One file's pending change: where it started (<see cref="OriginalContent"/>, the start of the turn minus any hunks
/// the user kept) and what it is now (<see cref="NewContent"/>: the file on disk, or the editor's text while it is
/// open), with the hunks between the two.
/// </summary>
internal sealed class InlineDiffSession
{
    public string FilePath { get; }
    public string OriginalContent { get; }
    public string NewContent { get; }
    public IReadOnlyList<LineHunk> Hunks { get; }

    /// <summary>Whether the agent changed, created or deleted the file (relative to the start of the turn).</summary>
    public DiffKind Kind { get; }

    /// <summary>Whether the agent reported the change or a workspace scan found it (decides which group of the list it is in).</summary>
    public DiffSource Source { get; }

    /// <summary>Lines added relative to the original (for the pending-files list).</summary>
    public int AddedLines => Hunks.Sum(h => h.NewCount);

    /// <summary>Lines removed relative to the original.</summary>
    public int RemovedLines => Hunks.Sum(h => h.OldCount);

    public InlineDiffSession(string filePath, string originalContent, string newContent, DiffKind kind = DiffKind.Modified,
        DiffSource source = DiffSource.Agent)
    {
        Kind = kind;
        Source = source;
        FilePath = filePath;
        OriginalContent = originalContent;
        NewContent = newContent;
        Hunks = LineDiff.Compute(originalContent, newContent);
    }
}
