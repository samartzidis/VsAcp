using VsAcp.Core.Models;
using VsAcp.Core.Services;
using VsAcp.Core.Tests.Support;

namespace VsAcp.Core.Tests;

/// <summary>What happens around a turn besides the agent's answer: title, agent session id, notices, resync.</summary>
public class TurnBookkeepingTests
{
    private static object FailingTurn() => new
    {
        turns = new object[] { new { steps = new object[] { new { fail = "the agent said no" } } } }
    };

    // ---- title ----

    [Fact]
    public async Task The_first_message_names_the_chat_and_the_title_is_saved()
    {
        using var h = await ChatHarness.CreateAsync();

        await h.SendAsync("fix the login bug in the settings page today");
        await h.WaitUntilAsync(() => h.Title is not null, "the generated title");

        Assert.Equal("fix the login bug in the...", h.Title);
        Assert.Equal("fix the login bug in the...", (await h.IndexEntryAsync()).Title);
    }

    [Fact]
    public async Task Later_messages_do_not_rename_the_chat()
    {
        using var h = await ChatHarness.CreateAsync();
        await h.SendAsync("first question");
        await h.WaitUntilAsync(() => h.Title is not null, "the generated title");

        await h.SendAsync("something else entirely");

        Assert.Equal("first question", h.Title);
        Assert.Equal("first question", (await h.IndexEntryAsync()).Title);
    }

    // ---- agent session id ----

    [Fact]
    public async Task A_completed_turn_saves_the_agent_session_id_and_command()
    {
        using var h = await ChatHarness.CreateAsync();

        Assert.Equal(TurnResult.Completed, await h.SendAsync("hello"));

        var entry = await h.IndexEntryAsync();
        Assert.Equal("fake-session-1", entry.AcpSessionId);
        Assert.Equal(h.Options.AgentCommand, entry.AgentCommand);
    }

    [Fact]
    public async Task A_failed_turn_does_not_save_the_agent_session_id()
    {
        using var h = await ChatHarness.CreateAsync(FailingTurn());

        Assert.Equal(TurnResult.Errored, await h.SendAsync("hello"));

        Assert.Null((await h.IndexEntryAsync()).AcpSessionId);
    }

    [Fact]
    public async Task A_reopened_chat_continues_the_saved_agent_session()
    {
        using var h = await ChatHarness.CreateAsync(new { agentCapabilities = new { loadSession = true } });
        await h.SendAsync("hello");
        var saved = (await h.IndexEntryAsync()).AcpSessionId;
        Assert.NotNull(saved);

        // A new tab for the same chat is told the saved id, as when the chat is reopened.
        h.Service.ClearHistory();
        h.Service.RestoreHistory(saved!);
        await h.SendAsync("again");

        Assert.Equal(saved, (string?)h.Received("session/load").Single()["sessionId"]);
    }

    // ---- a restore that failed ----

    [Fact]
    public async Task A_failed_restore_forgets_the_saved_session_id_and_adds_a_notice()
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            agentCapabilities = new { loadSession = true },
            failLoad = true,
            turns = new object[] { new { steps = new object[] { new { fail = "no" } } } }
        });
        var entry = await h.IndexEntryAsync();
        entry.AcpSessionId = "gone-session";
        await h.Store.UpdateSessionAsync(entry);
        h.Service.RestoreHistory("gone-session");

        // The turn fails too, so nothing re-saves an id and the cleared state is visible.
        await h.SendAsync("hello");

        Assert.Null((await h.IndexEntryAsync()).AcpSessionId);
        Assert.Contains(h.Messages, m => m.Content.Contains("Agent session could not be restored"));
    }

    [Fact]
    public async Task A_failed_restore_followed_by_a_good_turn_saves_the_new_session_id()
    {
        using var h = await ChatHarness.CreateAsync(new { agentCapabilities = new { loadSession = true }, failLoad = true });
        var entry = await h.IndexEntryAsync();
        entry.AcpSessionId = "gone-session";
        await h.Store.UpdateSessionAsync(entry);
        h.Service.RestoreHistory("gone-session");

        await h.SendAsync("hello");

        Assert.Equal("fake-session-1", (await h.IndexEntryAsync()).AcpSessionId);
    }

    [Fact]
    public async Task A_disposed_controller_no_longer_reacts_to_restore_failures()
    {
        using var h = await ChatHarness.CreateAsync(new { agentCapabilities = new { loadSession = true }, failLoad = true });
        h.Controller.Dispose();
        h.Service.RestoreHistory("gone-session");

        await h.SendAsync("hello");

        Assert.DoesNotContain(h.Messages, m => m.Content.Contains("could not be restored"));
    }

    // ---- view resync ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_view_is_resynced_after_every_turn_whether_it_worked_or_not(bool failing)
    {
        using var h = await ChatHarness.CreateAsync(failing ? FailingTurn() : null);
        var resyncs = 0;
        h.Session.ResyncRequested += _ => resyncs++;

        await h.SendAsync("hello");

        Assert.Equal(1, resyncs);
    }

    [Fact]
    public async Task The_view_is_resynced_after_a_cancelled_turn()
    {
        using var h = await ChatHarness.CreateAsync(new { turns = new object[] { new { steps = new object[] { new { waitForCancel = true } } } } });
        var resyncs = 0;
        h.Session.ResyncRequested += _ => resyncs++;
        using var cts = new CancellationTokenSource();

        var sending = h.SendAsync("hello", cts.Token);
        await h.WaitUntilAsync(() => h.Received("session/prompt").Count > 0, "the prompt to reach the agent");
        cts.Cancel();
        await sending;

        Assert.Equal(1, resyncs);
    }
}
