namespace VsAcp.Core.Tests.Support;

/// <summary>
/// A stand-in for Visual Studio's UI thread: one thread that runs queued work and is the
/// <see cref="SynchronizationContext"/> that awaits resume on, like a WPF dispatcher. Work reaches it two ways,
/// mirroring the extension: awaited continuations (<see cref="Post"/>, normal priority) and
/// <see cref="PostBackground"/> (<c>UiThread.Post</c> = <c>SwitchToMainThreadAsync(alwaysYield: true)</c>).
/// With <c>normalOvertakesBackground</c> a normal item runs before any queued background item, which is how a
/// dispatcher treats priorities; without it everything is one first-in-first-out queue.
/// </summary>
public sealed class SimulatedUiThread : SynchronizationContext, IDisposable
{
    private readonly object _lock = new();
    private readonly Queue<Action> _normal = new();
    private readonly Queue<Action> _background = new();
    private readonly bool _normalOvertakesBackground;
    private readonly int _backgroundCostMs;
    private readonly Thread _thread;
    private bool _stopping;

    /// <param name="normalOvertakesBackground">Normal-priority work (continuations) jumps ahead of queued background work.</param>
    /// <param name="backgroundCostMs">How long each background item takes, standing in for a busy UI thread
    /// (every chunk update is also a WebView2 message in Visual Studio).</param>
    public SimulatedUiThread(bool normalOvertakesBackground, int backgroundCostMs = 0)
    {
        _normalOvertakesBackground = normalOvertakesBackground;
        _backgroundCostMs = backgroundCostMs;
        _thread = new Thread(Loop) { IsBackground = true, Name = "SimulatedUiThread" };
        _thread.Start();
    }

    public bool IsUiThread => Thread.CurrentThread == _thread;

    public override void Post(SendOrPostCallback d, object? state) => Enqueue(_normal, () => d(state));

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (IsUiThread)
        {
            d(state);
            return;
        }

        using var done = new ManualResetEventSlim();
        Exception? error = null;
        Post(_ =>
        {
            try { d(state); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        }, null);
        done.Wait();
        if (error is not null) throw error;
    }

    /// <summary>Queues work the way <c>UiThread.Post</c> does: never inline, behind what is already waiting.</summary>
    public void PostBackground(Action action)
        => Enqueue(_normalOvertakesBackground ? _background : _normal, () =>
        {
            if (_backgroundCostMs > 0) Thread.Sleep(_backgroundCostMs);
            action();
        });

    /// <summary>The view-model's <c>Dispatch</c>: inline when already on the UI thread, otherwise queued.</summary>
    public void Dispatch(Action action)
    {
        if (IsUiThread) action();
        else PostBackground(action);
    }

    /// <summary>Starts <paramref name="work"/> on the UI thread, so the awaits inside it resume there.</summary>
    public Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ =>
        {
            try
            {
                work().ContinueWith(t =>
                {
                    if (t.IsFaulted) done.TrySetException(t.Exception!.InnerExceptions);
                    else if (t.IsCanceled) done.TrySetCanceled();
                    else done.TrySetResult(t.Result);
                }, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        }, null);
        return done.Task;
    }

    private void Enqueue(Queue<Action> queue, Action action)
    {
        lock (_lock)
        {
            queue.Enqueue(action);
            Monitor.Pulse(_lock);
        }
    }

    private void Loop()
    {
        SetSynchronizationContext(this);
        while (true)
        {
            Action action;
            lock (_lock)
            {
                while (_normal.Count == 0 && _background.Count == 0)
                {
                    if (_stopping) return;
                    Monitor.Wait(_lock);
                }

                action = _normal.Count > 0 ? _normal.Dequeue() : _background.Dequeue();
            }

            try { action(); }
            catch { /* a throwing item must not kill the pump; the test sees its effect */ }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _stopping = true;
            Monitor.PulseAll(_lock);
        }
        _thread.Join(2000);
    }
}
