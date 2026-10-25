using Microsoft.VisualStudio.Shell;

namespace VsAcp;

/// <summary>
/// The one place that moves work onto the Visual Studio UI thread, replacing direct
/// <c>Dispatcher.BeginInvoke/Invoke</c> and bare <c>JoinableTaskFactory.RunAsync</c> calls (which the
/// threading analyzers flag: VSTHRD001 / VSTHRD110). Going through <see cref="ThreadHelper.JoinableTaskFactory"/>
/// keeps the work visible to VS's deadlock detection, and <see cref="Post(string, Action)"/> makes sure a
/// failure ends up in the log instead of vanishing with an unobserved task. The names follow the Win32 /
/// <c>SynchronizationContext</c> convention: <c>Post</c> queues and returns, <c>Send</c> waits for the work.
/// </summary>
internal static class UiThread
{
    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread without waiting for it. Always queued, even when
    /// already on the UI thread (like <c>Dispatcher.BeginInvoke</c>), so callers never run re-entrantly.
    /// An exception is logged as a warning under <paramref name="area"/>; it never reaches the caller.
    /// </summary>
    internal static void Post(string area, Action action) =>
        Post(area, () =>
        {
            action();
            return Task.CompletedTask;
        });

    /// <inheritdoc cref="Post(string, Action)"/>
    internal static void Post(string area, Func<Task> action)
    {
        var jtf = ThreadHelper.JoinableTaskFactory;
        _ = jtf.RunAsync(async () =>
        {
            try
            {
                await jtf.SwitchToMainThreadAsync(alwaysYield: true);
                await action();
            }
            catch (OperationCanceledException)
            {
                // shutting down: nothing to report
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[UiThread] {Area} failed", area);
            }
        });
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread and waits for it (like <c>Dispatcher.Invoke</c>): inline when
    /// already there, otherwise the calling thread blocks until the UI thread has run it. An exception
    /// propagates to the caller.
    /// </summary>
    internal static void Send(Action action)
    {
        var jtf = ThreadHelper.JoinableTaskFactory;
        jtf.Run(async () =>
        {
            await jtf.SwitchToMainThreadAsync();
            action();
        });
    }
}
