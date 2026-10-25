using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VsAcp.Core.Configuration;

namespace VsAcp.Core.Acp;

public sealed class AgentProcessHost : IDisposable
{
    readonly IOptions<VsAcpOptions> _options;
    readonly ILogger<AgentProcessHost> _logger;
    ProcessJobObject? _jobObject;
    Process? _process;

    public AgentProcessHost(IOptions<VsAcpOptions> options, ILogger<AgentProcessHost> logger)
    {
        _options = options;
        _logger = logger;
    }

    public TextReader? StandardOutput => _process?.StandardOutput;
    public TextWriter? StandardInput => _process?.StandardInput;
    public bool IsRunning => _process is { HasExited: false };

    public event Action? ProcessExited;

    public void Start()
    {
        if (IsRunning)
            return;

        var opts = _options.Value;

        if (string.IsNullOrWhiteSpace(opts.AgentCommand))
            throw new AgentConfigurationException(
                "Agent command is not configured.");

        var (fileName, arguments) = ResolveCommand(opts.AgentCommand, opts.AgentArgs);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = opts.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        psi.Arguments = arguments;

        foreach (var kvp in opts.AgentEnv)
            psi.Environment[kvp.Key] = kvp.Value;

        _logger.LogInformation("Starting agent process: {FileName} {Args}", fileName, arguments);

        try
        {
            _process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start agent process: {opts.AgentCommand}");
        }
        catch (Win32Exception ex)
        {
            var msg = $"Could not start agent process. " +
                $"FileName: '{fileName}', Arguments: '{arguments}', WorkingDirectory: '{opts.WorkingDirectory}'. " +
                $"Original AgentCommand setting: '{opts.AgentCommand}'. " +
                $"OS error: {ex.Message} (code {ex.NativeErrorCode})";
            throw new AgentConfigurationException(msg, ex);
        }

        _jobObject = new ProcessJobObject();
        _jobObject.AssignProcess(_process.Handle);

        _process.StandardInput.AutoFlush = true;

        _process.EnableRaisingEvents = true;
        _process.Exited += OnProcessExited;

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                _logger.LogWarning("Agent stderr: {Data}", e.Data);
        };
        _process.BeginErrorReadLine();
    }

    /// <summary>
    /// Kills the agent and everything it started. A launcher such as <c>cmd /c npx ...</c> holds the stdio pipes
    /// too, so killing only the top process can leave the pipe open and pending requests waiting forever.
    /// </summary>
    public void Kill()
    {
        if (_process is null)
            return;

        // KILL_ON_JOB_CLOSE: closing the job terminates every process in it.
        _jobObject?.Dispose();
        _jobObject = null;

        try
        {
            if (!_process.HasExited)
            {
                _logger.LogInformation("Killing agent process (PID {Pid})", _process.Id);
                _process.Kill();
            }
            // TerminateProcess is asynchronous; Start() right after must not see the old process as running.
            _process.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
            // Process already exited
        }
        catch (Win32Exception)
        {
            // Already terminating (the job closed it first)
        }
    }

    void OnProcessExited(object? sender, EventArgs e)
    {
        _logger.LogInformation("Agent process exited with code {ExitCode}", _process?.ExitCode);
        ProcessExited?.Invoke();
    }

    public void Dispose()
    {
        // Diagnostic: when an agent unexpectedly stops working after a revert/reload,
        // this log identifies whether the host was disposed (e.g. by the DI container
        // being torn down on solution reload) vs. the agent process exiting on its own.
        _logger.LogInformation("AgentProcessHost.Dispose called (pid={Pid})", _process?.Id);
        Kill();
        if (_process is not null)
        {
            _process.Exited -= OnProcessExited;
            _process.Dispose();
            _process = null;
        }
        _jobObject?.Dispose();
        _jobObject = null;
    }

    /// <summary>
    /// .ps1 files can't be executed directly via CreateProcess; .cmd/.bat files need
    /// cmd.exe /c to work reliably with stdio redirection. This rewrites the
    /// FileName/Arguments so the user can point AgentCommand at a script directly.
    /// Bare names without an extension (e.g. "npx") are resolved via PATHEXT search
    /// to mimic cmd.exe behaviour - if the match is a .cmd/.bat the wrap is applied
    /// automatically so the user never needs to specify the extension.
    /// </summary>
    static (string FileName, string Arguments) ResolveCommand(string command, string[] args)
    {
        var joinedArgs = string.Join(" ", args);
        var extension = Path.GetExtension(command);

        if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            var quoted = command.Contains(' ') ? $"\"{command}\"" : command;
            return ("pwsh", $"-NoProfile -ExecutionPolicy Bypass -File {quoted} {joinedArgs}".Trim());
        }

        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            var quoted = command.Contains(' ') ? $"\"{command}\"" : command;
            return ("cmd.exe", $"/c {quoted} {joinedArgs}".Trim());
        }

        // Bare name with no extension and no path separator - mimic cmd.exe PATHEXT search
        // so that e.g. "npx" (which exists as npx.cmd on Windows) works without requiring
        // the user to spell out "npx.cmd".
        if (string.IsNullOrEmpty(extension) && !command.Contains('\\') && !command.Contains('/'))
        {
            var resolved = ResolveOnPath(command);
            if (resolved is not null)
            {
                var resolvedExt = Path.GetExtension(resolved);
                if (resolvedExt.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                    resolvedExt.Equals(".bat", StringComparison.OrdinalIgnoreCase))
                {
                    var quoted = resolved.Contains(' ') ? $"\"{resolved}\"" : resolved;
                    return ("cmd.exe", $"/c {quoted} {joinedArgs}".Trim());
                }
                if (resolvedExt.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
                {
                    var quoted = resolved.Contains(' ') ? $"\"{resolved}\"" : resolved;
                    return ("pwsh", $"-NoProfile -ExecutionPolicy Bypass -File {quoted} {joinedArgs}".Trim());
                }
            }
        }

        return (command, joinedArgs);
    }

    /// <summary>
    /// Searches PATH for <paramref name="bare"/> trying each PATHEXT extension in order,
    /// mimicking cmd.exe resolution. Returns the full path of the first match, or null.
    /// </summary>
    static string? ResolveOnPath(string bare)
    {
        var pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
        var extensions = pathExt.Split(';');
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';');

        foreach (var dir in pathDirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir.Trim(), bare + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }
}
