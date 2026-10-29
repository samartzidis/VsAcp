using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace VsAcp.Core.IdeTools;

/// <summary>
/// The Output window tools one chat's agent gets (ADR-061): list the panes, read one. Read-only, never activates a
/// pane. Panes in <c>hiddenPaneIds</c> (VsAcp's own log, which holds every tab's ACP traffic) are neither listed nor
/// read. Errors the agent can act on are thrown as <see cref="McpException"/>, whose message reaches the model.
/// </summary>
public sealed class OutputPaneTools
{
    public const int DefaultTailLines = 200;
    public const int MaxLines = 2000;
    public const int MaxChars = 64_000;
    /// <summary>Visual Studio's Build pane (<c>VSConstants.OutputWindowPaneGuid.BuildOutputPane_guid</c>).</summary>
    public const string BuildPaneId = "{1BD8A850-02D1-11D1-BEE7-00A0C913D1F8}";

    readonly IOutputPaneReader _reader;
    readonly HashSet<string> _hiddenPaneIds;
    readonly string _chatId;
    readonly ILogger _logger;
    readonly IIdeStateReader? _state;

    /// <param name="state">Tells a Build pane read whether a build is still running; without it, reads don't say.</param>
    public OutputPaneTools(IOutputPaneReader reader, IEnumerable<string> hiddenPaneIds, string chatId, ILogger logger,
        IIdeStateReader? state = null)
    {
        _reader = reader;
        _hiddenPaneIds = new HashSet<string>(hiddenPaneIds.Select(NormalizeId), StringComparer.OrdinalIgnoreCase);
        _chatId = chatId;
        _logger = logger;
        _state = state;
    }

    /// <summary>The tools as the MCP SDK serves them.</summary>
    public IReadOnlyList<McpServerTool> CreateTools() =>
    [
        McpServerTool.Create(ListOutputPanesAsync, new McpServerToolCreateOptions
        {
            Name = "list_output_panes",
            Title = "List Output window panes",
            Description = "Lists the panes of Visual Studio's Output window with their line counts. The panes show what " +
                "Visual Studio itself ran: builds started in Visual Studio (Build), the debugger (Debug), test runs " +
                "(Tests), package restores, source control and extensions. Read one with read_output_pane.",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false
        }),
        McpServerTool.Create(ReadOutputPaneAsync, new McpServerToolCreateOptions
        {
            Name = "read_output_pane",
            Title = "Read an Output window pane",
            Description = "Reads text from a pane of Visual Studio's Output window, by default its last " +
                $"{DefaultTailLines} lines. Use it for output the user saw in Visual Studio (a build, debug or test run " +
                "they started there), not for builds you run yourself. Line numbers are 1-based; at most " +
                $"{MaxLines} lines and {MaxChars} characters per call. While a build is running in Visual Studio, a " +
                "read of the Build pane says so: its output is incomplete until the build ends.",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false
        })
    ];

    public Task<string> ListOutputPanesAsync(CancellationToken cancellationToken) =>
        ToolGuard.RunAsync(_logger, _chatId, "list_output_panes", "list the Output window panes", () => ListAsync(cancellationToken));

    async Task<string> ListAsync(CancellationToken cancellationToken)
    {
        var panes = await VisiblePanesAsync(cancellationToken);
        _logger.LogInformation("[IdeTools] {Chat} list_output_panes: {Count} pane(s)", _chatId, panes.Count);

        if (panes.Count == 0)
            return "The Output window has no panes yet.";

        var text = new StringBuilder("Output window panes:");
        foreach (var pane in panes)
        {
            var size = pane.LineCount is { } lines ? $"{lines} line{(lines == 1 ? "" : "s")}" : "no text yet (never shown)";
            text.Append($"\n- {pane.Name} ({pane.Id}): {size}");
        }
        return text.ToString();
    }

    public Task<string> ReadOutputPaneAsync(
        [Description("The pane's name as list_output_panes shows it (e.g. \"Build\"), or its id.")] string pane,
        [Description("Read the last this many lines (default 200, at most 2000). Not with fromLine. With contains: how many of the last matching lines to return.")] int? tailLines = null,
        [Description("Read from this 1-based line instead of the end. With contains: search from here.")] int? fromLine = null,
        [Description("With fromLine: how many lines to read (default 200, at most 2000). With contains: how many lines to search.")] int? lineCount = null,
        [Description("Only lines containing this text (case-insensitive), each prefixed with its line number.")] string? contains = null,
        CancellationToken cancellationToken = default) =>
        ToolGuard.RunAsync(_logger, _chatId, "read_output_pane", $"read the {pane} pane",
            () => ReadAsync(pane, tailLines, fromLine, lineCount, contains, cancellationToken));

