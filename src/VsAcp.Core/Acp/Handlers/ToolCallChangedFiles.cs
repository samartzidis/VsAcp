using AcpProtocol;

namespace VsAcp.Core.Acp.Handlers;

/// <summary>
/// The files each tool call of the current turn changed, as the agent reported them, so the completed step can
/// carry them (<see cref="Abstractions.OutputItem.ChangedFiles"/>; persisted, they tell "Revert to here" which
/// files are the agent's). Files of an edit diff or a delete call count only when the call succeeded: a rejected
/// edit changed nothing. Files a shell command changed (claude-agent-acp's <c>bashEditDiff</c>) count whatever the
/// command's outcome, since a failing command can still have written them.
/// </summary>
public sealed class ToolCallChangedFiles
{
    readonly Dictionary<string, HashSet<string>> _ifSucceeded = new();
    readonly Dictionary<string, HashSet<string>> _always = new();
    readonly HashSet<string> _deleteCalls = new();

    public void Reset()
    {
        _ifSucceeded.Clear();
        _always.Clear();
        _deleteCalls.Clear();
    }

    /// <summary>Notes what a <c>tool_call</c> or <c>tool_call_update</c> says about the files the call touches.</summary>
    public void Record(string toolCallId, ToolKind? kind, ToolCallContent[]? content, ToolCallLocation[]? locations,
        IReadOnlyList<string> shellChangedFiles)
    {
        if (kind == ToolKind.Delete) _deleteCalls.Add(toolCallId);

        if (content is not null)
            foreach (var diff in content.OfType<ToolCallContentDiff>())
                Add(_ifSucceeded, toolCallId, diff.Path);

        // Updates usually omit the kind, so a delete call is recognised by its first report.
        if (locations is not null && _deleteCalls.Contains(toolCallId))
            foreach (var location in locations)
                Add(_ifSucceeded, toolCallId, location.Path);

        foreach (var path in shellChangedFiles)
            Add(_always, toolCallId, path);
    }

    /// <summary>The files the call changed, for its completed step (empty, never null: the step was tracked).</summary>
    public IReadOnlyList<string> Of(string toolCallId, bool succeeded)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_always.TryGetValue(toolCallId, out var always)) files.UnionWith(always);
        if (succeeded && _ifSucceeded.TryGetValue(toolCallId, out var edited)) files.UnionWith(edited);
        return files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void Add(Dictionary<string, HashSet<string>> files, string toolCallId, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception) { return; }

        if (!files.TryGetValue(toolCallId, out var set))
            files[toolCallId] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(full);
    }
}
