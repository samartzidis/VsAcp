using System.Text.Json;
using Microsoft.Extensions.Logging;
using VsAcp.Core.Acp.Handlers;
using AcpProtocol;

namespace VsAcp.Core.Acp;

public sealed class AcpClientHandler : IAcpClient
{
    readonly SessionUpdateAdapter _sessionUpdateAdapter;
    readonly PermissionHandler _permissionHandler;
    readonly ElicitationHandler _elicitationHandler;
    readonly FileSystemHandler _fileSystemHandler;
    readonly TerminalHandler _terminalHandler;
    readonly ILogger<AcpClientHandler> _logger;

    public AcpClientHandler(
        SessionUpdateAdapter sessionUpdateAdapter,
        PermissionHandler permissionHandler,
        ElicitationHandler elicitationHandler,
        FileSystemHandler fileSystemHandler,
        TerminalHandler terminalHandler,
        ILogger<AcpClientHandler> logger)
    {
        _sessionUpdateAdapter = sessionUpdateAdapter;
        _permissionHandler = permissionHandler;
        _elicitationHandler = elicitationHandler;
        _fileSystemHandler = fileSystemHandler;
        _terminalHandler = terminalHandler;
        _logger = logger;
    }

    public ValueTask<RequestPermissionResponse> RequestPermissionAsync(
        RequestPermissionRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("ACP request: session/request_permission");
        return _permissionHandler.HandleAsync(request, cancellationToken);
    }

    public ValueTask<CreateElicitationResponse> CreateElicitationAsync(
        CreateElicitationRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("ACP request: elicitation/create - mode={Mode}", request.Mode);
        return _elicitationHandler.HandleAsync(request, cancellationToken);
    }

    public ValueTask CompleteElicitationAsync(
        CompleteElicitationNotification notification, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("ACP notification: elicitation/complete - {Id}", notification.ElicitationId);
        return _elicitationHandler.CompleteAsync(notification);
    }

    public ValueTask SessionNotificationAsync(
        SessionNotification notification, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("SessionNotification: updateType={UpdateType} sessionId={SessionId}",
            notification.Update?.GetType().Name ?? "(null)", notification.SessionId);
        return _sessionUpdateAdapter.HandleAsync(notification, cancellationToken);
    }

    public ValueTask<ReadTextFileResponse> ReadTextFileAsync(
        ReadTextFileRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("ACP request: fs/read_text_file - {Path}", request.Path);
        return _fileSystemHandler.ReadAsync(request, cancellationToken);
    }

    public ValueTask<WriteTextFileResponse> WriteTextFileAsync(
        WriteTextFileRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("ACP request: fs/write_text_file - {Path}", request.Path);
        return _fileSystemHandler.WriteAsync(request, cancellationToken);
    }

    public ValueTask<CreateTerminalResponse> CreateTerminalAsync(
        CreateTerminalRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("ACP request: terminal/create - {Command}", request.Command);
        return _terminalHandler.CreateAsync(request, cancellationToken);
    }

    public ValueTask<TerminalOutputResponse> TerminalOutputAsync(
        TerminalOutputRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("ACP request: terminal/output - {TerminalId}", request.TerminalId);
        return _terminalHandler.OutputAsync(request, cancellationToken);
    }

    public ValueTask<ReleaseTerminalResponse> ReleaseTerminalAsync(
        ReleaseTerminalRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("ACP request: terminal/release - {TerminalId}", request.TerminalId);
        return _terminalHandler.ReleaseAsync(request, cancellationToken);
    }

    public ValueTask<WaitForTerminalExitResponse> WaitForTerminalExitAsync(
        WaitForTerminalExitRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("ACP request: terminal/wait_for_exit - {TerminalId}", request.TerminalId);
        return _terminalHandler.WaitForExitAsync(request, cancellationToken);
    }

    public ValueTask<KillTerminalResponse> KillTerminalCommandAsync(
        KillTerminalRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("ACP request: terminal/kill - {TerminalId}", request.TerminalId);
        return _terminalHandler.KillAsync(request, cancellationToken);
    }

    public ValueTask<JsonElement> ExtMethodAsync(
        string method, JsonElement request, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("Unknown ACP extension method: {Method} params={Params}", method, request.ToString());
        throw new NotImplementedException($"Unknown ACP extension method: {method}");
    }

    public ValueTask ExtNotificationAsync(
        string method, JsonElement notification, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("Unknown ACP extension notification: {Method} params={Params}", method, notification.ToString());
        return default;
    }
}
