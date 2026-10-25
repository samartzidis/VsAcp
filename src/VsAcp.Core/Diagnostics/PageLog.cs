using System.Text;
using System.Text.Json;

namespace VsAcp.Core.Diagnostics;

public enum PageLogLevel { Debug, Info, Warning, Error }

/// <summary>One line from the chat page for the extension log.</summary>
public readonly record struct PageLogEntry(PageLogLevel Level, string Text);

/// <summary>
/// Turns the chat page's console output and uncaught exceptions, as WebView2 reports them through the DevTools
/// protocol (<c>Runtime.consoleAPICalled</c>, <c>Runtime.exceptionThrown</c>), into log lines. The page shows agent
/// text, so a line is untrusted: it is cut to <see cref="MaxLength"/> and kept on one line, so it cannot pose as
/// other log entries.
/// </summary>
public static class PageLog
{
    public const int MaxLength = 2000;

    /// <summary>A <c>Runtime.consoleAPICalled</c> event, or null when it cannot be read.</summary>
    public static PageLogEntry? FromConsoleApiCalled(string parametersJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(parametersJson);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            var level = type switch
            {
                "error" or "assert" => PageLogLevel.Error,
                "warning" => PageLogLevel.Warning,
                "debug" => PageLogLevel.Debug,
                _ => PageLogLevel.Info
            };

            var parts = new List<string>();
            if (root.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
                foreach (var arg in args.EnumerateArray())
                    parts.Add(Describe(arg));

            return new PageLogEntry(level, Clean(string.Join(" ", parts)));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A <c>Runtime.exceptionThrown</c> event (an uncaught error or unhandled promise rejection), or null.</summary>
    public static PageLogEntry? FromExceptionThrown(string parametersJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(parametersJson);
            if (!doc.RootElement.TryGetProperty("exceptionDetails", out var details))
                return null;

            var text = details.TryGetProperty("text", out var t) ? t.GetString() : null;
            var description = details.TryGetProperty("exception", out var ex) && ex.TryGetProperty("description", out var d)
                ? d.GetString()
                : null;
            var line = details.TryGetProperty("lineNumber", out var l) && l.TryGetInt32(out var ln) ? ln + 1 : (int?)null;

            var message = string.Join(": ", new[] { text, description }.Where(s => !string.IsNullOrEmpty(s)));
            if (line is not null) message += $" (line {line})";
            return new PageLogEntry(PageLogLevel.Error, Clean(message));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A console argument as the DevTools protocol sends it (a RemoteObject).
    private static string Describe(JsonElement arg)
    {
        if (arg.TryGetProperty("value", out var value))
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
        if (arg.TryGetProperty("unserializableValue", out var unserializable))
            return unserializable.GetString() ?? "";
        if (arg.TryGetProperty("description", out var description))
            return description.GetString() ?? "";
        return arg.TryGetProperty("type", out var type) ? type.GetString() ?? "" : "";
    }

    /// <summary>One line, no control characters, at most <see cref="MaxLength"/> characters.</summary>
    public static string Clean(string text)
    {
        var sb = new StringBuilder(Math.Min(text.Length, MaxLength) + 16);
        var cut = false;
        foreach (var c in text)
        {
            if (sb.Length >= MaxLength) { cut = true; break; }
            if (c == '\n') sb.Append("\\n");
            else if (c == '\r') { }
            else if (c == '\t') sb.Append(' ');
            else if (char.IsControl(c)) sb.Append('?');
            else sb.Append(c);
        }
        if (cut)
        {
            sb.Length = MaxLength;
            sb.Append("... (cut)");
        }
        return sb.ToString();
    }
}

/// <summary>
/// At most <c>maxPerSecond</c> page lines per tab, so a page that logs in a loop (or agent output echoed into the
/// console) cannot flood the extension log. Lines over the limit are counted and reported with the next one let through.
/// </summary>
public sealed class PageLogLimiter
{
    private readonly int _maxPerSecond;
    private readonly Func<DateTime> _now;
    private readonly object _lock = new();
    private DateTime _windowStart = DateTime.MinValue;
    private int _taken;
    private int _dropped;

    public PageLogLimiter(int maxPerSecond = 50, Func<DateTime>? now = null)
    {
        _maxPerSecond = maxPerSecond;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>True when this line may be logged; <paramref name="droppedBefore"/> = lines dropped since the last one that was.</summary>
    public bool TryTake(out int droppedBefore)
    {
        lock (_lock)
        {
            var now = _now();
            if (now - _windowStart >= TimeSpan.FromSeconds(1))
            {
                _windowStart = now;
                _taken = 0;
            }

            if (_taken < _maxPerSecond)
            {
                _taken++;
                droppedBefore = _dropped;
                _dropped = 0;
                return true;
            }

            _dropped++;
            droppedBefore = 0;
            return false;
        }
    }
}
