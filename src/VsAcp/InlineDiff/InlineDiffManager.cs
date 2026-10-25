using System.Collections.Concurrent;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.TextManager.Interop;
using VsAcp.Core.Abstractions;
using VsAcp.Core.Services;

namespace VsAcp.InlineDiff;

/// <summary>One row of the pending-changes list.</summary>
internal sealed record PendingFile(string FilePath, DiffKind Kind, int Added, int Removed, DiffSource Source);

internal sealed class InlineDiffManager : IDisposable
{
    static readonly ConcurrentDictionary<string, InlineDiffManager> _fileRegistry =
        new(StringComparer.OrdinalIgnoreCase);

    internal static InlineDiffManager? GetManagerForFile(string filePath) =>
        _fileRegistry.TryGetValue(filePath, out var mgr) ? mgr : null;

    readonly IDiffBroker _diffBroker;
    readonly AsyncPackage _package;
    readonly ConcurrentDictionary<string, InlineDiffSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when the set of pending files, or the line counts of one of them, changes.</summary>
    internal event Action? PendingChanged;

    internal int PendingCount => _sessions.Count;

    /// <summary>The files with a pending change (modified, created or deleted), ordered by path.</summary>
    internal IReadOnlyList<PendingFile> PendingFiles() =>
        _sessions.Values
            .OrderBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(s => new PendingFile(s.FilePath, s.Kind, s.AddedLines, s.RemovedLines, s.Source))
            .ToList();

