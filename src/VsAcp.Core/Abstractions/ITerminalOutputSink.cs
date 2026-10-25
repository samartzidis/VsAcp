namespace VsAcp.Core.Abstractions;

/// <summary>
/// Optional sink for terminal output. When registered, the <see cref="Acp.Handlers.TerminalHandler"/>
/// forwards process output in real time. The VS host implements this to write to an IVsOutputWindowPane.
/// </summary>
public interface ITerminalOutputSink
{
    void OnTerminalCreated(string terminalId, string command);
    void OnTerminalOutput(string terminalId, string data);
    void OnTerminalExited(string terminalId, int exitCode);
    void OnTerminalReleased(string terminalId);
}
