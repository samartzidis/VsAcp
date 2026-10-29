using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using VsAcp.Core.IdeTools;
using VsAcp.Core.Tests.Support;

namespace VsAcp.Core.Tests;

/// <summary>
/// The IDE tools (ADR-061): the Output window tools' behaviour, the loopback MCP server's access rules, and the server
/// entry each session setup offers the agent.
/// </summary>
public class IdeToolsTests
{
    const string BuildId = "{1BD8A850-02D1-11D1-BEE7-00A0C913D1F8}";
    const string DebugId = "{FC076020-078A-11D1-A7DF-00A0C9110051}";
    const string VsAcpId = "{7E2A4A6B-1F3C-4D8E-9B5C-2A1D3E4F6B7A}";
    const string NeverShownId = "{0F44E2D1-F5FA-4D2D-AB30-22BE8ECD9789}";

    static string[] Numbered(int count, string prefix = "line ") => Enumerable.Range(1, count).Select(i => prefix + i).ToArray();

    // The Output window as the host reads it: a pane without lines has no text buffer yet.
    sealed class FakePanes : IOutputPaneReader
    {
        public readonly List<(string Name, string Id, string[]? Lines)> Panes = new()
        {
            ("Build", BuildId, Numbered(1011)),
            ("Debug", DebugId, Array.Empty<string>()),
            ("VsAcp", VsAcpId, new[] { "ACP send: secret" }),
            ("Package Manager", NeverShownId, null)
        };

        /// <summary>Thrown by every read, as DTE throws a COMException.</summary>
        public Exception? Fail;

        public Task<IReadOnlyList<OutputPane>> ListAsync(CancellationToken cancellationToken) =>
            Fail is not null
                ? Task.FromException<IReadOnlyList<OutputPane>>(Fail)
                : Task.FromResult<IReadOnlyList<OutputPane>>(Panes.Select(p => new OutputPane(p.Name, p.Id, p.Lines?.Length)).ToList());

        public Task<OutputPaneLines?> ReadAsync(string paneId, int? fromLine, int maxLines, CancellationToken cancellationToken)
        {
            if (Fail is not null)
                return Task.FromException<OutputPaneLines?>(Fail);
            var pane = Panes.FirstOrDefault(p => p.Id == paneId);
            if (pane.Lines is null)
                return Task.FromResult<OutputPaneLines?>(null);

            var total = pane.Lines.Length;
            var first = fromLine ?? Math.Max(1, total - maxLines + 1);
            var lines = pane.Lines.Skip(first - 1).Take(maxLines).ToArray();
            return Task.FromResult<OutputPaneLines?>(new OutputPaneLines(first, lines, total));
        }

        public void Set(string name, string[] lines)
        {
            var index = Panes.FindIndex(p => p.Name == name);
            Panes[index] = (name, Panes[index].Id, lines);
        }
    }

    sealed class FakeState : IIdeStateReader
    {
        public IdeState State = new();
        public Exception? Fail;

        public Task<IdeState> ReadAsync(CancellationToken cancellationToken) =>
            Fail is not null ? Task.FromException<IdeState>(Fail) : Task.FromResult(State);
    }

    // Everything logged, by category, as the host's Serilog pipeline would receive it.
    sealed class CaptureLoggers : ILoggerFactory
    {
        public readonly ConcurrentQueue<(string Category, LogLevel Level, string Message, Exception? Exception)> Entries = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        sealed class Logger : ILogger
        {
            readonly CaptureLoggers _owner;
            readonly string _category;

            public Logger(CaptureLoggers owner, string category) { _owner = owner; _category = category; }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                _owner.Entries.Enqueue((_category, logLevel, formatter(state, exception), exception));
        }
    }

    static OutputPaneTools Tools(FakePanes? panes = null, FakeState? state = null, ILogger? logger = null) =>
        new(panes ?? new FakePanes(), [VsAcpId], "chat-1", logger ?? NullLogger.Instance, state);

    static string[] BodyLines(string result) => result.Split('\n').Skip(1).ToArray();

    // ---- the tools ----

