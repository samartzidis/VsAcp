using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VsAcp.Core.Abstractions;
using AcpProtocol;
using VsAcp.Core.Configuration;

namespace VsAcp.Core.Acp.Handlers;

public sealed class PermissionHandler
{
    readonly IPermissionBroker _broker;
    readonly IOptions<VsAcpOptions> _options;
    readonly ILogger<PermissionHandler> _logger;

    public PermissionHandler(IPermissionBroker broker, IOptions<VsAcpOptions> options, ILogger<PermissionHandler> logger)
    {
        _broker = broker;
        _options = options;
        _logger = logger;
    }

    public async ValueTask<RequestPermissionResponse> HandleAsync(
        RequestPermissionRequest request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Permission request received: options=[{Options}]",
            string.Join(", ", request.Options.Select(o => $"{o.Name}({o.Kind})")));

        if (_broker.AutoApprove)
        {
            // Prefer allow-once over allow-always; never pick a reject option on the user's behalf.
            var allow = Array.Find(request.Options, o => o.Kind == PermissionOptionKind.AllowOnce)
                ?? Array.Find(request.Options, o => o.Kind == PermissionOptionKind.AllowAlways);

            if (allow is not null)
            {
                _logger.LogInformation("Auto-approved permission: {OptionId} ({Name}, {Kind})", allow.OptionId, allow.Name, allow.Kind);
                return new RequestPermissionResponse
                {
                    Outcome = new RequestPermissionOutcomeSelected { OptionId = allow.OptionId }
                };
            }

            _logger.LogInformation("Auto-approve is on but the request has no allow option; asking the user");
        }

        var toolCallTitle = request.ToolCall.Title ?? "Permission request";

        _logger.LogInformation("Prompting user for permission: '{Title}'", toolCallTitle);

        var acpRequest = new AcpPermissionRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Title = toolCallTitle,
            Options = request.Options.Select(o => new AcpPermissionOption
            {
                OptionId = o.OptionId,
                Name = o.Name,
                Kind = o.Kind.ToString()
            }).ToList()
        };

        var selectedOptionId = await _broker.SubmitAsync(acpRequest, cancellationToken);

        if (selectedOptionId is null)
        {
            _logger.LogInformation("Permission cancelled by user");
            return new RequestPermissionResponse { Outcome = new RequestPermissionOutcomeCancelled() };
        }

        _logger.LogInformation("Permission granted by user: {OptionId}", selectedOptionId);
        return new RequestPermissionResponse
        {
            Outcome = new RequestPermissionOutcomeSelected { OptionId = selectedOptionId }
        };
    }
}
