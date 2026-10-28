using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace VsAcp.Core.IdeTools;

/// <summary>
/// Runs a tool's work so that an unexpected failure (a <c>COMException</c> from DTE, a bug of ours) is logged and reaches
/// the agent as a message it can act on. Without it the MCP SDK answers only "an error occurred invoking the tool" and
/// the exception is lost. <see cref="McpException"/> (an error meant for the agent) and cancellation pass through as
/// they are.
/// </summary>
static class ToolGuard
{
    /// <param name="what">What the tool was doing, completing "Visual Studio could not …", e.g. "read the Build pane".</param>
    public static async Task<T> RunAsync<T>(ILogger logger, string chatId, string tool, string what, Func<Task<T>> work)
    {
        try
        {
            return await work();
        }
        catch (Exception ex) when (ex is not McpException and not OperationCanceledException)
        {
            logger.LogWarning(ex, "[IdeTools] {Chat} {Tool} failed", chatId, tool);
            throw new McpException(
                $"Visual Studio could not {what} ({ex.GetType().Name}: {ex.Message}). Try again; if it keeps failing, tell the user.");
        }
    }
}

/// <summary>
/// Gives the MCP SDK our logging, warnings and errors only: at Debug and Trace it logs whole JSON-RPC messages, which
/// would copy pane text and editor state into the VsAcp log and the Output window.
/// </summary>
sealed class WarningsOnlyLoggerFactory : ILoggerFactory
{
    readonly ILoggerFactory _inner;

    public WarningsOnlyLoggerFactory(ILoggerFactory inner) => _inner = inner;

    public ILogger CreateLogger(string categoryName) => new Filtered(_inner.CreateLogger(categoryName));

    public void AddProvider(ILoggerProvider provider) => _inner.AddProvider(provider);

    // The host owns the inner factory.
    public void Dispose() { }

    sealed class Filtered : ILogger
    {
        readonly ILogger _inner;

        public Filtered(ILogger inner) => _inner = inner;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning && _inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                _inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
