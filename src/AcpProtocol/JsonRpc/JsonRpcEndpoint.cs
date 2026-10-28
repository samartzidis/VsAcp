using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AcpProtocol;

internal enum JsonRpcErrorCode
{
    ParseError = -32700,
    InvalidRequest = -32600,
    MethodNotFound = -32601,
    InvalidParams = -32602,
    InternalError = -32603
}

internal sealed class JsonRpcEndpoint
{
    readonly Func<CancellationToken, ValueTask<string?>> readFunc;
    readonly Func<string, CancellationToken, ValueTask> writeFunc;
    readonly Func<string, CancellationToken, ValueTask> errorWriteFunc;
    readonly ILogger _logger;

    readonly ConcurrentDictionary<RequestId, TaskCompletionSource<JsonRpcResponse>> pendingRequests = new();
    readonly ConcurrentDictionary<string, Func<JsonRpcRequest, CancellationToken, ValueTask<JsonRpcResponse>>> requestHandlers = new();
    readonly ConcurrentDictionary<string, Func<JsonRpcNotification, CancellationToken, ValueTask>> notificationHandlers = new();
    Func<JsonRpcRequest, CancellationToken, ValueTask<JsonRpcResponse>>? defaultRequestHandler;
    Func<JsonRpcNotification, CancellationToken, ValueTask>? defaultNotificationHandler;
    int nextRequestId = 0;

    public JsonRpcEndpoint(
        Func<CancellationToken, ValueTask<string?>> readFunc,
        Func<string, CancellationToken, ValueTask> writeFunc,
        Func<string, CancellationToken, ValueTask> errorWriteFunc,
        ILogger? logger = null)
    {
        this.readFunc = readFunc;
        this.writeFunc = writeFunc;
        this.errorWriteFunc = errorWriteFunc;
        _logger = logger ?? NullLogger.Instance;
    }

    public void SetRequestHandler(string method, Func<JsonRpcRequest, CancellationToken, ValueTask<JsonRpcResponse>> handler)
    {
        requestHandlers.TryAdd(method, handler);
    }

    public void SetNotificationHandler(string method, Func<JsonRpcNotification, CancellationToken, ValueTask> handler)
    {
        notificationHandlers.TryAdd(method, handler);
    }

    public void SetDefaultRequestHandler(Func<JsonRpcRequest, CancellationToken, ValueTask<JsonRpcResponse>> handler)
    {
        defaultRequestHandler = handler;
    }

    public void SetDefaultNotificationHandler(Func<JsonRpcNotification, CancellationToken, ValueTask> handler)
    {
        defaultNotificationHandler = handler;
    }

    public async Task ReadMessagesAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = null;
            try
            {
                line = await readFunc(cancellationToken).ConfigureAwait(false);
                if (line is null) break; // EOF - process exited
                if (string.IsNullOrWhiteSpace(line)) continue;

                var trimmedLineSpan = line.AsSpan().Trim();
                if (trimmedLineSpan.Length < 2 || trimmedLineSpan[0] != '{' || trimmedLineSpan[^1] != '}') continue;

                if (_logger.IsEnabled(LogLevel.Trace))
                    _logger.LogTrace("ACP recv: {Json}", line);

                var message = JsonSerializer.Deserialize(line, AcpJsonSerializerContext.Default.Options.GetTypeInfo<JsonRpcMessage>()!);

                switch (message)
                {
                    case JsonRpcRequest request:
                        _logger.LogDebug("ACP recv request: method='{Method}' id={RequestId}", request.Method, request.Id);
                        try
                        {
                            JsonRpcResponse response;
                            if (requestHandlers.TryGetValue(request.Method, out var requestHandler))
                            {
                                response = await requestHandler(request, cancellationToken);
                            }
                            else if (defaultRequestHandler != null)
                            {
                                response = await defaultRequestHandler(request, cancellationToken);
                            }
                            else
                            {
                                _logger.LogWarning("ACP no handler for request method '{Method}'", request.Method);
                                response = new JsonRpcResponse
                                {
                                    Id = request.Id,
                                    Error = new()
                                    {
                                        Code = (int)JsonRpcErrorCode.MethodNotFound,
                                        Message = $"Method '{request.Method}' is not available",
                                    }
                                };
                            }

                            var responseJson = JsonSerializer.Serialize(response, AcpJsonSerializerContext.Default.Options.GetTypeInfo<JsonRpcMessage>());
                            if (_logger.IsEnabled(LogLevel.Trace))
                                _logger.LogTrace("ACP send response: {Json}", responseJson);
                            await writeFunc(responseJson, cancellationToken);
                        }
                        catch (NotImplementedException)
                        {
                            _logger.LogWarning("ACP method '{Method}' not implemented", request.Method);
                            await writeFunc(JsonSerializer.Serialize(new JsonRpcResponse
                            {
                                Id = request.Id,
                                Error = new()
                                {
                                    Code = (int)JsonRpcErrorCode.MethodNotFound,
                                    Message = $"Method '{request.Method}' is not available",
                                }
                            }, AcpJsonSerializerContext.Default.Options.GetTypeInfo<JsonRpcMessage>()), cancellationToken);
                        }
                        catch (AcpException acpException)
                        {
                            _logger.LogWarning(acpException, "ACP handler for '{Method}' threw AcpException (code={Code})",
                                request.Method, acpException.Code);
                            await writeFunc(JsonSerializer.Serialize(new JsonRpcResponse
                            {
                                Id = request.Id,
                                Error = new()
                                {
                                    Code = acpException.Code,
                                    Data = acpException.ErrorData,
                                    Message = acpException.Message,
                                }
                            }, AcpJsonSerializerContext.Default.Options.GetTypeInfo<JsonRpcMessage>()), cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "ACP unhandled exception in handler for '{Method}'", request.Method);
                            await errorWriteFunc($"Unhandled exception in handler for '{request.Method}': {ex}", cancellationToken);
                            await writeFunc(JsonSerializer.Serialize(new JsonRpcResponse
                            {
                                Id = request.Id,
                                Error = new()
                                {
                                    Code = (int)JsonRpcErrorCode.InternalError,
                                    Message = $"Internal error handling '{request.Method}': {ex.Message}",
                                }
                            }, AcpJsonSerializerContext.Default.Options.GetTypeInfo<JsonRpcMessage>()), cancellationToken);
                        }
                        break;
                    case JsonRpcResponse response:
                        _logger.LogDebug("ACP recv response: id={ResponseId} hasError={HasError}",
                            response.Id, response.Error is not null);
                        if (pendingRequests.TryRemove(response.Id, out var tcs))
                        {
                            tcs.TrySetResult(response);
                        }
                        else
                        {
                            _logger.LogWarning("ACP received response for unknown request id={ResponseId}", response.Id);
                        }
                        break;
                    case JsonRpcNotification notification:
                        _logger.LogDebug("ACP recv notification: method='{Method}'", notification.Method);
                        if (notificationHandlers.TryGetValue(notification.Method, out var notificationHandler))
                        {
                            await notificationHandler(notification, cancellationToken);
                        }
                        else if (defaultNotificationHandler != null)
                        {
                            await defaultNotificationHandler(notification, cancellationToken);
                        }
                        else
                        {
                            _logger.LogDebug("ACP no handler for notification method '{Method}'", notification.Method);
                        }
                        break;
                    default:
                        throw new AcpException($"Invalid response type: {message?.GetType().Name}", null, (int)JsonRpcErrorCode.InternalError);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("ACP message loop cancelled");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ACP failed to process message");
                if (_logger.IsEnabled(LogLevel.Trace) && line is not null)
                    _logger.LogTrace("ACP raw message that caused failure: {Json}", line);
                await errorWriteFunc(ex.ToString(), cancellationToken);
            }
        }

