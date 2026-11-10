using VsAcp.Core.Models;

namespace VsAcp.Core.Services;

/// <summary>What to do with the chat list when the saved index is read again (a solution reload) or the solution closes.</summary>
/// <typeparam name="T">The host's list entry.</typeparam>
/// <param name="Removed">Entries to take out of the list: none of them has an open tab.</param>
/// <param name="Updated">Entries without an open tab whose saved chat is still there, with that chat's saved state.</param>
/// <param name="Added">Saved chats no entry stands for yet, most recently active first; they go after the kept entries.</param>
public sealed record SessionListChanges<T>(
    IReadOnlyList<T> Removed,
    IReadOnlyList<(T Item, SessionEntry Entry)> Updated,
    IReadOnlyList<SessionEntry> Added);

public static class SessionListMerge
{
    /// <summary>
    /// Matches the list entries to the saved chats by saved chat id, keeping every entry that still stands for one,
    /// so an open tab keeps its id, its running turn and its agent. An entry with an open tab is never removed or
    /// updated (its view-model owns the chat's state). Pass no saved chats to keep only the open tabs.
    /// </summary>
    public static SessionListChanges<T> Plan<T>(
        IEnumerable<T> current,
        IReadOnlyList<SessionEntry> saved,
        Func<T, string?> savedIdOf,
        Func<T, bool> isOpen)
    {
        var byId = new Dictionary<string, SessionEntry>(StringComparer.Ordinal);
        foreach (var entry in saved)
            byId[entry.Id] = entry;

        var matched = new HashSet<string>(StringComparer.Ordinal);
        var removed = new List<T>();
        var updated = new List<(T, SessionEntry)>();

        foreach (var item in current)
        {
            var id = savedIdOf(item);
            var entry = id is not null && byId.TryGetValue(id, out var e) && matched.Add(id) ? e : null;

            if (isOpen(item))
                continue;
            if (entry is null)
                removed.Add(item);
            else
                updated.Add((item, entry));
        }

        var added = saved
            .Where(e => !matched.Contains(e.Id))
            .OrderByDescending(e => e.LastActivityUtc)
            .ToList();

        return new SessionListChanges<T>(removed, updated, added);
    }
}
