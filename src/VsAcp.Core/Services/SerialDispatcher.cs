namespace VsAcp.Core.Services;

/// <summary>
/// Runs actions through a host dispatch delegate (the hop to the UI thread) strictly in the order they were
/// posted, from whatever thread posts them. The host delegate alone cannot promise that: Visual Studio's runs inline
/// when already on the UI thread and queues otherwise, so the end of a turn (raised on the UI thread) used to
/// overtake the chunk updates still queued from the agent's reader thread, and the reply was saved without its tail.
/// </summary>
/// <remarks>
/// One drain at a time goes through the host delegate and runs everything queued, so a burst of updates costs one
/// hop. While nothing is queued a post still reaches the delegate at once (inline on the UI thread in Visual Studio).
/// An action posted from inside a running action joins the queue behind it rather than nesting.
/// </remarks>
public sealed class SerialDispatcher
{
    private readonly Action<Action> _dispatch;
    private readonly object _lock = new();
    private readonly Queue<Action> _queue = new();
    // A drain has been handed to the host and has not yet found the queue empty.
    private bool _draining;

    public SerialDispatcher(Action<Action> dispatch) => _dispatch = dispatch;

    public void Post(Action action)
    {
        lock (_lock)
        {
            _queue.Enqueue(action);
            if (_draining) return;
            _draining = true;
        }
        _dispatch(Drain);
    }

    private void Drain()
    {
        while (true)
        {
            Action next;
            lock (_lock)
            {
                if (_queue.Count == 0)
                {
                    _draining = false;
                    return;
                }
                next = _queue.Dequeue();
            }

            try
            {
                next();
            }
            catch (Exception ex)
            {
                // one failing update must not strand the ones behind it
                Serilog.Log.Warning(ex, "[SerialDispatcher] A queued session update failed");
            }
        }
    }
}
