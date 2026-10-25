using System.ComponentModel.Composition;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using VsAcp.Core.Abstractions;
using VsAcp.InlineDiff;

namespace VsAcp.ToolWindows;

/// <summary>
/// Recovers where pasted text came from so that pasting a multi-line editor selection into the chat
/// input can become a file + line-range reference (like Cursor) instead of raw text. The clipboard
/// carries no source information, so the source is found by matching the pasted text against (1) the
/// most recent non-empty selection reported by any editor view, then (2) the live selection of the
/// active document's editor. A stale or unrelated selection fails the match and the paste falls back
/// to plain text.
/// </summary>
[Export(typeof(IWpfTextViewCreationListener))]
[ContentType("any")] // not "text": editors such as Markdown may use a content type that does not derive from it
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class EditorSelectionTracker : IWpfTextViewCreationListener
{
    private sealed record Tracked(IWpfTextView View, SnapshotSpan Span);

    // Only touched on the UI thread (selection events and paste handling both run there).
    private static Tracked? _latest;

    public void TextViewCreated(IWpfTextView textView)
    {
        Serilog.Log.Debug("[CodeRef] Tracking editor view (contentType={ContentType})", textView.TextBuffer.ContentType.TypeName);

        textView.Selection.SelectionChanged += OnSelectionChanged;
        textView.Closed += OnClosed;

        void OnSelectionChanged(object? sender, EventArgs e)
        {
            var tracked = Capture(textView);
            if (tracked is not null) _latest = tracked;
        }

        void OnClosed(object? sender, EventArgs e)
        {
            textView.Selection.SelectionChanged -= OnSelectionChanged;
            textView.Closed -= OnClosed;
            if (_latest?.View == textView) _latest = null;
        }
    }

    /// <summary>
    /// Returns the editor reference for <paramref name="clipboardText"/> when it is a multi-line copy of
    /// an editor selection from a file on disk; otherwise null (paste it as plain text).
    /// Single-line selections are deliberately not referenced.
    /// </summary>
    internal static CodeSelection? TryResolve(string clipboardText)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // Only multi-line pastes are interesting; log why one was not turned into a reference.
        var multiLine = clipboardText.TrimEnd('\r', '\n').Contains('\n');

        var tracked = _latest;
        var live = Capture(GetActiveDocumentView());
        if (tracked is null && live is null)
        {
            if (multiLine) Serilog.Log.Information("[CodeRef] Paste not referenced: no editor selection (none tracked, none live)");
            return null;
        }

        var wanted = Normalize(clipboardText);
        foreach (var candidate in new[] { tracked, live })
        {
            if (candidate is null) continue;
            var text = candidate.Span.GetText();
            if (Normalize(text) != wanted) continue;
            return ToCodeSelection(candidate, text);
        }

        if (multiLine)
            Serilog.Log.Information("[CodeRef] Paste not referenced: clipboard ({ClipLen} chars) matches neither tracked ({TrackedLen}) nor live ({LiveLen}) selection",
                clipboardText.Length, tracked?.Span.Length, live?.Span.Length);
        return null;
    }

    private static CodeSelection? ToCodeSelection(Tracked tracked, string text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var span = tracked.Span;
        var snapshot = span.Snapshot;
        var startLine = snapshot.GetLineNumberFromPosition(span.Start);
        var endLine = snapshot.GetLineNumberFromPosition(span.End);
        // A selection ending at column 0 of a line does not include that line.
        if (endLine > startLine && snapshot.GetLineFromLineNumber(endLine).Start.Position == span.End.Position)
            endLine--;
        if (endLine <= startLine) return null;

        var filePath = InlineDiffTextViewListener.GetFilePath(tracked.View);
        if (string.IsNullOrEmpty(filePath))
        {
            Serilog.Log.Information("[CodeRef] Paste not referenced: selection matched but the editor has no file path");
            return null;
        }

        Serilog.Log.Information("[CodeRef] Paste referenced as {File} lines {Start}-{End}", filePath, startLine + 1, endLine + 1);

        return new CodeSelection
        {
            FilePath = filePath!,
            StartLine = startLine + 1,
            EndLine = endLine + 1,
            Text = text
        };
    }

    /// <summary>The view's current selection, or null if it is empty or not a stream selection (box selections copy in a different shape).</summary>
    private static Tracked? Capture(IWpfTextView? view)
    {
        if (view is null) return null;
        var selection = view.Selection;
        if (selection.IsEmpty || selection.Mode != TextSelectionMode.Stream) return null;
        return new Tracked(view, selection.StreamSelectionSpan.SnapshotSpan);
    }

    /// <summary>
    /// The editor view of the last active document frame (still set while the chat tool window has
    /// focus), found through the shell rather than MEF so it works for any editor type.
    /// </summary>
    private static IWpfTextView? GetActiveDocumentView()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (Package.GetGlobalService(typeof(SVsShellMonitorSelection)) is not IVsMonitorSelection monSel) return null;
            monSel.GetCurrentElementValue(2u /* SEID_DocumentFrame */, out var frameObj);
            if (frameObj is not IVsWindowFrame frame) return null;

            var vsTextView = VsShellUtilities.GetTextView(frame);
            if (vsTextView is null) return null;

            var componentModel = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
            return componentModel?.GetService<IVsEditorAdaptersFactoryService>()?.GetWpfTextView(vsTextView);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "[CodeRef] Could not get the active document's editor view");
            return null;
        }
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n");
}
