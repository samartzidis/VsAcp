using System.ComponentModel.Design;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using VsAcp.Core.Abstractions;
using VsAcp.Core.DependencyInjection;
using VsAcp.Core.Services;
using VsAcp.Controls;
using VsAcp.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Utilities.UnifiedSettings;
using Serilog;
using Serilog.Formatting.Display;
using VsAcp.Core.Configuration;
using VsAcp.InlineDiff;
using VsAcp.Options;
using VsAcp.ToolWindows;
using Task = System.Threading.Tasks.Task;

namespace VsAcp;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideBindingPath]
[ProvideAutoLoad(UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideSettingsManifest(PackageRelativeManifestFile = @"UnifiedSettings\registration.json")]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideToolWindow(typeof(VsAcpToolWindow), Style = VsDockStyle.Tabbed, Window = EnvDTE.Constants.vsWindowKindOutput)][Guid("c3d4e5f6-a7b8-4c9d-0e1f-2a3b4c5d6e7f")]
public sealed class VsAcpPackage : AsyncPackage, IVsSolutionEvents
{
    private static VsAcpPackage? _instance;

    // Must match guidVsAcpCmdSet / cmdidOpenChat in VsAcpPackage.vsct.
    private static readonly Guid CommandSet = new("6f1d2c8a-3b4e-4a7f-9c21-5d8e0b7a4f13");
    private const int OpenChatCommandId = 0x0100;

    internal static SessionListViewModel? SessionListVM => _instance?._sessionListViewModel;

    private SessionListViewModel? _sessionListViewModel;
    private ISessionStore? _sessionStore;
    // Tools > Options > ACP Agent (see VsAcpSettings); replaced whole on every settings change.
    private AgentSettings _agentSettings = VsAcpSettings.Read(null);
    private IDisposable? _settingsSubscription;
    private ISettingsManager? _unifiedSettings;
    private readonly Dictionary<string, Microsoft.Extensions.DependencyInjection.ServiceProvider> _sessionProviders = new();
    private readonly Dictionary<string, ChatSessionViewModel> _chatViewModels = new();
    private readonly Dictionary<string, InlineDiffManager> _sessionManagers = new();

    // One extra-folder list per solution folder, shared by all its tabs (so the chip shows the same list everywhere).
    private readonly Dictionary<string, ExtraFoldersController> _extraFolders = new(StringComparer.OrdinalIgnoreCase);
    private string? _solutionDirectory;
    private string? _previousSolutionDirectory;
    private uint _solutionEventsCookie;
    private uint _selectionEventsCookie;
    private IVsOutputWindowPane? _outputPane;
    // Read by the log pipeline on any thread, written on the UI thread by ApplySettings.
    // Starts on, like the setting's default, so startup lines before the first settings read are kept.
    private static volatile bool _fileLoggingEnabled = true;

    public static bool IsLoaded => _instance is not null;

    /// <summary>Raised on the UI thread after the package has fully initialized.</summary>
    internal static event Action? Initialized;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await base.InitializeAsync(cancellationToken, progress);
        _instance = this;

        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        InitializeLogging();

