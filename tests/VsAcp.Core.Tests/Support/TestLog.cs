using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace VsAcp.Core.Tests.Support;

/// <summary>Collects everything the code under test logs, so a failing test can print it.</summary>
public sealed class TestLog
{
    private readonly ConcurrentQueue<string> _lines = new();

    public void Add(string line) => _lines.Enqueue(line);

    public IReadOnlyList<string> Lines => _lines.ToArray();

    public override string ToString() => string.Join(Environment.NewLine, _lines);
}

public sealed class TestLogger<T> : ILogger<T>
{
    private readonly TestLog _log;

    public TestLogger(TestLog log) => _log = log;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var line = $"{logLevel,-11} {typeof(T).Name}: {formatter(state, exception)}";
        if (exception is not null) line += Environment.NewLine + exception;
        _log.Add(line);
    }
}
