using System.IO;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;

namespace VsAcp;

internal sealed class VsOutputWindowSink : ILogEventSink
{
    private readonly IVsOutputWindowPane _pane;
    private readonly ITextFormatter _formatter;

    public VsOutputWindowSink(IVsOutputWindowPane pane, ITextFormatter formatter)
    {
        _pane = pane;
        _formatter = formatter;
    }

    public void Emit(LogEvent logEvent)
    {
        var writer = new StringWriter();
        _formatter.Format(logEvent, writer);
        var text = writer.ToString();

        // OutputStringThreadSafe is safe to call from any thread
#pragma warning disable VSTHRD010
        _pane.OutputStringThreadSafe(text);
#pragma warning restore VSTHRD010
    }
}
