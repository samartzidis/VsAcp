using VsAcp.Core.Abstractions;
using VsAcp.Core.Models;
using VsAcp.Core.Services;
using VsAcp.Core.Tests.Support;

namespace VsAcp.Core.Tests;

/// <summary>The controller's own contract; its main paths are also exercised end to end through <see cref="ChatHarness"/>.</summary>
public class ChatSessionControllerTests
{
    private static ChatSessionController Bare(ChatSession session) =>
        new(session, null, null, null, null, action => action());

    [Fact]
    public async Task Reopening_a_tab_restores_the_transcript_from_the_store()
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            turns = new[]
            {
                new
                {
                    steps = new object[]
                    {
                        new { say = "Looking." },
                        new { update = new { sessionUpdate = "tool_call", toolCallId = "t1", title = "Read a.txt", kind = "read", status = "completed" } },
                        new { say = "Done." }
                    }
                }
            }
        });
        await h.SendAsync("read a.txt");
        await h.FlushAsync();

        var reopened = new ChatSession();
        using var controller = Bare(reopened);
        controller.AttachStore(h.Store, h.Entry.Id);
        await controller.RestoreAsync();

        Assert.Equal(h.Messages.Select(m => (m.Id, m.Type, m.Content, m.Title)).ToArray(),
            reopened.Messages.Select(m => (m.Id, m.Type, m.Content, m.Title)).ToArray());
        Assert.Equal(h.Messages[0].CheckpointId, reopened.Messages[0].CheckpointId);
    }

    [Fact]
    public async Task Restoring_before_a_store_is_attached_does_nothing()
    {
        var session = new ChatSession();
        using var controller = Bare(session);

        await controller.RestoreAsync();
        await controller.FlushAsync();

        Assert.Empty(session.Messages);
    }

    [Fact]
    public async Task Session_index_changes_are_applied_in_order_after_the_message_writes()
    {
        using var h = await ChatHarness.CreateAsync();
        await h.SendAsync("hello");

        h.Controller.EnqueueSessionIndexMutation("test title", e => e.Title = "Renamed");
        h.Controller.EnqueueSessionIndexMutation("test acp id", e => e.AcpSessionId = "acp-1");
        await h.FlushAsync();

        var entry = (await h.Store.GetSessionIndexAsync()).Single(e => e.Id == h.Entry.Id);
        Assert.Equal("Renamed", entry.Title);
        Assert.Equal("acp-1", entry.AcpSessionId);
    }

    [Fact]
    public void Index_changes_before_a_store_is_attached_are_ignored()
    {
        using var controller = Bare(new ChatSession());

        controller.EnqueueSessionIndexMutation("early", _ => throw new InvalidOperationException("must not run"));
    }

    [Fact]
    public void Agent_steps_reach_the_session_only_through_the_dispatcher()
    {
        var session = new ChatSession();
        var queued = new List<Action>();
        using var controller = new ChatSessionController(session, null, null, null, null, queued.Add);

        controller.OnStepStarted(new OutputItem { Id = "s1", ToolName = "Read file", Title = "Read file" });

        Assert.Empty(session.Messages);
        queued.ForEach(a => a());
        Assert.Equal("s1", Assert.Single(session.Messages).Id);
    }

    [Fact]
    public void Stop_cancels_the_turn_token()
    {
        using var controller = Bare(new ChatSession());
        using var turn = new CancellationTokenSource();

        controller.Stop(turn);

        Assert.True(turn.IsCancellationRequested);
    }

    [Fact]
    public void Stop_tolerates_a_missing_or_already_disposed_token()
    {
        using var controller = Bare(new ChatSession());
        var turn = new CancellationTokenSource();
        turn.Dispose();

        controller.Stop(null);
        controller.Stop(turn);
    }

    [Fact]
    public async Task Reverting_without_a_checkpoint_service_is_refused()
    {
        using var controller = Bare(new ChatSession());

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RevertAsync("whatever", includeOther: true));
    }

    [Fact]
    public async Task A_revert_started_straight_after_a_turn_still_sees_its_last_message()
    {
        // The coordinator reads the store; without the flush the assistant reply may not have landed yet.
        using var h = await ChatHarness.CreateAsync();
        await h.SendAsync("one");
        await h.SendAsync("two");

        var reverted = await h.Controller.RevertAsync(h.Messages.Last(m => m.Type == ChatItemType.User).CheckpointId!, includeOther: true);

        Assert.Equal("two", reverted!.Content);
        Assert.Equal(new[] { "one", "OK" }, h.Messages.Select(m => m.Content).ToArray());
    }
}
