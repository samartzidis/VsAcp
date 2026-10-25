using VsAcp.Core.Diagnostics;

namespace VsAcp.Core.Tests;

public class PageLogTests
{
    [Fact]
    public void A_console_call_becomes_one_line_of_its_arguments()
    {
        var entry = PageLog.FromConsoleApiCalled("""
            {"type":"info","args":[{"type":"string","value":"messages.load"},{"type":"number","value":3,"description":"3"},
            {"type":"object","className":"Object","description":"Object"},{"type":"undefined"}],"executionContextId":1,"timestamp":1}
            """);

        Assert.Equal(new PageLogEntry(PageLogLevel.Info, "messages.load 3 Object undefined"), entry);
    }

    [Theory]
    [InlineData("error", PageLogLevel.Error)]
    [InlineData("assert", PageLogLevel.Error)]
    [InlineData("warning", PageLogLevel.Warning)]
    [InlineData("debug", PageLogLevel.Debug)]
    [InlineData("log", PageLogLevel.Info)]
    [InlineData("table", PageLogLevel.Info)]
    public void Console_types_map_to_log_levels(string type, PageLogLevel level)
    {
        var entry = PageLog.FromConsoleApiCalled($$"""{"type":"{{type}}","args":[{"type":"string","value":"x"}]}""")!.Value;

        Assert.Equal(level, entry.Level);
    }

    [Fact]
    public void An_uncaught_exception_is_an_error_with_its_description_and_line()
    {
        var entry = PageLog.FromExceptionThrown("""
            {"timestamp":1,"exceptionDetails":{"exceptionId":1,"text":"Uncaught","lineNumber":41,"columnNumber":3,
            "exception":{"type":"object","subtype":"error","className":"TypeError","description":"TypeError: x is not a function\n    at render (spa.js:42:4)"}}}
            """);

        Assert.Equal(new PageLogEntry(PageLogLevel.Error,
            "Uncaught: TypeError: x is not a function\\n    at render (spa.js:42:4) (line 42)"), entry);
    }

    [Fact]
    public void An_unhandled_rejection_without_an_error_object_still_logs_its_text()
    {
        var entry = PageLog.FromExceptionThrown("""{"exceptionDetails":{"text":"Uncaught (in promise)"}}""");

        Assert.Equal("Uncaught (in promise)", entry!.Value.Text);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"exceptionDetails\":")]
    public void Unreadable_events_are_skipped(string json)
    {
        Assert.Null(PageLog.FromConsoleApiCalled(json));
        Assert.Null(PageLog.FromExceptionThrown(json));
    }

    [Fact]
    public void A_line_can_not_pose_as_another_log_entry()
    {
        Assert.Equal("a\\n12:00:00.000 [ERR] forged b?c", PageLog.Clean("a\r\n12:00:00.000 [ERR] forged\tb\u0007c"));
    }

    [Fact]
    public void Long_text_is_cut()
    {
        var cleaned = PageLog.Clean(new string('x', PageLog.MaxLength + 500));

        Assert.Equal(PageLog.MaxLength + "... (cut)".Length, cleaned.Length);
        Assert.EndsWith("... (cut)", cleaned);
        Assert.Equal(new string('x', PageLog.MaxLength), PageLog.Clean(new string('x', PageLog.MaxLength)));
    }

    [Fact]
    public void The_limiter_lets_a_burst_through_up_to_the_limit_and_reports_what_it_dropped()
    {
        var now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        var limiter = new PageLogLimiter(maxPerSecond: 3, () => now);

        Assert.True(limiter.TryTake(out _));
        Assert.True(limiter.TryTake(out _));
        Assert.True(limiter.TryTake(out _));
        Assert.False(limiter.TryTake(out _));
        Assert.False(limiter.TryTake(out _));

        now = now.AddSeconds(1);
        Assert.True(limiter.TryTake(out var dropped));
        Assert.Equal(2, dropped);
        Assert.True(limiter.TryTake(out dropped));
        Assert.Equal(0, dropped);
    }
}
