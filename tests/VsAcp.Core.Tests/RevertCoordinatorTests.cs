using VsAcp.Core.Models;
using VsAcp.Core.Services;

namespace VsAcp.Core.Tests;

/// <summary>Which files "Revert to here" counts as the agent's, from the stored history.</summary>
public class RevertCoordinatorTests
{
    private static PersistedMessage User(int ordinal, string checkpointId, bool tracks = true) =>
        new() { Ordinal = ordinal, ItemType = "User", CheckpointId = checkpointId, TracksChangedFiles = tracks };

    private static PersistedMessage Step(int ordinal, params string[] files) =>
        new() { Ordinal = ordinal, ItemType = "ToolStep", ChangedFiles = files.ToList() };

    [Fact]
    public void The_agents_files_are_those_its_tool_steps_reported_from_the_target_turn_on()
    {
        var messages = new[]
        {
            User(0, "c0"), Step(1, @"C:\ws\before.cs"),
            User(2, "c2"), Step(3, @"C:\ws\a.cs"), Step(4, @"C:\ws\b.cs", @"C:\ws\a.cs"),
            User(5, "c5"), Step(6, @"C:\ws\later.cs"),
        };

        var files = RevertCoordinator.AgentChangedFiles(messages, "c2");

        Assert.Equal(new[] { @"C:\ws\a.cs", @"C:\ws\b.cs", @"C:\ws\later.cs" }, files!.OrderBy(f => f).ToArray());
    }

    [Fact]
    public void Steps_without_files_and_other_messages_add_nothing()
    {
        var messages = new[]
        {
            User(0, "c0"),
            new PersistedMessage { Ordinal = 1, ItemType = "Assistant", Content = "done" },
            new PersistedMessage { Ordinal = 2, ItemType = "ToolStep" }, // e.g. a plan
            Step(3),
        };

        Assert.Empty(RevertCoordinator.AgentChangedFiles(messages, "c0")!);
    }

    [Fact]
    public void History_saved_before_files_were_recorded_makes_them_unknown()
    {
        var messages = new[] { User(0, "c0", tracks: false), User(2, "c2"), Step(3, @"C:\ws\a.cs") };

        Assert.Null(RevertCoordinator.AgentChangedFiles(messages, "c0"));
        // ...but a revert that only discards newer turns still knows them.
        Assert.NotNull(RevertCoordinator.AgentChangedFiles(messages, "c2"));
    }

    [Fact]
    public void An_unknown_target_makes_them_unknown()
    {
        Assert.Null(RevertCoordinator.AgentChangedFiles(new[] { User(0, "c0") }, "missing"));
    }

    // ---- where the agent session is forked ----

    private static PersistedMessage Reply(int ordinal, string? agentMessageId) =>
        new() { Ordinal = ordinal, ItemType = "Assistant", Content = "reply", AgentMessageId = agentMessageId };

    [Fact]
    public void The_fork_point_is_the_last_kept_reply()
    {
        var kept = new[] { User(0, "c0"), Reply(1, "m1"), Step(2), Reply(3, "m3"), User(4, "c4"), Reply(5, "m5") };

        Assert.Equal("m5", RevertCoordinator.ForkPoint(kept));
    }

    [Fact]
    public void A_compaction_after_the_last_reply_does_not_move_the_fork_point()
    {
        var kept = new[] { User(0, "c0"), Reply(1, "m1"), new PersistedMessage { Ordinal = 2, ItemType = "Compaction" } };

        Assert.Equal("m1", RevertCoordinator.ForkPoint(kept));
    }

    [Fact]
    public void A_kept_turn_that_ended_in_a_tool_call_or_thinking_has_no_fork_point()
    {
        Assert.Null(RevertCoordinator.ForkPoint(new[] { User(0, "c0"), Reply(1, "m1"), Step(2) }));
        Assert.Null(RevertCoordinator.ForkPoint(new[]
        {
            User(0, "c0"), Reply(1, "m1"), new PersistedMessage { Ordinal = 2, ItemType = "Thinking", AgentMessageId = "m2" }
        }));
    }

    [Fact]
    public void A_last_reply_without_an_agent_message_id_or_nothing_kept_has_no_fork_point()
    {
        Assert.Null(RevertCoordinator.ForkPoint(new[] { User(0, "c0"), Reply(1, "m1"), User(2, "c2"), Reply(3, null) }));
        Assert.Null(RevertCoordinator.ForkPoint(Array.Empty<PersistedMessage>()));
    }
}
