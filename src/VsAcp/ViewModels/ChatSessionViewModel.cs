using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VsAcp.Core.Abstractions;
using VsAcp.Core.Acp;
using AcpProtocol;
using VsAcp.Core.Configuration;
using VsAcp.Core.Models;
using VsAcp.Core.Services;
using VsAcp.ToolWindows;
using Microsoft.Extensions.Options;

namespace VsAcp.ViewModels;

public partial class ChatSessionViewModel : ObservableObject, IDisposable
{
    private readonly IChatService? _chatService;
    private readonly IAcpSessionLifecycle? _acpSessionLifecycle;
    private int _userMsgCounter;
    // Per-process token ensures user/transient message IDs never collide with IDs from a
    // previously-saved session that are restored into the same ChatSession on reload.
    private readonly string _idPrefix = Guid.NewGuid().ToString("N")[..8];

    // Single in-memory source of truth for this session's messages: ordering, identity, creation
    // sequence (persisted as PersistedMessage.Ordinal), and step->message mapping all live here.
    // The WebView and the persisted JSONL are projections of this aggregate.
    private readonly ChatSession _session = new();

    // Maps an element-id prefix to a session-unique id (single shared counter for turns, restore
    // notices, and connection errors so WebView element ids never collide).
    private string NextElementId(string prefix) => $"{prefix}-{_idPrefix}-{++_userMsgCounter}";

    // The non-UI half of this tab (agent output into the session, persistence, restore, revert, stop). The
    // headless tests drive the same class, so this view-model only adds bindable state on top of it.
    private readonly ChatSessionController _controller;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _inputText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isBusy;