    [Fact]
    public async Task Listing_shows_every_pane_but_VsAcp_with_its_size()
    {
        var text = await Tools().ListOutputPanesAsync(CancellationToken.None);

        Assert.Contains($"- Build ({BuildId}): 1011 lines", text);
        Assert.Contains($"- Debug ({DebugId}): 0 lines", text);
        Assert.Contains("- Package Manager", text);
        Assert.Contains("no text yet", text);
        Assert.DoesNotContain("VsAcp", text);
    }

    [Fact]
    public async Task A_read_defaults_to_the_last_200_lines()
    {
        var text = await Tools().ReadOutputPaneAsync("Build");

        Assert.StartsWith("Build: lines 812-1011 of 1011\n", text);
        var lines = BodyLines(text);
        Assert.Equal(200, lines.Length);
        Assert.Equal("line 812", lines[0]);
        Assert.Equal("line 1011", lines[^1]);
    }

    [Fact]
    public async Task A_read_takes_a_tail_or_a_range()
    {
        var tail = await Tools().ReadOutputPaneAsync("Build", tailLines: 3);
        var range = await Tools().ReadOutputPaneAsync("Build", fromLine: 10, lineCount: 3);

        Assert.Equal("Build: lines 1009-1011 of 1011\nline 1009\nline 1010\nline 1011", tail);
        Assert.Equal("Build: lines 10-12 of 1011\nline 10\nline 11\nline 12", range);
    }

    [Theory]
    [InlineData("build")]
    [InlineData(" Build ")]
    [InlineData(BuildId)]
    [InlineData("1bd8a850-02d1-11d1-bee7-00a0c913d1f8")]
    public async Task A_pane_is_found_by_name_or_id(string pane)
    {
        Assert.StartsWith("Build: lines 1011-1011", await Tools().ReadOutputPaneAsync(pane, tailLines: 1));
    }

    [Theory]
    [InlineData("VsAcp")]
    [InlineData(VsAcpId)]
    [InlineData("7E2A4A6B-1F3C-4D8E-9B5C-2A1D3E4F6B7A")]
    public async Task The_VsAcp_pane_cannot_be_read(string pane)
    {
        var error = await Assert.ThrowsAsync<McpException>(() => Tools().ReadOutputPaneAsync(pane));

        Assert.Contains("VsAcp's own log", error.Message);
    }

    [Fact]
    public async Task An_unknown_pane_names_the_ones_there_are_but_not_VsAcp()
    {
        var error = await Assert.ThrowsAsync<McpException>(() => Tools().ReadOutputPaneAsync("Tests"));

        Assert.Equal("There is no Output pane 'Tests'. Panes: Build, Debug, Package Manager.", error.Message);
    }

    [Fact]
    public async Task A_pane_never_shown_says_how_to_get_its_text()
    {
        var error = await Assert.ThrowsAsync<McpException>(() => Tools().ReadOutputPaneAsync("Package Manager"));

        Assert.Contains("has no text yet", error.Message);
        Assert.Contains("open it in the Output window", error.Message);
    }

    [Fact]
    public async Task An_empty_pane_says_so()
    {
        Assert.Equal("Debug: empty", await Tools().ReadOutputPaneAsync("Debug"));
    }

    [Fact]
    public async Task A_range_past_the_end_says_how_long_the_pane_is()
    {
        Assert.Equal("Build: no lines there (the pane has 1011)", await Tools().ReadOutputPaneAsync("Build", fromLine: 5000));
    }

    [Fact]
    public async Task A_filter_returns_the_last_matching_lines_with_their_numbers()
    {
        var panes = new FakePanes();
        panes.Set("Build", ["Build started", "a.cs(1): error CS1002", "ok", "b.cs(2): Error CS0103", "c.cs(3): error CS0246", "done"]);

        var text = await Tools(panes).ReadOutputPaneAsync("Build", tailLines: 2, contains: "ERROR CS");

        Assert.Equal("Build: 3 lines contain \"ERROR CS\" (6 lines); the last 2 shown\n4: b.cs(2): Error CS0103\n5: c.cs(3): error CS0246", text);
    }

