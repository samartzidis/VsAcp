using VsAcp.Core.Abstractions;
using VsAcp.Core.Services;
using VsAcp.Core.Tests.Support;

namespace VsAcp.Core.Tests;

/// <summary>
/// "Fork from here" end to end: a chat is copied up to a message into a new chat, which a second tab on the same
/// workspace and store opens with its own agent process.
/// </summary>
public class ForkFlowTests
{
    private static object CanFork() => new { loadSession = false, sessionCapabilities = new { fork = new { }, close = new { } } };

    // Like claude-agent-acp, every reply carries the agent's message id.
    private static object[] Turns() => new object[]
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
        new
        {
            when = "mid",
            steps = new object[]
            {
                new { say = "looking", messageId = "m-looking" },
                new { say = "done", messageId = "m-done" }
            }
        },
        new { steps = new object[] { new { say = "ok", messageId = "m-ok" } } }
    };

    private static string? ForkPointOf(System.Text.Json.Nodes.JsonObject forkRequest) =>
        (string?)forkRequest["_meta"]?["jetbrains"]?["air"]?["fork"]?["messageId"];

    private static string IdOf(ChatHarness h, ChatItemType type, string content) =>
        h.Messages.Single(m => m.Type == type && m.Content == content).Id;

    private static string[] Conversation(IEnumerable<(ChatItemType Type, string? Content)> messages) =>
        messages.Where(m => m.Type is ChatItemType.User or ChatItemType.Assistant).Select(m => m.Content ?? "").ToArray();

    private static string[] Conversation(ChatHarness h) => Conversation(h.Messages.Select(m => (m.Type, (string?)m.Content)));

    private static string[] Conversation(ForkResult fork) =>
        Conversation(fork.Messages.Select(m => ((ChatItemType)Enum.Parse(typeof(ChatItemType), m.ItemType), (string?)m.Content)));

    // What the host does with a fork: a new tab on the same workspace and store opens the new chat, and its agent is
    // primed before the first send.
    private static async Task<ChatHarness> OpenForkAsync(ChatHarness source, ForkResult fork)
    {
        var tab = await ChatHarness.CreateAsync(
            new { agentCapabilities = new { loadSession = true }, turns = Turns() },
            o => o.WorkingDirectory = source.Workspace,
            sessionId: fork.Entry.Id);
        await tab.OpenStoredChatAsync();
        await tab.Controller.PrimeForkedChatAsync(fork);
        return tab;
    }

    [Fact]
    public async Task A_fork_from_a_reply_copies_the_agent_session_and_the_new_tab_resumes_it_while_the_source_carries_on()
    {
        using var a = await ChatHarness.CreateAsync(new { agentCapabilities = CanFork(), turns = Turns() });
        await a.SendAsync("one");
        await a.SendAsync("two");

        var fork = await a.Controller.ForkAsync(IdOf(a, ChatItemType.Assistant, "wrote one"));

        var forkRequest = Assert.Single(a.Received("session/fork"));
        Assert.Equal("fake-session-1", (string?)forkRequest["sessionId"]);
        Assert.Equal("m-one", ForkPointOf(forkRequest));
        // Only the copy is closed in the source tab's agent: the source chat keeps its own session.
        Assert.Equal("fake-session-2", (string?)Assert.Single(a.Received("session/close"))["sessionId"]);
        Assert.True(fork.AgentForked);
        Assert.Null(fork.Draft);
        Assert.Equal(new[] { "one", "wrote one" }, Conversation(fork));
        var entry = (await a.Store.GetSessionIndexAsync()).Single(e => e.Id == fork.Entry.Id);
        Assert.Equal("fake-session-2", entry.AcpSessionId);
        Assert.Equal($"Fork of {(await a.IndexEntryAsync()).Title}", entry.Title);
        Assert.True(entry.TitleLocked);

        await a.SendAsync("three");
        Assert.Equal("fake-session-1", (string?)a.Received("session/prompt").Last()["sessionId"]);
        Assert.Single(a.Received("session/new"));

        using var b = await OpenForkAsync(a, fork);
        Assert.Equal(new[] { "one", "wrote one" }, Conversation(b));
        await b.SendAsync("four");

        Assert.Equal("fake-session-2", (string?)Assert.Single(b.Received("session/load"))["sessionId"]);
        Assert.Empty(b.Received("session/new"));
        var prompt = Assert.Single(b.PromptTexts());
        Assert.StartsWith("[This conversation was forked from another chat", prompt);
        Assert.EndsWith("four", prompt);
        Assert.DoesNotContain("<prior_conversation_context>", prompt);
        Assert.Equal(new[] { "one", "wrote one", "four", "ok" }, Conversation(b));
        Assert.Equal(new[] { "one", "wrote one", "two", "wrote two", "three", "ok" }, Conversation(a));
    }

    [Fact]
    public async Task A_fork_from_a_user_message_keeps_what_came_before_and_hands_the_message_back_as_a_draft()
    {
        using var a = await ChatHarness.CreateAsync(new { agentCapabilities = CanFork(), turns = Turns() });
        await a.SendAsync("one");
        await a.SendAsync("two");

        var fork = await a.Controller.ForkAsync(IdOf(a, ChatItemType.User, "two"));

        Assert.Equal("m-one", ForkPointOf(Assert.Single(a.Received("session/fork"))));
        Assert.True(fork.AgentForked);
        Assert.Equal(new[] { "one", "wrote one" }, Conversation(fork));
        Assert.Equal("two", fork.Draft?.Content);
    }

    [Fact]
    public async Task A_fork_from_a_reply_in_the_middle_of_a_turn_gives_the_new_tab_the_conversation_as_text()
    {
        using var a = await ChatHarness.CreateAsync(new { agentCapabilities = CanFork(), turns = Turns() });
        await a.SendAsync("mid");

        var fork = await a.Controller.ForkAsync(IdOf(a, ChatItemType.Assistant, "looking"));

        Assert.Empty(a.Received("session/fork"));
        Assert.False(fork.AgentForked);
        Assert.Null(fork.Entry.AcpSessionId);
        Assert.Equal(new[] { "mid", "looking" }, Conversation(fork));

        using var b = await OpenForkAsync(a, fork);
        await b.SendAsync("next");

        Assert.Empty(b.Received("session/load"));
        var prompt = Assert.Single(b.PromptTexts());
        Assert.StartsWith("[Continuing a conversation forked from another chat", prompt);
        Assert.Contains("NOT rolled back", prompt);
        Assert.Contains("[Assistant]: looking", prompt);
        Assert.DoesNotContain("done", prompt);
        Assert.EndsWith("My next request:" + Environment.NewLine + "next", prompt);
    }

    [Fact]
    public async Task A_fork_on_an_agent_that_cannot_fork_gives_the_new_tab_the_conversation_as_text()
    {
        using var a = await ChatHarness.CreateAsync(new { turns = Turns() });
        await a.SendAsync("one");

        var fork = await a.Controller.ForkAsync(IdOf(a, ChatItemType.Assistant, "wrote one"));

        Assert.Empty(a.Received("session/fork"));
        Assert.False(fork.AgentForked);

        using var b = await OpenForkAsync(a, fork);
        await b.SendAsync("next");

        Assert.Contains("<prior_conversation_context>", Assert.Single(b.PromptTexts()));
        Assert.Single(b.Received("session/new"));
    }

    [Fact]
    public async Task The_new_tab_reverts_to_its_copied_checkpoints_after_the_source_deleted_the_originals()
    {
        using var a = await ChatHarness.CreateAsync(new { agentCapabilities = CanFork(), turns = Turns() });
        a.WriteFile("a.txt", "orig");
        await a.SendAsync("one");
        await a.SendAsync("two");

        var fork = await a.Controller.ForkAsync(IdOf(a, ChatItemType.Assistant, "wrote two"));

        var originals = a.Messages.Where(m => m.Type == ChatItemType.User).Select(m => m.CheckpointId).ToArray();
        var copies = fork.Messages.Where(m => m.ItemType == nameof(ChatItemType.User)).Select(m => m.CheckpointId).ToArray();
        Assert.Equal(2, copies.Length);
        Assert.All(copies, id => Assert.NotNull(id));
        Assert.Empty(copies.Intersect(originals));

        // The source chat's revert deletes the checkpoint of "two", which it discards.
        await a.RevertToUserMessageAsync(0);
        Assert.Equal("orig", a.ReadFile("a.txt"));

        using var b = await OpenForkAsync(a, fork);
        await b.RevertToUserMessageAsync(1);

        Assert.Equal("one", a.ReadFile("a.txt"));
        Assert.Equal(new[] { "one", "wrote one" }, Conversation(b));
    }

    [Fact]
    public async Task A_fork_request_for_a_message_of_another_chat_changes_nothing()
    {
        using var a = await ChatHarness.CreateAsync(new { agentCapabilities = CanFork(), turns = Turns() });
        await a.SendAsync("one");

        await Assert.ThrowsAsync<InvalidOperationException>(() => a.Controller.ForkAsync("not-a-message-here"));

        Assert.Single(await a.Store.GetSessionIndexAsync());
        Assert.Empty(a.Received("session/fork"));
    }
}
