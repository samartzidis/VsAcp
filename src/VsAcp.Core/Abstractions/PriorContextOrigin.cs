namespace VsAcp.Core.Abstractions;

/// <summary>Why the agent is given the earlier conversation as text, which decides what it is told about the files.</summary>
public enum PriorContextOrigin
{
    /// <summary>"Revert to here": the workspace files were restored to that point.</summary>
    Revert,

    /// <summary>A chat forked from another one: the workspace files were left as they are.</summary>
    Fork
}