    [Fact]
    public async Task A_filter_with_no_matches_says_so()
    {
        Assert.Equal("Build: no lines contain \"warning\" (1011 lines)", await Tools().ReadOutputPaneAsync("Build", contains: "warning"));
    }

    [Fact]
    public async Task A_tail_over_the_character_limit_keeps_the_newest_lines()
    {
        var panes = new FakePanes();
        panes.Set("Build", Numbered(200, new string('x', 990) + " "));

        var text = await Tools(panes).ReadOutputPaneAsync("Build");

        Assert.True(text.Length <= OutputPaneTools.MaxChars + 200, $"{text.Length} characters");
        var lines = BodyLines(text);
        Assert.EndsWith(" 200", lines[^1]);
        Assert.Contains($"Build: lines {200 - lines.Length + 1}-200 of 200 ({200 - lines.Length} more lines before these went over the 64000-character limit)", text);
    }

    [Fact]
    public async Task A_range_over_the_character_limit_keeps_its_first_lines()
    {
        var panes = new FakePanes();
        panes.Set("Build", Numbered(200, new string('x', 990) + " "));

        var text = await Tools(panes).ReadOutputPaneAsync("Build", fromLine: 1, lineCount: 200);

        var lines = BodyLines(text);
        Assert.EndsWith(" 1", lines[0]);
        Assert.Contains($"Build: lines 1-{lines.Length} of 200 ({200 - lines.Length} more lines after these", text);
    }

    [Fact]
    public async Task A_single_line_over_the_limit_is_cut()
    {
        var panes = new FakePanes();
        panes.Set("Build", [new string('a', 10) + new string('b', 70_000)]);

        var lines = BodyLines(await Tools(panes).ReadOutputPaneAsync("Build"));

        Assert.Equal(new string('b', OutputPaneTools.MaxChars), Assert.Single(lines));
    }

    [Theory]
    [InlineData(5, 1, null, "either tailLines or fromLine")]
    [InlineData(null, null, 5, "lineCount needs fromLine")]
    [InlineData(0, null, null, "between 1 and 2000")]
    [InlineData(null, 1, 2001, "between 1 and 2000")]
    [InlineData(null, 0, null, "1-based")]
    public async Task Bad_arguments_are_explained(int? tailLines, int? fromLine, int? lineCount, string message)
    {
        var error = await Assert.ThrowsAsync<McpException>(() =>
            Tools().ReadOutputPaneAsync("Build", tailLines: tailLines, fromLine: fromLine, lineCount: lineCount));

        Assert.Contains(message, error.Message);
    }

    // ---- unexpected failures ----

    [Fact]
    public async Task A_failure_reading_a_pane_is_logged_and_explained_to_the_agent()
    {
        var loggers = new CaptureLoggers();
        var panes = new FakePanes { Fail = new COMException("The application is busy.") };

        var error = await Assert.ThrowsAsync<McpException>(() =>
            Tools(panes, logger: loggers.CreateLogger("tools")).ReadOutputPaneAsync("Build"));

        Assert.Equal("Visual Studio could not read the Build pane (COMException: The application is busy.). " +
            "Try again; if it keeps failing, tell the user.", error.Message);
        var logged = Assert.Single(loggers.Entries);
        Assert.Equal(LogLevel.Warning, logged.Level);
        Assert.Equal("[IdeTools] chat-1 read_output_pane failed", logged.Message);
        Assert.Same(panes.Fail, logged.Exception);
    }

    [Fact]
    public async Task A_failure_listing_panes_or_reading_the_state_is_explained_too()
    {
        var panes = new FakePanes { Fail = new InvalidOperationException("boom") };
        var state = new FakeState { Fail = new COMException("gone") };

        var list = await Assert.ThrowsAsync<McpException>(() => Tools(panes).ListOutputPanesAsync(CancellationToken.None));
        var read = await Assert.ThrowsAsync<McpException>(() =>
            new IdeStateTools(state, "chat-1", NullLogger.Instance).GetIdeStateAsync(CancellationToken.None));

        Assert.StartsWith("Visual Studio could not list the Output window panes (InvalidOperationException: boom).", list.Message);
        Assert.StartsWith("Visual Studio could not read its state (COMException: gone).", read.Message);
    }

