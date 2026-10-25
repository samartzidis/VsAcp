namespace VsAcp.Core.Abstractions;

/// <summary>What the agent did to a file, relative to the start of the turn.</summary>
public enum DiffKind
{
    /// <summary>The file existed and its content changed.</summary>
    Modified,

    /// <summary>The file did not exist (the checkpoint proves it) and the agent created it.</summary>
    Added,

    /// <summary>The file existed and the agent removed it; <see cref="DiffRequest.OriginalContent"/> is what it held.</summary>
    Deleted,
}

/// <summary>How a pending change became known.</summary>
public enum DiffSource
{
    /// <summary>
    /// The agent reported it: a tool call's diff content, a delete call naming the file, or a shell command the
    /// agent said changed the file (claude-agent-acp's <c>bashEditDiff</c>).
    /// </summary>
    Agent,

    /// <summary>
    /// Found by comparing the workspace with the turn's checkpoint, and nothing says who made it: a shell command,
    /// a build, or the user's own edits.
    /// </summary>
    Scan,
}

public class DiffRequest
{
    public required string RequestId { get; init; }

    string _filePath = "";

    /// <summary>
    /// Absolute path of the file. Normalised on assignment (slash style, "..", relative form) so the same
    /// file reported by different sources (an edit tool vs a workspace scan) is one entry, not two.
    /// </summary>
    public required string FilePath
    {
        get => _filePath;
        init => _filePath = NormalizePath(value);
    }

    static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception) { return path; }
    }

    public required string OriginalContent { get; init; }
    public required string ProposedContent { get; init; }
    public DiffKind Kind { get; init; } = DiffKind.Modified;

    /// <summary>
    /// Who made the change, which decides the group of the pending list: a <see cref="DiffSource.Scan"/> change is
    /// listed apart from what the agent did.
    /// </summary>
    public DiffSource Source { get; init; } = DiffSource.Agent;

    /// <summary>
    /// True when the workspace scan found the change rather than an edit tool reporting it. A scan reports every
    /// changed file again each time it runs, so the list skips a report that changes nothing it already shows.
    /// </summary>
    public bool FoundByScan { get; init; }
}

public interface IDiffBroker
{
    /// <summary>
    /// Raised when a file has been written by the agent.
    /// The file is already on disk; subscribers show the inline diff overlay.
    /// </summary>
    event Action<DiffRequest>? DiffCompleted;

    /// <summary>
    /// Fires <see cref="DiffCompleted"/> to notify subscribers of a completed file write.
    /// </summary>
    void FireDiffCompleted(DiffRequest request);
}
