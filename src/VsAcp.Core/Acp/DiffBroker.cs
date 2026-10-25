using Microsoft.Extensions.Logging;
using VsAcp.Core.Abstractions;

namespace VsAcp.Core.Acp;

public class DiffBroker : IDiffBroker
{
    readonly ILogger<DiffBroker> _logger;

    public event Action<DiffRequest>? DiffCompleted;

    public DiffBroker(ILogger<DiffBroker> logger)
    {
        _logger = logger;
    }

    public void FireDiffCompleted(DiffRequest request)
    {
        _logger.LogInformation("DiffCompleted: {FilePath} (requestId={RequestId})",
            request.FilePath, request.RequestId);
        DiffCompleted?.Invoke(request);
    }
}