    [Fact]
    public async Task Errors_meant_for_the_agent_are_not_rewrapped_or_logged_as_failures()
    {
        var loggers = new CaptureLoggers();

        var error = await Assert.ThrowsAsync<McpException>(() =>
            Tools(logger: loggers.CreateLogger("tools")).ReadOutputPaneAsync("Tests"));

        Assert.StartsWith("There is no Output pane 'Tests'.", error.Message);
        Assert.DoesNotContain(loggers.Entries, e => e.Message.Contains("failed"));
    }

    [Fact]
    public async Task A_build_pane_read_still_works_when_the_build_state_cannot_be_read()
    {
        var loggers = new CaptureLoggers();
        var state = new FakeState { Fail = new COMException("gone") };

        var text = await Tools(state: state, logger: loggers.CreateLogger("tools")).ReadOutputPaneAsync("Build", tailLines: 1);

        Assert.Equal("Build: lines 1011-1011 of 1011\nline 1011", text);
        Assert.Contains(loggers.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("could not tell whether a build is running"));
    }

    // ---- a build still running ----

    const string StillRunning = " [a build is still running, so this output is incomplete; read again when it has finished]";

    [Fact]
    public async Task A_Build_pane_read_during_a_build_says_the_output_is_incomplete()
    {
        var state = new FakeState { State = new IdeState { Building = true } };

        var text = await Tools(state: state).ReadOutputPaneAsync("Build", tailLines: 1);

        Assert.Equal("Build: lines 1011-1011 of 1011" + StillRunning + "\nline 1011", text);
    }

    [Fact]
    public async Task A_filtered_Build_pane_read_during_a_build_says_so_too()
    {
        var state = new FakeState { State = new IdeState { Building = true } };

        var text = await Tools(state: state).ReadOutputPaneAsync("Build", contains: "nothing like this");

        Assert.Equal("Build: no lines contain \"nothing like this\" (1011 lines)" + StillRunning, text);
    }

    [Fact]
    public async Task Other_panes_and_finished_builds_have_no_note()
    {
        var building = new FakeState { State = new IdeState { Building = true } };
        var idle = new FakeState();

        Assert.DoesNotContain("still running", await Tools(state: building).ReadOutputPaneAsync("Debug"));
        Assert.DoesNotContain("still running", await Tools(state: idle).ReadOutputPaneAsync("Build"));
    }

    // ---- get_ide_state ----

    static readonly IdeState Busy = new()
    {
        SolutionPath = @"C:\git\VsAcp\VsAcp.slnx",
        Configuration = "Debug",
        Platform = "Any CPU",
        StartupProjects = [@"src\VsAcp\VsAcp.csproj"],
        Debugger = DebuggerMode.Break,
        Building = true,
        SolutionLoading = true,
        ActiveWindow = "ACP Chat",
        ActiveDocument = new ActiveDocument(@"C:\git\VsAcp\src\VsAcp\VsAcpPackage.cs", 238, 13, 231, 240),
        OpenDocuments = [@"C:\git\VsAcp\src\VsAcp\VsAcpPackage.cs", @"C:\git\VsAcp\README.md"]
    };

    [Fact]
    public async Task The_state_describes_the_solution_debugger_busy_work_and_editor()
    {
        var text = await new IdeStateTools(new FakeState { State = Busy }, "chat-1", NullLogger.Instance).GetIdeStateAsync(CancellationToken.None);

        Assert.Equal(
            "Solution: C:\\git\\VsAcp\\VsAcp.slnx\n" +
            "Configuration: Debug | Any CPU\n" +
            "Startup project: src\\VsAcp\\VsAcp.csproj\n" +
            "Debugger: stopped (break mode)\n" +
            "Busy: a build is running; the solution is still loading\n" +
            "Active window: ACP Chat\n" +
            "Active document: C:\\git\\VsAcp\\src\\VsAcp\\VsAcpPackage.cs, caret at line 238, column 13, selection lines 231-240\n" +
            "Open documents (2):\n" +
            "- C:\\git\\VsAcp\\src\\VsAcp\\VsAcpPackage.cs\n" +
            "- C:\\git\\VsAcp\\README.md",
            text);
    }

