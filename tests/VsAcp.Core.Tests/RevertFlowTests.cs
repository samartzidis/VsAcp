using VsAcp.Core.Abstractions;
using VsAcp.Core.Services;
using VsAcp.Core.Tests.Support;

namespace VsAcp.Core.Tests;

/// <summary>Checkpoint + "Revert to here" end to end: agent edits files, user reverts, conversation is rebuilt.</summary>
public class RevertFlowTests
{
    private static object Scenario() => new { turns = Turns() };

    private static object[] Turns() => new object[]
    {
        new
        {
            when = "one",
            steps = new object[]
            {
                new { write = new { path = "a.txt", content = "one" } },
                new { say = "wrote one" }
            }
        },
        new
        {
            when = "two",
            steps = new object[]
            {
                new { write = new { path = "a.txt", content = "two" } },
                new { write = new { path = "new.txt", content = "created by two" } },
                new { say = "wrote two" }
            }
        },
        new { steps = new object[] { new { say = "ok" } } }
    };

    [Fact]
    public async Task Files_written_by_the_agent_through_the_client_land_in_the_workspace()
    {
        using var h = await ChatHarness.CreateAsync(Scenario());

        await h.SendAsync("one");

        Assert.Equal("one", h.ReadFile("a.txt"));
    }

    [Fact]
    public async Task Revert_restores_files_and_truncates_the_conversation()
    {
        using var h = await ChatHarness.CreateAsync(Scenario());
        h.WriteFile("a.txt", "orig");

        await h.SendAsync("one");
        await h.SendAsync("two");
        Assert.Equal("two", h.ReadFile("a.txt"));
        Assert.True(h.FileExists("new.txt"));

        var reverted = await h.RevertToUserMessageAsync(1);

        Assert.Equal("two", reverted!.Content);
        Assert.Equal("one", h.ReadFile("a.txt"));
        Assert.False(h.FileExists("new.txt"));
        Assert.Equal(new[] { "one", "wrote one" }, h.Messages.Select(m => m.Content).ToArray());

        var persisted = await h.PersistedAsync();
        Assert.Equal(new[] { "one", "wrote one" }, persisted.Select(p => p.Content).ToArray());

        await h.RevertToUserMessageAsync(0);

        Assert.Equal("orig", h.ReadFile("a.txt"));
        Assert.Empty(h.Messages);
    }

    [Fact]
    public async Task Side_effects_made_directly_on_disk_are_reverted_too()
    {
        // Terminal-style changes: the agent edits and deletes files itself, bypassing fs/write_text_file.
        using var h = await ChatHarness.CreateAsync(new
        {
            turns = new[]
            {
                new
                {
                    steps = new object[]
                    {
                        new { disk = new { path = "made.txt", content = "from a shell" } },
                        new { disk = new { path = "keep.txt", content = "changed" } },
                        new { deleteDisk = "gone.txt" },
                        new { say = "done" }
                    }
                }
            }
        });
        h.WriteFile("keep.txt", "original");
        h.WriteFile("gone.txt", "precious");

        await h.SendAsync("shell stuff");
        Assert.True(h.FileExists("made.txt"));
        Assert.False(h.FileExists("gone.txt"));

        await h.RevertToUserMessageAsync(0);

        Assert.False(h.FileExists("made.txt"));
        Assert.Equal("original", h.ReadFile("keep.txt"));
        Assert.Equal("precious", h.ReadFile("gone.txt"));
    }

    // The agent edits a.txt with a tool call that reports the diff, and its command also leaves side.txt behind
    // without saying so.
    private static object ReportedEditScenario() => new
    {
        turns = new[]
        {
            new
            {
                steps = new object[]
                {
                    new { update = new { sessionUpdate = "tool_call", toolCallId = "e", title = "Edit a.txt", kind = "edit", status = "pending" } },
                    new { disk = new { path = "a.txt", content = "agent" } },
                    new { disk = new { path = "side.txt", content = "side effect" } },
                    new { update = new
                    {
                        sessionUpdate = "tool_call_update",
                        toolCallId = "e",
                        status = "completed",
                        content = new[] { new { type = "diff", path = "{cwd}\\a.txt", newText = "agent" } }
                    } },
                    new { say = "done" }
                }
            }
        }
    };

