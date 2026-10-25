using VsAcp.Core.Services;
using VsAcp.Core.Tests.Support;

namespace VsAcp.Core.Tests;

/// <summary>The agent is a child process: it can crash, be killed, fail to start, or outlive the chat.</summary>
public class AgentProcessTests
{
    private static object CrashOnBoom(bool loadSession = false) => new
    {
        agentCapabilities = new { loadSession },
        turns = new object[]
        {
            new { when = "boom", steps = new object[] { new { say = "partial answer" }, new { exit = 3 } } },
            new { steps = new object[] { new { say = "back again" } } }
        }
    };

    private static async Task WaitForExitAsync(int pid, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (ChatHarness.IsRunning(pid))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Process {pid} was still running after {timeoutMs} ms.");
            await Task.Delay(50);
        }
    }

    private static bool HasText(ChatHarness h, string fragment) =>
        h.Messages.Any(m => m.Content?.Contains(fragment) == true);

    // ---- the agent stops answering ----

    private static object HangOnSlow(bool loadSession = false) => new
    {
        agentCapabilities = new { loadSession },
        turns = new object[]
        {
            new { when = "slow", steps = new object[] { new { say = "working" }, new { hang = true } } },
            new { when = "patient", steps = new object[] { new { say = "working" }, new { waitForCancel = true } } },
            new { steps = new object[] { new { say = "back again" } } }
        }
    };

    private static async Task<TurnResult> StopWhileWorkingAsync(ChatHarness h, string prompt)
    {
        using var turn = new CancellationTokenSource();
        var sending = h.SendAsync(prompt, turn.Token);
        await h.WaitUntilAsync(() => HasText(h, "working"), "the agent to start answering");
        h.Stop(turn);
        return await sending;
    }

    [Fact]
    public async Task Stop_on_an_agent_that_stopped_answering_replaces_it_so_the_next_message_works()
    {
        // The agent process stays alive but never answers again (as when the launcher outlives a dead agent and
        // keeps the pipe open): Stop must not leave every later message hanging on it.
        using var h = await ChatHarness.CreateAsync(HangOnSlow(loadSession: true),
            o => o.CancelGracePeriod = TimeSpan.FromMilliseconds(500));

        Assert.Equal(TurnResult.Cancelled, await StopWhileWorkingAsync(h, "slow"));
        var result = await h.SendWithinAsync("hello");

        Assert.Equal(TurnResult.Completed, result);
        Assert.True(HasText(h, "back again"));
        var pids = h.AgentProcessIds();
        Assert.Equal(2, pids.Count);
        Assert.False(ChatHarness.IsRunning(pids[0]));
        Assert.Single(h.Received("session/load"));
    }

    [Fact]
    public async Task Stop_on_an_agent_that_ends_the_turn_keeps_it()
    {
        using var h = await ChatHarness.CreateAsync(HangOnSlow(),
            o => o.CancelGracePeriod = TimeSpan.FromMilliseconds(500));

        Assert.Equal(TurnResult.Cancelled, await StopWhileWorkingAsync(h, "patient"));
        await h.WaitUntilAsync(() => h.AgentEvents("cancelObserved").Count == 1, "the agent to end the turn");
        await Task.Delay(1000); // past the grace period
        var result = await h.SendWithinAsync("hello");

        Assert.Equal(TurnResult.Completed, result);
        Assert.Single(h.AgentProcessIds());
    }

    // ---- the agent dies ----

    [Fact]
    public async Task An_agent_that_crashes_mid_turn_ends_the_turn_with_an_error_instead_of_hanging()
    {
        using var h = await ChatHarness.CreateAsync(CrashOnBoom());

        var result = await h.SendWithinAsync("boom");

        Assert.Equal(TurnResult.Errored, result);
        Assert.True(HasText(h, "**Error:**"), h.Log.ToString());
    }

    [Fact]
    public async Task The_next_message_after_a_crash_starts_a_fresh_agent()
    {
        using var h = await ChatHarness.CreateAsync(CrashOnBoom());
        await h.SendWithinAsync("boom");

        var result = await h.SendWithinAsync("hello");

        Assert.Equal(TurnResult.Completed, result);
        Assert.True(HasText(h, "back again"));
        var pids = h.AgentProcessIds();
        Assert.Equal(2, pids.Count);
        Assert.NotEqual(pids[0], pids[1]);
        Assert.False(ChatHarness.IsRunning(pids[0]));
        Assert.Equal(2, h.Received("initialize").Count);
    }

    [Fact]
    public async Task After_a_crash_the_new_agent_is_asked_to_load_the_old_session_when_it_can()
    {
        using var h = await ChatHarness.CreateAsync(CrashOnBoom(loadSession: true));
        await h.SendWithinAsync("first");
        await h.SendWithinAsync("boom");

        var result = await h.SendWithinAsync("hello");

        Assert.Equal(TurnResult.Completed, result);
        var load = Assert.Single(h.Received("session/load"));
        Assert.Equal("fake-session-1", (string?)load["sessionId"]);
        Assert.Single(h.Received("session/new"));
    }

    [Fact]
    public async Task After_a_crash_without_load_support_the_new_agent_starts_a_new_session()
    {
        using var h = await ChatHarness.CreateAsync(CrashOnBoom());
        await h.SendWithinAsync("first");
        await h.SendWithinAsync("boom");

        await h.SendWithinAsync("hello");

        Assert.Empty(h.Received("session/load"));
        Assert.Equal(2, h.Received("session/new").Count);
    }

    [Fact]
    public async Task An_agent_killed_between_turns_is_replaced_on_the_next_message()
    {
        using var h = await ChatHarness.CreateAsync();
        await h.SendWithinAsync("one");
        var first = Assert.Single(h.AgentProcessIds());

        ChatHarness.Kill(first);
        var result = await h.SendWithinAsync("two");

        Assert.Equal(TurnResult.Completed, result);
        Assert.Equal(2, h.AgentProcessIds().Count);
    }

    [Fact]
    public async Task A_crash_leaves_the_chat_usable_with_nothing_stuck_streaming()
    {
        using var h = await ChatHarness.CreateAsync(CrashOnBoom());

        await h.SendWithinAsync("boom");
        await h.SendWithinAsync("again");

        Assert.DoesNotContain(h.Messages, m => m.IsStreaming);
    }

    // ---- the chat ends the agent ----

    [Fact]
    public async Task Disposing_the_chat_stops_the_agent_process()
    {
        var h = await ChatHarness.CreateAsync();
        int pid;
        try
        {
            await h.SendWithinAsync("hello");
            pid = Assert.Single(h.AgentProcessIds());
            Assert.True(ChatHarness.IsRunning(pid));
        }
        finally
        {
            h.Dispose();
        }

        await WaitForExitAsync(pid);
    }

    [Fact]
    public async Task Clearing_the_chat_stops_the_agent_and_the_next_message_starts_a_new_one()
    {
        using var h = await ChatHarness.CreateAsync();
        await h.SendWithinAsync("hello");
        var first = Assert.Single(h.AgentProcessIds());

        h.Service.ClearHistory();
        await WaitForExitAsync(first);
        await h.SendWithinAsync("again");

        var pids = h.AgentProcessIds();
        Assert.Equal(2, pids.Count);
        Assert.NotEqual(first, pids[1]);
        Assert.Equal(2, h.Received("session/new").Count);
    }

    // ---- the agent cannot be started ----

    [Fact]
    public async Task A_missing_agent_executable_is_reported_as_a_start_failure()
    {
        using var h = await ChatHarness.CreateAsync(configure: o => o.AgentCommand = @"C:\definitely\not\here\agent.exe");

        var result = await h.SendWithinAsync("hello");

        Assert.Equal(TurnResult.Errored, result);
        Assert.True(HasText(h, "Could not start agent process"), h.Log.ToString());
    }

    [Fact]
    public async Task A_blank_agent_command_is_reported_as_not_configured()
    {
        using var h = await ChatHarness.CreateAsync(configure: o => o.AgentCommand = "  ");

        var result = await h.SendWithinAsync("hello");

        Assert.Equal(TurnResult.Errored, result);
        Assert.True(HasText(h, "Agent command is not configured"), h.Log.ToString());
    }

    [Fact]
    public async Task A_start_failure_does_not_stop_a_later_attempt_with_a_fixed_command()
    {
        var good = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FakeAgent", "VsAcp.FakeAgent.exe");
        using var h = await ChatHarness.CreateAsync(configure: o => o.AgentCommand = @"C:\definitely\not\here\agent.exe");
        Assert.Equal(TurnResult.Errored, await h.SendWithinAsync("hello"));

        h.Options.AgentCommand = good;

        Assert.Equal(TurnResult.Completed, await h.SendWithinAsync("hello again"));
    }

    // ---- launching through a script ----

    [Fact]
    public async Task An_agent_started_through_a_cmd_script_works_and_is_cleaned_up_with_the_chat()
    {
        using var scripts = new TempDirectory();
        var exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FakeAgent", "VsAcp.FakeAgent.exe");
        var script = Path.Combine(scripts.Path, "agent.cmd");
        File.WriteAllText(script, $"@\"{exe}\" %*{Environment.NewLine}");

        var h = await ChatHarness.CreateAsync(configure: o => o.AgentCommand = script);
        int pid;
        try
        {
            var result = await h.SendWithinAsync("hello");
            Assert.Equal(TurnResult.Completed, result);
            pid = Assert.Single(h.AgentProcessIds());
            Assert.True(ChatHarness.IsRunning(pid));
        }
        finally
        {
            h.Dispose();
        }

        // The agent is the cmd.exe's child; the job object must take it down with the chat.
        await WaitForExitAsync(pid);
    }

    // ---- what a crash does to the conversation ----

    [Fact]
    public async Task Without_load_support_a_crash_tells_the_user_the_agent_forgot_the_conversation()
    {
        // The UI keeps the history, but the replacement agent gets a brand new session and is not told what
        // was said before; the user is warned once, and the history is not replayed to the agent.
        using var h = await ChatHarness.CreateAsync(CrashOnBoom());
        var restoreFailed = 0;
        h.Service.SessionRestoreFailed += () => restoreFailed++;
        await h.SendWithinAsync("remember the number 42");
        await h.SendWithinAsync("boom");
        Assert.Equal(0, restoreFailed);

        await h.SendWithinAsync("what number?");

        Assert.Equal(1, restoreFailed);
        Assert.Equal("what number?", h.PromptTexts().Last());
        Assert.True(HasText(h, "remember the number 42"));
    }

    [Fact]
    public async Task With_load_support_a_crash_is_recovered_silently()
    {
        using var h = await ChatHarness.CreateAsync(CrashOnBoom(loadSession: true));
        var restoreFailed = 0;
        h.Service.SessionRestoreFailed += () => restoreFailed++;
        await h.SendWithinAsync("first");
        await h.SendWithinAsync("boom");

        await h.SendWithinAsync("hello");

        Assert.Equal(0, restoreFailed);
    }
}
