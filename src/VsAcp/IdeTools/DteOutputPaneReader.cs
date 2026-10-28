using System.Runtime.InteropServices;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using VsAcp.Core.IdeTools;

namespace VsAcp.IdeTools;

/// <summary>
/// Reads Visual Studio's Output window through DTE for the IDE tools (ADR-061), on the UI thread. Never activates a
/// pane (that would take focus from wherever the user is, see ADR-057): a pane Visual Studio has not shown yet has no
/// text buffer, and is reported as such. Reads only the requested lines, so a large Build pane costs little.
/// </summary>
internal sealed class DteOutputPaneReader : IOutputPaneReader
{
    readonly JoinableTaskFactory _jtf;

    public DteOutputPaneReader(JoinableTaskFactory jtf) => _jtf = jtf;

    public async Task<IReadOnlyList<OutputPane>> ListAsync(CancellationToken cancellationToken)
    {
        await _jtf.SwitchToMainThreadAsync(cancellationToken);

        var result = new List<OutputPane>();
        if (Panes() is not { } panes)
            return result;

        for (var i = 1; i <= panes.Count; i++)
        {
            var pane = panes.Item(i);
            result.Add(new OutputPane(NameOf(pane), IdOf(pane), TextOf(pane) is { } text ? LineCount(text) : null));
        }
        return result;
    }

    public async Task<OutputPaneLines?> ReadAsync(string paneId, int? fromLine, int maxLines, CancellationToken cancellationToken)
    {
        await _jtf.SwitchToMainThreadAsync(cancellationToken);

        if (Find(paneId) is not { } pane || TextOf(pane) is not { } text)
            return null;

        var total = LineCount(text);
        var first = fromLine ?? Math.Max(1, total - maxLines + 1);
        if (total == 0 || first > total)
            return new OutputPaneLines(first, Array.Empty<string>(), total);

        var count = (int)Math.Min(maxLines, (long)total - first + 1);
        var start = text.StartPoint.CreateEditPoint();
        start.MoveToLineAndOffset(first, 1);
        var end = text.StartPoint.CreateEditPoint();
        if (first + count <= text.EndPoint.Line)
            end.MoveToLineAndOffset(first + count, 1);
        else
            end.EndOfDocument();

        var lines = start.GetText(end).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).Take(count).ToArray();
        return new OutputPaneLines(first, lines, total);
    }

    static OutputWindowPanes? Panes()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return (Package.GetGlobalService(typeof(DTE)) as DTE2)?.ToolWindows.OutputWindow.OutputWindowPanes;
        }
        catch (COMException)
        {
            return null;
        }
    }

    static OutputWindowPane? Find(string paneId)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Panes() is not { } panes)
            return null;

        for (var i = 1; i <= panes.Count; i++)
        {
            var pane = panes.Item(i);
            if (string.Equals(IdOf(pane), paneId, StringComparison.OrdinalIgnoreCase))
                return pane;
        }
        return null;
    }

    // Null while the pane has no text buffer (never shown in this Visual Studio session).
    static TextDocument? TextOf(OutputWindowPane pane)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return pane.TextDocument;
        }
        catch (COMException)
        {
            return null;
        }
    }

    // Output panes end their text with a newline, which leaves an empty last line; it is not counted.
    static int LineCount(TextDocument text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var end = text.EndPoint;
        return end.LineCharOffset == 1 ? end.Line - 1 : end.Line;
    }

    static string NameOf(OutputWindowPane pane)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { return pane.Name ?? ""; }
        catch (COMException) { return ""; }
    }

    static string IdOf(OutputWindowPane pane)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { return pane.Guid ?? ""; }
        catch (COMException) { return ""; }
    }
}
