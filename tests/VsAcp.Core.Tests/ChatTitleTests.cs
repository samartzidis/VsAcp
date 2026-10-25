using VsAcp.Core.Models;
using VsAcp.Core.Services;
using VsAcp.Core.Tests.Support;

namespace VsAcp.Core.Tests;

/// <summary>How a chat gets its tab title: placeholder from the first message, the agent's own title, the user's rename.</summary>
public class ChatTitleTests
{
    private static object TitledTurn(string title) => new
    {
        turns = new[]
        {
            new
            {
                steps = new object[]
                {
                    new { update = new { sessionUpdate = "session_info_update", title } },
                    new { say = "OK" }
                }
            }
        }
    };

    [Fact]
    public async Task The_first_message_names_the_chat_until_the_agent_does()
    {
        using var h = await ChatHarness.CreateAsync();

        await h.SendAsync("please look at the chat page and tell me what is wrong with it");

        await h.WaitUntilAsync(() => h.Title is not null, "the placeholder title");
        Assert.Equal("please look at the chat page...", h.Title);
    }

    [Fact]
    public async Task The_agents_title_replaces_the_placeholder_and_is_persisted()
    {
        using var h = await ChatHarness.CreateAsync(TitledTurn("Preact chat UI"));

        await h.SendAsync("please look at the chat page and tell me what is wrong with it");

        await h.WaitUntilAsync(() => h.Title == "Preact chat UI", "the agent's title");
        var entry = await h.IndexEntryAsync();
        Assert.Equal("Preact chat UI", entry.Title);
        Assert.False(entry.TitleLocked);
    }

    [Fact]
    public async Task A_rename_survives_the_agents_title()
    {
        using var h = await ChatHarness.CreateAsync(TitledTurn("Agent title"));
        h.Controller.Rename("  My name ");

        await h.SendAsync("hello");

        // The assistant text comes after the title update in the same turn, so by now the update was seen.
        Assert.Contains(h.Messages, m => m.Content == "OK");
        Assert.Equal("My name", h.Title);
        var entry = await h.IndexEntryAsync();
        Assert.Equal("My name", entry.Title);
        Assert.True(entry.TitleLocked);
    }

    [Fact]
    public async Task A_blank_rename_is_ignored()
    {
        using var h = await ChatHarness.CreateAsync();

        h.Controller.Rename("   ");

        Assert.Null(h.Title);
        Assert.False((await h.IndexEntryAsync()).TitleLocked);
    }

    [Fact]
    public async Task A_reopened_chat_keeps_the_name_the_user_gave_it()
    {
        using var h = await ChatHarness.CreateAsync(TitledTurn("Agent title"));
        h.Controller.Rename("My name");
        await h.FlushAsync();

        var reopenedTitles = new List<string>();
        using var reopened = new ChatSessionController(new ChatSession(), h.Service, null, null, null, action => action());
        reopened.TitleChanged += reopenedTitles.Add;
        reopened.AttachStore(h.Store, h.Entry.Id);
        await reopened.RestoreAsync();

        await h.SendAsync("hello");
        await h.FlushAsync();

        Assert.Contains(h.Messages, m => m.Content == "OK");
        Assert.Empty(reopenedTitles);
        Assert.Equal("My name", (await h.IndexEntryAsync()).Title);
    }
}
