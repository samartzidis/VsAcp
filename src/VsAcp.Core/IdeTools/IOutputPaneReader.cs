namespace VsAcp.Core.IdeTools;

/// <summary>A pane of Visual Studio's Output window.</summary>
/// <param name="Id">The pane's GUID, as Visual Studio formats it.</param>
/// <param name="LineCount">Null while the pane has no text buffer (Visual Studio creates one when the pane is first shown).</param>
public sealed record OutputPane(string Name, string Id, int? LineCount);

/// <summary>Consecutive lines of a pane.</summary>
/// <param name="FirstLine">The 1-based number of <c>Lines[0]</c>.</param>
/// <param name="TotalLines">How many lines the pane holds.</param>
public sealed record OutputPaneLines(int FirstLine, IReadOnlyList<string> Lines, int TotalLines);

/// <summary>Reads the Output window. The host implements it over Visual Studio; it must never activate a pane.</summary>
public interface IOutputPaneReader
{
    Task<IReadOnlyList<OutputPane>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Up to <paramref name="maxLines"/> lines from 1-based <paramref name="fromLine"/>, or the last
    /// <paramref name="maxLines"/> when <paramref name="fromLine"/> is null. Null when no pane has that id or the pane
    /// has no text buffer yet.
    /// </summary>
    Task<OutputPaneLines?> ReadAsync(string paneId, int? fromLine, int maxLines, CancellationToken cancellationToken);
}
