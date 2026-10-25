namespace AcpProtocol;

public static class AgentMethods
{
    public const string Authenticate = "authenticate";
    public const string Initialize = "initialize";
    public const string SessionCancel = "session/cancel";
    public const string SessionClose = "session/close";
    public const string SessionDelete = "session/delete";
    /// <summary>Unstable (see UnstableTypes.cs).</summary>
    public const string SessionFork = "session/fork";
    public const string SessionLoad = "session/load";
    public const string SessionNew = "session/new";
    public const string SessionPrompt = "session/prompt";
    public const string SessionSetMode = "session/set_mode";
    public const string SessionSetConfigOption = "session/set_config_option";
}

public static class ClientMethods
{
    public const string ElicitationCreate = "elicitation/create";
    public const string ElicitationComplete = "elicitation/complete";
    public const string FsReadTextFile = "fs/read_text_file";
    public const string FsWriteTextFile = "fs/write_text_file";
    public const string SessionRequestPermission = "session/request_permission";
    public const string SessionUpdate = "session/update";
    public const string TerminalCreate = "terminal/create";
    public const string TerminalKill = "terminal/kill";
    public const string TerminalOutput = "terminal/output";
    public const string TerminalRelease = "terminal/release";
    public const string TerminalWaitForExit = "terminal/wait_for_exit";
}