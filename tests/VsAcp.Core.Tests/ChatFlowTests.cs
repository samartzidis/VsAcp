using VsAcp.Core.Abstractions;
using VsAcp.Core.Services;
using VsAcp.Core.Tests.Support;

namespace VsAcp.Core.Tests;

/// <summary>Headless end-to-end: the real chat pipeline against the scripted fake agent process.</summary>
public class ChatFlowTests
{
    [Fact]
    public async Task Reply_streams_into_the_transcript_and_is_persisted()
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            turns = new[] { new { steps = new object[] { new { say = "Hello " }, new { say = "world" } } } }
        });

        var result = await h.SendAsync("hi there");

        Assert.Equal(TurnResult.Completed, result);
        var messages = h.Messages;
        Assert.Collection(messages,
            m => { Assert.Equal(ChatItemType.User, m.Type); Assert.Equal("hi there", m.Content); Assert.NotNull(m.CheckpointId); },
            m => { Assert.Equal(ChatItemType.Assistant, m.Type); Assert.Equal("Hello world", m.Content); });

        var persisted = await h.PersistedAsync();
        Assert.Equal(new[] { "User", "Assistant" }, persisted.Select(p => p.ItemType).ToArray());
        Assert.Equal("Hello world", persisted[1].Content);
        Assert.Equal(messages[0].CheckpointId, persisted[0].CheckpointId);
    }

    [Fact]
    public async Task Thinking_is_a_separate_message()
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            turns = new[] { new { steps = new object[] { new { think = "hmm" }, new { say = "answer" } } } }
        });

        await h.SendAsync("question");

        Assert.Equal(new[] { ChatItemType.User, ChatItemType.Thinking, ChatItemType.Assistant },
            h.Messages.Select(m => m.Type).ToArray());
    }

    [Fact]
    public async Task Client_advertises_its_capabilities_and_the_workspace()
    {
        using var h = await ChatHarness.CreateAsync();

        await h.SendAsync("hello");

        var init = Assert.Single(h.Received("initialize"));
        var caps = init["clientCapabilities"]!;
        Assert.NotNull(caps["session"]!["notices"]);
        Assert.NotNull(caps["session"]!["compaction"]);
        Assert.True((bool)caps["fs"]!["writeTextFile"]!);

        var created = Assert.Single(h.Received("session/new"));
        Assert.Equal(Path.GetFullPath(h.Workspace).TrimEnd('\\'), Path.GetFullPath((string)created["cwd"]!).TrimEnd('\\'),
            ignoreCase: true);
    }

    [Fact]
    public async Task Notice_is_shown_but_transient_and_not_persisted()
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            turns = new[]
            {
                new
                {
                    steps = new object[]
                    {
                        new { update = new { sessionUpdate = "notice", severity = "warning", title = "Rate limited", description = "Retrying soon" } },
                        new { say = "done" }
                    }
                }
            }
        });

        await h.SendAsync("go");

        var notice = Assert.Single(h.Messages, m => m.Type == ChatItemType.Notice);
        Assert.True(notice.IsTransient);
        Assert.Equal("Rate limited", notice.Title);
        Assert.Equal(OutputItemStatus.Warning, notice.Status);
        Assert.Equal("Retrying soon", notice.Body);

        var persisted = await h.PersistedAsync();
        Assert.DoesNotContain(persisted, p => p.ItemType == "Notice");
        Assert.Contains(persisted, p => p.ItemType == "Assistant");
    }

    [Fact]
    public async Task Compaction_streams_a_summary_and_is_persisted_once_finished()
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            turns = new[]
            {
                new
                {
                    steps = new object[]
                    {
                        new { update = new { sessionUpdate = "compaction_update", compactionId = "c1", status = "in_progress" } },
                        new { update = new { sessionUpdate = "compaction_summary_chunk", compactionId = "c1", content = new { type = "text", text = "We built a thing." } } },
                        new { update = new { sessionUpdate = "compaction_update", compactionId = "c1", status = "completed" } },
                        new { say = "continuing" }
                    }
                }
            }
        });

        await h.SendAsync("long chat");

        var compaction = Assert.Single(h.Messages, m => m.Type == ChatItemType.Compaction);
        Assert.Equal("Context compacted", compaction.Title);
        Assert.Equal(OutputItemStatus.Success, compaction.Status);
        Assert.Contains("We built a thing.", compaction.Body);

        var persisted = await h.PersistedAsync();
        var stored = Assert.Single(persisted, p => p.ItemType == "Compaction");
        Assert.Contains("We built a thing.", stored.Body);
    }

    [Fact]
    public async Task Agent_error_is_reported_and_the_chat_stays_usable()
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            turns = new object[]
            {
                new { when = "boom", steps = new object[] { new { fail = "backend exploded" } } },
                new { steps = new object[] { new { say = "fine" } } }
            }
        });

        var failed = await h.SendAsync("boom");

        Assert.Equal(TurnResult.Errored, failed);
        var error = Assert.Single(h.Messages, m => m.IsTransient);
        Assert.Contains("backend exploded", error.Content);

        Assert.Equal(TurnResult.Completed, await h.SendAsync("again"));
        Assert.Contains(h.Messages, m => m.Type == ChatItemType.Assistant && m.Content == "fine");

        var persisted = await h.PersistedAsync();
        Assert.DoesNotContain(persisted, p => p.Content.Contains("backend exploded"));
    }

    [Fact]
    public async Task Stop_cancels_the_turn_and_the_agent_observes_it()
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            turns = new[] { new { steps = new object[] { new { say = "working" }, new { waitForCancel = true } } } }
        });

        using var turn = new CancellationTokenSource();
        var sending = h.SendAsync("slow", turn.Token);
        await h.WaitUntilAsync(() => h.Messages.Any(m => m.Type == ChatItemType.Assistant && m.Content == "working"),
            "the agent to start answering");

        h.Stop(turn);
        var result = await sending;

        Assert.Equal(TurnResult.Cancelled, result);
        // The cancel notification is sent in the background, so it may land just after the turn ends.
        await h.WaitUntilAsync(() => h.Received("session/cancel").Count == 1, "session/cancel to reach the agent");
        await h.WaitUntilAsync(() => h.AgentEvents("cancelObserved").Count == 1, "the agent to see the cancel");
    }

    [Fact]
    public async Task Permission_request_waits_for_the_user_and_returns_the_choice()
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            turns = new[] { new { steps = new object[] { new { permission = new { title = "Run tests?" } }, new { say = "ran" } } } }
        });
        AcpPermissionRequest? asked = null;
        h.Permissions.PermissionRequested += r => asked = r;

        var sending = h.SendAsync("run");
        await h.WaitUntilAsync(() => asked is not null, "a permission prompt");

        Assert.Equal("Run tests?", asked!.Title);
        Assert.Contains(asked.Options, o => o.OptionId == "allow");
        Assert.False(sending.IsCompleted);

        h.Permissions.Resolve(asked.RequestId, "allow");
        Assert.Equal(TurnResult.Completed, await sending);

        var result = Assert.Single(h.AgentEvents("permissionResult"));
        Assert.Equal("selected", (string?)result["response"]!["outcome"]!["outcome"]);
        Assert.Equal("allow", (string?)result["response"]!["outcome"]!["optionId"]);
    }

    [Fact]
    public async Task Cancelling_a_pending_permission_prompt_reports_cancelled_to_the_agent()
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            turns = new[] { new { steps = new object[] { new { permission = new { title = "Delete everything?" } }, new { say = "after" } } } }
        });
        AcpPermissionRequest? asked = null;
        h.Permissions.PermissionRequested += r => asked = r;

        var sending = h.SendAsync("danger");
        await h.WaitUntilAsync(() => asked is not null, "a permission prompt");
        h.Permissions.CancelAll();
        await sending;

        var result = Assert.Single(h.AgentEvents("permissionResult"));
        Assert.Equal("cancelled", (string?)result["response"]!["outcome"]!["outcome"]);
    }

    [Fact]
    public async Task Auto_approve_answers_without_prompting()
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            turns = new[] { new { steps = new object[] { new { permission = new { title = "Edit file?" } }, new { say = "ok" } } } }
        });
        var prompted = false;
        h.Permissions.PermissionRequested += _ => prompted = true;
        h.Permissions.SetAutoApprove(true);

        Assert.Equal(TurnResult.Completed, await h.SendAsync("edit"));

        Assert.False(prompted);
        var result = Assert.Single(h.AgentEvents("permissionResult"));
        Assert.Equal("selected", (string?)result["response"]!["outcome"]!["outcome"]);
        Assert.Equal("allow", (string?)result["response"]!["outcome"]!["optionId"]);
    }

    [Fact]
    public async Task Reopening_a_session_loads_it_and_does_not_replay_old_turns_into_the_transcript()
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            agentCapabilities = new { loadSession = true },
            replayOnLoad = new object[]
            {
                new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "OLD-REPLAY" } },
                new { sessionUpdate = "notice", severity = "error", title = "OLD-NOTICE" }
            },
            turns = new[] { new { steps = new object[] { new { say = "fresh" } } } }
        });
        h.Service.RestoreHistory("fake-session-1");

        await h.SendAsync("hello again");

        Assert.Single(h.Received("session/load"));
        Assert.Empty(h.Received("session/new"));
        Assert.Equal(new[] { "hello again", "fresh" }, h.Messages.Select(m => m.Content).ToArray());
    }

    [Fact]
    public async Task A_tool_call_streams_into_a_step_that_is_persisted_when_it_finishes()
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            turns = new[]
            {
                new
                {
                    steps = new object[]
                    {
                        new { say = "Running the tests." },
                        new { update = new { sessionUpdate = "tool_call", toolCallId = "t1", title = "Run tests", kind = "execute", status = "pending", rawInput = new { command = "dotnet test" } } },
                        new { update = new { sessionUpdate = "tool_call_update", toolCallId = "t1", status = "in_progress" } },
                        new { update = new { sessionUpdate = "tool_call_update", toolCallId = "t1", status = "completed", rawOutput = new { output = "Passed!" } } },
                        new { say = "All green." }
                    }
                }
            }
        });

        await h.SendAsync("test it");

        Assert.Equal(new[] { ChatItemType.User, ChatItemType.Assistant, ChatItemType.ToolStep, ChatItemType.Assistant },
            h.Messages.Select(m => m.Type).ToArray());
        var step = h.Messages.Single(m => m.Type == ChatItemType.ToolStep);
        Assert.Equal("t1", step.Id);
        Assert.Equal(OutputItemStatus.Success, step.Status);
        Assert.Equal("Run tests dotnet test", step.Title);
        Assert.Equal("dotnet test\nPassed!", step.Body);

        var stored = Assert.Single(await h.PersistedAsync(), p => p.ItemType == "ToolStep");
        Assert.Equal("Success", stored.StatusText);
        Assert.Equal("dotnet test\nPassed!", stored.Body);
    }

    [Fact]
    public async Task A_failed_tool_call_is_persisted_as_an_error()
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            turns = new[]
            {
                new
                {
                    steps = new object[]
                    {
                        new { update = new { sessionUpdate = "tool_call", toolCallId = "t1", title = "Build", kind = "execute", status = "in_progress" } },
                        new { update = new { sessionUpdate = "tool_call_update", toolCallId = "t1", status = "failed", rawOutput = new { error = "exit code 1" } } }
                    }
                }
            }
        });

        await h.SendAsync("build");

        var stored = Assert.Single(await h.PersistedAsync(), p => p.ItemType == "ToolStep");
        Assert.Equal("Error", stored.StatusText);
        Assert.Equal("exit code 1", stored.Body);
    }

    [Fact]
    public async Task A_tool_call_first_reported_as_already_completed_is_still_persisted()
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            turns = new[]
            {
                new
                {
                    steps = new object[]
                    {
                        new { update = new { sessionUpdate = "tool_call", toolCallId = "t1", title = "Read a.txt", kind = "read", status = "completed" } },
                        new { say = "done" }
                    }
                }
            }
        });

        await h.SendAsync("read it");

        Assert.Equal(OutputItemStatus.Success, h.Messages.Single(m => m.Type == ChatItemType.ToolStep).Status);
        Assert.Contains(await h.PersistedAsync(), p => p.ItemType == "ToolStep");
    }

    [Fact]
    public async Task Usage_update_is_exposed_on_the_service()
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            turns = new[]
            {
                new
                {
                    steps = new object[]
                    {
                        new { update = new { sessionUpdate = "usage_update", used = 1234, size = 200000 } },
                        new { say = "ok" }
                    }
                }
            }
        });

        await h.SendAsync("count");
        await h.WaitUntilAsync(() => h.Service.Usage is not null, "the usage update");

        Assert.Equal(1234UL, h.Service.Usage!.Used);
        Assert.Equal(200000UL, h.Service.Usage.Size);
    }
}
