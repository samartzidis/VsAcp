using AcpProtocol;
using Microsoft.Extensions.Logging;
using VsAcp.Core.Abstractions;

namespace VsAcp.Core.Acp.Handlers;

/// <summary>
/// Fires inline-diff adornments for file edits reported in tool-call content. The canonical
/// "before" is the shadow-git checkpoint taken at the start of the turn (agent-agnostic, no fragile
/// OldText parsing); the "after" is the file's current on-disk content. Also reports files the agent
/// created (the checkpoint proves they did not exist) and files it deleted (in the checkpoint, gone from
/// disk). Owns the checkpoint and diff-broker dependencies so the <see cref="SessionUpdateAdapter"/>
/// stays pure translation.
/// </summary>
public sealed class ToolCallDiffNotifier
{
    readonly IDiffBroker _diffBroker;
    readonly ICheckpointService _checkpointService;
    readonly ILogger<ToolCallDiffNotifier> _logger;

    // Paths a delete-kind tool call announced, by tool call id; checked when the call completes.
    readonly Dictionary<string, List<string>> _deleteTargets = new();

    // The kind each tool call announced (updates usually omit it), to know which completed calls may have
    // touched files without saying so (a shell command).
    readonly Dictionary<string, ToolKind?> _callKinds = new();

    // What was last reported per path ("kind:length:hash"), so a workspace scan does not report the same
    // state again. Touched by the adapter's thread and by scans on the thread pool, hence the lock.
    readonly Dictionary<string, string> _reported = new(StringComparer.OrdinalIgnoreCase);

    // Files the agent said one of its shell commands changed this turn: when the scan finds them, they count as
    // the agent's (DiffSource.Agent), not as unattributed changes.
    readonly HashSet<string> _agentChanged = new(StringComparer.OrdinalIgnoreCase);
    readonly object _gate = new();
    bool _scanning;
    bool _scanAgain;

    public ToolCallDiffNotifier(IDiffBroker diffBroker, ICheckpointService checkpointService, ILogger<ToolCallDiffNotifier> logger)
    {
        _diffBroker = diffBroker;
        _checkpointService = checkpointService;
        _logger = logger;
    }

    public void Reset()
    {
        _deleteTargets.Clear();
        lock (_gate)
        {
            _callKinds.Clear();
            _reported.Clear();
            _agentChanged.Clear();
        }
    }