        if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commands)
        {
            commands.AddCommand(new MenuCommand(
                (_, _) => _ = JoinableTaskFactory.RunAsync(ShowChatSessionWindowAsync),
                new CommandID(CommandSet, OpenChatCommandId)));
        }

        await LoadSettingsAsync(cancellationToken);

        _sessionListViewModel = new SessionListViewModel();

        _sessionListViewModel.SessionRemoved += session =>
        {
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                await DeleteAgentSessionAsync(session.Id);
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                GetTabbedChat()?.CloseTab(session);
                DisposeSessionProvider(session.Id);
            });
        };

        // Listen for file-open requests from rendered markdown
        ChatWebView.FileOpenRequested += OnFileOpenRequested;


        // Look the services up asynchronously, then hop to the UI thread once to subscribe (both Advise calls
        // are UI-thread only).
        var solutionService = await GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
        var monSel = await GetServiceAsync(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        // Listen for solution open/close/switch events
        solutionService?.AdviseSolutionEvents(this, out _solutionEventsCookie);

        // Track the active document so each chat tab shows it as a context chip
        monSel?.AdviseSelectionEvents(new DocumentMonitor(this), out _selectionEventsCookie);

        Initialized?.Invoke();

        // Load sessions AFTER solution events are wired.  Only load when a
        // real solution/folder is open - no fallback to the user profile,
        // which would create a stale "ghost" session bucket.
        var initialDir = GetSolutionDirectory();
        if (initialDir is not null)
            await SwitchWorkspaceAsync(initialDir);

    
    }

    /// <summary>
    /// Reads the ACP Agent settings from Unified Settings (declared in <c>UnifiedSettings/registration.json</c>)
    /// and re-reads them whenever one changes. If the service is unavailable the declared defaults stay in
    /// place so the package still loads.
    /// </summary>
    private async Task LoadSettingsAsync(CancellationToken cancellationToken)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        try
        {
            _unifiedSettings = await this.GetServiceAsync(typeof(SVsUnifiedSettingsManager)) as ISettingsManager;
            if (_unifiedSettings is null)
            {
                Log.Warning("[Settings] Unified Settings service unavailable; using default settings");
                return;
            }

            ApplySettings();
            _settingsSubscription = _unifiedSettings.GetReader().SubscribeToChanges(
                update => _ = JoinableTaskFactory.RunAsync(async () =>
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    ApplySettings();
                }),
                VsAcpSettings.AllMonikers);
        }
        catch (Exception ex)
        {
            Log.Warning("[Settings] Failed to read settings ({Type}: {Message}); using default settings",
                ex.GetType().Name, ex.Message);
        }
    }

    private void ApplySettings()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var resolved = VsAcpSettings.Read(_unifiedSettings?.GetReader());
        _agentSettings = resolved;
        Controls.ChatWebView.DevToolsEnabled = resolved.EnableChatDevTools;
        _fileLoggingEnabled = resolved.EnableFileLogging;
        Log.Information("[Settings] Preset='{Preset}', AgentCommand='{Cmd}', AgentArguments='{Args}', DevTools={DevTools}, FileLogging={FileLogging}",
            resolved.Preset, resolved.AgentCommand, resolved.AgentArguments, resolved.EnableChatDevTools, resolved.EnableFileLogging);
    }

    /// <summary>
    /// Opens Tools &gt; Options on the ACP Agent page: the Unified Settings UI takes the category moniker
    /// (<c>acpAgent</c>) as the command argument (verified in VS 2026).
    /// </summary>
    internal static void OpenSettings()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_instance?.GetService(typeof(SVsUIShell)) is not IVsUIShell shell)
        {
            Log.Warning("[Settings] Open: IVsUIShell unavailable");
            return;
        }

        var group = Microsoft.VisualStudio.VSConstants.GUID_VSStandardCommandSet97;
        object arg = "acpAgent";
        var hr = shell.PostExecCommand(ref group, (uint)Microsoft.VisualStudio.VSConstants.VSStd97CmdID.ToolsOptions, 0, ref arg);
        Log.Information("[Settings] Open: PostExecCommand(ToolsOptions, '{Arg}') hr=0x{Hr:X8}", arg, hr);
    }

    private ChatSessionViewModel CreateChatViewModel(
        string sessionId,
        string? savedAgentCommand = null,
        string? savedAgentArgs = null,
        string? savedAgentPreset = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var options = _agentSettings;

        var currentSolutionDir = GetSolutionDirectory();
        var isNetworkPath = currentSolutionDir is not null && VsAcpPaths.IsNetworkPath(currentSolutionDir);
        var workingDir = currentSolutionDir
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var additionalDirs = currentSolutionDir is not null && !isNetworkPath
            ? GetProjectDirectoriesOutside(currentSolutionDir)
            : Array.Empty<string>();

        var services = new ServiceCollection();

        services.AddLogging(builder => builder.AddSerilog(dispose: false));

        var outputListener = new OutputListener();
        services.AddSingleton(outputListener);
        services.AddSingleton<IOutputListener>(outputListener);

        var terminalOutputSink = new VsTerminalOutputSink(this);
        services.AddSingleton<ITerminalOutputSink>(terminalOutputSink);

        // Use per-session saved command when available (restored sessions); fall back to global options.
        var effectiveCommand = savedAgentCommand ?? options.AgentCommand;
        var effectiveArgs = savedAgentArgs ?? options.AgentArguments;
        // The preset goes with the command: a saved chat keeps its own (none for chats saved before it was recorded).
        var effectivePreset = savedAgentCommand is null ? options.Preset : savedAgentPreset;

        Log.Information("[Settings] Building tab DI container: AgentCommand='{Cmd}', AgentArguments='{Args}'{Override}",
            effectiveCommand, effectiveArgs,
            savedAgentCommand is not null ? " [session-saved]" : "");

        services.AddVsAcpServices(o =>
        {
            o.WorkingDirectory = workingDir;
            o.HasProjectDirectory = currentSolutionDir is not null && !isNetworkPath;
            o.AdditionalDirectories = additionalDirs;
            o.AgentCommand = effectiveCommand;
            o.AgentPreset = effectivePreset;
            o.AgentArgs = effectiveArgs
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        });

        var provider = services.BuildServiceProvider();
        _sessionProviders[sessionId] = provider;

        var acpService = provider.GetRequiredService<VsAcp.Core.Acp.AcpSessionService>();
        IChatService chatService = acpService;
        VsAcp.Core.Abstractions.IAcpSessionLifecycle acpSessionLifecycle = acpService;
        var optionsAccessor = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<VsAcp.Core.Configuration.VsAcpOptions>>();
        var permissionBroker = provider.GetRequiredService<VsAcp.Core.Abstractions.IPermissionBroker>();
        var elicitationBroker = provider.GetRequiredService<VsAcp.Core.Abstractions.IElicitationBroker>();
        var diffBroker = provider.GetRequiredService<VsAcp.Core.Abstractions.IDiffBroker>();
        var checkpointService = provider.GetRequiredService<VsAcp.Core.Abstractions.ICheckpointService>();

        var diffManager = new InlineDiff.InlineDiffManager(diffBroker, this);
        _sessionManagers[sessionId] = diffManager;

        outputListener.StepCompleted += item =>
        {
            if (item.Body is null) return;
            var match = System.Text.RegularExpressions.Regex.Match(
                item.Body, @"file:///[^\s\r\n]+\.plan\.md",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success) return;
            string localPath;
            try { localPath = new Uri(match.Value).LocalPath; }
            catch { return; }
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    VsShellUtilities.OpenDocument(this, localPath, Guid.Empty,
                        out _, out _, out IVsWindowFrame? planFrame);
                    planFrame?.Show();
                }
                catch { }
            });
        };

        var vm = new ChatSessionViewModel(chatService, acpSessionLifecycle, outputListener, optionsAccessor, permissionBroker, checkpointService, elicitationBroker);

        // Folders beyond the solution folder: the auto-detected project folders plus the user's choices for this
        // solution. Only with a local project folder (that is where the choices are saved). The list is shared by
        // every tab of the solution; each tab puts the merged list into its own agent options, so it applies to
        // every session that tab starts or loads.
        if (currentSolutionDir is not null && !isNetworkPath)
        {
            var key = ProjectFolders.Normalize(currentSolutionDir) ?? currentSolutionDir;
            if (_extraFolders.TryGetValue(key, out var extraFolders))
                extraFolders.SetAutoDirectories(additionalDirs); // the solution's projects may have changed since
            else
                _extraFolders[key] = extraFolders = new ExtraFoldersController(
                    currentSolutionDir,
                    additionalDirs,
                    new ExtraFoldersStore(VsAcpPaths.ExtraFoldersPath(currentSolutionDir)));
            vm.AttachExtraFolders(extraFolders, dirs => optionsAccessor.Value.AdditionalDirectories = dirs);
        }

        vm.RevertCompleted += () => _ = JoinableTaskFactory.RunAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            ReloadOpenDocuments();

            // The revert rewrote files: the "N Files" list must match what is on disk now.
            diffManager.ResyncWithDisk();
        });

        return vm;
    }

    /// <summary>
    /// Reloads all open text documents from disk so VS editor buffers reflect restored file content.
    /// Must be called on the UI thread.
    /// </summary>
    private void ReloadOpenDocuments()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (GetService(typeof(SVsRunningDocumentTable)) is not IVsRunningDocumentTable rdt) return;

            rdt.GetRunningDocumentsEnum(out var enumerator);
            if (enumerator is null) return;

            var cookies = new uint[32];
            while (true)
            {
                enumerator.Next((uint)cookies.Length, cookies, out var fetched);
                if (fetched == 0) break;
                for (uint i = 0; i < fetched; i++)
                {
                    try
                    {
                        rdt.GetDocumentInfo(cookies[i],
                            out _, out _, out _,
                            out var moniker,
                            out IVsHierarchy? hierarchy, out _, out _);

                        if (string.IsNullOrEmpty(moniker) || !File.Exists(moniker)) continue;

                        // Ask VS to reload the document from disk
                        if (hierarchy is IVsPersistDocData persist)
                            persist.ReloadDocData(0);
                    }
                    catch { /* best effort per document */ }
                }
            }
        }
        catch { /* best effort */ }
    }

    private static readonly Guid OutputPaneGuid = new("7E2A4A6B-1F3C-4D8E-9B5C-2A1D3E4F6B7A");

    private IVsOutputWindowPane? EnsureOutputPane()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_outputPane is not null)
            return _outputPane;

        if (GetService(typeof(SVsOutputWindow)) is not IVsOutputWindow outputWindow)
            return null;

        var guid = OutputPaneGuid;
        if (outputWindow.GetPane(ref guid, out _outputPane) != Microsoft.VisualStudio.VSConstants.S_OK || _outputPane is null)
        {
            outputWindow.CreatePane(ref guid, "VsAcp", fInitVisible: 1, fClearWithSolution: 0);
            outputWindow.GetPane(ref guid, out _outputPane);
        }

        return _outputPane;
    }

    private void InitializeLogging()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VsAcp", "logs", "vsacp-.log");

        const string outputTemplate = "{Timestamp:HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";

        var outputPane = EnsureOutputPane();
        var logConfig = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
            // On until the setting says otherwise (ApplySettings); the rolling sink creates its file on the first event.
            .WriteTo.Conditional(_ => _fileLoggingEnabled, sink => sink.File(logPath,
                rollingInterval: Serilog.RollingInterval.Day, outputTemplate: outputTemplate));

        if (outputPane is not null)
            logConfig = logConfig.WriteTo.Sink(new VsOutputWindowSink(outputPane, new MessageTemplateTextFormatter(outputTemplate)));

        Log.Logger = logConfig.CreateLogger();
        Log.Information("[VsAcp] Logging initialised");
    }

    private string? GetSolutionDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (GetService(typeof(SVsSolution)) is IVsSolution solution)
        {
            solution.GetSolutionInfo(out string solutionDir, out _, out _);
            if (!string.IsNullOrEmpty(solutionDir))
                return solutionDir;
        }
        return null;
    }

    /// <summary>
    /// Directories of loaded projects that live outside <paramref name="solutionDir"/>. Shared with
    /// the agent as ACP <c>additionalDirectories</c> so a solution whose projects sit next to (not
    /// under) the .sln folder is fully visible to it.
    /// </summary>
    private string[] GetProjectDirectoriesOutside(string solutionDir)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (GetService(typeof(SVsSolution)) is not IVsSolution solution)
            return Array.Empty<string>();

        var guid = Guid.Empty;
        if (solution.GetProjectEnum((uint)__VSENUMPROJFLAGS.EPF_LOADEDINSOLUTION, ref guid, out var projects) != Microsoft.VisualStudio.VSConstants.S_OK)
            return Array.Empty<string>();

        var projectDirs = new List<string?>();
        var hierarchy = new IVsHierarchy[1];
        while (projects.Next(1, hierarchy, out var fetched) == Microsoft.VisualStudio.VSConstants.S_OK && fetched == 1)
        {
            if (hierarchy[0].GetProperty(Microsoft.VisualStudio.VSConstants.VSITEMID_ROOT, (int)__VSHPROPID.VSHPROPID_ProjectDir, out var value) == Microsoft.VisualStudio.VSConstants.S_OK
                && value is string projectDir)
                projectDirs.Add(projectDir);
        }

        var result = ProjectFolders.Outside(solutionDir, projectDirs);
        if (result.Length > 0)
            Log.Information("[Workspace] {Count} project folder(s) outside solution dir: {Dirs}", result.Length, string.Join(", ", result));

        return result;
    }

    public static async Task ShowChatSessionWindowAsync()
    {
        if (_instance is null) return;

        await _instance.JoinableTaskFactory.SwitchToMainThreadAsync();

        // If the window already exists and is visible, hide it (toggle off).
        var existing = _instance.FindToolWindow(typeof(VsAcpToolWindow), 0, false);
        if (existing?.Frame is IVsWindowFrame existingFrame)
        {
            if (existingFrame.IsVisible() == Microsoft.VisualStudio.VSConstants.S_OK)
            {
                existingFrame.Hide();
                return;
            }
        }

        var window = await _instance.ShowToolWindowAsync(
            typeof(VsAcpToolWindow), 0, true, _instance.DisposalToken);

        if (window is VsAcpToolWindow tw)
        {
            tw.TabbedChat.BindIfNeeded();
        }

        if (window?.Frame is IVsWindowFrame frame)
        {
            frame.Show();
        }
    }

    private TabbedChatControl? GetTabbedChat()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var window = FindToolWindow(typeof(VsAcpToolWindow), 0, false);
        return (window as VsAcpToolWindow)?.TabbedChat;
    }

    /// <summary>
    /// Pushes updated settings into every per-tab DI container and restarts
    /// running agents so the next message uses the new values.
    /// </summary>

    /// <summary>
    /// Best-effort <c>session/delete</c> on the agent before a removed chat's container is torn down.
    /// Only possible while the tab is open (a live agent process exists) and the agent advertised
    /// <c>sessionCapabilities.delete</c>; otherwise the local delete proceeds alone.
    /// </summary>
    private async Task DeleteAgentSessionAsync(string sessionId)
    {
        if (!_sessionProviders.TryGetValue(sessionId, out var provider)) return;

        try
        {
            var acpService = provider.GetRequiredService<VsAcp.Core.Acp.AcpSessionService>();
            if (!acpService.CanDeleteAgentSession) return;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await acpService.DeleteAgentSessionAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Session] session/delete on agent failed for {SessionId}; local delete continues", sessionId);
        }
    }

    /// <summary>
    /// "Fork from here" in a chat tab: copies the chat up to <paramref name="messageId"/> into a new chat, opens it in a
    /// new tab and primes that tab's agent before its first message.
    /// </summary>
    internal static async Task ForkChatAsync(ChatSessionViewModel source, string messageId)
    {
        if (_instance is not { _sessionListViewModel: { } sessions } package) return;

        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (await source.ForkAsync(messageId) is not { } fork) return;

        await package.JoinableTaskFactory.SwitchToMainThreadAsync();
        var session = sessions.OpenForkedSession(fork.Entry);
        if (package._chatViewModels.TryGetValue(session.Id, out var forked))
            await forked.ContinueForkAsync(fork);
        else
            Log.Warning("[Fork] The tab of forked chat {SessionId} did not open; its agent was not primed", fork.Entry.Id);
    }

    internal static void DisposeSessionProvider(string sessionId)
    {
        if (_instance is null) return;
        if (_instance._chatViewModels.TryGetValue(sessionId, out var vm))
        {
            _instance._chatViewModels.Remove(sessionId);
            vm.Dispose();
        }

        if (_instance._sessionManagers.TryGetValue(sessionId, out var manager))
        {
            _instance._sessionManagers.Remove(sessionId);
            manager.Dispose();
        }

        if (_instance._sessionProviders.TryGetValue(sessionId, out var provider))
        {
            _instance._sessionProviders.Remove(sessionId);
            provider.Dispose();
        }
    }

    internal static void InitializeChatControl(ChatSessionControl control, SessionInfo session)
    {
        if (_instance is null) return;

        ThreadHelper.ThrowIfNotOnUIThread();

        ChatSessionViewModel viewModel;
        try
        {
            viewModel = _instance.CreateChatViewModel(session.Id, session.AgentCommand, session.AgentArgs, session.AgentPreset);
        }
        catch (Exception ex)
        {
            viewModel = new ChatSessionViewModel(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            System.Diagnostics.Debug.WriteLine($"VsAcp: Failed to create chat service: {ex}");
            MessageBox.Show(
                $"VsAcp service init failed:\n\n{ex.Message}\n\n{ex.InnerException?.Message}",
                "VsAcp Debug", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        var manager = _instance._sessionManagers.TryGetValue(session.Id, out var m) ? m : null;
        control.Initialize(viewModel, manager, session.Id);

        _instance._chatViewModels[session.Id] = viewModel;

        // Prime the auto-context chip with whatever document is open right now
        var initialDoc = _instance.GetCurrentActiveDocumentPath();
        if (initialDoc is not null)
            viewModel.SetActiveContextFile(initialDoc);

        if (session.PersistedId is not null
            && _instance._sessionStore is not null)
        {
            viewModel.EnablePersistence(_instance._sessionStore!, session.PersistedId);

            if (!string.IsNullOrEmpty(session.Name) && session.Name != "New Session")
                viewModel.SessionTitle = session.Name;
        }
        else
        {
            viewModel.StartConnection();
        }

        viewModel.SessionInfo = session;

        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatSessionViewModel.SessionTitle))
            {
                session.Name = viewModel.SessionTitle;
            }
        };
    }

    private async Task SwitchWorkspaceAsync(string newSolutionDirectory)
    {
        if (_sessionListViewModel is null) return;

        // _solutionDirectory may be null here if OnAfterCloseSolution just fired
        // (including during a reload). Use _previousSolutionDirectory to detect
        // whether this is a reload (same dir as before the close) vs a genuine switch.
        var previousDir = _previousSolutionDirectory ?? _solutionDirectory;
        if (string.Equals(previousDir, newSolutionDirectory, StringComparison.OrdinalIgnoreCase))
        {
            // Same directory: this is a reload (e.g. triggered by checkpoint restore
            // rewriting files). Restore state without closing chat tabs.
            _solutionDirectory = newSolutionDirectory;
            _previousSolutionDirectory = null;
            _sessionStore ??= new JsonSessionStore(newSolutionDirectory, !VsAcpPaths.IsNetworkPath(newSolutionDirectory), Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonSessionStore>.Instance);
            Serilog.Log.Information("[Workspace] Reload: {Dir}", newSolutionDirectory);
            try
            {
                await _sessionStore.EnsureWorkspaceAsync();
                _sessionListViewModel.Initialize(_sessionStore);
                await _sessionListViewModel.LoadSessionsAsync();
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "[Workspace] Failed to reload workspace: {Dir}", newSolutionDirectory);
            }
            return;
        }

        // Different directory: genuine workspace switch. Close ALL tabs - they belong to a
        // different (or absent) workspace and must not outlive the switch.
        await JoinableTaskFactory.SwitchToMainThreadAsync();
        var tabbedChat = GetTabbedChat();
        Serilog.Log.Information("[Workspace] Closing all tabs (tabbedChat={HasChat})", tabbedChat is not null);
        try
        {
            tabbedChat?.CloseAllTabs();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[Workspace] CloseAllTabs threw - continuing workspace switch");
        }

        _solutionDirectory = newSolutionDirectory;
        _previousSolutionDirectory = null;
        var hasLocalProject = !VsAcpPaths.IsNetworkPath(newSolutionDirectory);
        _sessionStore = new JsonSessionStore(newSolutionDirectory, hasLocalProject, Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonSessionStore>.Instance);
        Serilog.Log.Information("[Workspace] Switch: {Dir} (local={Local})", newSolutionDirectory, hasLocalProject);

        try
        {
            await _sessionStore.EnsureWorkspaceAsync();
            Serilog.Log.Information("[Workspace] EnsureWorkspace complete: {Dir}", newSolutionDirectory);
            _sessionListViewModel.Initialize(_sessionStore);
            await _sessionListViewModel.LoadSessionsAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[Workspace] Failed to switch workspace: {Dir}", newSolutionDirectory);
        }
    }

    // --- IVsSolutionEvents implementation ---

    int IVsSolutionEvents.OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
    {
        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var newDir = GetSolutionDirectory();
            if (newDir is not null)
                await SwitchWorkspaceAsync(newDir);
        });
        return Microsoft.VisualStudio.VSConstants.S_OK;
    }

    int IVsSolutionEvents.OnBeforeCloseSolution(object pUnkReserved) => Microsoft.VisualStudio.VSConstants.S_OK;
    int IVsSolutionEvents.OnAfterCloseSolution(object pUnkReserved)
    {
        // Clear workspace state so the session list reflects "no solution open".
        // Do NOT dispose or close chat tabs here: VS also fires OnAfterCloseSolution
        // during a solution RELOAD (triggered by external file changes such as our
        // checkpoint restore), followed immediately by OnAfterOpenSolution with the
        // same directory. We save the current directory as _previousSolutionDirectory
        // so SwitchWorkspaceAsync can tell a reload (same dir) from a genuine switch
        // (different dir) and only close tabs in the latter case.
        _previousSolutionDirectory = _solutionDirectory;
        _solutionDirectory = null;
        _sessionListViewModel?.Clear();
        return Microsoft.VisualStudio.VSConstants.S_OK;
    }

    int IVsSolutionEvents.OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => Microsoft.VisualStudio.VSConstants.S_OK;
    int IVsSolutionEvents.OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => Microsoft.VisualStudio.VSConstants.S_OK;
    int IVsSolutionEvents.OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => Microsoft.VisualStudio.VSConstants.S_OK;
    int IVsSolutionEvents.OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => Microsoft.VisualStudio.VSConstants.S_OK;
    int IVsSolutionEvents.OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => Microsoft.VisualStudio.VSConstants.S_OK;
    int IVsSolutionEvents.OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => Microsoft.VisualStudio.VSConstants.S_OK;
    int IVsSolutionEvents.OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => Microsoft.VisualStudio.VSConstants.S_OK;

    private void OnActiveDocumentChanged(string? path)
    {
        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            foreach (var vm in _chatViewModels.Values)
                vm.SetActiveContextFile(path);
        });
    }

    private string? GetCurrentActiveDocumentPath()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (GetService(typeof(SVsShellMonitorSelection)) is IVsMonitorSelection monSel)
        {
            monSel.GetCurrentElementValue(2u /* SEID_DocumentFrame */, out var frameObj);
            if (frameObj is IVsWindowFrame frame)
            {
                frame.GetProperty((int)__VSFPROPID.VSFPROPID_pszMkDocument, out var pathObj);
                var path = pathObj as string;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return path;
            }
        }
        return null;
    }

    private sealed class DocumentMonitor : IVsSelectionEvents
    {
        private readonly VsAcpPackage _package;
        public DocumentMonitor(VsAcpPackage package) => _package = package;

        public int OnElementValueChanged(uint elementid, object varValueOld, object varValueNew)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (elementid != 2u /* SEID_DocumentFrame */)
                return Microsoft.VisualStudio.VSConstants.S_OK;

            string? path = null;
            if (varValueNew is IVsWindowFrame frame)
            {
                frame.GetProperty((int)__VSFPROPID.VSFPROPID_pszMkDocument, out var pathObj);
                var p = pathObj as string;
                if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    path = p;
            }
            _package.OnActiveDocumentChanged(path);
            return Microsoft.VisualStudio.VSConstants.S_OK;
        }

        public int OnSelectionChanged(
            IVsHierarchy h1, uint i1, IVsMultiItemSelect m1, ISelectionContainer s1,
            IVsHierarchy h2, uint i2, IVsMultiItemSelect m2, ISelectionContainer s2)
            => Microsoft.VisualStudio.VSConstants.S_OK;

        public int OnCmdUIContextChanged(uint dwCmdID, int fActive)
            => Microsoft.VisualStudio.VSConstants.S_OK;
    }

    private void OnFileOpenRequested(string rawPath)
    {
        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            var solutionDirectory = _solutionDirectory;

            // Path parsing and the disk lookups (including the suffix search) run off the UI thread.
            var link = await Task.Run(() => FileLinkResolver.Resolve(rawPath, solutionDirectory));
            await JoinableTaskFactory.SwitchToMainThreadAsync();

            if (link is null)
            {
                Log.Warning("[OpenFile] File not found for '{Raw}' (solution dir: {SolutionDir})", rawPath, solutionDirectory);
                return;
            }

            var filePath = link.FilePath;
            var line = link.Line;
            try
            {
                VsShellUtilities.OpenDocument(this, filePath, Guid.Empty,
                    out _, out _, out IVsWindowFrame? frame);
                frame?.Show();

                if (line > 0 && frame is not null)
                {
                    // Navigate to the specific line
                    if (VsShellUtilities.GetTextView(frame) is var textView && textView is not null)
                    {
                        textView.SetCaretPos(line - 1, 0);
                        textView.CenterLines(line - 1, 1);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"VsAcp: Failed to open file: {ex.Message}");
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (disposing)
        {
            ChatWebView.FileOpenRequested -= OnFileOpenRequested;

            _settingsSubscription?.Dispose();
            _settingsSubscription = null;

            if (_solutionEventsCookie != 0)
            {
                if (GetService(typeof(SVsSolution)) is IVsSolution solutionService)
                {
                    solutionService.UnadviseSolutionEvents(_solutionEventsCookie);
                }
                _solutionEventsCookie = 0;
            }

            if (_selectionEventsCookie != 0)
            {
                if (GetService(typeof(SVsShellMonitorSelection)) is IVsMonitorSelection monSel)
                {
                    monSel.UnadviseSelectionEvents(_selectionEventsCookie);
                }
                _selectionEventsCookie = 0;
            }

            foreach (var manager in _sessionManagers.Values)
                manager.Dispose();
            _sessionManagers.Clear();

            foreach (var provider in _sessionProviders.Values)
                provider.Dispose();
            _sessionProviders.Clear();
        }
        base.Dispose(disposing);
    }
}