        // Fail all pending requests so callers don't hang forever after the pipe closes.
        var error = new IOException("ACP connection closed");
        foreach (var key in pendingRequests.Keys)
        {
            if (pendingRequests.TryRemove(key, out var pending))
                pending.TrySetException(error);
        }
    }

    public async ValueTask SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        if (message is JsonRpcRequest request && !request.Id.IsValid)
        {
            request.Id = Interlocked.Increment(ref nextRequestId);
        }

        var json = JsonSerializer.Serialize(message, AcpJsonSerializerContext.Default.Options.GetTypeInfo<JsonRpcMessage>());

        var method = message switch
        {
            JsonRpcRequest r => r.Method,
            JsonRpcNotification n => n.Method,
            _ => null
        };
        _logger.LogDebug("ACP send {MessageType}: method='{Method}'", message.GetType().Name, method);
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("ACP send: {Json}", RedactForLog(json));

        await writeFunc(json, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.Id.IsValid) request.Id = Interlocked.Increment(ref nextRequestId);

        var json = JsonSerializer.Serialize(request, AcpJsonSerializerContext.Default.Options.GetTypeInfo<JsonRpcRequest>());

        _logger.LogDebug("ACP send request: method='{Method}' id={RequestId}", request.Method, request.Id);
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("ACP send: {Json}", RedactForLog(json));

        var tcs = new TaskCompletionSource<JsonRpcResponse>();
        pendingRequests.TryAdd(request.Id, tcs);

        // A cancelled request stops waiting; an answer that still arrives is logged as an unknown id.
        var id = request.Id;
        using var registration = cancellationToken.Register(() =>
        {
            if (pendingRequests.TryRemove(id, out var pending))
                pending.TrySetCanceled(cancellationToken);
        });

        await writeFunc(json, cancellationToken).ConfigureAwait(false);

        return await tcs.Task;
    }

    // Session setup can carry MCP server credentials (an HTTP server's Authorization header, a stdio server's env), and
    // the Trace log goes to a file and the Output window: mask those values there. Fails closed.
    internal static string RedactForLog(string json)
    {
        if (json.IndexOf("\"mcpServers\"", StringComparison.Ordinal) < 0)
            return json;
        try
        {
            var root = JsonNode.Parse(json);
            if (root?["params"]?["mcpServers"] is not JsonArray servers)
                return json;

            foreach (var server in servers.OfType<JsonObject>())
            {
                foreach (var key in new[] { "headers", "env" })
                {
                    if (server[key] is not JsonArray pairs) continue;
                    foreach (var pair in pairs.OfType<JsonObject>())
                    {
                        // No characters JSON escapes, so the log reads as written.
                        if (pair.ContainsKey("value"))
                            pair["value"] = "(redacted)";
                    }
                }
            }
            return root.ToJsonString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return "<message with mcpServers, not logged>";
        }
    }
}
