using System.Text.Json;
using VsAcp.Core.Abstractions;
using VsAcp.Core.Models;

namespace VsAcp.Core.Services;

/// <summary>What <see cref="ForkCoordinator.ForkAsync"/> made: the new chat, and how its agent gets the conversation.</summary>
public sealed class ForkResult
{
    /// <summary>The new chat's index entry; its <see cref="SessionEntry.AcpSessionId"/> is the forked agent session, if any.</summary>
    public required SessionEntry Entry { get; init; }

    /// <summary>The messages copied into the new chat, in creation order.</summary>
    public required IReadOnlyList<PersistedMessage> Messages { get; init; }

    /// <summary>
    /// The agent session was forked, so the new chat's agent resumes the real conversation. Otherwise it is given
    /// <see cref="Messages"/> as text with its first message.
    /// </summary>
    public bool AgentForked { get; init; }

    /// <summary>Forked from a user message: that message, to put back into the new chat's input.</summary>
    public PersistedMessage? Draft { get; init; }
}

/// <summary>
/// "Fork from here": copies a chat up to one of its messages into a new chat. From a reply, the new chat keeps
/// everything up to and including it; from a user message, everything before it, and that message goes into the new
/// chat's input. The conversation is forked, the workspace files are not.
/// <para>
/// When the reply ends its turn and the agent supports <c>session/fork</c>, the source chat's agent copies its session
/// up to there and the new chat resumes the copy; otherwise the new chat's agent is given the conversation as text
/// with its first message (<see cref="ChatSessionController.PrimeForkedChatAsync"/>). The copied user messages get
/// copies of their checkpoints: the shadow repo is shared, and a revert in the source chat deletes the checkpoints of
/// the messages it discards.
/// </para>
/// </summary>
public sealed class ForkCoordinator
{
    private readonly ICheckpointService? _checkpointService;
    private readonly IChatService? _sourceAgent;

    public ForkCoordinator(ICheckpointService? checkpointService, IChatService? sourceAgent)
    {
        _checkpointService = checkpointService;
        _sourceAgent = sourceAgent;
    }

    public async Task<ForkResult> ForkAsync(ISessionStore store, string sourceSessionId, string messageId)
    {
        var allMessages = await store.GetMessagesAsync(sourceSessionId);
        var target = allMessages.FirstOrDefault(m => m.Id == messageId)
            ?? throw new InvalidOperationException($"Message {messageId} is not a message of chat {sourceSessionId}; nothing was forked.");

        var fromUser = target.ItemType == nameof(ChatItemType.User);
        var kept = allMessages
            .Where(m => fromUser ? m.Ordinal < target.Ordinal : m.Ordinal <= target.Ordinal)
            .OrderBy(m => m.Ordinal)
            .ToList();

        // A reply in the middle of a turn can share its agent message with the tool call after it, and a fork there
        // could cut that call from its result: only a reply that ends its turn is a fork point.
        var forkPoint = fromUser || EndsTurn(allMessages, target) ? RevertCoordinator.ForkPoint(kept) : null;
        var agentSessionId = forkPoint is not null && _sourceAgent is not null
            ? await _sourceAgent.TryForkSessionForOtherChatAsync(forkPoint)
            : null;
        Serilog.Log.Information("[Fork] Forking chat {SessionId} at {MessageId} ({Kept} messages kept): {Outcome}",
            sourceSessionId, messageId, kept.Count, agentSessionId is not null ? $"agent session forked as {agentSessionId}"
                : forkPoint is null ? "no fork point, the new chat gets the conversation as text"
                : "the agent did not fork its session, the new chat gets the conversation as text");

        var checkpointIds = kept.Select(m => m.CheckpointId).OfType<string>().ToList();
        IReadOnlyDictionary<string, string> checkpointCopies = _checkpointService is not null && checkpointIds.Count > 0
            ? await _checkpointService.CloneCheckpointsAsync(checkpointIds)
            : new Dictionary<string, string>();

        var source = (await store.GetSessionIndexAsync()).FirstOrDefault(e => e.Id == sourceSessionId);
        var entry = await store.CreateSessionAsync($"Fork of {source?.Title ?? "chat"}");
        // Locked, or the agent's title for its copy ("<title> (fork)", published without the suffix) would replace it.
        entry.TitleLocked = true;
        entry.AcpSessionId = agentSessionId;
        entry.AgentCommand = source?.AgentCommand;
        entry.AgentArgs = source?.AgentArgs;
        await store.UpdateSessionAsync(entry);

        var messages = kept.Select(m => Copy(m, checkpointCopies)).ToList();
        foreach (var message in messages)
            await store.AppendMessageAsync(entry.Id, message);

        return new ForkResult
        {
            Entry = entry,
            Messages = messages,
            AgentForked = agentSessionId is not null,
            Draft = fromUser ? target : null
        };
    }

    // True when nothing but notices and compactions follows the reply before the next user message.
    private static bool EndsTurn(IReadOnlyList<PersistedMessage> messages, PersistedMessage reply) =>
        messages
            .Where(m => m.Ordinal > reply.Ordinal && m.ItemType is not (nameof(ChatItemType.Notice) or nameof(ChatItemType.Compaction)))
            .OrderBy(m => m.Ordinal)
            .FirstOrDefault() is not { } next
        || next.ItemType == nameof(ChatItemType.User);

    // A message of the new chat: the same content, with its own copy of the checkpoint (none if it could not be copied:
    // the original belongs to the source chat, whose revert may delete it).
    private static PersistedMessage Copy(PersistedMessage message, IReadOnlyDictionary<string, string> checkpointCopies)
    {
        var copy = JsonSerializer.Deserialize<PersistedMessage>(JsonSerializer.Serialize(message))!;
        copy.CheckpointId = message.CheckpointId is not null && checkpointCopies.TryGetValue(message.CheckpointId, out var id) ? id : null;
        return copy;
    }
}
