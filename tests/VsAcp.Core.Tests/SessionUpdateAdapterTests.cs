using AcpProtocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VsAcp.Core.Abstractions;
using VsAcp.Core.Acp.Handlers;
using VsAcp.Core.DependencyInjection;
using VsAcp.Core.Tests.Support;

namespace VsAcp.Core.Tests;

/// <summary>The adapter in isolation: ACP session updates in, listener callbacks out.</summary>
public sealed class SessionUpdateAdapterTests : IDisposable
{
    private readonly RecordingListener _listener = new();
    private readonly ServiceProvider _provider;
    private readonly SessionUpdateAdapter _adapter;

    public SessionUpdateAdapterTests()
    {
        var services = new ServiceCollection();
        services.AddVsAcpServices(options =>
        {
            options.WorkingDirectory = Path.GetTempPath();
            options.HasProjectDirectory = false;
        });
        services.AddSingleton(new TestLog());
        services.AddSingleton(typeof(ILogger<>), typeof(TestLogger<>));
        services.AddSingleton<IOutputListener>(_listener);
        _provider = services.BuildServiceProvider();
        _adapter = _provider.GetRequiredService<SessionUpdateAdapter>();
        _adapter.Reset();
    }

    public void Dispose() => _provider.Dispose();

    private Task Send(SessionUpdate update) =>
        _adapter.HandleAsync(new SessionNotification { SessionId = "s1", Update = update }, CancellationToken.None).AsTask();

    private static SessionUpdateCompactionUpdate Compaction(string id, CompactionStatus status, string? error = null, params string[] summary) =>
        new()
        {
            CompactionId = id,
            Status = status,
            Error = error,
            Summary = summary.Length == 0 ? null : summary.Select(s => (ContentBlock)new ContentBlockText { Text = s }).ToArray()
        };

    private static SessionUpdateCompactionSummaryChunk Chunk(string id, string text) =>
        new() { CompactionId = id, Content = new ContentBlockText { Text = text } };

    // ---- notices ----

    [Theory]
    [InlineData(NoticeSeverity.Info, OutputItemStatus.Info)]
    [InlineData(NoticeSeverity.Warning, OutputItemStatus.Warning)]
    [InlineData(NoticeSeverity.Error, OutputItemStatus.Error)]
    public async Task Notice_severity_maps_to_item_status(NoticeSeverity severity, OutputItemStatus expected)
    {
        await Send(new SessionUpdateNotice { Severity = severity, Title = "Heads up", Description = "details" });

        var item = Assert.Single(_listener.Started);
        Assert.Equal("Notice", item.ToolName);
        Assert.Equal("Heads up", item.Title);
        Assert.Equal(expected, item.Status);
        Assert.Equal("details", item.Body);
        Assert.Equal(OutputBodyMode.PlainText, item.BodyMode);
    }

    [Fact]
    public async Task Notice_without_a_description_has_no_body()
    {
        await Send(new SessionUpdateNotice { Severity = NoticeSeverity.Info, Title = "Hi", Description = "  " });

        Assert.Null(Assert.Single(_listener.Started).Body);
    }

    [Fact]
    public async Task Each_notice_is_its_own_item()
    {
        await Send(new SessionUpdateNotice { Severity = NoticeSeverity.Info, Title = "A" });
        await Send(new SessionUpdateNotice { Severity = NoticeSeverity.Info, Title = "A" });

        Assert.Equal(2, _listener.Started.Select(i => i.Id).Distinct().Count());
    }

    // ---- compaction ----

    [Fact]
    public async Task Compaction_in_progress_shows_a_pending_item()
    {
        await Send(Compaction("c1", CompactionStatus.InProgress));

        var item = Assert.Single(_listener.Started);
        Assert.Equal("Compaction", item.ToolName);
        Assert.Equal("Compacting context...", item.Title);
        Assert.Equal(OutputItemStatus.Pending, item.Status);
        Assert.Empty(_listener.Completed);
    }

    [Fact]
    public async Task Compaction_summary_chunks_accumulate_and_completion_finishes_the_item()
    {
        await Send(Compaction("c1", CompactionStatus.InProgress));
        await Send(Chunk("c1", "First part."));
        await Send(Chunk("c1", "Second part."));
        await Send(Compaction("c1", CompactionStatus.Completed));

        var done = Assert.Single(_listener.Completed);
        Assert.Equal("Context compacted", done.Title);
        Assert.Equal(OutputItemStatus.Success, done.Status);
        Assert.Equal("First part.Second part.", done.Body);
        Assert.Equal(Assert.Single(_listener.Started).Id, done.Id);
    }

