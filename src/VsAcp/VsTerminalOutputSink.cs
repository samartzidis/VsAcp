using System;
using System.Collections.Concurrent;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using VsAcp.Core.Abstractions;

namespace VsAcp;

/// <summary>
/// Streams agent terminal output into per-terminal VS Output window panes.
/// Each terminal created by the agent gets its own named pane ("ACP: {command}").
/// </summary>
internal sealed class VsTerminalOutputSink : ITerminalOutputSink
{
    private readonly ConcurrentDictionary<string, IVsOutputWindowPane> _panes = new();
    private readonly AsyncPackage _package;

    public VsTerminalOutputSink(AsyncPackage package)
    {
        _package = package;
    }

    public void OnTerminalCreated(string terminalId, string command)
    {
        UiThread.Post("Terminal pane create", () =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var outputWindow = Package.GetGlobalService(typeof(SVsOutputWindow)) as IVsOutputWindow;
            if (outputWindow is null) return;

            var paneGuid = Guid.NewGuid();
            var paneName = $"ACP: {command}";

            if (ErrorHandler.Succeeded(outputWindow.CreatePane(ref paneGuid, paneName, fInitVisible: 1, fClearWithSolution: 0)))
            {
                if (ErrorHandler.Succeeded(outputWindow.GetPane(ref paneGuid, out var pane)))
                {
                    _panes[terminalId] = pane;
                    pane.Activate();
                }
            }
        });
    }

    public void OnTerminalOutput(string terminalId, string data)
    {
        if (!_panes.TryGetValue(terminalId, out var pane)) return;

        UiThread.Post("Terminal output", () =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            pane.OutputStringThreadSafe(data + Environment.NewLine);
        });
    }

    public void OnTerminalExited(string terminalId, int exitCode)
    {
        if (!_panes.TryGetValue(terminalId, out var pane)) return;

        UiThread.Post("Terminal exit", () =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            pane.OutputStringThreadSafe($"{Environment.NewLine}[Process exited with code {exitCode}]{Environment.NewLine}");
        });
    }

    public void OnTerminalReleased(string terminalId)
    {
        _panes.TryRemove(terminalId, out _);
    }
}
