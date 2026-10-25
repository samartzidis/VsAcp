using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using VsAcp.Core.Abstractions;
using VsAcp.Core.Services;

namespace VsAcp.InlineDiff;

/// <summary>
/// The inline diff in an open editor: added lines tinted, removed lines marked, a Keep / Undo pair on each hunk
/// (modified files only) and a file bar pinned to the bottom right of the viewport with the change count and
/// Keep file / Undo file.
/// <para>
/// The hunks follow the editor's text: after an edit settles (<see cref="RecomputeDelay"/>) the buffer's text
/// becomes the session's current content (<see cref="InlineDiffManager.UpdateFromEditor"/>). A hunk's line numbers
/// are only valid for the text it was computed from, so the per-hunk buttons are shown only while the buffer is
/// still at that version.
/// </para>
/// </summary>
internal sealed class InlineDiffAdornment
{
    public const string LayerName = "VsAcp.InlineDiff";
    public const string ToolbarLayerName = "VsAcp.InlineDiff.Toolbar";

    static readonly TimeSpan RecomputeDelay = TimeSpan.FromMilliseconds(250);
    const int MaxTooltipLines = 20;

    static readonly Brush AddedLineBrush = new SolidColorBrush(Color.FromArgb(40, 35, 180, 80));
    static readonly Brush DeletedMarkerBrush = new SolidColorBrush(Color.FromArgb(180, 220, 50, 47));
    static readonly Brush ToolbarBackground = new SolidColorBrush(Color.FromArgb(230, 45, 45, 48));
    static readonly Brush ButtonBackground = new SolidColorBrush(Color.FromArgb(255, 60, 60, 64));
    static readonly Brush ButtonHoverBg = new SolidColorBrush(Color.FromArgb(255, 80, 80, 85));
    static readonly Brush ButtonForeground = Brushes.White;

    readonly IWpfTextView _textView;
    readonly string _filePath;
    readonly InlineDiffManager _manager;
    readonly IAdornmentLayer _highlightLayer;
    readonly IAdornmentLayer _toolbarLayer;
    readonly DispatcherTimer _recomputeTimer;
    readonly StackPanel _fileBar;
    readonly TextBlock _fileBarLabel;

    // The session being drawn, and the buffer version whose text its NewContent is (-1: not known to match).
    InlineDiffSession _session;
    int _syncedVersion = -1;

    // One Keep / Undo pair per hunk of _session, built when first shown.
    readonly Dictionary<LineHunk, FrameworkElement> _hunkBars = new();

