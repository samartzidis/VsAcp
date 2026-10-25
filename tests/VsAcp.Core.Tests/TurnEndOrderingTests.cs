using VsAcp.Core.Abstractions;
using VsAcp.Core.Models;
using VsAcp.Core.Services;
using VsAcp.Core.Tests.Support;

namespace VsAcp.Core.Tests;

/// <summary>
/// A reply that arrives as a burst of chunks must be persisted whole, whatever the UI thread has queued when the
/// turn ends. Found in a real chat (2026-10-21): the agent sent 1,240 characters in 82 chunks within 100 ms, all
/// reached the adapter, but the saved message (and so the reloaded chat) stopped after 639.
/// </summary>
public class TurnEndOrderingTests
{
    private const int Chunks = 300;

    private static string Chunk(int i) => $"word{i} ";

    private static object Scenario() => new
    {
        turns = new[]
        {
            new { steps = Enumerable.Range(0, Chunks).Select(i => (object)new { say = Chunk(i) }).ToArray() }
        }
    };

    private static string Expected() => string.Concat(Enumerable.Range(0, Chunks).Select(Chunk));

    [Fact]
    public async Task Without_a_ui_thread_the_burst_is_whole_in_memory_and_on_disk()
    {
        using var h = await ChatHarness.CreateAsync(Scenario());

        Assert.Equal(TurnResult.Completed, await h.SendWithinAsync("go", timeoutMs: 30000));

        Assert.Equal(Expected(), h.Messages.Single(m => m.Type == ChatItemType.Assistant).Content);
        Assert.Equal(Expected(), (await h.PersistedAsync()).Single(p => p.ItemType == "Assistant").Content);
    }

    // Before the controller's SerialDispatcher this saved 36-543 of 2290 chars: the turn's loop drained the buffered
    // chunks without yielding, so Complete() ran on the UI thread and the view-model's Dispatch applied CompleteStep
    // inline - the persister saved the message before the queued chunk updates had landed.
    [Theory]
    [InlineData(false)] // one first-in-first-out queue
    [InlineData(true)]  // continuations (normal priority) overtake queued background updates
    public async Task A_busy_ui_thread_does_not_cut_the_saved_reply(bool normalOvertakesBackground)
    {
        using var ui = new SimulatedUiThread(normalOvertakesBackground, backgroundCostMs: 2);
        using var h = await ChatHarness.CreateAsync(Scenario(), uiThread: ui);

        Assert.Equal(TurnResult.Completed, await h.SendWithinAsync("go", timeoutMs: 30000));

        // the view model's backlog drains after the turn; the in-memory transcript always ends up whole
        await h.WaitUntilAsync(
            () => h.Messages.Any(m => m.Type == ChatItemType.Assistant && m.Content == Expected()),
            "the whole reply in the in-memory transcript", timeoutMs: 30000);

        // ...and so must the saved copy (this is what a restart or a reopened tab shows)
        var saved = (await h.PersistedAsync()).Single(p => p.ItemType == "Assistant").Content;
        Assert.True(Expected() == saved,
            $"saved reply is {saved.Length} of {Expected().Length} characters (ends with \"{saved.Substring(Math.Max(0, saved.Length - 20))}\")");
    }

    [Fact]
    public async Task The_stopped_line_goes_behind_agent_output_that_is_still_queued()
    {
        // Deterministic: the agent's tool call is queued (it came from the reader thread and the UI thread has not
        // got to it yet) when Stop ends the turn on the UI thread, where the host would run the notice inline.
        var onUiThread = true;
        var held = new Queue<Action>();
        var session = new ChatSession();
        ChatSessionController controller = null!;
        var chat = new StoppedChatService(() =>
        {
            onUiThread = false;
            controller.OnStepStarted(new OutputItem { Id = "build", ToolName = "Run build", Title = "Run build", Status = OutputItemStatus.Pending });
            onUiThread = true;
        });
        controller = new ChatSessionController(session, chat, null, null, null,
            action => { if (onUiThread) action(); else held.Enqueue(action); });

        Assert.Equal(TurnResult.Cancelled, await controller.SendAsync(new TurnSubmission("go"), CancellationToken.None));
        while (held.Count > 0) held.Dequeue()();

        Assert.Equal(new[] { "go", "Run build", "_Processing stopped._" },
            session.Messages.Select(m => m.Type == ChatItemType.ToolStep ? m.Title : m.Content).ToArray());
    }

    /// <summary>A chat service whose turn produces some agent output, then is stopped.</summary>
    private sealed class StoppedChatService(Action duringTurn) : IChatService
    {
        public async IAsyncEnumerable<string> SendMessageAsync(string userMessage, IReadOnlyList<ImageAttachment>? images = null,
            IReadOnlyList<string>? filePaths = null, IReadOnlyList<string>? folderPaths = null, IReadOnlyList<CodeSelection>? selections = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            duringTurn();
            throw new OperationCanceledException();
#pragma warning disable CS0162 // an iterator needs a yield
            yield break;
#pragma warning restore CS0162
        }

        public Task<string> GenerateTitleAsync(string userMessage, CancellationToken cancellationToken = default) => Task.FromResult("title");
        public event Action<string>? TitleChanged { add { } remove { } }
        public void ClearHistory() { }
        public void ResetSession() { }
        public Task InitializeWithContextAsync(IReadOnlyList<PersistedMessage> messages, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Cancel() { }
        public decimal? GetSessionCost() => null;
        public AcpProtocol.UsageUpdate? Usage => null;
        public event Action? UsageChanged { add { } remove { } }
        public IReadOnlyList<AcpProtocol.SessionConfigOption> ConfigOptions => [];
        public Task SetConfigOptionAsync(string configId, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public event Action? ConfigOptionsChanged { add { } remove { } }
        public IReadOnlyList<AcpProtocol.AvailableCommand> AvailableCommands => [];
        public event Action? CommandsChanged { add { } remove { } }
        public IReadOnlyList<AcpProtocol.SessionMode> AvailableModes => [];
        public string? CurrentModeId => null;
        public Task SetModeAsync(string modeId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public event Action? ModesChanged { add { } remove { } }
    }
}
