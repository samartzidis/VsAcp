using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using VsAcp.Core.Abstractions;
using VsAcp.Core.Configuration;

namespace VsAcp.Core.Acp;

public class PermissionBroker : IPermissionBroker
{
    sealed record Pending(AcpPermissionRequest Request, TaskCompletionSource<string?> Tcs);

    readonly ConcurrentDictionary<string, Pending> _pending = new();

    public event Action<AcpPermissionRequest>? PermissionRequested;

    public bool AutoApprove { get; private set; }

    public Task<string?> SubmitAsync(AcpPermissionRequest request, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.RequestId] = new Pending(request, tcs);

        cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(request.RequestId, out var removed))
                removed.Tcs.TrySetCanceled(cancellationToken);
        });

        PermissionRequested?.Invoke(request);

        return tcs.Task;
    }

    public void Resolve(string requestId, string? selectedOptionId)
    {
        if (_pending.TryRemove(requestId, out var pending))
            pending.Tcs.TrySetResult(selectedOptionId);
    }

    public int SetAutoApprove(bool enabled)
    {
        AutoApprove = enabled;
        if (!enabled) return 0;

        // A prompt may already be waiting; approve it now so the user doesn't have to click once more.
        var approved = 0;
        foreach (var key in _pending.Keys.ToArray())
        {
            if (!_pending.TryGetValue(key, out var pending)) continue;

            var allow = PickAllowOption(pending.Request.Options);
            if (allow is null) continue; // nothing safe to pick: leave it for the user

            if (_pending.TryRemove(key, out var removed))
            {
                removed.Tcs.TrySetResult(allow);
                approved++;
            }
        }
        return approved;
    }

    /// <summary>
    /// Prefers a one-time grant over a permanent one: auto-approve is a per-tab convenience and
    /// should not quietly write "always allow" rules into the agent. Returns null if the request
    /// offers no allow option, so a reject option is never chosen on the user's behalf.
    /// </summary>
    public static string? PickAllowOption(IReadOnlyList<AcpPermissionOption> options)
        => (options.FirstOrDefault(o => o.Kind == "AllowOnce")
            ?? options.FirstOrDefault(o => o.Kind == "AllowAlways"))?.OptionId;

    public void CancelAll()
    {
        foreach (var key in _pending.Keys.ToArray())
        {
            if (_pending.TryRemove(key, out var pending))
                pending.Tcs.TrySetResult(null);
        }
    }
}
