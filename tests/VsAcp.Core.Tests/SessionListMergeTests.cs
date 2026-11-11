using VsAcp.Core.Models;
using VsAcp.Core.Services;

namespace VsAcp.Core.Tests;

/// <summary>
/// The chat list across a solution reload or close: an entry that still stands for a saved chat is kept (so an open
/// tab keeps its id, its turn and its agent), and an entry with an open tab is never removed or touched.
/// </summary>
public class SessionListMergeTests
{
    private sealed record Item(string? SavedId, bool Open = false);

    private static SessionEntry Saved(string id, int minutesAgo = 0) =>
        new() { Id = id, Title = "chat " + id, LastActivityUtc = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc).AddMinutes(-minutesAgo) };

    private static SessionListChanges<Item> Plan(IEnumerable<Item> current, params SessionEntry[] saved) =>
        SessionListMerge.Plan(current, saved, i => i.SavedId, i => i.Open);

    [Fact]
    public void A_reload_with_the_same_chats_keeps_every_entry()
    {
        var open = new Item("a", Open: true);
        var closed = new Item("b");

        var changes = Plan([open, closed], Saved("a"), Saved("b"));

        Assert.Empty(changes.Removed);
        Assert.Empty(changes.Added);
        var update = Assert.Single(changes.Updated);
        Assert.Same(closed, update.Item);
        Assert.Equal("b", update.Entry.Id);
    }

    [Fact]
    public void An_open_tab_is_never_updated_from_disk()
    {
        var open = new Item("a", Open: true);

        var changes = Plan([open], Saved("a"));

        Assert.Empty(changes.Updated);
        Assert.Empty(changes.Removed);
        Assert.Empty(changes.Added);
    }

    [Fact]
    public void A_chat_new_on_disk_is_added_most_recent_first()
    {
        var changes = Plan([new Item("a")], Saved("a"), Saved("old", minutesAgo: 30), Saved("new", minutesAgo: 1));

        Assert.Equal(new[] { "new", "old" }, changes.Added.Select(e => e.Id).ToArray());
    }

    [Fact]
    public void A_chat_gone_from_disk_is_removed_unless_its_tab_is_open()
    {
        var goneClosed = new Item("gone");
        var goneOpen = new Item("gone-open", Open: true);

        var changes = Plan([goneClosed, goneOpen], Saved("other"));

        Assert.Same(goneClosed, Assert.Single(changes.Removed));
        Assert.Equal("other", Assert.Single(changes.Added).Id);
    }

    [Fact]
    public void An_open_chat_on_disk_is_not_added_a_second_time()
    {
        var changes = Plan([new Item("a", Open: true)], Saved("a"));

        Assert.Empty(changes.Added);
    }

    [Fact]
    public void An_entry_that_was_never_saved_is_kept_only_while_open()
    {
        var unsavedOpen = new Item(null, Open: true);
        var unsavedClosed = new Item(null);

        var changes = Plan([unsavedOpen, unsavedClosed]);

        Assert.Same(unsavedClosed, Assert.Single(changes.Removed));
    }

    [Fact]
    public void Closing_the_solution_keeps_only_the_open_tabs()
    {
        var open = new Item("a", Open: true);
        var closed = new Item("b");

        var changes = Plan([open, closed]);

        Assert.Same(closed, Assert.Single(changes.Removed));
        Assert.Empty(changes.Updated);
        Assert.Empty(changes.Added);
    }

    [Fact]
    public void Two_entries_for_one_saved_chat_keep_only_the_first()
    {
        var first = new Item("a");
        var second = new Item("a");

        var changes = Plan([first, second], Saved("a"));

        Assert.Same(first, Assert.Single(changes.Updated).Item);
        Assert.Same(second, Assert.Single(changes.Removed));
    }
}