    [Fact]
    public void Nothing_open_and_nothing_running_reads_plainly()
    {
        Assert.Equal("Solution: none open\nDebugger: not running\nBusy: no\nActive document: none", IdeStateTools.Format(new IdeState()));
    }

    [Theory]
    [InlineData(null, null, "caret at line 5, column 2")]
    [InlineData(7, 7, "caret at line 5, column 2, selection on line 7")]
    public void A_selection_is_shown_only_when_there_is_one(int? from, int? to, string expected)
    {
        var text = IdeStateTools.Format(new IdeState { ActiveDocument = new ActiveDocument(@"C:\a.cs", 5, 2, from, to) });

        Assert.EndsWith($"Active document: C:\\a.cs, {expected}", text);
    }

    [Fact]
    public void A_long_list_of_open_documents_is_capped()
    {
        var docs = Enumerable.Range(1, 130).Select(i => $@"C:\d{i}.cs").ToArray();

        var lines = IdeStateTools.Format(new IdeState { OpenDocuments = docs }).Split('\n');

        Assert.Contains("Open documents (130):", lines);
        Assert.Equal(IdeStateTools.MaxOpenDocuments + 1, lines.Count(l => l.StartsWith("- ")));
        Assert.Equal("- … 30 more", lines[^1]);
    }

    [Fact]
    public void Several_startup_projects_are_listed_together()
    {
        var text = IdeStateTools.Format(new IdeState { SolutionPath = @"C:\s.sln", StartupProjects = ["A", "B"] });

        Assert.Contains("\nStartup projects: A, B\n", text);
    }

    // ---- the server ----

    sealed class Server : IDisposable
    {
        public readonly FakePanes Panes = new();
        public readonly FakeState State = new();
        public readonly CaptureLoggers Loggers = new();
        public bool Enabled = true;
        public readonly IdeToolsServer Instance;

        /// <param name="loadingSdk">Runs where the server first uses the MCP SDK; throwing stands in for an SDK that fails to load.</param>
        public Server(Action? loadingSdk = null) =>
            Instance = new IdeToolsServer(Panes, State, [VsAcpId], () => Enabled, Loggers, loadingSdk);

        // A started server and one chat's URL and bearer token.
        public (IdeToolsEndpoint Endpoint, string Url, string Token) Chat(string chatId = "chat-1")
        {
            var endpoint = Instance.CreateEndpoint(chatId);
            var entry = (AcpProtocol.McpServerHttp)endpoint.Describe()!;
            return (endpoint, entry.Url, entry.Headers.Single().Value.Substring("Bearer ".Length));
        }

        public void Dispose() => Instance.Dispose();
    }

    static readonly HttpClient Http = new();

    static async Task<(HttpStatusCode Status, JsonNode? Message)> PostAsync(string url, string? token, string json,
        Action<HttpRequestMessage>? adjust = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token is not null)
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        adjust?.Invoke(request);