    /// <summary>
    /// Remembers files the agent says a shell command changed (absolute paths; others are ignored), so the scan
    /// that finds them lists them as the agent's. Call before the tool call's completion triggers the scan.
    /// </summary>
    public void RecordChangedFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        lock (_gate)
        {
            foreach (var path in paths)
                if (System.IO.Path.IsPathRooted(path)) _agentChanged.Add(Canonical(path));
        }
        _logger.LogInformation("The agent reported {Count} file(s) changed by a shell command", paths.Count);
    }

    DiffSource ScanSource(string path)
    {
        path = Canonical(path);
        lock (_gate) return _agentChanged.Contains(path) ? DiffSource.Agent : DiffSource.Scan;
    }

    static string Signature(DiffKind kind, string content) => $"{kind}:{content.Length}:{content.GetHashCode()}";

    bool AlreadyReported(string path, string signature)
    {
        path = Canonical(path);
        lock (_gate) return _reported.TryGetValue(path, out var last) && last == signature;
    }

    static string Canonical(string path)
    {
        try { return System.IO.Path.GetFullPath(path); }
        catch (Exception) { return path; }
    }

    void MarkReported(string path, string signature)
    {
        path = Canonical(path);
        lock (_gate) _reported[path] = signature;
    }

    static bool LooksBinary(string text) => text.IndexOf('\0') >= 0;

    /// <summary>
    /// Compares the workspace with the turn's checkpoint when a tool call that can change files without
    /// saying which (a shell command, a move, an unclassified call) has finished, and reports what it
    /// created, edited or removed. Returns at once; the scan runs in the background.
    /// </summary>
    public void RequestScanAfterToolCall(string toolCallId)
    {
        ToolKind? kind;
        lock (_gate) _callKinds.TryGetValue(toolCallId, out kind);
        // Reads and searches change nothing; an edit reports its own diff.
        if (kind is ToolKind.Read or ToolKind.Search or ToolKind.Fetch or ToolKind.Think or ToolKind.SwitchMode or ToolKind.Edit)
            return;
        RequestScan();
    }

    /// <summary>Compares the workspace with the turn's checkpoint in the background (also done once the turn ends).</summary>
    public void RequestScan()
    {
        lock (_gate)
        {
            if (_scanning)
            {
                _scanAgain = true; // changes may have landed while the running scan was already walking
                return;
            }
            _scanning = true;
        }
        _ = Task.Run(ScanLoopAsync);
    }

    async Task ScanLoopAsync()
    {
        while (true)
        {
            lock (_gate) _scanAgain = false;
            try
            {
                await ScanOnceAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Workspace scan for file changes failed");
            }

            lock (_gate)
            {
                if (!_scanAgain)
                {
                    _scanning = false;
                    return;
                }
            }
        }
    }

    async Task ScanOnceAsync()
    {
        var changes = await _checkpointService.PreviewChangesSinceLatestCheckpointAsync().ConfigureAwait(false);
        if (changes is null || !changes.HasChanges) return;

        foreach (var path in changes.Recreated) ReportRemoved(path);
        foreach (var path in changes.Deleted) ReportCreated(path);
        foreach (var path in changes.Modified) ReportModified(path);
    }

    // In the checkpoint, gone from disk now: removed since the turn started.
    void ReportRemoved(string path)
    {
        if (AlreadyReported(path, Signature(DiffKind.Deleted, ""))) return;
        FireDeleted(path, foundByScan: true);
    }

    // On disk, not in the checkpoint: created since the turn started.
    void ReportCreated(string path)
    {
        // Only a file the checkpoint proves was absent (not an excluded or oversize one).
        if (!_checkpointService.WasAbsentAtLatestCheckpoint(path)) return;

        string current;
        try { current = System.IO.File.ReadAllText(path); }
        catch { return; }
        // The text of a binary file is meaningless as a diff; the row still lists it and Undo deletes it.
        if (LooksBinary(current)) current = string.Empty;

        var signature = Signature(DiffKind.Added, current);
        if (AlreadyReported(path, signature)) return;
        MarkReported(path, signature);
        var source = ScanSource(path);
        _logger.LogInformation("Firing DiffCompleted for {Path} (kind=Added, found by workspace scan, source={Source})", path, source);
        _diffBroker.FireDiffCompleted(new DiffRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            FilePath = path,
            OriginalContent = string.Empty,
            ProposedContent = current,
            Kind = DiffKind.Added,
            Source = source,
            FoundByScan = true
        });
    }

    // In the checkpoint and on disk, with different content.
    void ReportModified(string path)
    {
        var original = _checkpointService.GetFileAtLatestCheckpoint(path);
        if (original is null) return;

        string current;
        try { current = System.IO.File.ReadAllText(path); }
        catch { return; }

        // Undo would write the text back, which would corrupt a binary file.
        if (LooksBinary(original) || LooksBinary(current)) return;
        if (string.Equals(original, current, StringComparison.Ordinal)) return;

        var signature = Signature(DiffKind.Modified, current);
        if (AlreadyReported(path, signature)) return;
        MarkReported(path, signature);
        var source = ScanSource(path);
        _logger.LogInformation("Firing DiffCompleted for {Path} (kind=Modified, found by workspace scan, source={Source})", path, source);
        _diffBroker.FireDiffCompleted(new DiffRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            FilePath = path,
            OriginalContent = original,
            ProposedContent = current,
            Kind = DiffKind.Modified,
            Source = source,
            FoundByScan = true
        });
    }

    public void FireIfNeeded(ToolCallContent[]? content)
    {
        if (content is null) return;

        foreach (var diff in content.OfType<ToolCallContentDiff>())
        {
            string currentContent;
            try
            {
                currentContent = System.IO.File.ReadAllText(diff.Path);
            }
            catch (Exception ex)
            {
                // Gone from disk: if the turn started with it, the agent deleted it.
                if (!System.IO.File.Exists(diff.Path) && FireDeleted(diff.Path)) continue;

                // A new file: the diff can arrive before the write actually lands (e.g. mid permission
                // prompt), and claude-agent-acp does not resend it once the file exists. Nothing to report
                // yet, but mark it as the agent's now so the scan that eventually finds it (ADR-023) credits
                // the agent instead of leaving it unattributed in "Other changes".
                if (_checkpointService.WasAbsentAtLatestCheckpoint(diff.Path))
                {
                    lock (_gate) _agentChanged.Add(Canonical(diff.Path));
                    _logger.LogInformation("Diff content for {Path} arrived before the write landed; marked as the agent's for the later scan", diff.Path);
                    continue;
                }

                _logger.LogWarning("Could not read {Path} for inline diff: {Msg}", diff.Path, ex.Message);
                continue;
            }

            // The canonical "before" is whatever the shadow git checkpoint recorded at the
            // start of this agent turn - authoritative, agent-agnostic, no fragile OldText
            // parsing. Null means the file was not tracked at checkpoint time (new file, or one
            // the snapshot cannot hold).
            var checkpointContent = _checkpointService.GetFileAtLatestCheckpoint(diff.Path);
            var originalContent = checkpointContent ?? string.Empty;

            // If the file is identical to the checkpoint the agent has not written it yet.
            // This is the normal state during a claude-agent-acp permission prompt (the edit
            // is proposed but not yet applied). Skip the adornment here; it will fire again
            // via the same code path once the user approves and the file is actually written.
            if (string.Equals(originalContent, currentContent, StringComparison.Ordinal))
            {
                _logger.LogDebug("File {Path} unchanged from checkpoint - skipping inline diff (pre-approval or no-op)", diff.Path);
                continue;
            }

            // Only a file the checkpoint proves was absent counts as created (Undo will delete it).
            var kind = checkpointContent is null && _checkpointService.WasAbsentAtLatestCheckpoint(diff.Path)
                ? DiffKind.Added
                : DiffKind.Modified;

            _logger.LogInformation("Firing DiffCompleted for {Path} (kind={Kind}, originalLines={OrigLines}, proposedLines={PropLines})",
                diff.Path,
                kind,
                originalContent.Split('\n').Length,
                currentContent.Split('\n').Length);

            MarkReported(diff.Path, Signature(kind, currentContent));
            _diffBroker.FireDiffCompleted(new DiffRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                FilePath = diff.Path,
                OriginalContent = originalContent,
                ProposedContent = currentContent,
                Kind = kind
            });
        }
    }

    /// <summary>Remembers the files a delete-kind tool call is about (call before <see cref="FireDeletedIfFinished"/>).</summary>
    public void RecordToolCall(string toolCallId, ToolKind? kind, ToolCallLocation[]? locations)
    {
        if (kind is not null)
            lock (_gate) _callKinds[toolCallId] = kind;

        if (kind != ToolKind.Delete || locations is null) return;

        if (!_deleteTargets.TryGetValue(toolCallId, out var paths))
            _deleteTargets[toolCallId] = paths = new List<string>();
        foreach (var location in locations)
        {
            if (!paths.Contains(location.Path, StringComparer.OrdinalIgnoreCase))
                paths.Add(location.Path);
        }
    }

    /// <summary>Called when a tool call completed successfully: reports the files a delete call really removed.</summary>
    public void FireDeletedIfFinished(string toolCallId)
    {
        if (!_deleteTargets.TryGetValue(toolCallId, out var paths)) return;
        _deleteTargets.Remove(toolCallId);

        foreach (var path in paths)
        {
            // A path that still exists was not deleted (a folder, or the call did nothing).
            if (!System.IO.File.Exists(path)) FireDeleted(path);
        }
    }

    /// <summary>Reports a deletion when the latest checkpoint held the file. Returns whether it did.</summary>
    bool FireDeleted(string path, bool foundByScan = false)
    {
        var original = _checkpointService.GetFileAtLatestCheckpoint(path);
        if (original is null) return false;

        // Undo would write the text back, which would corrupt a binary file.
        if (foundByScan && LooksBinary(original)) return false;

        var source = foundByScan ? ScanSource(path) : DiffSource.Agent;
        _logger.LogInformation("Firing DiffCompleted for {Path} (kind=Deleted, originalLines={OrigLines}{Found}, source={Source})",
            path, original.Split('\n').Length, foundByScan ? ", found by workspace scan" : "", source);

        MarkReported(path, Signature(DiffKind.Deleted, ""));
        _diffBroker.FireDiffCompleted(new DiffRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            FilePath = path,
            OriginalContent = original,
            ProposedContent = string.Empty,
            Kind = DiffKind.Deleted,
            Source = source,
            FoundByScan = foundByScan
        });
        return true;
    }
}
