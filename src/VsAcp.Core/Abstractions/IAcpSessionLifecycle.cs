using VsAcp.Core.Models;

namespace VsAcp.Core.Abstractions;

/// <summary>
/// ACP session continuation contract: persist/restore the agent session ID across VS restarts
/// and eagerly connect to the backend on tab open. Separated from <see cref="IChatService"/>
/// because these operations are specific to the ACP transport layer; a non-ACP backend would
/// never need to implement them.
/// </summary>
public interface IAcpSessionLifecycle
{
    /// <summary>
    /// Serializes the current conversation continuation token (e.g. the ACP session ID) to a
    /// string suitable for persistence. Call after a completed turn and store on the session entry.
    /// </summary>
    string SerializeHistory();

    /// <summary>
    /// Restores a previously serialized continuation token so the next
    /// <see cref="IChatService.LoadSessionAsync"/> will resume the correct backend session.
    /// </summary>
    void RestoreHistory(string serializedHistory);

    /// <summary>
    /// Eagerly connects to the agent and loads a previous session so that
    /// history is replayed via session notifications.
    /// </summary>
    Task LoadSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fired when a session restore (<see cref="LoadSessionAsync"/>) fails, meaning the agent
    /// started fresh with no memory of prior conversation. The stale session token should be
    /// cleared from disk and the user should be informed.
    /// </summary>
    event Action? SessionRestoreFailed;

    /// <summary>
    /// True when the agent advertised <c>sessionCapabilities.delete</c> and a live session exists,
    /// so <see cref="DeleteAgentSessionAsync"/> can remove the agent's copy of this conversation.
    /// </summary>
    bool CanDeleteAgentSession { get; }

    /// <summary>
    /// Whether the agent advertised <c>sessionCapabilities.additionalDirectories</c>, i.e. whether extra
    /// folders are passed on to it. Null until the agent has answered <c>initialize</c>.
    /// </summary>
    bool? SupportsAdditionalDirectories { get; }

    /// <summary>
    /// The extra folders actually sent to the agent when this session was started or loaded (empty when
    /// none were configured or the agent does not take them). Null until a session exists. Later changes
    /// to the configured list do not alter it: it describes the running session.
    /// </summary>
    IReadOnlyList<string>? ActiveAdditionalDirectories { get; }

    /// <summary>Raised (from any thread) when <see cref="ActiveAdditionalDirectories"/> changes.</summary>
    event Action? ActiveAdditionalDirectoriesChanged;

    /// <summary>
    /// Sends <c>session/delete</c> for the current agent session (no-op when
    /// <see cref="CanDeleteAgentSession"/> is false). Call when the user deletes the chat locally.
    /// </summary>
    Task DeleteAgentSessionAsync(CancellationToken cancellationToken = default);
}
