using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
// The MCP SDK and the ACP library both define McpServer, Implementation and JsonRpcMessage: unqualified names here
// are the SDK's; the ACP ones are aliased.
using AcpHttpHeader = AcpProtocol.HttpHeader;
using AcpMcpServer = AcpProtocol.McpServer;
using AcpMcpServerHttp = AcpProtocol.McpServerHttp;

namespace VsAcp.Core.IdeTools;

/// <summary>
/// The IDE tools MCP server (ADR-061): one per Visual Studio process, on <c>http://127.0.0.1:&lt;free port&gt;/mcp/</c>,
/// started by the first chat that offers it to its agent. Each chat tab has its own bearer token
/// (<see cref="CreateEndpoint"/>). Stateless: every POST gets its own SDK transport and server, so there are no
/// sessions to keep and no server-to-client messages.
/// </summary>
public sealed class IdeToolsServer : IDisposable
{
    public const string ServerName = "visual-studio";
    const int MaxBodyBytes = 1024 * 1024;
    const int StartAttempts = 5;

    readonly IOutputPaneReader _reader;
    readonly IIdeStateReader _state;
    readonly string[] _hiddenPaneIds;
    readonly Func<bool> _isEnabled;
    readonly ILogger<IdeToolsServer> _logger;
    readonly ILoggerFactory _sdkLoggers;
    readonly ConcurrentDictionary<string, Chat> _chats = new(StringComparer.Ordinal);
    readonly CancellationTokenSource _stopping = new();
    readonly object _gate = new();
    readonly Action? _loadingSdk;
    HttpListener? _listener;
    string? _url;
    // The MCP SDK failed to load (a missing or mismatched assembly); the tools stay off until Visual Studio restarts.
    volatile bool _sdkUnavailable;

    /// <param name="hiddenPaneIds">Panes the tools never list or read.</param>
    /// <param name="isEnabled">The user's setting, read at every session setup and every request.</param>
    /// <param name="loggers">The server's and the tools' logging; the MCP SDK gets only its warnings and errors.</param>
    /// <param name="loadingSdk">Runs just before a chat's tools are built, the first use of the MCP SDK. Tests make it
    /// throw to stand in for an SDK that fails to load.</param>
    public IdeToolsServer(IOutputPaneReader reader, IIdeStateReader state, IEnumerable<string> hiddenPaneIds, Func<bool> isEnabled,
        ILoggerFactory loggers, Action? loadingSdk = null)
    {
        _reader = reader;
        _state = state;
        _hiddenPaneIds = hiddenPaneIds.ToArray();
        _isEnabled = isEnabled;
        _logger = loggers.CreateLogger<IdeToolsServer>();
        _sdkLoggers = new WarningsOnlyLoggerFactory(loggers);
        _loadingSdk = loadingSdk;
    }

    /// <summary>The URL agents are given, once a chat started the server; null before.</summary>
    public string? Url => _url;

    /// <summary>
    /// One chat tab's access: dispose it when the tab closes, which revokes its token. Touches nothing of the MCP SDK,
    /// so every tab can have one whatever the setting: the SDK is first used when the endpoint offers the server
    /// (<see cref="Offer"/>), only while the setting is on.
    /// </summary>
    public IdeToolsEndpoint CreateEndpoint(string chatId) => new(this, chatId, NewToken());

    internal void Revoke(string token) => _chats.TryRemove(token, out _);

    internal bool IsEnabled => _isEnabled();

    /// <summary>
    /// Registers the chat's tools if this is its first offer, starts the listener if needed, and returns the URL; null
    /// when the server can't be offered. Never throws: an MCP SDK that fails to load costs the feature, not the chat.
    /// </summary>
    internal string? Offer(string token, string chatId)
    {
        if (_sdkUnavailable)
            return null;

        if (!_chats.ContainsKey(token))
        {
            try
            {
                Register(token, chatId);
            }
            catch (Exception ex)
            {
                // FileNotFoundException, FileLoadException or TypeLoadException from a broken binding, which won't fix
                // itself; or anything else the SDK throws while building the tools.
                _sdkUnavailable = true;
                _logger.LogWarning(ex, "[IdeTools] The MCP SDK could not be loaded; IDE tools are off until Visual Studio restarts");
                return null;
            }
        }

        return EnsureStarted();
    }

