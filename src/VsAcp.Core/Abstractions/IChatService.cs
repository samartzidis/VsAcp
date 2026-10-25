using AcpProtocol;
using VsAcp.Core.Models;

namespace VsAcp.Core.Abstractions;

public interface IChatService
{
    IAsyncEnumerable<string> SendMessageAsync(
        string userMessage,
        IReadOnlyList<ImageAttachment>? images = null,
        IReadOnlyList<string>? filePaths = null,
        IReadOnlyList<string>? folderPaths = null,
        IReadOnlyList<CodeSelection>? selections = null,
        CancellationToken cancellationToken = default);
    Task<string> GenerateTitleAsync(string userMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// The agent gave the session a title (<c>session_info_update</c>), typically once it has seen the first
    /// exchange. Raised on a background thread, possibly more than once; never with a blank title.
    /// </summary>
    event Action<string>? TitleChanged;

    /// <summary>Drops the agent session and stops the agent process; the next message starts a new one.</summary>
    void ClearHistory();

    /// <summary>
    /// Drops the agent session (closing it when the agent supports <c>session/close</c>) but keeps a healthy
    /// agent process, so the next message opens a new session on it instead of a cold start. Used by revert.
    /// </summary>
    void ResetSession();

    /// <summary>
    /// Replaces the agent session with a copy of it that ends at the agent's message <paramref name="agentMessageId"/>
    /// (the unstable <c>session/fork</c> with a fork point) and closes the old one. Used by revert, so the agent keeps
    /// the real conversation up to there instead of a text summary of it. Returns the new session id, or null when the
    /// agent cannot fork (not advertised, no live session, or the fork failed); the current session is then unchanged.
    /// </summary>
    Task<string?> TryForkSessionAsync(string agentMessageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a prior-conversation context block from the given messages and
    /// stores it so it is transparently prepended to the user's next SendMessageAsync
    /// call. Call after <see cref="ResetSession"/> during a revert the agent could not fork.
    /// </summary>
    Task InitializeWithContextAsync(IReadOnlyList<PersistedMessage> messages, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a cancel notification to the agent for the active session turn.
    /// The process stays alive; the agent should stop its current operation.
    /// </summary>
    void Cancel();

    /// <summary>
    /// Returns the session cost reported by the agent's latest <c>usage_update</c>, or null when the
    /// agent has not reported cost. Currency is in <see cref="Usage"/>.<c>Cost.Currency</c>.
    /// </summary>
    decimal? GetSessionCost();

    // --- Context usage (tokens / cost) ---

    /// <summary>Latest <c>usage_update</c> from the agent, or null if none has been received.</summary>
    UsageUpdate? Usage { get; }
    event Action? UsageChanged;

    // --- Config options (model selection, etc.) ---

    IReadOnlyList<SessionConfigOption> ConfigOptions { get; }
    Task SetConfigOptionAsync(string configId, string value, CancellationToken cancellationToken = default);
    event Action? ConfigOptionsChanged;

    // --- Available commands (slash commands) ---

    IReadOnlyList<AvailableCommand> AvailableCommands { get; }
    event Action? CommandsChanged;

    // --- Mode selection ---

    IReadOnlyList<SessionMode> AvailableModes { get; }
    string? CurrentModeId { get; }
    Task SetModeAsync(string modeId, CancellationToken cancellationToken = default);
    event Action? ModesChanged;
}