    [Fact]
    public async Task Summary_blocks_on_completion_are_separated_by_a_blank_line()
    {
        await Send(Compaction("c1", CompactionStatus.Completed, null, "Block one.", "Block two."));

        Assert.Equal("Block one.\n\nBlock two.", Assert.Single(_listener.Completed).Body?.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Compaction_completion_may_carry_the_whole_summary()
    {
        await Send(Compaction("c1", CompactionStatus.InProgress));
        await Send(Compaction("c1", CompactionStatus.Completed, null, "All in one."));

        Assert.Equal("All in one.", Assert.Single(_listener.Completed).Body);
    }

    [Fact]
    public async Task A_compaction_first_seen_as_completed_is_created_and_completed()
    {
        await Send(Compaction("c9", CompactionStatus.Completed, null, "Done already."));

        var done = Assert.Single(_listener.Completed);
        Assert.Equal("Context compacted", done.Title);
        Assert.Equal("Done already.", done.Body);
    }

    [Fact]
    public async Task Failed_compaction_reports_the_error()
    {
        await Send(Compaction("c1", CompactionStatus.InProgress));
        await Send(Compaction("c1", CompactionStatus.Failed, "model overloaded"));

        var done = Assert.Single(_listener.Completed);
        Assert.Equal("Context compaction failed", done.Title);
        Assert.Equal(OutputItemStatus.Error, done.Status);
        Assert.Contains("**Error:** model overloaded", done.Body);
    }

    [Fact]
    public async Task Cancelled_compaction_is_informational()
    {
        await Send(Compaction("c1", CompactionStatus.InProgress));
        await Send(Compaction("c1", CompactionStatus.Cancelled));

        var done = Assert.Single(_listener.Completed);
        Assert.Equal("Context compaction cancelled", done.Title);
        Assert.Equal(OutputItemStatus.Info, done.Status);
    }

    [Fact]
    public async Task Late_chunks_for_a_finished_compaction_are_ignored()
    {
        await Send(Compaction("c1", CompactionStatus.InProgress));
        await Send(Compaction("c1", CompactionStatus.Completed, null, "Summary."));
        var updatesBefore = _listener.Updated.Count;

        await Send(Chunk("c1", "straggler"));

        Assert.Equal(updatesBefore, _listener.Updated.Count);
        Assert.Single(_listener.Completed);
    }

    [Fact]
    public async Task Compactions_survive_a_cancelled_prompt()
    {
        // A cancelled prompt closes the gate on turn content, but a compaction must still be able to finish.
        using var cts = new CancellationTokenSource();
        _adapter.Reset(cts.Token);
        await Send(Compaction("c1", CompactionStatus.InProgress));
        cts.Cancel();

        await Send(Compaction("c1", CompactionStatus.Completed, null, "Summary."));

        Assert.Equal(OutputItemStatus.Success, Assert.Single(_listener.Completed).Status);
    }

    [Fact]
    public async Task Reusing_a_compaction_id_after_a_reset_starts_a_new_item()
    {
        await Send(Compaction("c1", CompactionStatus.Completed, null, "One."));
        _adapter.ResetCompactions();
        await Send(Compaction("c1", CompactionStatus.Completed, null, "Two."));

        Assert.Equal(2, _listener.Completed.Count);
    }

    // ---- title ----

    [Fact]
    public async Task The_agents_session_title_is_reported_trimmed()
    {
        var titles = new List<string>();
        _adapter.TitleUpdated += titles.Add;

        await Send(new SessionUpdateSessionInfoUpdate { Title = "  Preact chat UI " });

        Assert.Equal("Preact chat UI", Assert.Single(titles));
        Assert.Empty(_listener.Started);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_cleared_or_blank_title_is_not_reported(string? title)
    {
        var titles = new List<string>();
        _adapter.TitleUpdated += titles.Add;

        await Send(new SessionUpdateSessionInfoUpdate { Title = title });

        Assert.Empty(titles);
    }

    [Fact]
    public async Task A_title_arriving_after_the_prompt_was_cancelled_is_still_reported()
    {
        using var cts = new CancellationTokenSource();
        _adapter.Reset(cts.Token);
        cts.Cancel();
        var titles = new List<string>();
        _adapter.TitleUpdated += titles.Add;

        await Send(new SessionUpdateSessionInfoUpdate { Title = "Late title" });

        Assert.Equal("Late title", Assert.Single(titles));
    }

    [Fact]
    public async Task Titles_are_dropped_while_replaying_history()
    {
        _adapter.Mode = AdapterMode.Replay;
        var titles = new List<string>();
        _adapter.TitleUpdated += titles.Add;

        await Send(new SessionUpdateSessionInfoUpdate { Title = "old" });

        Assert.Empty(titles);
    }

    // ---- replay ----

    [Fact]
    public async Task Notices_and_compactions_are_dropped_while_replaying_history()
    {
        _adapter.Mode = AdapterMode.Replay;

        await Send(new SessionUpdateNotice { Severity = NoticeSeverity.Error, Title = "old" });
        await Send(Compaction("c1", CompactionStatus.Completed, null, "old summary"));
        await Send(Chunk("c2", "old chunk"));

        Assert.Empty(_listener.Started);
        Assert.Empty(_listener.Updated);
        Assert.Empty(_listener.Completed);
    }

    [Fact]
    public async Task Usage_is_still_applied_while_replaying_history()
    {
        _adapter.Mode = AdapterMode.Replay;
        UsageUpdate? seen = null;
        _adapter.UsageUpdated += u => seen = u;

        await Send(new SessionUpdateUsageUpdate { Used = 10, Size = 100 });

        Assert.Equal(10UL, seen!.Used);
    }

    // ---- ordinary turn content ----

    [Fact]
    public async Task Message_chunks_stream_into_one_item_that_completes_on_Complete()
    {
        await Send(new SessionUpdateAgentMessageChunk { Content = new ContentBlockText { Text = "Hel" } });
        await Send(new SessionUpdateAgentMessageChunk { Content = new ContentBlockText { Text = "lo" } });
        _adapter.Complete();

        var started = Assert.Single(_listener.Started);
        Assert.Equal("AI", started.ToolName);
        Assert.Equal(started.Id, Assert.Single(_listener.Completed).Id);
    }

    private sealed class RecordingListener : IOutputListener
    {
        public List<OutputItem> Started { get; } = new();
        public List<OutputItem> Updated { get; } = new();
        public List<OutputItem> Completed { get; } = new();

        // Items are mutable records, so keep a snapshot of what was reported at the time of the call.
        public void OnStepStarted(OutputItem item) { lock (Started) Started.Add(item with { }); }
        public void OnStepUpdated(OutputItem item) { lock (Updated) Updated.Add(item with { }); }
        public void OnStepCompleted(OutputItem item) { lock (Completed) Completed.Add(item with { }); }
    }
}
