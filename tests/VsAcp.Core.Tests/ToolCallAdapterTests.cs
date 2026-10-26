using System.Text.Json;
using AcpProtocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VsAcp.Core.Abstractions;
using VsAcp.Core.Acp.Handlers;
using VsAcp.Core.DependencyInjection;
using VsAcp.Core.Tests.Support;

namespace VsAcp.Core.Tests;

/// <summary>Tool calls, plans and session-state updates as they pass through the adapter.</summary>
public sealed class ToolCallAdapterTests : IDisposable
{
    private readonly List<(string Kind, OutputItem Item)> _events = new();
    private readonly StubCheckpoints _checkpoints = new();
    private readonly List<DiffRequest> _diffs = new();
    private readonly TempDirectory _dir = new();
    private readonly ServiceProvider _provider;
    private readonly SessionUpdateAdapter _adapter;

    public ToolCallAdapterTests()
    {
        var services = new ServiceCollection();
        services.AddVsAcpServices(options =>
        {
            options.WorkingDirectory = _dir.Path;
            options.HasProjectDirectory = false;
        });
        services.AddSingleton(new TestLog());
        services.AddSingleton(typeof(ILogger<>), typeof(TestLogger<>));
        services.AddSingleton<IOutputListener>(new Listener(_events));
        services.AddSingleton<ICheckpointService>(_checkpoints);
        _provider = services.BuildServiceProvider();
        // workspace scans report from a thread-pool thread
        _provider.GetRequiredService<IDiffBroker>().DiffCompleted += d => { lock (_diffs) _diffs.Add(d); };
        _adapter = _provider.GetRequiredService<SessionUpdateAdapter>();
        _adapter.Reset();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _dir.Dispose();
    }

    private Task Send(SessionUpdate update) =>
        _adapter.HandleAsync(new SessionNotification { SessionId = "s1", Update = update }, CancellationToken.None).AsTask();

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static SessionUpdateToolCall Call(string id, string title = "Run", ToolCallStatus status = ToolCallStatus.Pending,
        ToolKind kind = ToolKind.Other, ToolCallContent[]? content = null, JsonElement? rawInput = null, JsonElement? rawOutput = null,
        string? name = null) =>
        new() { ToolCallId = id, Title = title, Status = status, Kind = kind, Content = content, RawInput = rawInput, RawOutput = rawOutput, Name = name };

    private static SessionUpdateToolCallUpdate Update(string id, ToolCallStatus? status = null, string? title = null,
        ToolCallContent[]? content = null, JsonElement? rawInput = null, JsonElement? rawOutput = null, JsonElement? meta = null) =>
        new() { ToolCallId = id, Status = status, Title = title, Content = content, RawInput = rawInput, RawOutput = rawOutput, Meta = meta };

    private IEnumerable<OutputItem> Of(string kind) => _events.Where(e => e.Kind == kind).Select(e => e.Item);

    private static ToolCallContent[] Diff(string path, string newText = "new") =>
        new ToolCallContent[] { new ToolCallContentDiff { Path = path, NewText = newText } };

    // ---- lifecycle ----

    [Fact]
    public async Task A_new_tool_call_starts_a_pending_step_keyed_by_its_id()
    {
        await Send(Call("call-1", "Run tests", kind: ToolKind.Execute));

        var started = Assert.Single(Of("started"));
        Assert.Equal("call-1", started.Id);
        Assert.Equal("Run tests", started.Title);
        Assert.Equal("Run tests", started.ToolName);
        Assert.Equal(OutputItemStatus.Pending, started.Status);
    }

    [Fact]
    public async Task Updates_stream_into_the_same_step_and_completion_finishes_it()
    {
        await Send(Call("c", "Run tests", rawInput: Json("""{"command":"dotnet test"}""")));
        await Send(Update("c", ToolCallStatus.InProgress));
        await Send(Update("c", ToolCallStatus.Completed, rawOutput: Json("""{"output":"Passed!"}""")));

        Assert.Single(Of("started"));
        Assert.Single(Of("updated"));
        var done = Assert.Single(Of("completed"));
        Assert.Equal("c", done.Id);
        Assert.Equal(OutputItemStatus.Success, done.Status);
        Assert.Equal("Run tests dotnet test", done.Title);
        Assert.Equal("dotnet test\nPassed!", done.Body);
    }