    /// <summary>Keeps one file's changes (the list's per-file Keep): drops the session and the editor adornment.</summary>
    internal void KeepFile(string filePath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!_sessions.ContainsKey(filePath)) return;
        KeepSession(filePath);
        DetachAdornment(filePath);
    }

    /// <summary>
    /// Undoes one file's change (the list's per-file Undo): a created file is deleted (and its editor closed), a
    /// deleted file is recreated, a modified one gets its original back. A modified file that is open is undone as
    /// one editor edit (Ctrl+Z brings the change back; see <see cref="InlineDiffAdornment.ReplaceText"/> for saving),
    /// otherwise the file on disk is rewritten and an open document reloaded.
    /// </summary>
    internal void UndoFile(string filePath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!_sessions.TryGetValue(filePath, out var session)) return;
        var view = TryGetOpenView(filePath);
        if (view is not null && session.Kind == DiffKind.Modified
            && InlineDiffAdornment.ReplaceText(view, session.OriginalContent))
        {
            Log($"Undo in the editor: {filePath}");
            InlineDiffAdornment.DetachIfPresent(view);
            RemoveSession(session);
            return;
        }
        UndoSession(filePath);
        if (view is not null) InlineDiffAdornment.DetachIfPresent(view);
        if (session.Kind == DiffKind.Added)
            CloseDocument(filePath);
        else if (view is not null)
            InlineDiffAdornment.ReloadDocument(view);
    }

    /// <summary>Opens a pending file in the editor with its diff adornment (a click on a row of the list). A deleted file has nothing to open.</summary>
    internal void OpenFile(string filePath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!_sessions.TryGetValue(filePath, out var session) || session.Kind == DiffKind.Deleted) return;
        NavigateToFile(filePath);
    }

    /// <summary>Closes the editor of a file without saving (after Undo deleted a file the agent created).</summary>
    internal void CloseDocument(string filePath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (VsShellUtilities.IsDocumentOpen(_package, filePath, Guid.Empty, out _, out _, out IVsWindowFrame? frame))
                frame?.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
        }
        catch (Exception ex)
        {
            Log($"CloseDocument failed for {filePath}: {ex.Message}");
        }
    }

    public InlineDiffManager(IDiffBroker diffBroker, AsyncPackage package)
    {
        _diffBroker = diffBroker;
        _package = package;
        _diffBroker.DiffCompleted += OnDiffCompleted;
    }

    public InlineDiffSession? GetSession(string filePath)
    {
        _sessions.TryGetValue(filePath, out var session);
        return session;
    }

    public void KeepSession(string filePath)
    {
        Log($"Keep: {filePath}");
        _sessions.TryRemove(filePath, out _);
        UnregisterFile(filePath);
        RaisePendingCountChanged();
    }

    /// <summary>Drops <paramref name="session"/> unless it was replaced in the meantime. Returns whether it did.</summary>
    bool RemoveSession(InlineDiffSession session)
    {
        var entry = new KeyValuePair<string, InlineDiffSession>(session.FilePath, session);
        if (!((ICollection<KeyValuePair<string, InlineDiffSession>>)_sessions).Remove(entry)) return false;
        UnregisterFile(session.FilePath);
        RaisePendingCountChanged();
        return true;
    }

    /// <summary>
    /// Takes the text of the file's open editor as its current content, so the hunks follow what the user sees and
    /// types (the user's own edits count as changes against the start of the turn). Returns the session to draw, or
    /// null when nothing is pending any more (the session is dropped).
    /// </summary>
    internal InlineDiffSession? UpdateFromEditor(string filePath, string text)
    {
        if (!_sessions.TryGetValue(filePath, out var session)) return null;
        if (string.Equals(session.NewContent, text, StringComparison.Ordinal)) return session;

        var updated = new InlineDiffSession(filePath, session.OriginalContent, text, session.Kind, session.Source);
        if (updated.Kind == DiffKind.Modified && updated.Hunks.Count == 0)
        {
            Log($"Editor text is back to the original: {filePath}");
            RemoveSession(session);
            return null;
        }
        if (!_sessions.TryUpdate(filePath, updated, session)) return GetSession(filePath);
        RaisePendingCountChanged();
        return updated;
    }

    /// <summary>
    /// Keeps one hunk of <paramref name="session"/>: it becomes part of the original, so it is no longer a change and
    /// Undo leaves it alone. Returns the session to draw, or null when that was the last hunk (the session is dropped).
    /// Does nothing to a session that was replaced in the meantime (returns the current one).
    /// </summary>
    internal InlineDiffSession? KeepHunk(InlineDiffSession session, LineHunk hunk)
    {
        var filePath = session.FilePath;
        var original = LineDiff.KeepHunk(session.OriginalContent, session.NewContent, hunk);
        var updated = new InlineDiffSession(filePath, original, session.NewContent, session.Kind, session.Source);
        if (updated.Kind == DiffKind.Modified && updated.Hunks.Count == 0)
        {
            Log($"Keep (last hunk): {filePath}");
            return RemoveSession(session) ? null : GetSession(filePath);
        }
        if (!_sessions.TryUpdate(filePath, updated, session)) return GetSession(filePath);
        Log($"Keep hunk at line {hunk.NewStart + 1}: {filePath}");
        RaisePendingCountChanged();
        return updated;
    }

    public void UndoSession(string filePath)
    {
        if (_sessions.TryRemove(filePath, out var session))
        {
            Log($"Undo: reverting {filePath}");
            UnregisterFile(filePath);
            try
            {
                DiffKindRules.RestoreOriginal(session.Kind, filePath, session.OriginalContent);
            }
            catch (Exception ex)
            {
                Log($"Undo failed: {ex.Message}");
            }
            RaisePendingCountChanged();
        }
    }

    /// <summary>Paths of the pending files from one source, or all of them when <paramref name="source"/> is null.</summary>
    List<string> PathsFrom(DiffSource? source) =>
        _sessions.Values.Where(s => source is null || s.Source == source).Select(s => s.FilePath).ToList();

    /// <summary>Keeps every pending change of one group of the list (null = both groups).</summary>
    internal void AcceptAll(DiffSource? source = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var paths = PathsFrom(source);
        Log($"AcceptAll ({source?.ToString() ?? "all"}): {paths.Count} sessions");
        foreach (var path in paths)
        {
            _sessions.TryRemove(path, out _);
            UnregisterFile(path);
            DetachAdornment(path);
        }
        RaisePendingCountChanged();
    }

    /// <summary>Undoes every pending change of one group of the list (null = both groups).</summary>
    internal void RejectAll(DiffSource? source = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var paths = PathsFrom(source);
        Log($"RejectAll ({source?.ToString() ?? "all"}): {paths.Count} sessions");
        foreach (var path in paths)
        {
            var created = false;
            if (_sessions.TryRemove(path, out var session))
            {
                UnregisterFile(path);
                created = session.Kind == DiffKind.Added;
                try
                {
                    DiffKindRules.RestoreOriginal(session.Kind, path, session.OriginalContent);
                }
                catch (Exception ex)
                {
                    Log($"RejectAll revert failed for {path}: {ex.Message}");
                }
            }
            DetachAdornment(path);
            if (created) CloseDocument(path);
        }
        RaisePendingCountChanged();
    }

    void NavigateToFile(string filePath)
    {
        UiThread.Post("InlineDiff navigate", () =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { OpenFileAndAttachAdornment(filePath); }
            catch (Exception ex) { Log($"NavigateToFile failed: {ex.Message}"); }
        });
    }

    void RaisePendingCountChanged()
    {
        PendingChanged?.Invoke();
    }

    void OnDiffCompleted(DiffRequest request)
    {
        Log($"DiffCompleted: {request.FilePath}");

        // Preserve the true original across multiple LLM edits to the same file.
        var originalContent = _sessions.TryGetValue(request.FilePath, out var existing)
            ? existing.OriginalContent
            : request.OriginalContent;

        // A file that was created or deleted stays that way across later edits; a file created and then
        // deleted again in the same turn is no change at all (kind == null).
        var kind = DiffKindRules.Merge(existing?.Kind, request.Kind);
        var source = DiffKindRules.MergeSource(existing?.Source, request.Source);
        var session = kind is { } k ? new InlineDiffSession(request.FilePath, originalContent, request.ProposedContent, k, source) : null;

        // A modification without hunks changes nothing. A created or deleted file counts even when it is empty.
        if (session is null || (session.Kind == DiffKind.Modified && session.Hunks.Count == 0))
        {
            Log("No change left - skipping inline diff");
            if (existing is not null)
            {
                // LLM reverted to original — remove phantom session and adornment.
                _sessions.TryRemove(request.FilePath, out _);
                UnregisterFile(request.FilePath);
                RaisePendingCountChanged();
                UiThread.Post("InlineDiff detach", () =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    DetachAdornment(request.FilePath);
                });
            }
            return;
        }

        // A scan reporting what is already listed changes nothing (unless it now knows the agent made the change).
        if (request.FoundByScan && existing is not null && existing.Kind == session.Kind && existing.Source == session.Source
            && string.Equals(existing.NewContent, session.NewContent, StringComparison.Ordinal))
            return;

        bool isUpdate = existing is not null;
        _sessions[request.FilePath] = session;
        // A deleted file has no editor to decorate, and must not decorate one the user recreates later.
        if (session.Kind == DiffKind.Deleted) UnregisterFile(request.FilePath);
        else _fileRegistry[request.FilePath] = this;
        RaisePendingCountChanged();

        // Never opens the file: opening would take focus from wherever the user is typing and pile up a tab per
        // edited file. The pending list shows the change; the diff appears in an editor that is open or opened later
        // (InlineDiffTextViewListener).
        UiThread.Post("InlineDiff update", () =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (isUpdate)
                    DetachAdornment(request.FilePath);
                if (session.Kind == DiffKind.Deleted) return;
                AttachAdornmentIfOpen(request.FilePath);
            }
            catch (Exception ex)
            {
                Log($"Failed to decorate the open editor: {ex.Message}");
            }
        });
    }

    /// <summary>Decorates the editor of a pending file if it is open already; never opens it.</summary>
    void AttachAdornmentIfOpen(string filePath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var session = GetSession(filePath);
        if (session is null || session.Kind == DiffKind.Deleted) return;
        var view = TryGetOpenView(filePath);
        if (view is not null) InlineDiffAdornment.Attach(view, session, this);
    }

    /// <summary>
    /// Looks at every pending file on disk again and fixes the list: a file that is back to how it started
    /// drops out, a created file that is gone drops out, a modified one that is gone becomes deleted, and so on.
    /// Call after something rewrote files behind the list's back (a revert to an earlier chat message).
    /// </summary>
    internal void ResyncWithDisk()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var changed = false;
        foreach (var path in _sessions.Keys.ToList())
        {
            if (!_sessions.TryGetValue(path, out var session)) continue;

            string? current = null;
            try
            {
                if (System.IO.File.Exists(path)) current = System.IO.File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                Log($"Resync: could not read {path}: {ex.Message}");
                continue;
            }

            var kind = DiffKindRules.Classify(session.Kind != DiffKind.Added, session.OriginalContent, current);
            if (kind is null)
            {
                Log($"Resync: nothing pending for {path} any more");
                _sessions.TryRemove(path, out _);
                UnregisterFile(path);
                DetachAdornment(path);
                changed = true;
                continue;
            }

            if (kind == session.Kind && string.Equals(current ?? "", session.NewContent, StringComparison.Ordinal))
                continue;

            Log($"Resync: {path} is now {kind}");
            var updated = new InlineDiffSession(path, session.OriginalContent, current ?? "", kind.Value, session.Source);
            _sessions[path] = updated;
            if (kind == DiffKind.Deleted) UnregisterFile(path);
            else _fileRegistry[path] = this;
            DetachAdornment(path);
            AttachAdornmentIfOpen(path);
            changed = true;
        }
        if (changed) RaisePendingCountChanged();
    }

    void OpenFileAndAttachAdornment(string filePath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        VsShellUtilities.OpenDocument(_package, filePath, Guid.Empty,
            out _, out _, out IVsWindowFrame? frame);
        frame?.Show();

        var wpfTextView = GetWpfTextView(frame);
        if (wpfTextView is null)
        {
            Log("Could not get IWpfTextView from frame");
            return;
        }

        var session = GetSession(filePath);
        if (session is null)
        {
            Log("Session was removed before adornment could attach");
            return;
        }

        Log($"Attaching inline diff adornment to {filePath} ({session.Hunks.Count} hunks)");
        InlineDiffAdornment.Attach(wpfTextView, session, this);
    }

    static IWpfTextView? GetWpfTextView(IVsWindowFrame? frame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (frame is null) return null;

        var vsTextView = VsShellUtilities.GetTextView(frame);
        if (vsTextView is null) return null;

        var editorAdaptersFactory = Package.GetGlobalService(typeof(SComponentModel))
            as IComponentModel;
        if (editorAdaptersFactory is null) return null;

        var adaptersFactory = editorAdaptersFactory.GetService<IVsEditorAdaptersFactoryService>();
        return adaptersFactory?.GetWpfTextView(vsTextView);
    }

    /// <summary>The editor view of a file that is already open, without opening it.</summary>
    IWpfTextView? TryGetOpenView(string filePath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return VsShellUtilities.IsDocumentOpen(_package, filePath, Guid.Empty, out _, out _, out IVsWindowFrame? frame)
            ? GetWpfTextView(frame)
            : null;
    }

    void DetachAdornment(string filePath)
    {
        try
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var wpfTextView = TryGetOpenView(filePath);
            if (wpfTextView is not null)
                InlineDiffAdornment.DetachIfPresent(wpfTextView);
        }
        catch (Exception ex)
        {
            Log($"DetachAdornment failed for {filePath}: {ex.Message}");
        }
    }

    void UnregisterFile(string filePath)
    {
        if (_fileRegistry.TryGetValue(filePath, out var owner) && owner == this)
            _fileRegistry.TryRemove(filePath, out _);
    }

    public void Dispose()
    {
        _diffBroker.DiffCompleted -= OnDiffCompleted;
        foreach (var path in _sessions.Keys.ToList())
            UnregisterFile(path);
        _sessions.Clear();
    }

    static void Log(string message)
    {
        Serilog.Log.Information("[InlineDiffManager] {Message}", message);
    }
}
