using VsAcp.Core.Models;

namespace VsAcp.Core.Abstractions;

public interface ISessionStore
{
    // Workspace
    Task EnsureWorkspaceAsync();

    // Session index
    Task<IReadOnlyList<SessionEntry>> GetSessionIndexAsync();
    Task<SessionEntry> CreateSessionAsync(string title);
    Task UpdateSessionAsync(SessionEntry entry);
    Task DeleteSessionAsync(string sessionId);

    // Messages (for UI display only - agent context is restored via LoadSession)
    Task<IReadOnlyList<PersistedMessage>> GetMessagesAsync(string sessionId);
    Task AppendMessageAsync(string sessionId, PersistedMessage message);
    Task ClearMessagesAsync(string sessionId);

    /// <summary>
    /// Removes all messages with <see cref="PersistedMessage.Ordinal"/> greater than
    /// <paramref name="keepUpToOrdinal"/>. Pass -1 to remove all messages.
    /// </summary>
    Task TruncateMessagesAsync(string sessionId, int keepUpToOrdinal);
}