    // Everything that touches the MCP SDK on a tab's path is here. Not inlined, so a missing or mismatched SDK assembly
    // throws when this is called (inside Offer's try) instead of when Offer itself is compiled.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    void Register(string token, string chatId)
    {
        _loadingSdk?.Invoke();
        var tools = new OutputPaneTools(_reader, _hiddenPaneIds, chatId, _logger, _state).CreateTools()
            .Concat(new IdeStateTools(_state, chatId, _logger).CreateTools());
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = ServerName, Version = "1.0.0" },
            ToolCollection = [.. tools]
        };
        _chats[token] = new Chat(chatId, options);
    }

    /// <summary>Starts listening if no chat has yet. Returns the URL, or null when the listener could not start.</summary>
    internal string? EnsureStarted()
    {
        lock (_gate)
        {
            if (_url is not null || _stopping.IsCancellationRequested)
                return _url;

            for (var attempt = 1; attempt <= StartAttempts; attempt++)
            {
                var url = $"http://127.0.0.1:{FreeLoopbackPort()}/mcp/";
                var listener = new HttpListener();
                listener.Prefixes.Add(url);
                try
                {
                    listener.Start();
                }
                catch (HttpListenerException ex)
                {
                    // The port was taken between finding it and binding it, or http.sys refused the prefix.
                    _logger.LogWarning("[IdeTools] Could not listen on {Url} (attempt {Attempt}): {Error}", url, attempt, ex.Message);
                    listener.Close();
                    continue;
                }

                _listener = listener;
                _url = url;
                _ = Task.Run(() => AcceptLoopAsync(listener));
                _logger.LogInformation("[IdeTools] Server listening on {Url}", url);
                return url;
            }

            _logger.LogWarning("[IdeTools] Server not started; the agent gets no IDE tools");
            return null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopping.IsCancellationRequested) return;
            _stopping.Cancel();
            _listener?.Close();
            _listener = null;
        }
        _chats.Clear();
    }

    static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    static string NewToken()
    {
        var bytes = new byte[32];
        using (var random = RandomNumberGenerator.Create())
            random.GetBytes(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    async Task AcceptLoopAsync(HttpListener listener)
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException or InvalidOperationException)
            {
                if (!_stopping.IsCancellationRequested)
                    _logger.LogWarning("[IdeTools] Server stopped accepting requests: {Error}", ex.Message);
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            // No browser is a legitimate caller; this also stops a DNS-rebinding page that found the port.
            if (request.Headers["Origin"] is not null)
            {
                Reject(response, HttpStatusCode.Forbidden, "Requests from browsers are not accepted.");
                return;
            }
            if (!TryGetChat(request.Headers["Authorization"], out var chat))
            {
                Reject(response, HttpStatusCode.Unauthorized, "Missing or unknown bearer token.");
                return;
            }
            if (!_isEnabled())
            {
                Reject(response, HttpStatusCode.Forbidden, "The user turned Visual Studio's IDE tools off.");
                return;
            }
            if (request.HttpMethod != "POST")
            {
                // Stateless: no SSE stream (GET) and no session to end (DELETE). MCP clients try the GET stream at
                // every session start and the spec lets a server decline it with 405, so that is not worth a warning.
                response.AddHeader("Allow", "POST");
                Reject(response, HttpStatusCode.MethodNotAllowed, "Only POST is supported.", LogLevel.Debug);
                return;
            }

            var body = await ReadBodyAsync(request);
            if (body is null)
            {
                Reject(response, (HttpStatusCode)413, $"The request body is over {MaxBodyBytes} bytes.");
                return;
            }

            JsonRpcMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<JsonRpcMessage>(body, McpJsonUtilities.DefaultOptions);
            }
            catch (JsonException)
            {
                message = null;
            }
            if (message is null)
            {
                Reject(response, HttpStatusCode.BadRequest, "The body is not a JSON-RPC message.");
                return;
            }

            await ServeAsync(chat, message, response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IdeTools] Request failed");
            try { response.StatusCode = (int)HttpStatusCode.InternalServerError; } catch { /* headers already sent */ }
        }
        finally
        {
            try { response.Close(); } catch { /* the client went away */ }
        }
    }

    async Task ServeAsync(Chat chat, JsonRpcMessage message, HttpListenerResponse response)
    {
        var transport = new StreamableHttpServerTransport(_sdkLoggers) { Stateless = true };
        var server = McpServer.Create(transport, (McpServerOptions)chat.Options, _sdkLoggers);
        var run = server.RunAsync(_stopping.Token);
        try
        {
            response.ContentType = "text/event-stream";
            response.AddHeader("Cache-Control", "no-cache");
            if (!await transport.HandlePostRequestAsync(message, response.OutputStream, _stopping.Token))
                response.StatusCode = (int)HttpStatusCode.Accepted; // a notification: nothing to answer
        }
        finally
        {
            await server.DisposeAsync();
            await transport.DisposeAsync();
            try { await run; } catch (OperationCanceledException) { }
        }
    }

    bool TryGetChat(string? authorization, out Chat chat)
    {
        const string scheme = "Bearer ";
        chat = null!;
        return authorization is not null
            && authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            && _chats.TryGetValue(authorization.Substring(scheme.Length).Trim(), out chat!);
    }

    // The body, or null when it is over MaxBodyBytes (checked while reading, so a chunked body can't get around it).
    static async Task<byte[]?> ReadBodyAsync(HttpListenerRequest request)
    {
        if (request.ContentLength64 > MaxBodyBytes)
            return null;

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.InputStream.ReadAsync(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    void Reject(HttpListenerResponse response, HttpStatusCode status, string reason, LogLevel level = LogLevel.Warning)
    {
        _logger.Log(level, "[IdeTools] Refused a request: {Status} {Reason}", (int)status, reason);
        response.StatusCode = (int)status;
        response.ContentType = "text/plain; charset=utf-8";
        var bytes = System.Text.Encoding.UTF8.GetBytes(reason);
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    // Options is the SDK's McpServerOptions, typed object so that nothing a tab's path loads (this record, the _chats
    // dictionary, Offer) names an SDK type.
    sealed record Chat(string ChatId, object Options);
}

/// <summary>
/// One chat tab's view of the <see cref="IdeToolsServer"/>: the MCP server entry its agent is offered at session
/// setup. Disposing it (with the tab) revokes the tab's token.
/// </summary>
public sealed class IdeToolsEndpoint : IIdeToolsEndpoint, IDisposable
{
    readonly IdeToolsServer _server;
    readonly string _token;

    internal IdeToolsEndpoint(IdeToolsServer server, string chatId, string token)
    {
        _server = server;
        ChatId = chatId;
        _token = token;
    }

    public string ChatId { get; }

    public AcpMcpServer? Describe()
    {
        if (!_server.IsEnabled || _server.Offer(_token, ChatId) is not { } url)
            return null;

        return new AcpMcpServerHttp
        {
            Name = IdeToolsServer.ServerName,
            Url = url,
            Headers = [new AcpHttpHeader { Name = "Authorization", Value = "Bearer " + _token }]
        };
    }

    public void Dispose() => _server.Revoke(_token);
}

/// <summary>What <see cref="Acp.AcpSessionService"/> needs from the IDE tools: the server entry to offer, if any.</summary>
public interface IIdeToolsEndpoint
{
    /// <summary>The MCP server entry for this chat's agent, or null when the user turned the tools off or the server
    /// could not start.</summary>
    AcpMcpServer? Describe();
}