    [Fact]
    public async Task A_failed_update_completes_the_step_as_an_error()
    {
        await Send(Call("c"));
        await Send(Update("c", ToolCallStatus.Failed, rawOutput: Json("""{"error":"exit code 1"}""")));

        var done = Assert.Single(Of("completed"));
        Assert.Equal(OutputItemStatus.Error, done.Status);
        Assert.Equal("exit code 1", done.Body);
    }

    [Theory]
    [InlineData(ToolCallStatus.Completed, OutputItemStatus.Success)]
    [InlineData(ToolCallStatus.Failed, OutputItemStatus.Error)]
    public async Task A_tool_call_that_arrives_finished_is_started_and_completed(ToolCallStatus status, OutputItemStatus expected)
    {
        await Send(Call("c", "Read a.txt", status));

        Assert.Equal("c", Assert.Single(Of("started")).Id);
        var done = Assert.Single(Of("completed"));
        Assert.Equal("c", done.Id);
        Assert.Equal(expected, done.Status);
    }

    [Fact]
    public async Task A_finished_tool_call_followed_by_a_trailing_completed_update_is_completed_once()
    {
        await Send(Call("c", "Read a.txt", ToolCallStatus.Completed));
        await Send(Update("c", ToolCallStatus.Completed));

        Assert.Single(Of("completed"));
    }

    [Fact]
    public async Task A_pending_tool_call_is_not_completed_by_its_start()
    {
        await Send(Call("c", "Read a.txt", ToolCallStatus.InProgress));

        Assert.Empty(Of("completed"));
    }

    [Fact]
    public async Task A_new_prompt_forgets_which_calls_finished()
    {
        await Send(Call("c", "Run", ToolCallStatus.Completed));
        _adapter.Reset();
        await Send(Call("c", "Run", ToolCallStatus.Completed));

        Assert.Equal(2, Of("completed").Count());
    }

    [Fact]
    public async Task A_repeated_tool_call_is_an_update_not_a_second_step()
    {
        await Send(Call("c", "First"));
        await Send(Call("c", "Second"));

        Assert.Single(Of("started"));
        Assert.Equal("Second", Assert.Single(Of("updated")).Title);
    }