    async Task<string> ReadAsync(string pane, int? tailLines, int? fromLine, int? lineCount, string? contains,
        CancellationToken cancellationToken)
    {
        if (tailLines is not null && fromLine is not null)
            throw new McpException("Pass either tailLines or fromLine, not both.");
        if (lineCount is not null && fromLine is null)
            throw new McpException("lineCount needs fromLine.");
        if (tailLines is < 1 or > MaxLines || lineCount is < 1 or > MaxLines)
            throw new McpException($"tailLines and lineCount must be between 1 and {MaxLines}.");
        if (fromLine is < 1)
            throw new McpException("fromLine is 1-based.");

        var target = await FindPaneAsync(pane, cancellationToken);
        var filter = string.IsNullOrEmpty(contains) ? null : contains;

        var read = filter is null
            ? await _reader.ReadAsync(target.Id, fromLine, (fromLine is null ? tailLines : lineCount) ?? DefaultTailLines, cancellationToken)
            : await _reader.ReadAsync(target.Id, fromLine ?? 1, lineCount ?? int.MaxValue, cancellationToken);
        if (read is null)
            throw new McpException(
                $"The {target.Name} pane has no text yet: Visual Studio creates it when the pane is first shown. " +
                "Ask the user to open it in the Output window.");

        var result = filter is null
            ? FormatLines(target.Name, read, keepEnd: fromLine is null)
            : FormatMatches(target.Name, read, filter, tailLines ?? DefaultTailLines);

        if (string.Equals(NormalizeId(target.Id), NormalizeId(BuildPaneId), StringComparison.OrdinalIgnoreCase)
            && await IsBuildingAsync(cancellationToken))
        {
            var headerEnd = result.IndexOf('\n') is var i and >= 0 ? i : result.Length;
            result = result.Insert(headerEnd, " [a build is still running, so this output is incomplete; read again when it has finished]");
        }

        _logger.LogInformation("[IdeTools] {Chat} read_output_pane pane={Pane} {Window} chars={Chars}",
            _chatId, target.Name, filter is null ? $"lines={read.FirstLine}+{read.Lines.Count}" : "filtered", result.Length);
        return result;
    }

    // Whether a VS build is running, for the Build pane's note. The read itself succeeded, so a failure here only costs
    // the note.
    async Task<bool> IsBuildingAsync(CancellationToken cancellationToken)
    {
        if (_state is null)
            return false;
        try
        {
            return (await _state.ReadAsync(cancellationToken)).Building;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[IdeTools] {Chat} read_output_pane: could not tell whether a build is running", _chatId);
            return false;
        }
    }

    async Task<List<OutputPane>> VisiblePanesAsync(CancellationToken cancellationToken) =>
        (await _reader.ListAsync(cancellationToken)).Where(p => !_hiddenPaneIds.Contains(NormalizeId(p.Id))).ToList();

    async Task<OutputPane> FindPaneAsync(string nameOrId, CancellationToken cancellationToken)
    {
        var all = await _reader.ListAsync(cancellationToken);
        var query = nameOrId.Trim();
        var match = all.FirstOrDefault(p => string.Equals(p.Name, query, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(p => string.Equals(NormalizeId(p.Id), NormalizeId(query), StringComparison.OrdinalIgnoreCase));

        if (match is not null && _hiddenPaneIds.Contains(NormalizeId(match.Id)))
        {
            _logger.LogWarning("[IdeTools] {Chat} read_output_pane refused: pane '{Pane}' is hidden", _chatId, match.Name);
            throw new McpException($"The {match.Name} pane is VsAcp's own log and cannot be read.");
        }
        if (match is null)
        {
            var names = all.Where(p => !_hiddenPaneIds.Contains(NormalizeId(p.Id))).Select(p => p.Name);
            throw new McpException($"There is no Output pane '{query}'. Panes: {string.Join(", ", names)}.");
        }
        return match;
    }

    // A pane's lines under a header that says exactly which lines are shown. Over MaxChars, whole lines are dropped
    // from the start (a tail read: the newest output matters) or from the end (a range read).
    static string FormatLines(string paneName, OutputPaneLines read, bool keepEnd)
    {
        if (read.TotalLines == 0)
            return $"{paneName}: empty";
        if (read.Lines.Count == 0)
            return $"{paneName}: no lines there (the pane has {read.TotalLines})";

        var lines = read.Lines.ToList();
        var first = read.FirstLine;
        var dropped = TrimToMaxChars(lines, keepEnd);
        if (keepEnd) first += dropped;

        var header = $"{paneName}: lines {first}-{first + lines.Count - 1} of {read.TotalLines}";
        if (dropped > 0)
            header += $" ({dropped} more line{(dropped == 1 ? "" : "s")} {(keepEnd ? "before" : "after")} these went over the {MaxChars}-character limit)";
        return header + "\n" + string.Join("\n", lines);
    }

    static string FormatMatches(string paneName, OutputPaneLines read, string filter, int maxMatches)
    {
        var matches = read.Lines
            .Select((line, i) => (Number: read.FirstLine + i, Line: line))
            .Where(m => m.Line.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
        var range = read.FirstLine == 1 ? "" : $" from line {read.FirstLine}";
        if (matches.Count == 0)
            return $"{paneName}: no lines contain \"{filter}\"{range} ({read.TotalLines} lines)";

        var shown = matches.Skip(Math.Max(0, matches.Count - maxMatches)).Select(m => $"{m.Number}: {m.Line}").ToList();
        var dropped = TrimToMaxChars(shown, keepEnd: true);

        var header = $"{paneName}: {matches.Count} line{(matches.Count == 1 ? "" : "s")} contain \"{filter}\"{range} ({read.TotalLines} lines)";
        if (shown.Count < matches.Count)
            header += $"; the last {shown.Count} shown";
        if (dropped > 0)
            header += $" (the {MaxChars}-character limit)";
        return header + "\n" + string.Join("\n", shown);
    }

    // Drops whole lines until the joined text fits MaxChars, keeping the end or the start. A single line that is
    // still too long is cut. Returns how many lines were dropped.
    static int TrimToMaxChars(List<string> lines, bool keepEnd)
    {
        var total = lines.Sum(l => l.Length + 1) - 1;
        var dropped = 0;
        while (total > MaxChars && lines.Count > 1)
        {
            var index = keepEnd ? 0 : lines.Count - 1;
            total -= lines[index].Length + 1;
            lines.RemoveAt(index);
            dropped++;
        }
        if (lines.Count == 1 && lines[0].Length > MaxChars)
            lines[0] = keepEnd ? lines[0].Substring(lines[0].Length - MaxChars) : lines[0].Substring(0, MaxChars);
        return dropped;
    }

    static string NormalizeId(string id) => id.Trim().Trim('{', '}');
}