    InlineDiffAdornment(IWpfTextView textView, InlineDiffSession session, InlineDiffManager manager)
    {
        _textView = textView;
        _session = session;
        _filePath = session.FilePath;
        _manager = manager;
        _highlightLayer = textView.GetAdornmentLayer(LayerName);
        _toolbarLayer = textView.GetAdornmentLayer(ToolbarLayerName);

        FreezeResources();
        _fileBarLabel = new TextBlock
        {
            Foreground = ButtonForeground,
            FontSize = 12,
            FontFamily = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 6, 0)
        };
        _fileBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Background = ToolbarBackground,
            Children =
            {
                _fileBarLabel,
                CreateButton("Undo file", "Undo all changes to this file", OnUndoFileClicked),
                CreateButton("Keep file", "Keep all changes to this file", OnKeepFileClicked)
            }
        };

        _recomputeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RecomputeDelay };
        _recomputeTimer.Tick += (_, _) => Recompute();

        _textView.LayoutChanged += OnLayoutChanged;
        _textView.Closed += OnViewClosed;
        _textView.TextBuffer.Changed += OnBufferChanged;

        AdoptEditorText();
        Redraw();
    }

    public static void Attach(IWpfTextView textView, InlineDiffSession session, InlineDiffManager manager)
    {
        if (textView.Properties.ContainsProperty(typeof(InlineDiffAdornment)))
            return;

        var adornment = new InlineDiffAdornment(textView, session, manager);
        textView.Properties.AddProperty(typeof(InlineDiffAdornment), adornment);
    }

    public static void DetachIfPresent(IWpfTextView textView)
    {
        if (textView.Properties.TryGetProperty(typeof(InlineDiffAdornment), out InlineDiffAdornment adornment))
            adornment.Detach();
    }

    /// <summary>
    /// Decides on attach whether the editor's text is the file's current content. It is when it equals the
    /// session's text, or when the document has unsaved edits (the user's own, which count). A clean document with
    /// other text has not been reloaded since the agent wrote the file yet: wait for the reload's buffer change
    /// instead of taking the old text (that would make the agent's change look undone).
    /// </summary>
    void AdoptEditorText()
    {
        var snapshot = _textView.TextBuffer.CurrentSnapshot;
        var text = snapshot.GetText();
        if (string.Equals(text, _session.NewContent, StringComparison.Ordinal))
            _syncedVersion = snapshot.Version.VersionNumber;
        else if (TryGetDocument(_textView)?.IsDirty == true)
            _recomputeTimer.Start();
    }

    bool IsSynced =>
        _syncedVersion == _textView.TextBuffer.CurrentSnapshot.Version.VersionNumber
        && ReferenceEquals(_manager.GetSession(_filePath), _session);

    void OnBufferChanged(object sender, TextContentChangedEventArgs e)
    {
        if (!ThreadHelper.CheckAccess())
        {
            UiThread.Post("InlineDiff buffer changed", RestartRecomputeTimer);
            return;
        }
        RestartRecomputeTimer();
    }

    void RestartRecomputeTimer()
    {
        _recomputeTimer.Stop();
        _recomputeTimer.Start();
    }

    /// <summary>Makes the buffer's text the session's current content and redraws; detaches when nothing is left.</summary>
    void Recompute()
    {
        _recomputeTimer.Stop();
        var snapshot = _textView.TextBuffer.CurrentSnapshot;
        var text = snapshot.GetText();
        var session = _manager.UpdateFromEditor(_filePath, text);
        if (session is null)
        {
            Detach();
            return;
        }

        var synced = string.Equals(session.NewContent, text, StringComparison.Ordinal);
        Show(session, synced ? snapshot.Version.VersionNumber : -1);
        // Another report replaced the session while this one was being built: try again once things settle.
        if (!synced) RestartRecomputeTimer();
    }

    void Show(InlineDiffSession session, int syncedVersion)
    {
        if (!ReferenceEquals(session, _session)) _hunkBars.Clear();
        _session = session;
        _syncedVersion = syncedVersion;
        Redraw();
    }

    static Button CreateButton(string text, string description, RoutedEventHandler clickHandler, double fontSize = 12)
    {
        var btn = new Button
        {
            Content = text,
            ToolTip = description,
            Foreground = ButtonForeground,
            Background = ButtonBackground,
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Padding = fontSize < 12 ? new Thickness(6, 0, 6, 0) : new Thickness(10, 3, 10, 3),
            Margin = new Thickness(2),
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = fontSize,
            FontFamily = new FontFamily("Segoe UI")
        };
        AutomationProperties.SetName(btn, description);

        btn.MouseEnter += (_, _) => btn.Background = ButtonHoverBg;
        btn.MouseLeave += (_, _) => btn.Background = ButtonBackground;
        btn.Click += clickHandler;
        return btn;
    }

    FrameworkElement HunkBar(LineHunk hunk)
    {
        if (_hunkBars.TryGetValue(hunk, out var bar)) return bar;

        var undo = CreateButton("Undo", UndoDescription(hunk), (_, _) => OnUndoHunk(hunk), fontSize: 11);
        var keep = CreateButton("Keep", "Keep this change", (_, _) => OnKeepHunk(hunk), fontSize: 11);
        bar = new StackPanel { Orientation = Orientation.Horizontal, Background = ToolbarBackground, Children = { undo, keep } };
        bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _hunkBars[hunk] = bar;
        return bar;
    }

    /// <summary>What Undo on a hunk does, with the lines it brings back (removed lines are only a red marker in the editor).</summary>
    static string UndoDescription(LineHunk hunk)
    {
        if (hunk.OldCount == 0)
            return hunk.NewCount == 1 ? "Undo: remove the added line" : $"Undo: remove the {hunk.NewCount} added lines";

        var lines = hunk.DeletedLines.Take(MaxTooltipLines).ToList();
        var more = hunk.OldCount > lines.Count ? $"\n... and {hunk.OldCount - lines.Count} more" : "";
        var restore = hunk.OldCount == 1 ? "Undo: restore this line" : $"Undo: restore these {hunk.OldCount} lines";
        return $"{restore}\n\n{string.Join("\n", lines)}{more}";
    }

    void OnKeepHunk(LineHunk hunk)
    {
        if (!IsSynced) return;
        var session = _manager.KeepHunk(_session, hunk);
        if (session is null)
        {
            Detach();
            return;
        }
        Show(session, ReferenceEquals(session.NewContent, _session.NewContent) ? _syncedVersion : -1);
    }

    void OnUndoHunk(LineHunk hunk)
    {
        if (!IsSynced) return;
        var edit = LineDiff.UndoEdit(_session.OriginalContent, _session.NewContent, hunk);
        if (ApplyEdit(_textView, edit.Start, edit.Length, edit.Text, EditOptions.None))
            Recompute();
    }

    void OnKeepFileClicked(object sender, RoutedEventArgs e)
    {
        _manager.KeepSession(_filePath);
        Detach();
    }

    void OnUndoFileClicked(object sender, RoutedEventArgs e)
    {
        var session = _session;
        // A modified file is undone as one editor edit, like a hunk; Recompute then finds nothing left and detaches.
        if (session.Kind == DiffKind.Modified && ReplaceText(_textView, session.OriginalContent))
        {
            Recompute();
            return;
        }

        var created = session.Kind == DiffKind.Added;
        _manager.UndoSession(_filePath);
        Detach();
        if (!created)
        {
            ReloadDocument(_textView);
            return;
        }

        // Undo deleted a file the agent created: close its editor (after this click has finished).
        UiThread.Post("InlineDiff close", () =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _manager.CloseDocument(_filePath);
        });
    }

    /// <summary>
    /// Replaces the whole text of <paramref name="textView"/>'s buffer as one undoable edit (Undo file). Like any
    /// Undo from the diff, the document is saved afterwards only if it had no unsaved changes before, so the user's
    /// unsaved work is never written behind their back. Returns false if the buffer refused the edit.
    /// </summary>
    internal static bool ReplaceText(IWpfTextView textView, string text) =>
        ApplyEdit(textView, 0, textView.TextBuffer.CurrentSnapshot.Length, text, EditOptions.DefaultMinimalChange);

    static bool ApplyEdit(IWpfTextView textView, int start, int length, string text, EditOptions options)
    {
        try
        {
            var document = TryGetDocument(textView);
            var wasDirty = document?.IsDirty ?? true;

            using (var edit = textView.TextBuffer.CreateEdit(options, null, null))
            {
                edit.Replace(start, length, text);
                edit.Apply();
                if (edit.Canceled) return false;
            }

            if (!wasDirty) document!.Save();
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning("[InlineDiffAdornment] Undo edit failed: {Error}", ex.Message);
            return false;
        }
    }

    static ITextDocument? TryGetDocument(IWpfTextView textView)
    {
        if (textView.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc))
            return doc;
        if (textView.TextDataModel?.DocumentBuffer?.Properties.TryGetProperty(typeof(ITextDocument), out doc) == true)
            return doc;
        return null;
    }

    /// <summary>Reloads the document behind a view after the file on disk was rewritten (Undo).</summary>
    internal static void ReloadDocument(IWpfTextView textView)
    {
        try
        {
            TryGetDocument(textView)?.Reload(EditOptions.DefaultMinimalChange);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning("[InlineDiffAdornment] Reload failed: {Error}", ex.Message);
        }
    }

    void Detach()
    {
        _recomputeTimer.Stop();
        _textView.LayoutChanged -= OnLayoutChanged;
        _textView.Closed -= OnViewClosed;
        _textView.TextBuffer.Changed -= OnBufferChanged;
        _highlightLayer.RemoveAllAdornments();
        _toolbarLayer.RemoveAllAdornments();
        _textView.Properties.RemoveProperty(typeof(InlineDiffAdornment));
    }

    void OnViewClosed(object sender, EventArgs e)
    {
        Detach();
    }

    void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
    {
        Redraw();
    }

    void Redraw()
    {
        _highlightLayer.RemoveAllAdornments();
        _toolbarLayer.RemoveAllAdornments();

        var snapshot = _textView.TextSnapshot;
        var perHunk = _session.Kind == DiffKind.Modified && IsSynced;

        foreach (var hunk in _session.Hunks)
        {
            int anchorLine;
            if (hunk.NewCount > 0)
            {
                anchorLine = hunk.NewStart;
                if (anchorLine >= snapshot.LineCount) continue;
                int endLine = Math.Min(hunk.NewStart + hunk.NewCount - 1, snapshot.LineCount - 1);
                for (int lineNum = anchorLine; lineNum <= endLine; lineNum++)
                    DrawLineHighlight(snapshot, lineNum, AddedLineBrush);
            }
            else
            {
                anchorLine = Math.Min(hunk.NewStart, snapshot.LineCount - 1);
                DrawDeletionMarker(snapshot, anchorLine);
            }

            if (perHunk) PlaceAtLineEnd(HunkBar(hunk), snapshot, anchorLine);
        }

        PlaceFileBar();
    }

    void DrawLineHighlight(ITextSnapshot snapshot, int lineNum, Brush brush)
    {
        if (lineNum >= snapshot.LineCount) return;

        var snapshotLine = snapshot.GetLineFromLineNumber(lineNum);
        var span = new SnapshotSpan(snapshotLine.Start, snapshotLine.EndIncludingLineBreak);
        var viewLine = _textView.TextViewLines.GetTextViewLineContainingBufferPosition(snapshotLine.Start);
        if (viewLine is null) return;

        var rect = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(_textView.ViewportWidth, _textView.MaxTextRightCoordinate),
            Height = viewLine.Height,
            Fill = brush
        };

        Canvas.SetLeft(rect, _textView.ViewportLeft);
        Canvas.SetTop(rect, viewLine.Top);

        _highlightLayer.AddAdornment(
            AdornmentPositioningBehavior.TextRelative,
            span, null, rect, null);
    }

    void DrawDeletionMarker(ITextSnapshot snapshot, int lineNum)
    {
        if (lineNum >= snapshot.LineCount) return;

        var snapshotLine = snapshot.GetLineFromLineNumber(lineNum);
        var span = new SnapshotSpan(snapshotLine.Start, snapshotLine.EndIncludingLineBreak);
        var viewLine = _textView.TextViewLines.GetTextViewLineContainingBufferPosition(snapshotLine.Start);
        if (viewLine is null) return;

        var marker = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(_textView.ViewportWidth, _textView.MaxTextRightCoordinate),
            Height = 2,
            Fill = DeletedMarkerBrush
        };

        Canvas.SetLeft(marker, _textView.ViewportLeft);
        Canvas.SetTop(marker, viewLine.Top - 1);

        _highlightLayer.AddAdornment(
            AdornmentPositioningBehavior.TextRelative,
            span, null, marker, null);
    }

    /// <summary>Puts a hunk's Keep / Undo pair at the right edge of the viewport, on the hunk's first line.</summary>
    void PlaceAtLineEnd(FrameworkElement bar, ITextSnapshot snapshot, int lineNum)
    {
        if (lineNum < 0 || lineNum >= snapshot.LineCount) return;

        var line = snapshot.GetLineFromLineNumber(lineNum);
        var viewLine = _textView.TextViewLines.GetTextViewLineContainingBufferPosition(line.Start);
        if (viewLine is null) return;

        double width = bar.DesiredSize.Width > 0 ? bar.DesiredSize.Width : 100;
        Canvas.SetTop(bar, viewLine.Top);
        Canvas.SetLeft(bar, _textView.ViewportRight - width - 16);

        _toolbarLayer.AddAdornment(
            AdornmentPositioningBehavior.TextRelative,
            new SnapshotSpan(line.Start, line.EndIncludingLineBreak), null, bar, null);
    }

    /// <summary>Pins the file bar to the bottom right of the viewport, so it never scrolls out of view.</summary>
    void PlaceFileBar()
    {
        var count = _session.Hunks.Count;
        _fileBarLabel.Text = _session.Kind == DiffKind.Added ? "New file"
            : count == 1 ? "1 change" : $"{count} changes";
        _fileBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        Canvas.SetLeft(_fileBar, _textView.ViewportRight - _fileBar.DesiredSize.Width - 16);
        Canvas.SetTop(_fileBar, _textView.ViewportBottom - _fileBar.DesiredSize.Height - 12);

        _toolbarLayer.AddAdornment(AdornmentPositioningBehavior.ViewportRelative, null, null, _fileBar, null);
    }

    static void FreezeResources()
    {
        if (AddedLineBrush.IsFrozen) return;
        AddedLineBrush.Freeze();
        DeletedMarkerBrush.Freeze();
        ToolbarBackground.Freeze();
        ButtonBackground.Freeze();
        ButtonHoverBg.Freeze();
    }
}