        using var response = await Http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        // An SSE stream of one "message" event.
        var data = body.Split('\n').FirstOrDefault(l => l.StartsWith("data: ", StringComparison.Ordinal));
        return (response.StatusCode, data is null ? null : JsonNode.Parse(data.Substring("data: ".Length)));
    }

    static string Call(string name, object arguments) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 3, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = name, ["arguments"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(arguments)) }
        }.ToJsonString();

    const string Initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""";
    const string ListTools = """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";

    [Fact]
    public void The_server_does_not_listen_until_a_chat_offers_it()
    {
        using var server = new Server();
        server.Instance.CreateEndpoint("chat-1");

        Assert.Null(server.Instance.Url);
    }

    [Fact]
    public async Task An_agent_initializes_lists_the_tools_and_reads_a_pane()
    {
        using var server = new Server();
        var (_, url, token) = server.Chat();

        var (initStatus, init) = await PostAsync(url, token, Initialize);
        var (_, list) = await PostAsync(url, token, ListTools);
        var (_, read) = await PostAsync(url, token, Call("read_output_pane", new { pane = "Build", tailLines = 2 }));

        Assert.Equal(HttpStatusCode.OK, initStatus);
        Assert.StartsWith("http://127.0.0.1:", url);
        Assert.Equal("visual-studio", (string?)init!["result"]!["serverInfo"]!["name"]);
        var tools = list!["result"]!["tools"]!.AsArray();
        Assert.Equal(new[] { "get_ide_state", "list_output_panes", "read_output_pane" },
            tools.Select(t => (string?)t!["name"]).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.All(tools, t => Assert.True((bool?)t!["annotations"]!["readOnlyHint"]));
        Assert.Equal("Build: lines 1010-1011 of 1011\nline 1010\nline 1011", (string?)read!["result"]!["content"]![0]!["text"]);
    }

    [Fact]
    public async Task An_agent_reads_the_ide_state()
    {
        using var server = new Server();
        server.State.State = Busy;
        var (_, url, token) = server.Chat();

        var (_, state) = await PostAsync(url, token, Call("get_ide_state", new { }));

        Assert.StartsWith("Solution: C:\\git\\VsAcp\\VsAcp.slnx\nConfiguration: Debug | Any CPU", (string?)state!["result"]!["content"]![0]!["text"]);
    }

    [Fact]
    public async Task A_tool_error_reaches_the_agent_as_an_error_result()
    {
        using var server = new Server();
        var (_, url, token) = server.Chat();

        var (_, read) = await PostAsync(url, token, Call("read_output_pane", new { pane = "VsAcp" }));

        Assert.True((bool?)read!["result"]!["isError"]);
        Assert.Contains("VsAcp's own log", (string?)read["result"]!["content"]![0]!["text"]);
    }

    [Fact]
    public async Task A_failure_in_Visual_Studio_reaches_the_agent_with_its_reason_and_our_log()
    {
        using var server = new Server();
        var (_, url, token) = server.Chat();
        server.Panes.Fail = new COMException("The application is busy.");

        var (_, read) = await PostAsync(url, token, Call("read_output_pane", new { pane = "Build" }));

        // The SDK puts its own prefix before an McpException's message; without the guard the agent gets the prefix alone.
        Assert.True((bool?)read!["result"]!["isError"]);
        Assert.Equal("An error occurred invoking 'read_output_pane': Visual Studio could not read the Build pane " +
            "(COMException: The application is busy.). Try again; if it keeps failing, tell the user.",
            (string?)read["result"]!["content"]![0]!["text"]);
        Assert.Contains(server.Loggers.Entries, e => e.Message == "[IdeTools] chat-1 read_output_pane failed" && e.Exception is COMException);
    }

    [Fact]
    public async Task The_SDK_logs_only_warnings_and_never_what_the_tools_return()
    {
        using var server = new Server();
        var (_, url, token) = server.Chat();

        await PostAsync(url, token, Initialize);
        await PostAsync(url, token, ListTools);
        await PostAsync(url, token, Call("read_output_pane", new { pane = "Build", tailLines = 3 }));
        await PostAsync(url, token, Call("no_such_tool", new { }));

        var sdk = server.Loggers.Entries.Where(e => !e.Category.StartsWith("VsAcp", StringComparison.Ordinal)).ToList();
        Assert.All(sdk, e => Assert.True(e.Level >= LogLevel.Warning, $"{e.Category} logged at {e.Level}: {e.Message}"));
        Assert.DoesNotContain(server.Loggers.Entries, e => e.Message.Contains("line 1011"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-token")]
    public async Task A_request_without_a_known_token_is_refused(string? token)
    {
        using var server = new Server();
        var (_, url, _) = server.Chat();

        var (status, _) = await PostAsync(url, token, ListTools);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task A_closed_tabs_token_stops_working()
    {
        using var server = new Server();
        var (endpoint, url, token) = server.Chat();
        var (_, otherUrl, otherToken) = server.Chat("chat-2");

        endpoint.Dispose();

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(url, token, ListTools)).Status);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(otherUrl, otherToken, ListTools)).Status);
    }

    [Fact]
    public async Task A_browser_request_is_refused_even_with_the_token()
    {
        using var server = new Server();
        var (_, url, token) = server.Chat();

        var (status, _) = await PostAsync(url, token, ListTools, r => r.Headers.Add("Origin", "http://evil.example"));

        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task Only_POST_is_served()
    {
        using var server = new Server();
        var (_, url, token) = server.Chat();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

        using var response = await Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("POST", string.Join(",", response.Content.Headers.Allow));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // no Content-Length up front: the limit has to hold while reading
    public async Task A_body_over_one_megabyte_is_refused(bool chunked)
    {
        using var server = new Server();
        var (_, url, token) = server.Chat();
        var huge = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{\"pad\":\"" + new string('x', 1024 * 1024) + "\"}}";

        var (status, _) = await PostAsync(url, token, huge, r => r.Headers.TransferEncodingChunked = chunked);

        Assert.Equal((HttpStatusCode)413, status);
    }

    [Fact]
    public async Task A_notification_is_accepted_without_an_answer()
    {
        using var server = new Server();
        var (_, url, token) = server.Chat();

        var (status, message) = await PostAsync(url, token, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Null(message);
    }

    [Fact]
    public async Task Turning_the_setting_off_withdraws_the_offer_and_refuses_requests()
    {
        using var server = new Server();
        var (endpoint, url, token) = server.Chat();

        server.Enabled = false;

        Assert.Null(endpoint.Describe());
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(url, token, ListTools)).Status);
    }

    // ---- what session setup offers the agent ----

    static object Agent(bool http, bool loadSession = false, object? sessionCapabilities = null) => new
    {
        agentCapabilities = new { loadSession, mcpCapabilities = new { http, sse = false }, sessionCapabilities = sessionCapabilities ?? new { } },
        turns = new[] { new { steps = new object[] { new { say = "ok", messageId = "m-ok" } } } }
    };

    static Task<ChatHarness> TabAsync(Server server, object scenario) =>
        ChatHarness.CreateAsync(scenario, services: s => s.AddSingleton<IIdeToolsEndpoint>(_ => server.Instance.CreateEndpoint("tab")));

    static JsonArray McpServers(JsonObject request) => (JsonArray)request["mcpServers"]!;

    [Fact]
    public async Task A_new_session_offers_the_server_with_the_tabs_token_and_the_agent_can_use_it()
    {
        using var server = new Server();
        using var h = await TabAsync(server, Agent(http: true));

        await h.SendAsync("hi");

        var entry = McpServers(h.Received("session/new").Single()).Single()!;
        Assert.Equal("http", (string?)entry["type"]);
        Assert.Equal("visual-studio", (string?)entry["name"]);
        Assert.Equal(server.Instance.Url, (string?)entry["url"]);
        var header = entry["headers"]![0]!;
        Assert.Equal("Authorization", (string?)header["name"]);
        var token = ((string)header["value"]!).Substring("Bearer ".Length);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync((string)entry["url"]!, token, ListTools)).Status);
    }

    [Fact]
    public async Task The_token_never_reaches_the_log()
    {
        using var server = new Server();
        using var h = await TabAsync(server, Agent(http: true));

        await h.SendAsync("hi");

        var header = (string)McpServers(h.Received("session/new").Single())[0]!["headers"]![0]!["value"]!;
        Assert.Contains(h.Log.Lines, l => l.Contains("ACP send:") && l.Contains("\"mcpServers\""));
        Assert.DoesNotContain(h.Log.Lines, l => l.Contains(header.Substring("Bearer ".Length)));
        Assert.Contains(h.Log.Lines, l => l.Contains("\"value\":\"(redacted)\""));
    }

    [Fact]
    public async Task An_agent_without_http_mcp_gets_no_server()
    {
        using var server = new Server();
        using var h = await TabAsync(server, Agent(http: false));

        await h.SendAsync("hi");

        Assert.Empty(McpServers(h.Received("session/new").Single()));
        Assert.Single(h.Log.Lines, l => l.Contains("takes no HTTP MCP servers"));
    }

    [Fact]
    public async Task With_the_setting_off_no_server_is_offered()
    {
        using var server = new Server { Enabled = false };
        using var h = await TabAsync(server, Agent(http: true));

        await h.SendAsync("hi");

        Assert.Empty(McpServers(h.Received("session/new").Single()));
        Assert.Null(server.Instance.Url);
    }

    [Fact]
    public async Task Without_the_host_endpoint_no_server_is_offered()
    {
        using var h = await ChatHarness.CreateAsync(Agent(http: true));

        await h.SendAsync("hi");

        Assert.Empty(McpServers(h.Received("session/new").Single()));
    }

    // ---- an MCP SDK that won't load costs the feature, never the chat ----

    [Fact]
    public async Task With_the_setting_off_the_SDK_is_never_touched()
    {
        var loads = 0;
        using var server = new Server(() => loads++) { Enabled = false };
        using var h = await TabAsync(server, Agent(http: true));

        await h.SendAsync("hi");

        Assert.Equal(0, loads);
    }

    [Fact]
    public void A_tabs_tools_are_built_once_when_it_first_offers_the_server()
    {
        var loads = 0;
        using var server = new Server(() => loads++);
        var endpoint = server.Instance.CreateEndpoint("chat-1");
        Assert.Equal(0, loads);

        endpoint.Describe();
        endpoint.Describe();

        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task An_SDK_that_fails_to_load_turns_the_tools_off_and_the_chat_still_works()
    {
        using var server = new Server(() => throw new FileNotFoundException(
            "Could not load file or assembly 'Microsoft.Extensions.AI.Abstractions, Version=10.4.0.0'"));
        using var h = await TabAsync(server, Agent(http: true));

        var result = await h.SendAsync("hi");

        Assert.Equal(VsAcp.Core.Services.TurnResult.Completed, result);
        Assert.Empty(McpServers(h.Received("session/new").Single()));
        Assert.Null(server.Instance.Url);
        var warning = Assert.Single(server.Loggers.Entries, e => e.Message.Contains("MCP SDK could not be loaded"));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.IsType<FileNotFoundException>(warning.Exception);
    }

    [Fact]
    public void After_the_SDK_failed_no_tab_tries_it_again()
    {
        var loads = 0;
        using var server = new Server(() => { loads++; throw new FileLoadException("mismatched version"); });
        var first = server.Instance.CreateEndpoint("chat-1");
        var second = server.Instance.CreateEndpoint("chat-2");

        Assert.Null(first.Describe());
        Assert.Null(second.Describe());
        Assert.Null(first.Describe());

        Assert.Equal(1, loads);
        Assert.Single(server.Loggers.Entries, e => e.Message.Contains("MCP SDK could not be loaded"));
    }

    [Fact]
    public async Task Loading_a_saved_session_offers_the_server_too()
    {
        using var server = new Server();
        using var h = await TabAsync(server, Agent(http: true, loadSession: true));
        h.Service.RestoreHistory("fake-session-1");

        await h.SendAsync("hi");

        Assert.Equal("http", (string?)McpServers(h.Received("session/load").Single()).Single()!["type"]);
    }

    [Fact]
    public async Task A_fork_offers_the_server_too()
    {
        using var server = new Server();
        using var h = await TabAsync(server, Agent(http: true, sessionCapabilities: new { fork = new { }, close = new { } }));
        await h.SendAsync("hi");

        await h.Controller.ForkAsync(h.Messages.Single(m => m.Type == VsAcp.Core.Abstractions.ChatItemType.Assistant).Id);

        Assert.Equal("http", (string?)McpServers(h.Received("session/fork").Single()).Single()!["type"]);
    }
}