    [Fact]
    public async Task Two_calls_in_flight_stay_separate()
    {
        await Send(Call("a", "Read a"));
        await Send(Call("b", "Read b"));
        await Send(Update("b", ToolCallStatus.Completed));
        await Send(Update("a", ToolCallStatus.Completed));

        Assert.Equal(new[] { "b", "a" }, Of("completed").Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task A_trailing_update_without_a_title_keeps_the_title()
    {
        await Send(Call("c", "Edit Program.cs", kind: ToolKind.Edit));
        await Send(Update("c", ToolCallStatus.Completed));

        Assert.Equal("Edit Program.cs", Assert.Single(Of("completed")).Title);
    }

    [Fact]
    public async Task An_update_for_a_call_that_was_never_started_still_completes()
    {
        await Send(Update("ghost", ToolCallStatus.Completed, "Done"));

        Assert.Equal("ghost", Assert.Single(Of("completed")).Id);
    }

    [Fact]
    public async Task A_tool_call_closes_the_message_being_streamed()
    {
        await Send(new SessionUpdateAgentMessageChunk { Content = new ContentBlockText { Text = "Let me look." } });
        await Send(Call("c"));

        Assert.Equal(new[] { "started:AI", "updated:AI", "completed:AI", "started:Run" },
            _events.Select(e => $"{e.Kind}:{e.Item.ToolName}").ToArray());
    }

    [Fact]
    public async Task Text_after_a_tool_call_starts_a_new_message()
    {
        await Send(new SessionUpdateAgentMessageChunk { Content = new ContentBlockText { Text = "Before." } });
        await Send(Call("c"));
        await Send(new SessionUpdateAgentMessageChunk { Content = new ContentBlockText { Text = "After." } });

        var messages = _events.Where(e => e.Kind == "started" && e.Item.ToolName == "AI").Select(e => e.Item.Id).ToArray();
        Assert.Equal(2, messages.Distinct().Count());
    }

    // ---- diffs ----

    [Fact]
    public async Task A_completed_edit_fires_an_inline_diff_against_the_checkpoint()
    {
        _dir.Write("a.txt", "after");
        _checkpoints.Originals[_dir.Combine("a.txt")] = "before";

        await Send(Call("c", "Edit a.txt", ToolCallStatus.Completed, ToolKind.Edit, Diff(_dir.Combine("a.txt"), "after")));

        var diff = Assert.Single(_diffs);
        Assert.Equal(_dir.Combine("a.txt"), diff.FilePath);
        Assert.Equal("before", diff.OriginalContent);
        Assert.Equal("after", diff.ProposedContent);
        Assert.Equal(DiffSource.Agent, diff.Source);
    }

    [Fact]
    public async Task A_new_file_diffs_against_nothing()
    {
        _dir.Write("new.txt", "hello");

        await Send(Call("c", "Write new.txt", ToolCallStatus.Completed, ToolKind.Edit, Diff(_dir.Combine("new.txt"), "hello")));

        var diff = Assert.Single(_diffs);
        Assert.Equal("", diff.OriginalContent);
        Assert.Equal("hello", diff.ProposedContent);
    }

    [Fact]
    public async Task A_file_identical_to_the_checkpoint_fires_nothing()
    {
        // The edit is proposed but not applied yet (a permission prompt is open).
        _dir.Write("a.txt", "same");
        _checkpoints.Originals[_dir.Combine("a.txt")] = "same";

        await Send(Update("c", ToolCallStatus.InProgress, content: Diff(_dir.Combine("a.txt"), "different")));

        Assert.Empty(_diffs);
    }

    [Fact]
    public async Task A_diff_in_an_in_progress_update_fires_once_the_file_has_changed()
    {
        _dir.Write("a.txt", "changed");
        _checkpoints.Originals[_dir.Combine("a.txt")] = "orig";

        await Send(Update("c", ToolCallStatus.InProgress, content: Diff(_dir.Combine("a.txt"))));

        Assert.Single(_diffs);
    }

    [Fact]
    public async Task A_pending_tool_call_does_not_fire_a_diff()
    {
        _dir.Write("a.txt", "changed");
        _checkpoints.Originals[_dir.Combine("a.txt")] = "orig";

        await Send(Call("c", "Edit", ToolCallStatus.Pending, ToolKind.Edit, Diff(_dir.Combine("a.txt"))));

        Assert.Empty(_diffs);
    }

    [Fact]
    public async Task A_diff_for_a_missing_file_is_skipped_without_failing_the_turn()
    {
        await Send(Update("c", ToolCallStatus.Completed, content: Diff(_dir.Combine("nope.txt"))));

        Assert.Empty(_diffs);
        Assert.Single(Of("completed"));
    }

    [Fact]
    public async Task The_step_body_shows_the_diff()
    {
        _dir.Write("a.txt", "after");

        await Send(Call("c", "Edit a.txt", ToolCallStatus.Completed, ToolKind.Edit,
            new ToolCallContent[] { new ToolCallContentDiff { Path = _dir.Combine("a.txt"), OldText = "before", NewText = "after" } }));

        var body = Assert.Single(Of("started")).Body!;
        Assert.Contains("- before", body);
        Assert.Contains("+ after", body);
    }

    // ---- the files each completed step records (what "Revert to here" restores by default) ----

    [Fact]
    public async Task A_completed_edit_records_the_file_it_changed()
    {
        _dir.Write("a.txt", "after");
        _checkpoints.Originals[_dir.Combine("a.txt")] = "before";

        await Send(Call("c", "Edit a.txt", ToolCallStatus.Pending, ToolKind.Edit));
        await Send(Update("c", content: Diff(_dir.Combine("a.txt"), "after")));
        await Send(Update("c", ToolCallStatus.Completed));

        Assert.Equal(new[] { _dir.Combine("a.txt") }, Assert.Single(Of("completed")).ChangedFiles);
    }

    [Fact]
    public async Task A_rejected_edit_records_no_file()
    {
        await Send(Call("c", "Edit a.txt", ToolCallStatus.Pending, ToolKind.Edit, Diff(_dir.Combine("a.txt"), "proposed")));
        await Send(Update("c", ToolCallStatus.Failed));

        var step = Assert.Single(Of("completed"));
        Assert.NotNull(step.ChangedFiles);
        Assert.Empty(step.ChangedFiles!);
    }

    [Fact]
    public async Task Files_a_shell_command_reported_count_even_when_the_command_failed()
    {
        var readme = _dir.Combine("README.md").Replace(@"\", @"\\");
        await Send(Call("sh", "sed -i ...", ToolCallStatus.Pending, ToolKind.Execute));
        await Send(Update("sh", meta: Json("{\"claudeCode\":{\"toolResponse\":{\"bashEditDiff\":{\"changedFiles\":[\"" + readme + "\"]}}}}")));
        await Send(Update("sh", ToolCallStatus.Failed));

        Assert.Equal(new[] { _dir.Combine("README.md") }, Assert.Single(Of("completed")).ChangedFiles);
    }

    [Fact]
    public async Task A_completed_delete_records_the_files_it_named()
    {
        await Send(new SessionUpdateToolCall
        {
            ToolCallId = "d", Title = "Delete old.cs", Status = ToolCallStatus.Pending, Kind = ToolKind.Delete,
            Locations = new[] { new ToolCallLocation { Path = _dir.Combine("old.cs") } }
        });
        await Send(Update("d", ToolCallStatus.Completed));

        Assert.Equal(new[] { _dir.Combine("old.cs") }, Assert.Single(Of("completed")).ChangedFiles);
    }

    [Fact]
    public async Task A_new_prompt_forgets_the_files_of_the_previous_one()
    {
        await Send(Call("c", "Edit a.txt", ToolCallStatus.Pending, ToolKind.Edit, Diff(_dir.Combine("a.txt"), "x")));
        _adapter.Reset();
        await Send(Update("c", ToolCallStatus.Completed));

        Assert.Empty(Assert.Single(Of("completed")).ChangedFiles!);
    }

    // ---- changes the tool calls did not report (workspace scan) ----

    private RestorePreview Found(string[]? modified = null, string[]? removed = null, string[]? created = null) =>
        new() { Modified = modified ?? [], Recreated = removed ?? [], Deleted = created ?? [] };

    private async Task<List<DiffRequest>> DiffsAsync(int count)
    {
        for (var i = 0; i < 200; i++)
        {
            lock (_diffs)
                if (_diffs.Count >= count) return _diffs.ToList();
            await Task.Delay(25);
        }
        lock (_diffs) return _diffs.ToList();
    }

    [Fact]
    public async Task A_shell_command_that_removed_a_file_lists_it_as_deleted()
    {
        var gone = _dir.Combine("Handlers\\Old.cs");
        _checkpoints.Originals[gone] = "class Old {}";
        _checkpoints.Scan = Found(removed: new[] { gone });

        await Send(Call("rm", "rm Old.cs", ToolCallStatus.Pending, ToolKind.Execute));
        await Send(Update("rm", ToolCallStatus.Completed));

        var diff = Assert.Single(await DiffsAsync(1));
        Assert.Equal(gone, diff.FilePath);
        Assert.Equal(DiffKind.Deleted, diff.Kind);
        Assert.Equal("class Old {}", diff.OriginalContent);
        Assert.Equal("", diff.ProposedContent);
        Assert.Equal(DiffSource.Scan, diff.Source);
    }

    [Fact]
    public async Task A_shell_command_that_created_or_edited_files_lists_them()
    {
        _dir.Write("made.txt", "from a shell");
        _dir.Write("sed.txt", "after");
        _checkpoints.Originals[_dir.Combine("sed.txt")] = "before";
        _checkpoints.Absent.Add(_dir.Combine("made.txt"));
        _checkpoints.Scan = Found(modified: new[] { _dir.Combine("sed.txt") }, created: new[] { _dir.Combine("made.txt") });

        await Send(Call("sh", "run", ToolCallStatus.Completed, ToolKind.Execute));

        var diffs = await DiffsAsync(2);
        var made = Assert.Single(diffs, d => d.FilePath == _dir.Combine("made.txt"));
        Assert.Equal(DiffKind.Added, made.Kind);
        Assert.Equal("from a shell", made.ProposedContent);
        var edited = Assert.Single(diffs, d => d.FilePath == _dir.Combine("sed.txt"));
        Assert.Equal(DiffKind.Modified, edited.Kind);
        Assert.Equal("before", edited.OriginalContent);
        Assert.Equal("after", edited.ProposedContent);
        Assert.All(diffs, d => Assert.Equal(DiffSource.Scan, d.Source));
    }

    [Fact]
    public async Task A_created_file_is_only_listed_when_the_checkpoint_proves_it_was_absent()
    {
        _dir.Write("bin-output.txt", "x");
        _checkpoints.Scan = Found(created: new[] { _dir.Combine("bin-output.txt") }); // not in Absent

        await Send(Call("sh", "run", ToolCallStatus.Completed, ToolKind.Execute));
        await Task.Delay(300);

        Assert.Empty(_diffs);
    }

    [Fact]
    public async Task Binary_files_are_not_offered_for_undo_by_rewriting_their_text()
    {
        _dir.Write("blob.bin", "a\0b");
        _checkpoints.Originals[_dir.Combine("blob.bin")] = "a\0c";
        _checkpoints.Scan = Found(modified: new[] { _dir.Combine("blob.bin") }, removed: new[] { _dir.Combine("gone.bin") });
        _checkpoints.Originals[_dir.Combine("gone.bin")] = "x\0y";

        await Send(Call("sh", "run", ToolCallStatus.Completed, ToolKind.Execute));
        await Task.Delay(300);

        Assert.Empty(_diffs);
    }

    [Fact]
    public async Task Reads_searches_and_edits_do_not_trigger_a_scan_but_the_end_of_the_turn_does()
    {
        await Send(Call("r", "Read", ToolCallStatus.Completed, ToolKind.Read));
        await Send(Call("s", "Search", ToolCallStatus.Completed, ToolKind.Search));
        await Send(Call("e", "Edit", ToolCallStatus.Completed, ToolKind.Edit));
        await Task.Delay(200);
        Assert.Equal(0, _checkpoints.ScanCount);

        _adapter.Complete();
        for (var i = 0; i < 100 && _checkpoints.ScanCount == 0; i++) await Task.Delay(25);
        Assert.Equal(1, _checkpoints.ScanCount);
    }

    [Fact]
    public async Task The_same_change_is_not_reported_twice()
    {
        _dir.Write("a.txt", "after");
        _checkpoints.Originals[_dir.Combine("a.txt")] = "before";
        _checkpoints.Scan = Found(modified: new[] { _dir.Combine("a.txt") });

        await Send(Call("sh", "run", ToolCallStatus.Completed, ToolKind.Execute));
        await DiffsAsync(1);
        await Send(Call("sh2", "run again", ToolCallStatus.Completed, ToolKind.Execute));
        _adapter.Complete();
        await Task.Delay(400);

        Assert.Single(_diffs);
    }

    [Fact]
    public async Task A_file_the_edit_tool_already_reported_is_not_reported_again_by_the_scan()
    {
        _dir.Write("a.txt", "after");
        _checkpoints.Originals[_dir.Combine("a.txt")] = "before";
        _checkpoints.Scan = Found(modified: new[] { _dir.Combine("a.txt") });

        await Send(Call("e", "Edit a.txt", ToolCallStatus.Completed, ToolKind.Edit, Diff(_dir.Combine("a.txt"), "after")));
        await Send(Call("sh", "run", ToolCallStatus.Completed, ToolKind.Execute));
        await Task.Delay(400);

        Assert.Single(_diffs);
    }

    // ---- plan ----

    [Fact]
    public async Task A_plan_is_shown_as_a_checklist()
    {
        await Send(new SessionUpdatePlan
        {
            Entries = new[]
            {
                new PlanEntry { Content = "Write the test", Priority = PlanEntryPriority.High, Status = PlanEntryStatus.Completed },
                new PlanEntry { Content = "Run it", Priority = PlanEntryPriority.Medium, Status = PlanEntryStatus.InProgress },
                new PlanEntry { Content = "Ship", Priority = PlanEntryPriority.Low, Status = PlanEntryStatus.Pending }
            }
        });

        var plan = Assert.Single(Of("started"));
        Assert.Equal("Plan", plan.ToolName);
        Assert.Equal(OutputItemStatus.Info, plan.Status);
        Assert.Equal("- [x] Write the test\n- [ ] Run it\n- [ ] Ship", plan.Body);
    }

    // ---- session state ----

    [Fact]
    public async Task A_mode_update_raises_the_mode_event()
    {
        string? mode = null;
        _adapter.ModeUpdated += m => mode = m;

        await Send(new SessionUpdateCurrentModeUpdate { CurrentModeId = "plan" });

        Assert.Equal("plan", mode);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task A_command_list_update_raises_the_commands_event()
    {
        AvailableCommand[]? commands = null;
        _adapter.CommandsUpdated += c => commands = c;

        await Send(new SessionUpdateAvailableCommandsUpdate
        {
            AvailableCommands = new[] { new AvailableCommand { Name = "review", Description = "Review changes" } }
        });

        Assert.Equal("review", Assert.Single(commands!).Name);
    }

    [Fact]
    public async Task An_unknown_update_type_is_ignored()
    {
        await Send(new SessionUpdateUnknown { RawType = "from_the_future" });

        Assert.Empty(_events);
    }

    [Fact]
    public async Task Echoed_user_messages_are_ignored()
    {
        await Send(new SessionUpdateUserMessageChunk { Content = new ContentBlockText { Text = "hello" } });

        Assert.Empty(_events);
    }

    // ---- gating ----

    [Fact]
    public async Task Tool_calls_are_dropped_while_replaying_history()
    {
        _adapter.Mode = AdapterMode.Replay;

        await Send(Call("c"));
        await Send(Update("c", ToolCallStatus.Completed));

        Assert.Empty(_events);
    }

    [Fact]
    public async Task Tool_calls_after_the_prompt_was_cancelled_are_dropped()
    {
        using var cts = new CancellationTokenSource();
        _adapter.Reset(cts.Token);
        cts.Cancel();

        await Send(Call("c"));

        Assert.Empty(_events);
    }

    // ---- thinking and message boundaries ----

    [Fact]
    public async Task Thought_chunks_stream_into_one_thinking_step_that_completes_as_Thought()
    {
        await Send(new SessionUpdateAgentThoughtChunk { Content = new ContentBlockText { Text = "hm" } });
        await Send(new SessionUpdateAgentThoughtChunk { Content = new ContentBlockText { Text = "mm" } });
        _adapter.Complete();

        var started = Assert.Single(Of("started"));
        Assert.Equal("Thinking", started.ToolName);
        Assert.Equal(new[] { "hm", "mm" }, Of("updated").Select(i => i.Delta).ToArray());
        Assert.Equal("Thought", Assert.Single(Of("completed")).Title);
    }

    [Fact]
    public async Task A_new_agent_message_id_starts_a_new_message()
    {
        await Send(new SessionUpdateAgentMessageChunk { MessageId = "m1", Content = new ContentBlockText { Text = "One." } });
        await Send(new SessionUpdateAgentMessageChunk { MessageId = "m2", Content = new ContentBlockText { Text = "Two." } });
        _adapter.Complete();

        var started = Of("started").ToArray();
        Assert.Equal(2, started.Length);
        Assert.Equal(new[] { "m1", "m2" }, started.Select(s => s.AgentMessageId).ToArray());
        Assert.Equal(2, Of("completed").Count());
    }

    [Fact]
    public async Task Whitespace_only_text_does_not_open_a_message()
    {
        await Send(new SessionUpdateAgentMessageChunk { Content = new ContentBlockText { Text = "  \n" } });

        Assert.Empty(_events);
    }

    private sealed class StubCheckpoints : ICheckpointService
    {
        public Dictionary<string, string> Originals { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? GetFileAtLatestCheckpoint(string absolutePath) =>
            Originals.TryGetValue(absolutePath, out var text) ? text : null;

        /// <summary>Absolute paths the checkpoint proves did not exist yet.</summary>
        public HashSet<string> Absent { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>What a workspace scan finds (null = no checkpoint).</summary>
        public RestorePreview? Scan { get; set; }

        public int ScanCount;

        public bool WasAbsentAtLatestCheckpoint(string absolutePath) => Absent.Contains(absolutePath);

        public Task<RestorePreview?> PreviewChangesSinceLatestCheckpointAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref ScanCount);
            return Task.FromResult(Scan);
        }

        public Task<string?> CreateCheckpointAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task RestoreAsync(string checkpointId, IReadOnlyCollection<string>? onlyPaths = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RestorePreview> PreviewRestoreAsync(string checkpointId, IReadOnlyCollection<string>? onlyPaths = null, CancellationToken ct = default) =>
            Task.FromResult(new RestorePreview { Modified = [], Recreated = [], Deleted = [] });
        public Task DeleteCheckpointsAsync(IReadOnlyCollection<string> checkpointIds, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<string, string>> CloneCheckpointsAsync(IReadOnlyCollection<string> checkpointIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
    }

    private sealed class Listener : IOutputListener
    {
        private readonly List<(string Kind, OutputItem Item)> _events;

        public Listener(List<(string Kind, OutputItem Item)> events) => _events = events;

        public void OnStepStarted(OutputItem item) => _events.Add(("started", item with { }));
        public void OnStepUpdated(OutputItem item) => _events.Add(("updated", item with { }));
        public void OnStepCompleted(OutputItem item) => _events.Add(("completed", item with { }));
    }
}
