using System.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace VsAcp.Core.IdeTools;

public enum DebuggerMode { NotRunning, Running, Break }

/// <summary>The active document and where the caret is in it. Lines and columns are 1-based.</summary>
/// <param name="SelectionStartLine">Null when nothing is selected.</param>
public sealed record ActiveDocument(string Path, int CaretLine, int CaretColumn, int? SelectionStartLine, int? SelectionEndLine);

/// <summary>What Visual Studio is showing and doing, read in one go on the UI thread.</summary>
public sealed record IdeState
{
    /// <summary>Null when no solution or folder is open.</summary>
    public string? SolutionPath { get; init; }
    public string? Configuration { get; init; }
    public string? Platform { get; init; }
    public IReadOnlyList<string> StartupProjects { get; init; } = [];
    public DebuggerMode Debugger { get; init; }
    public bool Building { get; init; }
    /// <summary>The solution is still loading, or IntelliSense is still processing it.</summary>
    public bool SolutionLoading { get; init; }
    /// <summary>The caption of the window that has focus (a tool window, such as the chat, or a document).</summary>
    public string? ActiveWindow { get; init; }
    /// <summary>The last document the user was in, even while a tool window has focus; null when none is open.</summary>
    public ActiveDocument? ActiveDocument { get; init; }
    public IReadOnlyList<string> OpenDocuments { get; init; } = [];
}

/// <summary>Reads <see cref="IdeState"/>. The host implements it over Visual Studio.</summary>
public interface IIdeStateReader
{
    Task<IdeState> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>The <c>get_ide_state</c> tool one chat's agent gets (ADR-061). Read-only.</summary>
public sealed class IdeStateTools
{
    public const int MaxOpenDocuments = 100;

    readonly IIdeStateReader _reader;
    readonly string _chatId;
    readonly ILogger _logger;

    public IdeStateTools(IIdeStateReader reader, string chatId, ILogger logger)
    {
        _reader = reader;
        _chatId = chatId;
        _logger = logger;
    }

    public IReadOnlyList<McpServerTool> CreateTools() =>
    [
        McpServerTool.Create(GetIdeStateAsync, new McpServerToolCreateOptions
        {
            Name = "get_ide_state",
            Title = "Get Visual Studio's state",
            Description = "Describes what Visual Studio is showing and doing: the open solution, its active configuration, " +
                "platform and startup project, whether a build is running, whether the debugger is running or stopped " +
                "(break mode), the active document with the caret and selection, and the open documents. Use it to build " +
                "or run what Visual Studio would, and to see what the user is looking at.",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false
        })
    ];

    public Task<string> GetIdeStateAsync(CancellationToken cancellationToken) =>
        ToolGuard.RunAsync(_logger, _chatId, "get_ide_state", "read its state", async () =>
        {
            var state = await _reader.ReadAsync(cancellationToken);
            _logger.LogInformation("[IdeTools] {Chat} get_ide_state", _chatId);
            return Format(state);
        });

    public static string Format(IdeState state)
    {
        var text = new StringBuilder();

        if (state.SolutionPath is null)
        {
            text.Append("Solution: none open");
        }
        else
        {
            text.Append("Solution: ").Append(state.SolutionPath);
            if (state.Configuration is not null)
                text.Append($"\nConfiguration: {state.Configuration}{(state.Platform is null ? "" : " | " + state.Platform)}");
            if (state.StartupProjects.Count > 0)
                text.Append($"\nStartup project{(state.StartupProjects.Count == 1 ? "" : "s")}: {string.Join(", ", state.StartupProjects)}");
        }

        text.Append("\nDebugger: ").Append(state.Debugger switch
        {
            DebuggerMode.Running => "running",
            DebuggerMode.Break => "stopped (break mode)",
            _ => "not running"
        });

        var busy = new List<string>();
        if (state.Building) busy.Add("a build is running");
        if (state.SolutionLoading) busy.Add("the solution is still loading");
        text.Append("\nBusy: ").Append(busy.Count == 0 ? "no" : string.Join("; ", busy));

        if (state.ActiveWindow is not null)
            text.Append("\nActive window: ").Append(state.ActiveWindow);

        if (state.ActiveDocument is { } doc)
        {
            text.Append($"\nActive document: {doc.Path}, caret at line {doc.CaretLine}, column {doc.CaretColumn}");
            if (doc.SelectionStartLine is { } from && doc.SelectionEndLine is { } to)
                text.Append(from == to ? $", selection on line {from}" : $", selection lines {from}-{to}");
        }
        else
        {
            text.Append("\nActive document: none");
        }

        if (state.OpenDocuments.Count > 0)
        {
            text.Append($"\nOpen documents ({state.OpenDocuments.Count}):");
            foreach (var path in state.OpenDocuments.Take(MaxOpenDocuments))
                text.Append("\n- ").Append(path);
            if (state.OpenDocuments.Count > MaxOpenDocuments)
                text.Append($"\n- … {state.OpenDocuments.Count - MaxOpenDocuments} more");
        }

        return text.ToString();
    }
}
