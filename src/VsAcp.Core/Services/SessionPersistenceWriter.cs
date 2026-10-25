using System.Threading.Channels;
using VsAcp.Core.Abstractions;
using VsAcp.Core.Models;

namespace VsAcp.Core.Services;

/// <summary>
/// Serializes all of a tab's session persistence (message appends and session-index mutations)
/// through a single-consumer queue.
///
/// Replaces per-write fire-and-forget <c>Task.Run</c> + <c>catch {}</c>, which raced for the
/// store lock (nondeterministic order) and swallowed failures. Enqueue is non-blocking; a single
/// background task drains the queue and runs each work item in enqueue order. Failures are
/// surfaced via <see cref="PersistFailed"/> instead of being silently dropped.
/// </summary>
public sealed class SessionPersistenceWriter : IDisposable
{
    private readonly record struct WorkItem(Func<Task> Run, string Description);

    private readonly ISessionStore _store;
    private readonly string _sessionId;
    private readonly Channel<WorkItem> _channel;
    private readonly Task _drainTask;

    /// <summary>Raised (on the drain thread) when a queued write fails. First arg describes the work item.</summary>
    public event Action<string, Exception>? PersistFailed;

    public SessionPersistenceWriter(ISessionStore store, string sessionId)
    {
        _store = store;
        _sessionId = sessionId;
        _channel = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _drainTask = Task.Run(DrainAsync);
    }

    /// <summary>Queues a message append for ordered persistence. Non-blocking.</summary>
    public void Enqueue(PersistedMessage message) => _channel.Writer.TryWrite(new WorkItem(
        () => _store.AppendMessageAsync(_sessionId, message),
        $"message {message.Id} (ord {message.Ordinal})"));

    /// <summary>
    /// Queues a session-index read-modify-write for ordered persistence. The mutation receives the
    /// store and is responsible for loading/updating the relevant <c>SessionEntry</c>. Non-blocking.
    /// </summary>
    public void EnqueueIndexMutation(Func<ISessionStore, Task> mutate, string description)
        => _channel.Writer.TryWrite(new WorkItem(() => mutate(_store), description));

    private async Task DrainAsync()
    {
        var reader = _channel.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var item))
            {
                try
                {
                    await item.Run().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    PersistFailed?.Invoke(item.Description, ex);
                }
            }
        }
    }

    public void Dispose()
    {
        // Signal the drain loop to stop. Do NOT block on _drainTask.Wait() here:
        // this is called from the VS main thread (inside a JoinableTaskFactory context)
        // and blocking can deadlock if any drain-task continuation gets marshalled back to the
        // main thread. The drain task runs entirely with ConfigureAwait(false) and will finish
        // on the thread pool without further main-thread involvement.
        _channel.Writer.TryComplete();
    }
}