    [Fact]
    public async Task By_default_a_revert_restores_only_the_files_the_agent_reported()
    {
        using var h = await ChatHarness.CreateAsync(ReportedEditScenario());
        h.WriteFile("a.txt", "orig");
        h.WriteFile("mine.txt", "orig");

        await h.SendAsync("edit it");
        h.WriteFile("mine.txt", "my own edit");

        var preview = await h.PreviewRevertAsync(0);
        Assert.True(preview.AgentFilesKnown);
        Assert.Equal(new[] { "a.txt" }, preview.Agent.Modified.ToArray());
        Assert.Equal(1, preview.Agent.TotalCount);
        Assert.Equal(new[] { "mine.txt" }, preview.Other.Modified.ToArray());
        Assert.Equal(new[] { "side.txt" }, preview.Other.Deleted.ToArray());

        await h.RevertToUserMessageAsync(0, includeOther: false);

        Assert.Equal("orig", h.ReadFile("a.txt"));
        Assert.Equal("my own edit", h.ReadFile("mine.txt"));
        Assert.Equal("side effect", h.ReadFile("side.txt"));
        Assert.Empty(h.Messages);
    }

    [Fact]
    public async Task Also_reverting_other_changes_restores_every_file()
    {
        using var h = await ChatHarness.CreateAsync(ReportedEditScenario());
        h.WriteFile("a.txt", "orig");
        h.WriteFile("mine.txt", "orig");

        await h.SendAsync("edit it");
        h.WriteFile("mine.txt", "my own edit");

        await h.RevertToUserMessageAsync(0, includeOther: true);

        Assert.Equal("orig", h.ReadFile("a.txt"));
        Assert.Equal("orig", h.ReadFile("mine.txt"));
        Assert.False(h.FileExists("side.txt"));
    }

    [Fact]
    public async Task The_files_a_step_changed_are_saved_with_it()
    {
        using var h = await ChatHarness.CreateAsync(ReportedEditScenario());
        h.WriteFile("a.txt", "orig");

        await h.SendAsync("edit it");

        var persisted = await h.PersistedAsync();
        Assert.True(Assert.Single(persisted, p => p.ItemType == "User").TracksChangedFiles);
        Assert.Equal(new[] { h.PathOf("a.txt") }, Assert.Single(persisted, p => p.ItemType == "ToolStep").ChangedFiles);
    }

    [Fact]
    public async Task After_a_revert_the_first_send_carries_the_prior_conversation_once()
    {
        using var h = await ChatHarness.CreateAsync(Scenario());

        await h.SendAsync("one");
        await h.SendAsync("two");
        await h.RevertToUserMessageAsync(1);

        await h.SendAsync("three");
        await h.SendAsync("four");

        var prompts = h.PromptTexts();
        Assert.Equal(4, prompts.Count);
        Assert.DoesNotContain("<prior_conversation_context>", prompts[0]);
        Assert.DoesNotContain("<prior_conversation_context>", prompts[1]);
        Assert.Contains("<prior_conversation_context>", prompts[2]);
        Assert.Contains("wrote one", prompts[2]);
        Assert.DoesNotContain("wrote two", prompts[2]);
        Assert.EndsWith("three", prompts[2]);
        Assert.DoesNotContain("<prior_conversation_context>", prompts[3]);

        // The revert drops the agent's session (this agent cannot fork it); the next send starts a fresh one.
        Assert.Equal(2, h.Received("session/new").Count);
        Assert.Empty(h.Received("session/fork"));
    }

    [Fact]
    public async Task Stopping_the_first_send_while_the_agent_starts_keeps_the_prior_conversation()
    {
        // 2026-10-25 in VS: after a revert the first message waited ~14 s for a cold agent start, Stop was pressed,
        // and the next message reached the agent without the conversation ("it reset the session"). The wait is
        // now only session/new on the running agent, but a Stop during it must still keep the context.
        using var h = await ChatHarness.CreateAsync(new
        {
            answerDelayMs = new Dictionary<string, int> { ["session/new"] = 1500 },
            turns = Turns()
        });
        await h.SendAsync("one");
        await h.SendAsync("two");
        await h.RevertToUserMessageAsync(1);

        using (var turn = new CancellationTokenSource())
        {
            var sending = h.SendAsync("three", turn.Token);
            await h.WaitUntilAsync(() => h.Received("session/new").Count == 2, "the new agent to receive session/new");
            h.Stop(turn);
            Assert.Equal(TurnResult.Cancelled, await sending);
        }
        await h.SendAsync("four");

        var prompts = h.PromptTexts();
        Assert.Equal(3, prompts.Count);
        Assert.Contains("<prior_conversation_context>", prompts[2]);
        Assert.Contains("wrote one", prompts[2]);
        Assert.EndsWith("four", prompts[2]);
    }

