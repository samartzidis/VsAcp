using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using VsAcp.Core.Abstractions;
using AcpProtocol;

namespace VsAcp.Core.Acp.Handlers;

public sealed class TerminalHandler : IDisposable
{
    readonly ConcurrentDictionary<string, ManagedTerminal> _terminals = new();
    readonly ILogger<TerminalHandler> _logger;
    readonly ITerminalOutputSink? _outputSink;

    public TerminalHandler(ILogger<TerminalHandler> logger, ITerminalOutputSink? outputSink = null)
    {
        _logger = logger;
        _outputSink = outputSink;
    }

    public ValueTask<CreateTerminalResponse> CreateAsync(
        CreateTerminalRequest request, CancellationToken cancellationToken)
    {
        var terminalId = Guid.NewGuid().ToString("N");

        var psi = new ProcessStartInfo
        {
            FileName = request.Command,
            WorkingDirectory = request.Cwd ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

        // args/env are optional in the protocol (absent = null)
        psi.Arguments = string.Join(" ", request.Args ?? []);

        foreach (var env in request.Env ?? [])
            psi.Environment[env.Name] = env.Value;

        var process = Process.Start(psi)
            ?? throw new AcpException($"Failed to start terminal process: {request.Command}", default, -32002);

        // Close stdin immediately - terminal commands are non-interactive;
        // leaving stdin open causes interactive programs (e.g., `more`) to hang.
        try { process.StandardInput.Close(); } catch { }

        var terminal = new ManagedTerminal(terminalId, process);
        _terminals[terminalId] = terminal;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                terminal.OutputBuffer.AppendLine(e.Data);
                _outputSink?.OnTerminalOutput(terminalId, e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                terminal.OutputBuffer.AppendLine(e.Data);
                _outputSink?.OnTerminalOutput(terminalId, e.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _logger.LogInformation("Created terminal {TerminalId}: {Command}", terminalId, request.Command);
        _outputSink?.OnTerminalCreated(terminalId, request.Command);

        return new(new CreateTerminalResponse { TerminalId = terminalId });
    }

    public ValueTask<TerminalOutputResponse> OutputAsync(
        TerminalOutputRequest request, CancellationToken cancellationToken)
    {
        if (!_terminals.TryGetValue(request.TerminalId, out var terminal))
            throw new AcpException($"Terminal not found: {request.TerminalId}", default, -32003);

        var output = terminal.OutputBuffer.ToString();

        return new(new TerminalOutputResponse
        {
            Output = output,
            Truncated = false
        });
    }

    public async ValueTask<WaitForTerminalExitResponse> WaitForExitAsync(
        WaitForTerminalExitRequest request, CancellationToken cancellationToken)
    {
        if (!_terminals.TryGetValue(request.TerminalId, out var terminal))
            throw new AcpException($"Terminal not found: {request.TerminalId}", default, -32003);

        var timeout = TimeSpan.FromMinutes(5);
        var exited = await Task.Run(() => terminal.Process.WaitForExit((int)timeout.TotalMilliseconds), cancellationToken);

        if (!exited)
        {
            _logger.LogWarning("Terminal {TerminalId} timed out after {Timeout}, killing", request.TerminalId, timeout);
            try { terminal.Process.Kill(); } catch { }
        }

        var exitCode = terminal.Process.HasExited ? terminal.Process.ExitCode : -1;
        _outputSink?.OnTerminalExited(request.TerminalId, exitCode);

        return new WaitForTerminalExitResponse
        {
            ExitCode = (uint?)exitCode
        };
    }

    public ValueTask<KillTerminalResponse> KillAsync(
        KillTerminalRequest request, CancellationToken cancellationToken)
    {
        if (!_terminals.TryGetValue(request.TerminalId, out var terminal))
            throw new AcpException($"Terminal not found: {request.TerminalId}", default, -32003);

        try
        {
            if (!terminal.Process.HasExited)
                terminal.Process.Kill();
        }
        catch (InvalidOperationException) { }

        _logger.LogInformation("Killed terminal {TerminalId}", request.TerminalId);

        return new(new KillTerminalResponse());
    }

    public ValueTask<ReleaseTerminalResponse> ReleaseAsync(
        ReleaseTerminalRequest request, CancellationToken cancellationToken)
    {
        if (_terminals.TryRemove(request.TerminalId, out var terminal))
        {
            try
            {
                if (!terminal.Process.HasExited)
                    terminal.Process.Kill();
            }
            catch (InvalidOperationException) { }

            terminal.Process.Dispose();
            _logger.LogInformation("Released terminal {TerminalId}", request.TerminalId);
            _outputSink?.OnTerminalReleased(request.TerminalId);
        }

        return new(new ReleaseTerminalResponse());
    }

    public void Dispose()
    {
        foreach (var kvp in _terminals)
        {
            try
            {
                if (!kvp.Value.Process.HasExited)
                    kvp.Value.Process.Kill();
                kvp.Value.Process.Dispose();
            }
            catch { }
        }
        _terminals.Clear();
    }

    sealed class ManagedTerminal
    {
        public string Id { get; }
        public Process Process { get; }
        public StringBuilder OutputBuffer { get; } = new();

        public ManagedTerminal(string id, Process process)
        {
            Id = id;
            Process = process;
        }
    }
}
