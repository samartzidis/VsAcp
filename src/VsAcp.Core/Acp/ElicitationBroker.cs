using System.Collections.Concurrent;
using VsAcp.Core.Abstractions;

namespace VsAcp.Core.Acp;

public class ElicitationBroker : IElicitationBroker
{
    readonly ConcurrentDictionary<string, TaskCompletionSource<AcpElicitationResult>> _pending = new();

    public event Action<AcpElicitationRequest>? ElicitationRequested;
    public event Action<string>? ElicitationDismissed;

    public Task<AcpElicitationResult> SubmitAsync(AcpElicitationRequest request, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<AcpElicitationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.RequestId] = tcs;

        cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(request.RequestId, out var removed))
                removed.TrySetCanceled(cancellationToken);
        });

        ElicitationRequested?.Invoke(request);

        return tcs.Task;
    }

    public void Resolve(string requestId, AcpElicitationResult result)
    {
        if (_pending.TryRemove(requestId, out var tcs))
            tcs.TrySetResult(result);
    }

    public void Dismiss(string requestId)
    {
        if (_pending.TryRemove(requestId, out var tcs))
            tcs.TrySetResult(AcpElicitationResult.Cancelled());
        ElicitationDismissed?.Invoke(requestId);
    }

    public void CancelAll()
    {
        foreach (var key in _pending.Keys.ToArray())
        {
            if (_pending.TryRemove(key, out var tcs))
                tcs.TrySetResult(AcpElicitationResult.Cancelled());
        }
    }
}