    /// <summary>True while the inline "Revert to here" confirmation is open; blocks sending so no turn starts between the preview and the confirm.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isRevertConfirmPending;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(IsInputAllowed))]
    private bool _isInitializing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(IsInputAllowed))]
    private bool _isErrored;

    /// <summary>False when the input box should be locked due to initialisation or a fatal configuration error.</summary>
    public bool IsInputAllowed => !IsInitializing && !IsErrored;

    private CancellationTokenSource? _sendCts;

    public ObservableCollection<ImageAttachment> PendingImages { get; } = new();
    public ObservableCollection<FileAttachment> PendingFiles { get; } = new();
    public ObservableCollection<FolderAttachment> PendingFolders { get; } = new();
    public ObservableCollection<CodeSelection> PendingSelections { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveContextFileName))]
    [NotifyPropertyChangedFor(nameof(HasActiveContextFile))]
    private string? _activeContextFilePath;

    [ObservableProperty]
    private bool _isActiveContextEnabled = true;

    public string? ActiveContextFileName =>
        ActiveContextFilePath is not null ? Path.GetFileName(ActiveContextFilePath) : null;

    public bool HasActiveContextFile => ActiveContextFilePath is not null;

    [RelayCommand]
    private void ToggleActiveContext() => IsActiveContextEnabled = !IsActiveContextEnabled;

    // --- Extra folders (ACP additionalDirectories) ---

    /// <summary>The folders shared with the agent besides the solution folder; null until the host attaches it (or when unused).</summary>
    public ExtraFoldersController? ExtraFolders { get; private set; }

    /// <summary>Whether the agent passes extra folders on (null until it has answered <c>initialize</c>).</summary>
    public bool? SupportsAdditionalDirectories => _acpSessionLifecycle?.SupportsAdditionalDirectories;

    /// <summary>The extra folders sent to the agent for this tab's running session; null until the session exists.</summary>
    public IReadOnlyList<string>? ActiveAdditionalDirectories => _acpSessionLifecycle?.ActiveAdditionalDirectories;

    private ExtraFoldersController? _attachedFolders;
    private Action? _foldersChangedHandler;
    private IDisposable? _foldersSubscription;

    /// <summary>
    /// Hands this tab the solution's shared extra-folder list. <paramref name="apply"/> receives the full list now and
    /// after every change (it puts it into this tab's agent options); changes also refresh the composer. Undone by
    /// <see cref="Dispose"/>, because the controller outlives the tab.
    /// </summary>
    public void AttachExtraFolders(ExtraFoldersController controller, Action<string[]> apply)
    {
        DetachExtraFolders();
        ExtraFolders = controller;
        _attachedFolders = controller;
        _foldersChangedHandler = () => Dispatch(() => OnPropertyChanged(nameof(ExtraFolders)));
        controller.Changed += _foldersChangedHandler;
        _foldersSubscription = controller.Subscribe(apply);
        OnPropertyChanged(nameof(ExtraFolders));
    }

    private void DetachExtraFolders()
    {
        if (_attachedFolders is not null && _foldersChangedHandler is not null)
            _attachedFolders.Changed -= _foldersChangedHandler;
        _foldersSubscription?.Dispose();
        _attachedFolders = null;
        _foldersChangedHandler = null;
        _foldersSubscription = null;
    }

    /// <summary>Adds a folder; a refusal is shown as a warning line in the chat. Must run on the UI thread.</summary>
    public void AddExtraFolder(string path)
    {
        if (ExtraFolders?.Add(path) is { } error)
            ShowNotice("Folder not added", error);
    }

    /// <summary>
    /// Adds the folders among the dropped paths; refusals and non-folders are shown as warning lines in the
    /// chat. No paths means the drag carried none (e.g. from Solution Explorer). Must run on the UI thread.
    /// </summary>
    public void AddDroppedFolders(string[] paths)
    {
        if (ExtraFolders is null) return;
        if (paths.Length == 0)
        {
            ShowNotice("Folder not added", "The dropped item has no folder path. Drag a folder from Windows Explorer.");
            return;
        }

        var (folders, skipped) = VsAcp.Core.Services.ExtraFolders.FoldersAmong(paths);
        foreach (var folder in folders)
            AddExtraFolder(folder);
        if (skipped > 0)
            ShowNotice("Folder not added", skipped == 1 ? "Only folders can be added here." : $"{skipped} dropped items are not folders; only folders can be added here.");
    }

    /// <summary>Stops sharing a folder; a failure is shown as a warning line in the chat. Must run on the UI thread.</summary>
    public void RemoveExtraFolder(string path)
    {
        if (ExtraFolders?.Remove(path) is { } error)
            ShowNotice("Folder not removed", error);
    }

    /// <summary>A transient warning line in the message list (never persisted).</summary>
    private void ShowNotice(string title, string description) =>
        _controller.OnStepStarted(new OutputItem
        {
            Id = NextElementId("notice"),
            ToolName = "Notice",
            Title = title,
            Status = OutputItemStatus.Warning,
            Body = description,
            BodyMode = OutputBodyMode.PlainText
        });

    [ObservableProperty]
    private string _sessionTitle = "New Session";

    public string WorkingDirectory { get; }

    /// <summary>"claude" or "cursor" when this tab's agent was started from that built-in preset (<see cref="AgentPresets.Logo"/>), else null.</summary>
    public string? AgentLogo { get; }

    /// <summary>
    /// The <see cref="SessionInfo"/> entry in the session list that owns this view model.
    /// When set, cost is updated on the entry after each completed message exchange.
    /// </summary>
    public SessionInfo? SessionInfo { get; set; }

    // --- Model selection ---

    public ObservableCollection<ModelDisplayItem> AvailableModels { get; } = new();

    [ObservableProperty]
    private ModelDisplayItem? _selectedModel;

    private bool _suppressModelChange;

    partial void OnSelectedModelChanged(ModelDisplayItem? value)
    {
        if (_suppressModelChange || value is null || _chatService is null) return;
        _ = Task.Run(async () =>
        {
            try { await _chatService.SetConfigOptionAsync(value.ConfigId, value.Value); }
            catch (Exception ex)
            {
                // the agent kept its old model (e.g. no credits for the new one): say so and put the dropdown back
                Serilog.Log.Warning("Setting {ConfigId} to {Value} failed: {Message}", value.ConfigId, value.Value, ex.Message);
                var reason = AcpErrorText.Describe(ex);
                Dispatch(() =>
                {
                    ShowNotice($"Could not switch to {value.DisplayName}", reason);
                    OnConfigOptionsChanged();
                });
            }
        });
    }

    // --- Slash commands (the popup itself lives in the page; see Bridge/ComposerBridge) ---

    private IReadOnlyList<AvailableCommand> _slashCommands = Array.Empty<AvailableCommand>();

    /// <summary>Commands the agent currently advertises; the page filters and offers them as the user types <c>/</c>.</summary>
    public IReadOnlyList<AvailableCommand> SlashCommands => _slashCommands;

    /// <summary>
    /// Name of the slash command picked from the popup. Shown as an inline chip at the start of the
    /// input and prepended to the prompt text (<c>/name rest</c>) on send, which is how ACP agents
    /// receive slash commands.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string? _pendingCommandName;

    private void OnCommandsChanged()
    {
        if (_chatService is null) return;
        _slashCommands = _chatService.AvailableCommands.ToList();
        Dispatch(() => OnPropertyChanged(nameof(SlashCommands)));
    }

    // --- Mode selection ---

    public ObservableCollection<ModeDisplayItem> AvailableModes { get; } = new();

    [ObservableProperty]
    private ModeDisplayItem? _selectedMode;

    private bool _suppressModeChange;

    partial void OnSelectedModeChanged(ModeDisplayItem? value)
    {
        if (_suppressModeChange || value is null || _chatService is null) return;
        _ = Task.Run(async () =>
        {
            try { await _chatService.SetModeAsync(value.ModeId); }
            catch (Exception ex)
            {
                Serilog.Log.Warning("Setting mode {ModeId} failed: {Message}", value.ModeId, ex.Message);
                var reason = AcpErrorText.Describe(ex);
                Dispatch(() =>
                {
                    ShowNotice($"Could not switch to {value.DisplayName}", reason);
                    OnModesChanged();
                });
            }
        });
    }

    public event Action? ScrollRequested;

    /// <summary>
    /// The single in-memory source of truth for this tab's messages. WebView rendering is wired by
    /// subscribing a <c>ChatSessionProjector</c> to this session's change events.
    /// </summary>
    public ChatSession Session => _session;

    public event Action<AcpPermissionRequest, Action<string?>>? PermissionPromptRequested;
    public event Action? PermissionPromptDismissed;

    public event Action<AcpElicitationRequest, Action<AcpElicitationResult>>? ElicitationPromptRequested;
    public event Action? ElicitationPromptDismissed;

    private readonly IPermissionBroker? _permissionBroker;
    private readonly IElicitationBroker? _elicitationBroker;

    /// <summary>
    /// Per-tab "auto-approve" toggle shown in the toolbar. Starts off and is not persisted with the
    /// session, so a reopened tab is back to off.
    /// </summary>
    [ObservableProperty]
    private bool _autoApprove;

    partial void OnAutoApproveChanged(bool value)
    {
        if (_permissionBroker is null) return;

        var approved = _permissionBroker.SetAutoApprove(value);
        Serilog.Log.Information("[Permissions] Auto-approve {State} for this tab ({Approved} pending prompt(s) granted)",
            value ? "ON" : "OFF", approved);

        // A waiting prompt was just granted by the broker: take its banner down.
        if (approved > 0)
            Dispatch(() => PermissionPromptDismissed?.Invoke());
    }
    private readonly ICheckpointService? _checkpointService;

    /// <summary>Raised after a successful revert so the host can reload VS editor buffers.</summary>
    public event Action? RevertCompleted;

    /// <summary>
    /// Standalone constructor for use without a chat service (e.g. before service is wired up).
    /// </summary>
    public ChatSessionViewModel(string workingDirectory = "")
    {
        WorkingDirectory = workingDirectory;
        _controller = new ChatSessionController(_session, null, null, null, null, Dispatch);
    }

    public ChatSessionViewModel(IChatService chatService, IAcpSessionLifecycle? acpSessionLifecycle, OutputListener outputListener, IOptions<VsAcpOptions> options)
        : this(chatService, acpSessionLifecycle, outputListener, options, permissionBroker: null)
    {
    }

    public ChatSessionViewModel(
        IChatService chatService,
        IAcpSessionLifecycle? acpSessionLifecycle,
        OutputListener outputListener,
        IOptions<VsAcpOptions> options,
        IPermissionBroker? permissionBroker,
        ICheckpointService? checkpointService = null,
        IElicitationBroker? elicitationBroker = null)
    {
        _chatService = chatService;
        _acpSessionLifecycle = acpSessionLifecycle;
        WorkingDirectory = options.Value.WorkingDirectory;
        AgentLogo = AgentPresets.Logo(options.Value.AgentPreset);
        _checkpointService = checkpointService;
        _controller = new ChatSessionController(_session, chatService, checkpointService, permissionBroker, elicitationBroker, Dispatch,
            acpSessionLifecycle, options.Value.AgentCommand, string.Join(" ", options.Value.AgentArgs), NextElementId, options.Value.AgentPreset);
        _controller.TitleChanged += title => SessionTitle = title;

        // Agent steps go straight to the session, which mutates the ChatMessage and raises the matching change
        // events (projector renders, persister writes). The VM does not touch message state or the WebView.
        outputListener.StepStarted += _controller.OnStepStarted;
        outputListener.StepUpdated += _controller.OnStepUpdated;
        outputListener.StepCompleted += _controller.OnStepCompleted;

        _permissionBroker = permissionBroker;

        if (_permissionBroker is not null)
        {
            _permissionBroker.PermissionRequested += OnPermissionBrokerRequested;
            _autoApprove = _permissionBroker.AutoApprove; // field, not property: don't re-apply what the broker already has
        }

        _elicitationBroker = elicitationBroker;

        if (_elicitationBroker is not null)
        {
            _elicitationBroker.ElicitationRequested += OnElicitationBrokerRequested;
            _elicitationBroker.ElicitationDismissed += _ => Dispatch(() => ElicitationPromptDismissed?.Invoke());
        }

        if (acpSessionLifecycle is not null)
            acpSessionLifecycle.ActiveAdditionalDirectoriesChanged +=
                () => Dispatch(() => OnPropertyChanged(nameof(ActiveAdditionalDirectories)));

        chatService.ConfigOptionsChanged += OnConfigOptionsChanged;
        chatService.ModesChanged += OnModesChanged;
        chatService.CommandsChanged += OnCommandsChanged;
        chatService.UsageChanged += OnUsageChanged;
    }

    private void OnConfigOptionsChanged()
    {
        if (_chatService is null) return;
        Dispatch(() =>
        {
            _suppressModelChange = true;
            try
            {
                AvailableModels.Clear();

                var modelOption = _chatService.ConfigOptions
                    .OfType<SessionConfigOptionSelect>()
                    .FirstOrDefault(o => o.Category == SessionConfigOptionCategory.Model);

                if (modelOption is null) return;

                var options = SelectOptions.Parse(modelOption.Options);
                foreach (var opt in options)
                    AvailableModels.Add(new ModelDisplayItem(modelOption.Id, opt.Value, opt.Name));

                SelectedModel = AvailableModels.FirstOrDefault(m => m.Value == modelOption.CurrentValue)
                    ?? AvailableModels.FirstOrDefault();
            }
            finally { _suppressModelChange = false; }
        });
    }

    private void OnModesChanged()
    {
        if (_chatService is null) return;
        Dispatch(() =>
        {
            _suppressModeChange = true;
            try
            {
                AvailableModes.Clear();
                foreach (var m in _chatService.AvailableModes)
                    AvailableModes.Add(new ModeDisplayItem(m.Id, m.Name));

                var current = _chatService.CurrentModeId;
                SelectedMode = AvailableModes.FirstOrDefault(m => m.ModeId == current)
                    ?? AvailableModes.FirstOrDefault();
            }
            finally { _suppressModeChange = false; }
        });
    }

    // --- Context usage (tokens / cost) ---

    /// <summary>Compact toolbar readout, e.g. "12.3k / 200k (6%)  $0.42". Null until the agent reports usage.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUsage))]
    private string? _usageText;

    [ObservableProperty]
    private string? _usageToolTip;

    public bool HasUsage => UsageText is not null;

    private void OnUsageChanged()
    {
        if (_chatService is null) return;
        var usage = _chatService.Usage;
        Dispatch(() =>
        {
            if (usage is null)
            {
                UsageText = null;
                UsageToolTip = null;
                return;
            }

            var (text, tip) = UsageFormatter.Format(usage);
            UsageText = text;
            UsageToolTip = tip;

            if (SessionInfo is not null)
                SessionInfo.SessionCost = _chatService.GetSessionCost();
        });
    }

    private void OnElicitationBrokerRequested(AcpElicitationRequest request)
    {
        Dispatch(() =>
        {
            ElicitationPromptRequested?.Invoke(request, result =>
            {
                _elicitationBroker?.Resolve(request.RequestId, result);
            });
        });
    }

    private void OnPermissionBrokerRequested(AcpPermissionRequest request)
    {
        Dispatch(() =>
        {
            PermissionPromptRequested?.Invoke(request, selectedOptionId =>
            {
                _permissionBroker?.Resolve(request.RequestId, selectedOptionId);
            });
        });
    }

    /// <summary>
    /// Starts the agent connection. Safe to call multiple times — no-ops if already connecting or connected.
    /// </summary>
    public void StartConnection() => StartConnection(resumeStoredSession: false);

    /// <param name="resumeStoredSession">First resume the agent session the store holds for this chat (sending stays
    /// disabled meanwhile, so no message starts a new session before it is known).</param>
    private void StartConnection(bool resumeStoredSession)
    {
        if (_acpSessionLifecycle is null || IsInitializing)
            return;

        IsInitializing = true;

        _ = Task.Run(async () =>
        {
            try
            {
                if (resumeStoredSession && await _controller.ReadAcpSessionIdAsync() is { } acpSessionId)
                    _acpSessionLifecycle.RestoreHistory(acpSessionId);
                await _acpSessionLifecycle.LoadSessionAsync();
            }
            catch (VsAcp.Core.Acp.AgentConfigurationException ex)
            {
                Serilog.Log.Warning("StartConnection: agent configuration error - {Message}", ex.Message);
                var errContent = $"**Agent failed to start:** {ex.Message}\n\nCheck **Tools \u2192 Options \u2192 ACP Agent**.";
                Dispatch(() =>
                {
                    IsErrored = true;
                    var errId = $"err-{_idPrefix}-{++_userMsgCounter}";
                    _controller.AddNotice(errId, errContent);
                    RequestScroll();
                });
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning("StartConnection: eager agent warm-up failed - {Message}", ex.Message);
                var errContent = $"**Agent connection failed:** {ex.Message}";
                Dispatch(() =>
                {
                    var errId = $"err-{_idPrefix}-{++_userMsgCounter}";
                    _controller.AddNotice(errId, errContent);
                    RequestScroll();
                });
            }
            finally { Dispatch(() => IsInitializing = false); }
        });
    }

    /// <summary>
    /// Enables persistence for this chat session.
    /// Eagerly connects to the agent, resuming the agent session the store holds for this chat (if any) so it has
    /// context of prior conversation, and restores saved messages from the local store for UI display.
    /// </summary>
    public void EnablePersistence(ISessionStore sessionStore, string sessionId)
    {
        _controller.AttachStore(sessionStore, sessionId);
        StartConnection(resumeStoredSession: true);
        _ = _controller.RestoreAsync();
    }

    /// <summary>Renames the chat by hand; the name sticks, later agent titles do not replace it.</summary>
    public void RenameSession(string title) => _controller.Rename(title);

    private bool CanSend() => !IsBusy && !IsRevertConfirmPending && !IsInitializing && !IsErrored && (!string.IsNullOrWhiteSpace(InputText) || PendingImages.Count > 0 || PendingFiles.Count > 0 || PendingFolders.Count > 0 || PendingSelections.Count > 0 || PendingCommandName is not null);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var submission = Composer.BuildSubmission(
            InputText, PendingCommandName, PendingImages, PendingFiles, PendingFolders, PendingSelections,
            ActiveContextFilePath, IsActiveContextEnabled);

        PendingCommandName = null;
        InputText = "";
        PendingImages.Clear();
        PendingFiles.Clear();
        PendingFolders.Clear();
        PendingSelections.Clear();

        SendCommand.NotifyCanExecuteChanged();

        IsBusy = true;
        _sendCts = new CancellationTokenSource();
        try
        {
            // Title generation, session-id persistence and the post-turn view resync happen in the controller.
            var result = await _controller.SendAsync(submission, _sendCts.Token);

            if (result == TurnResult.Completed)
            {
                // Refresh cost display in the session list
                if (_chatService is not null && SessionInfo is not null)
                    SessionInfo.SessionCost = _chatService.GetSessionCost();
            }
        }
        finally
        {
            _sendCts?.Dispose();
            _sendCts = null;
            IsBusy = false;
        }
        RequestScroll();
    }

    private bool CanStop() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        // Cancels the agent's turn (ACP session/cancel), unblocks any pending permission/elicitation handlers so
        // they answer Cancelled, and cancels the send token.
        _controller.Stop(_sendCts);

        PermissionPromptDismissed?.Invoke();
        ElicitationPromptDismissed?.Invoke();
    }

    [RelayCommand]
    private void Clear() => _controller.ClearHistory();

    /// <summary>
    /// Dry run of <see cref="RevertToMessageAsync"/>'s file restore: which files would be overwritten,
    /// recreated or deleted, as the agent's files and everything else. Null when no checkpoint service is
    /// available (nothing will be restored).
    /// </summary>
    public Task<RevertPreview>? PreviewRevertAsync(string checkpointId)
        => _controller.PreviewRevertAsync(checkpointId);

    /// <summary>
    /// Restores the workspace to the snapshot taken before the user message identified by
    /// <paramref name="checkpointId"/> (the agent's files, or every file with <paramref name="includeOther"/>),
    /// truncates persisted chat history to the messages that preceded it, and resets the agent session.
    /// </summary>
    public async Task RevertToMessageAsync(string checkpointId, bool includeOther)
    {
        if (_checkpointService is null) return;

        // Mark the tab as busy so CloseAllIdleTabs (triggered by workspace/solution events that may
        // fire when checkpoint restoration changes files on disk) does not dispose this tab's DI
        // provider mid-revert and kill the agent process while context priming is still in flight.
        IsBusy = true;

        try
        {
            // Restore files, truncate history, reset the agent, prime context, then clear and reload the
            // truncated history into the session.
            var reverted = await _controller.RevertAsync(checkpointId, includeOther);

            // Restore the reverted user message (text + dropped file attachments) into the input so the
            // user can resubmit as-is or edit before sending again.
            if (reverted is not null)
                Dispatch(() => RestoreInputFromReverted(reverted));

            RevertCompleted?.Invoke();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[Revert] RevertToMessageAsync failed for checkpoint {Id}", checkpointId);
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// "Fork from here" on the message <paramref name="messageId"/>: copies this chat up to it into a new chat in the
    /// store (see <see cref="ForkCoordinator"/>), for the host to open. Null when ignored (a turn or a revert
    /// confirmation is running) or when it failed, which adds a notice to this chat.
    /// </summary>
    public async Task<ForkResult?> ForkAsync(string messageId)
    {
        if (IsBusy || IsRevertConfirmPending)
        {
            Serilog.Log.Information("[Fork] Ignored fork request for {Id}: busy={Busy} confirmPending={Pending}",
                messageId, IsBusy, IsRevertConfirmPending);
            return null;
        }

        // No turn may start while the agent copies its session.
        IsBusy = true;
        try
        {
            return await _controller.ForkAsync(messageId);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[Fork] ForkAsync failed for message {Id}", messageId);
            _controller.AddNotice(NextElementId("err"), $"**Could not fork the chat:** {ex.Message}");
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// In the new chat of a fork, right after its tab opened: primes what its agent gets with the first message and,
    /// when forked from a user message, puts that message back into the input.
    /// </summary>
    public async Task ContinueForkAsync(ForkResult fork)
    {
        await _controller.PrimeForkedChatAsync(fork);
        if (fork.Draft is { } draft)
            Dispatch(() => RestoreInputFromReverted(draft));
    }

    /// <summary>
    /// Repopulates the input with a reverted user message so it can be resubmitted or edited: the text
    /// goes into <see cref="InputText"/> and any dropped file attachments (that still exist on disk) are
    /// re-added as pending files.
    /// </summary>
    private void RestoreInputFromReverted(PersistedMessage reverted)
    {
        var draft = Composer.Restore(reverted, name => _slashCommands.Any(c => c.Name == name), ActiveContextFilePath);

        InputText = draft.Text;
        PendingCommandName = draft.CommandName;

        PendingFiles.Clear();
        foreach (var path in draft.FilePaths)
            PendingFiles.Add(new FileAttachment { FilePath = path });

        PendingFolders.Clear();
        foreach (var path in draft.FolderPaths)
            PendingFolders.Add(new FolderAttachment { FolderPath = path });

        PendingSelections.Clear();
        foreach (var selection in draft.Selections)
            PendingSelections.Add(selection);

        PendingImages.Clear();
        foreach (var image in draft.Images)
            PendingImages.Add(image);

        SendCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void AttachImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.webp|All files|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog() != true)
            return;

        foreach (var filePath in dialog.FileNames)
        {
            try
            {
                var data = File.ReadAllBytes(filePath);
                var mime = Path.GetExtension(filePath).ToLowerInvariant() switch
                {
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".gif" => "image/gif",
                    ".webp" => "image/webp",
                    _ => "application/octet-stream"
                };
                PendingImages.Add(new ImageAttachment
                {
                    Data = data,
                    MimeType = mime,
                    FileName = Path.GetFileName(filePath)
                });
            }
            catch { /* best effort */ }
        }

        SendCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RemoveImage(ImageAttachment image)
    {
        PendingImages.Remove(image);
        SendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Attaches dropped files and folders to the next message. A dropped folder is a one-off reference for
    /// this message only (a <see cref="ContentBlockResourceLink"/> to the agent); it does not grant the agent
    /// session-wide access the way the shared extra-folders chip does.
    /// </summary>
    public void AddDroppedFiles(string[] paths)
    {
        var folderPaths = paths.Where(Directory.Exists).ToArray();
        var filePaths = paths.Except(folderPaths).ToArray();

        var accepted = Composer.AcceptDroppedFiles(
            filePaths, PendingFiles.Select(f => f.FilePath).ToList(), ActiveContextFilePath, IsActiveContextEnabled);

        // If a dropped file is the active context file, deselect it to avoid sending it twice.
        if (accepted.DisableActiveContext)
            IsActiveContextEnabled = false;
        foreach (var path in accepted.ToAttach)
            PendingFiles.Add(new FileAttachment { FilePath = path });

        var acceptedFolders = Composer.AcceptDroppedFolders(folderPaths, PendingFolders.Select(f => f.FolderPath).ToList());
        foreach (var path in acceptedFolders)
            PendingFolders.Add(new FolderAttachment { FolderPath = path });

        SendCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RemoveFile(FileAttachment file)
    {
        PendingFiles.Remove(file);
        SendCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RemoveFolder(FolderAttachment folder)
    {
        PendingFolders.Remove(folder);
        SendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Called by the package when the active VS document changes.
    /// Updates the active-context indicator; resets to enabled for a new file.
    /// </summary>
    public void SetActiveContextFile(string? filePath)
    {
        Dispatch(() =>
        {
            if (string.Equals(filePath, ActiveContextFilePath, StringComparison.OrdinalIgnoreCase))
                return;
            ActiveContextFilePath = filePath;
            if (filePath is not null)
                IsActiveContextEnabled = true;
        });
    }

    /// <summary>
    /// Turns a paste of a multi-line editor selection into a file + line-range reference chip instead of
    /// inserting the text. Returns false (caller pastes the text normally) for single-line text or text
    /// that did not come from the latest editor selection.
    /// </summary>
    public bool TryPasteCodeReference(string clipboardText)
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        var selection = EditorSelectionTracker.TryResolve(clipboardText);
        if (selection is null)
            return false;

        var duplicate = PendingSelections.Any(s =>
            string.Equals(s.FilePath, selection.FilePath, StringComparison.OrdinalIgnoreCase) &&
            s.StartLine == selection.StartLine && s.EndLine == selection.EndLine && s.Text == selection.Text);
        if (!duplicate)
            PendingSelections.Add(selection);

        SendCommand.NotifyCanExecuteChanged();
        return true;
    }

    [RelayCommand]
    private void RemoveSelection(CodeSelection selection)
    {
        PendingSelections.Remove(selection);
        SendCommand.NotifyCanExecuteChanged();
    }

    public bool PasteImageFromClipboard()
    {
        if (!Clipboard.ContainsImage())
            return false;

        var bitmapSource = Clipboard.GetImage();
        if (bitmapSource is null)
            return false;

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
        using var ms = new MemoryStream();
        encoder.Save(ms);

        PendingImages.Add(new ImageAttachment
        {
            Data = ms.ToArray(),
            MimeType = "image/png",
            FileName = "clipboard.png"
        });

        SendCommand.NotifyCanExecuteChanged();
        return true;
    }

    // --- Session-index persistence (serialized via SessionPersistenceWriter) ---

    private void EnqueueSessionIndexMutation(string description, Action<SessionEntry> apply)
        => _controller.EnqueueSessionIndexMutation(description, apply);

    private void RequestScroll() => ScrollRequested?.Invoke();

    // Runs inline on the UI thread, otherwise queues to it; a failure in a queued action is logged by UiThread.
    private static void Dispatch(Action action)
    {
        if (Microsoft.VisualStudio.Shell.ThreadHelper.CheckAccess())
            action();
        else
            UiThread.Post(nameof(ChatSessionViewModel), action);
    }

    public void Dispose()
    {
        DetachExtraFolders();
        _controller.Dispose();
        _sendCts?.Dispose();
    }
}

public record ModelDisplayItem(string ConfigId, string Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public record ModeDisplayItem(string ModeId, string DisplayName)
{
    public override string ToString() => DisplayName;
}