    [Fact]
    public async Task A_revert_to_a_checkpoint_this_chat_does_not_own_changes_nothing()
    {
        // 2026-10-25 in VS: a Revert click in one tab also ran in every other open tab. There the checkpoint was
        // no message of the chat, and the revert restored every file and deleted the chat's whole history.
        using var h = await ChatHarness.CreateAsync(Scenario());
        await h.SendAsync("one");
        await h.SendAsync("two");
        var otherChats = await h.Checkpoints.CreateCheckpointAsync(); // as another tab's message would own it
        h.WriteFile("a.txt", "edited after that checkpoint");
        var persisted = await h.PersistedAsync();
        var shown = h.Messages.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.RevertToCheckpointAsync(otherChats!));

        Assert.Equal("edited after that checkpoint", h.ReadFile("a.txt"));
        Assert.Equal(persisted.Count, (await h.PersistedAsync()).Count);
        Assert.Equal(shown, h.Messages.Count);
        await h.SendAsync("three");
        Assert.Single(h.Received("session/new"));
        Assert.DoesNotContain("<prior_conversation_context>", h.PromptTexts().Last());
    }

    // ---- the agent process survives a revert ----

    private static object SupportsClose() => new { loadSession = false, sessionCapabilities = new { close = new { } } };

    [Fact]
    public async Task After_a_revert_the_next_send_opens_a_new_session_on_the_same_agent()
    {
        // A revert used to kill the agent, so the next message paid a cold start (6-15 s with npx).
        using var h = await ChatHarness.CreateAsync(Scenario());
        await h.SendAsync("one");
        await h.SendAsync("two");
        await h.RevertToUserMessageAsync(1);

        await h.SendAsync("three");

        Assert.Single(h.AgentProcessIds());
        Assert.Single(h.Received("initialize"));
        Assert.Equal(2, h.Received("session/new").Count);
        Assert.Equal("fake-session-2", (string?)h.Received("session/prompt").Last()["sessionId"]);
        Assert.Contains("wrote one", h.PromptTexts().Last());
        Assert.EndsWith("three", h.PromptTexts().Last());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_revert_closes_the_old_session_when_the_agent_supports_it(bool supportsClose)
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            agentCapabilities = supportsClose ? SupportsClose() : new { loadSession = false },
            turns = Turns()
        });
        await h.SendAsync("one");
        await h.RevertToUserMessageAsync(0);

        await h.SendAsync("again");

        if (supportsClose)
            Assert.Equal("fake-session-1", (string?)Assert.Single(h.Received("session/close"))["sessionId"]);
        else
            Assert.Empty(h.Received("session/close"));
    }

    [Fact]
    public async Task Late_updates_from_the_closed_session_do_not_reach_the_chat()
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            agentCapabilities = SupportsClose(),
            updatesOnClose = new object[]
            {
                new { sessionUpdate = "usage_update", used = 999, size = 1000 },
                new { sessionUpdate = "session_info_update", title = "STALE TITLE" }
            },
            turns = Turns()
        });
        await h.SendAsync("one");
        await h.SendAsync("two");
        await h.RevertToUserMessageAsync(1);
        await h.WaitUntilAsync(() => h.AgentLog().Any(e => (string?)e["dir"] == "out" && e.ToJsonString().Contains("STALE TITLE")),
            "the agent to send its late updates");

        await h.SendAsync("three"); // everything the agent sent before this turn's answers has been handled

        Assert.Null(h.Service.Usage);
        Assert.NotEqual("STALE TITLE", (await h.IndexEntryAsync()).Title);
    }

    [Fact]
    public async Task A_running_agent_that_does_not_open_a_new_session_in_time_is_restarted()
    {
        // The process may look alive while the agent is wedged (a launcher holding the pipes); the old kill on
        // revert recovered that, so the cheap path must give up and restart.
        using var h = await ChatHarness.CreateAsync(new
            {
                answerDelayMs = new Dictionary<string, int> { ["session/new"] = 1500 },
                turns = Turns()
            },
            o => o.NewSessionOnRunningAgentTimeout = TimeSpan.FromMilliseconds(300));
        await h.SendAsync("one");
        await h.SendAsync("two");
        await h.RevertToUserMessageAsync(1);

        var result = await h.SendWithinAsync("three");

        Assert.Equal(TurnResult.Completed, result);
        var pids = h.AgentProcessIds();
        Assert.Equal(2, pids.Count);
        Assert.False(ChatHarness.IsRunning(pids[0]));
        Assert.Contains("wrote one", h.PromptTexts().Last());
        Assert.EndsWith("three", h.PromptTexts().Last());
    }

    [Fact]
    public async Task Notices_and_compactions_are_left_out_of_the_prior_conversation_context()
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            turns = new object[]
            {
                new
                {
                    when = "first",
                    steps = new object[]
                    {
                        new { update = new { sessionUpdate = "notice", severity = "info", title = "NOTICE-MARKER" } },
                        new { update = new { sessionUpdate = "compaction_update", compactionId = "c1", status = "in_progress" } },
                        new { update = new { sessionUpdate = "compaction_summary_chunk", compactionId = "c1", content = new { type = "text", text = "SUMMARY-MARKER" } } },
                        new { update = new { sessionUpdate = "compaction_update", compactionId = "c1", status = "completed" } },
                        new { say = "first answer" }
                    }
                },
                new { steps = new object[] { new { say = "ok" } } }
            }
        });

        await h.SendAsync("first");
        await h.SendAsync("second");
        await h.RevertToUserMessageAsync(1);
        await h.SendAsync("third");

        var prefixed = h.PromptTexts().Last();
        Assert.Contains("<prior_conversation_context>", prefixed);
        Assert.Contains("first answer", prefixed);
        Assert.DoesNotContain("NOTICE-MARKER", prefixed);
        Assert.DoesNotContain("SUMMARY-MARKER", prefixed);

        // ...but the compaction itself is still part of the persisted transcript.
        Assert.Contains(await h.PersistedAsync(), p => p.ItemType == "Compaction");
    }

    [Fact]
    public async Task Reverting_to_the_first_message_sends_no_prefix()
    {
        using var h = await ChatHarness.CreateAsync(Scenario());

        await h.SendAsync("one");
        await h.RevertToUserMessageAsync(0);
        await h.SendAsync("again");

        Assert.DoesNotContain("<prior_conversation_context>", h.PromptTexts().Last());
    }

    // ---- an agent that can fork its session keeps the real conversation ----

    private static object CanFork() => new { loadSession = false, sessionCapabilities = new { fork = new { }, close = new { } } };

    // Like claude-agent-acp, every reply carries the agent's message id.
    private static object[] ForkTurns() => new object[]
    {
        new
        {
            when = "one",
            steps = new object[]
            {
                new { write = new { path = "a.txt", content = "one" } },
                new { say = "wrote one", messageId = "m-one" }
            }
        },
        new
        {
            when = "two",
            steps = new object[]
            {
                new { write = new { path = "a.txt", content = "two" } },
                new { say = "wrote two", messageId = "m-two" }
            }
        },
        new { steps = new object[] { new { say = "ok", messageId = "m-ok" } } }
    };

    private static string? ForkPointOf(System.Text.Json.Nodes.JsonObject forkRequest) =>
        (string?)forkRequest["_meta"]?["jetbrains"]?["air"]?["fork"]?["messageId"];

    [Fact]
    public async Task A_revert_forks_the_agent_session_at_the_last_kept_reply_when_the_agent_can()
    {
        using var h = await ChatHarness.CreateAsync(new { agentCapabilities = CanFork(), turns = ForkTurns() });
        h.WriteFile("a.txt", "orig");
        await h.SendAsync("one");
        await h.SendAsync("two");

        await h.RevertToUserMessageAsync(1);

        var fork = Assert.Single(h.Received("session/fork"));
        Assert.Equal("fake-session-1", (string?)fork["sessionId"]);
        Assert.Equal("m-one", ForkPointOf(fork));
        Assert.Equal("one", h.ReadFile("a.txt"));
        // The fork is a session of its own, saved at once, so a VS restart before the next message loads it.
        Assert.Equal("fake-session-2", (await h.IndexEntryAsync()).AcpSessionId);

        await h.SendAsync("three");

        var prompt = h.Received("session/prompt").Last();
        Assert.Equal("fake-session-2", (string?)prompt["sessionId"]);
        Assert.Equal("three", h.PromptTexts().Last());
        Assert.Single(h.Received("session/new"));
        Assert.Equal("fake-session-1", (string?)Assert.Single(h.Received("session/close"))["sessionId"]);
        Assert.Single(h.AgentProcessIds());
    }

    [Fact]
    public async Task A_forked_session_is_forked_again_by_the_next_revert()
    {
        // The copy keeps the agent's message ids, so a revert in it finds its fork point there.
        using var h = await ChatHarness.CreateAsync(new { agentCapabilities = CanFork(), turns = ForkTurns() });
        await h.SendAsync("one");
        await h.SendAsync("two");
        await h.RevertToUserMessageAsync(1);
        await h.SendAsync("two again");

        await h.RevertToUserMessageAsync(1);
        await h.SendAsync("three");

        var forks = h.Received("session/fork");
        Assert.Equal(2, forks.Count);
        Assert.Equal("fake-session-2", (string?)forks[1]["sessionId"]);
        Assert.Equal("m-one", ForkPointOf(forks[1]));
        Assert.Equal("fake-session-3", (string?)h.Received("session/prompt").Last()["sessionId"]);
        Assert.DoesNotContain(h.PromptTexts(), p => p.Contains("<prior_conversation_context>"));
    }

    [Fact]
    public async Task A_fork_the_agent_refuses_falls_back_to_the_prior_conversation_context()
    {
        using var h = await ChatHarness.CreateAsync(new { agentCapabilities = CanFork(), failFork = true, turns = ForkTurns() });
        await h.SendAsync("one");
        await h.SendAsync("two");

        await h.RevertToUserMessageAsync(1);

        Assert.Single(h.Received("session/fork"));
        Assert.Null((await h.IndexEntryAsync()).AcpSessionId);

        await h.SendAsync("three");

        Assert.Equal("fake-session-2", (string?)h.Received("session/prompt").Last()["sessionId"]);
        Assert.Contains("<prior_conversation_context>", h.PromptTexts().Last());
        Assert.Contains("wrote one", h.PromptTexts().Last());
        Assert.EndsWith("three", h.PromptTexts().Last());
    }

    [Fact]
    public async Task A_fork_that_does_not_answer_in_time_falls_back_to_the_prior_conversation_context()
    {
        using var h = await ChatHarness.CreateAsync(new
            {
                agentCapabilities = CanFork(),
                answerDelayMs = new Dictionary<string, int> { ["session/fork"] = 1500 },
                turns = ForkTurns()
            },
            o => o.NewSessionOnRunningAgentTimeout = TimeSpan.FromMilliseconds(300));
        await h.SendAsync("one");
        await h.SendAsync("two");

        await h.RevertToUserMessageAsync(1);
        var result = await h.SendWithinAsync("three");

        Assert.Equal(TurnResult.Completed, result);
        Assert.Contains("<prior_conversation_context>", h.PromptTexts().Last());
        Assert.Contains("wrote one", h.PromptTexts().Last());
    }

    [Fact]
    public async Task A_kept_turn_without_a_reply_id_is_not_forked()
    {
        using var h = await ChatHarness.CreateAsync(new { agentCapabilities = CanFork(), turns = Turns() });
        await h.SendAsync("one");
        await h.SendAsync("two");

        await h.RevertToUserMessageAsync(1);
        await h.SendAsync("three");

        Assert.Empty(h.Received("session/fork"));
        Assert.Contains("wrote one", h.PromptTexts().Last());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_fork_suffix_of_an_agent_title_is_dropped_when_the_agent_forks(bool canFork)
    {
        // The Claude Agent SDK names a fork "<title> (fork)", and claude-agent-acp publishes that title.
        using var h = await ChatHarness.CreateAsync(new
        {
            agentCapabilities = canFork ? CanFork() : new { loadSession = false },
            turns = new object[]
            {
                new { steps = new object[]
                {
                    new { update = new { sessionUpdate = "session_info_update", title = "Pelican facts (fork) (fork)" } },
                    new { say = "ok", messageId = "m-ok" }
                } }
            }
        });

        await h.SendAsync("hi");

        Assert.Equal(canFork ? "Pelican facts" : "Pelican facts (fork) (fork)", (await h.IndexEntryAsync()).Title);
    }
}
