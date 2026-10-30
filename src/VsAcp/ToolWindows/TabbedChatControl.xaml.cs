using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using VsAcp.Bridge;
using VsAcp.Controls;
using VsAcp.Core.Services;
using VsAcp.ViewModels;

namespace VsAcp.ToolWindows;

/// <summary>
/// The tool window's content: one page (<see cref="ChatWebView"/>) that draws the tab strip, a chat per open tab and
/// the footer. This class keeps the page in step with the session list (the tabs, which one is shown, the shared
/// folders), creates a tab's chat (<see cref="ChatTab"/>) the first time it is shown, and carries out what the page
/// asks for the panel (open, show, close and rename tabs; the settings; the shared folders).
/// </summary>
public partial class TabbedChatControl : UserControl
{
    private readonly Dictionary<string, ChatTab> _tabs = new();
    private readonly HashSet<SessionInfo> _watched = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private SessionListViewModel? _sessions;
    private SessionInfo? _activeSession;
    private bool _initialized;

    // The panel state (the footer): the shared folders followed for changes, the shown chat's view model followed for
    // whether its agent supports them, and the last state sent.
    private ExtraFoldersController? _folders;
    private ChatSessionViewModel? _shownViewModel;
    private bool _panelScheduled;
    private string? _lastPanelJson;

    public TabbedChatControl()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        InitializeComponent();
        Loaded += OnLoaded;
        ChatPage.PanelMessage += OnPanelMessage;
        ChatPage.PageReady += OnPageReady;

        ApplyThemeColors();
        VSColorTheme.ThemeChanged += _ => UiThread.Post("Apply theme colours", ApplyThemeColors);
    }

    // ---- keyboard focus (ADR-057) ----
    // A click into the page goes to Chromium's own window: WPF's keyboard focus stays "none" and VS does not learn
    // that the chat window is in use. Two things follow, and each has a fix here:
    //  - VS keeps another window (an editor, say) as its active one: the page reports its focus (`page.focused`) and the
    //    chat window is made VS's active window (VsAcpPackage.ActivateChatWindow).
    //  - When the VS window is activated again (the click came from another app), VS restores the WPF focus of the
    //    active window; the chat window has no WPF element to take it, so VS gives it to the VS window itself, and the
    //    native focus leaves the page a moment after the click gave it to the input (the log: "(none) -> MainWindow" at
    //    every such click). Focus landing on the window that holds this tool window while the chat window is VS's
    //    active one is therefore handed to the page. With another VS window active it is left alone.
    // The old WPF tab strip hid both: clicking a WPF tab made the chat window active and gave VS a WPF element to
    // restore the focus to.

    private static TabbedChatControl? _focusOwner;
    private static bool _windowFocusWatched;

    // A class handler, so the window's focus is seen whichever window holds the tool window (docked or floating);
    // the live control is the one in _focusOwner.
    private static void WatchWindowFocus(TabbedChatControl owner)
    {
        _focusOwner = owner;
        if (_windowFocusWatched) return;
        _windowFocusWatched = true;
        EventManager.RegisterClassHandler(typeof(Window), Keyboard.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnWindowGotKeyboardFocus), handledEventsToo: true);
    }

    private static void OnWindowGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // raised for every window of VS: never throw here; a window on another UI thread is not this tool window's
        if (!ThreadHelper.CheckAccess()) return;
        ThreadHelper.ThrowIfNotOnUIThread(); // cannot throw after the check; tells the threading analyzer
        // the window's own focus only, not focus moving to an element inside it
        if (!ReferenceEquals(sender, e.NewFocus) || _focusOwner is not { } owner) return;
        if (!ReferenceEquals(sender, Window.GetWindow(owner)) || !VsAcpPackage.IsChatWindowActive()) return;

        Serilog.Log.Debug("[TabbedChat] [Focus] VS gave the focus to the chat window's {Window:l}; passing it to the chat page", sender.GetType().Name);
        var window = (Window)sender;
        // After this focus change has finished (moving the focus from inside its own event is unreliable), and only if
        // nothing has moved it since.
        UiThread.Post("Focus chat page", () =>
        {
            if (Keyboard.FocusedElement == window) owner.ChatPage.FocusPage();
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        WatchWindowFocus(this);
        if (_initialized) return;

        if (!BindIfNeeded())
            VsAcpPackage.Initialized += OnPackageInitialized;
    }

    private void OnPackageInitialized()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        VsAcpPackage.Initialized -= OnPackageInitialized;
        BindIfNeeded();
    }

    public bool BindIfNeeded()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_initialized) return true;

        var vm = VsAcpPackage.SessionListVM;
        if (vm is null) return false;

        DataContext = vm;
        _sessions = vm;
        _initialized = true;

        vm.SessionOpenRequested += OnSessionOpenRequested;
        vm.Sessions.CollectionChanged += OnSessionsChanged;
        WatchSessions();
        PushTabs();
        SchedulePanel();
        return true;
    }

    private void OnSessionOpenRequested(SessionInfo session)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        ActivateTab(session);
    }

    private void OnPageReady()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        // the page reloaded: it has no panel state
        _lastPanelJson = null;
        PushPanel();
    }

    // ---- the tab list ----

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        WatchSessions();
        PushTabs();
        // a solution switch replaces the whole list: its shared folders may be another solution's
        SchedulePanel();
    }

    // Follows each listed session's name (the tab title) and nothing else.
    private void WatchSessions()
    {
        var current = new HashSet<SessionInfo>(_sessions?.Sessions ?? Enumerable.Empty<SessionInfo>());
        foreach (var gone in _watched.Where(s => !current.Contains(s)).ToList())
        {
            gone.PropertyChanged -= OnSessionPropertyChanged;
            _watched.Remove(gone);
        }
        foreach (var added in current.Where(s => _watched.Add(s)))
            added.PropertyChanged += OnSessionPropertyChanged;
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionInfo.Name)) PushTabs();
    }

    // The tab strip as the page draws it: every listed session, the open ones (with a chat) marked, the shown one active.
    private void PushTabs() =>
        ChatPage.SetTabs((_sessions?.Sessions ?? Enumerable.Empty<SessionInfo>()).Select(s => (object)new
        {
            id = s.Id,
            name = s.Name,
            active = s == _activeSession,
            open = _tabs.ContainsKey(s.Id)
        }));

    public void ActivateTab(SessionInfo session)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_activeSession == session && _tabs.ContainsKey(session.Id))
            return;

        if (_activeSession is not null)
            _activeSession.IsActive = false;

        session.IsActive = true;
        _activeSession = session;

        if (!_tabs.ContainsKey(session.Id))
        {
            var tab = new ChatTab(ChatPage);
            _tabs[session.Id] = tab;
            VsAcpPackage.InitializeChatControl(tab, session);
        }

        PushTabs();
        FollowShownChat();
    }

    public void CloseTab(SessionInfo session)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_tabs.TryGetValue(session.Id, out var tab))
        {
            tab.Dispose();
            _tabs.Remove(session.Id);
        }

        session.IsActive = false;

        if (_activeSession == session)
        {
            _activeSession = null;
            var next = _sessions?.Sessions.FirstOrDefault(s => s.IsActive)
                       ?? _sessions?.Sessions.FirstOrDefault();
            if (next is not null)
            {
                ActivateTab(next);
                return;
            }
        }

        PushTabs();
        FollowShownChat();
    }

    /// <summary>
    /// Closes all tabs that are not currently busy. Used during same-directory reloads
    /// where in-flight work should not be interrupted.
    /// </summary>
    public void CloseAllIdleTabs()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var toClose = _tabs
            .Where(t => t.Value.ViewModel is not { IsBusy: true })
            .Select(t => t.Key)
            .ToList();

        CloseTabs(toClose);
    }

    /// <summary>
    /// Closes every open tab regardless of busy state. Used when switching to a different
    /// workspace so no stale tabs from the previous workspace survive.
    /// </summary>
    public void CloseAllTabs()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        CloseTabs(_tabs.Keys.ToList());
    }

    private void CloseTabs(IEnumerable<string> ids)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        foreach (var id in ids)
        {
            Serilog.Log.Debug("[Tab] Closing tab {Id}", id);
            if (_tabs.TryGetValue(id, out var tab))
            {
                tab.Dispose();
                _tabs.Remove(id);
                Serilog.Log.Debug("[Tab] Removed the chat of {Id}", id);
            }

            try
            {
                VsAcpPackage.DisposeSessionProvider(id);
                Serilog.Log.Debug("[Tab] Disposed session provider for {Id}", id);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "[Tab] DisposeSessionProvider threw for {Id}", id);
            }
        }

        if (_activeSession is not null && !_tabs.ContainsKey(_activeSession.Id))
            _activeSession = null;
        PushTabs();
        FollowShownChat();
    }

    // ---- what the page asks for the panel ----

    private void OnPanelMessage(string type, JsonElement m, string[] droppedPaths)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            switch (type)
            {
                case "tabs.new":
                    if (_sessions is not null) _ = _sessions.NewSessionCommand.ExecuteAsync(null);
                    break;
                case "tabs.activate":
                    if (SessionOf(m) is { } shown) ActivateTab(shown);
                    break;
                case "tabs.close":
                    if (SessionOf(m) is not { } closing) break;
                    // closing deletes the chat, which a running revert is still writing to and its agent still forking
                    if (_tabs.TryGetValue(closing.Id, out var reverting) && reverting.ViewModel is { IsReverting: true })
                    {
                        Serilog.Log.Information("[Tab] Not closing {Id}: a revert is running", closing.Id);
                        break;
                    }
                    _sessions?.RemoveSessionCommand.Execute(closing);
                    break;
                case "tabs.rename":
                    if (SessionOf(m) is { } renamed) Rename(renamed, m.GetProperty("name").GetString() ?? "");
                    break;
                case "page.focused":
                    VsAcpPackage.ActivateChatWindow();
                    break;
                case "settings.open":
                    UiThread.Post("settings.open", VsAcpPackage.OpenSettings);
                    break;
                case "logs.open":
                    UiThread.Post("logs.open", VsAcpPackage.ShowLogs);
                    break;
                case "folders.add":
                    // Not inside the WebView2 message callback: the folder dialog is modal.
                    UiThread.Post("folders.add", PickAndAddFolder);
                    break;
                case "folders.remove":
                    RemoveFolder(m.GetProperty("path").GetString() ?? "");
                    break;
                case "folders.drop":
                    AddDroppedFolders(droppedPaths);
                    break;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[TabbedChat] Failed to handle page message '{Type}'", type);
        }
    }

    private SessionInfo? SessionOf(JsonElement m)
    {
        var id = m.TryGetProperty("id", out var p) ? p.GetString() : null;
        return _sessions?.Sessions.FirstOrDefault(s => s.Id == id);
    }

    private void Rename(SessionInfo session, string name)
    {
        var title = name.Trim();
        if (title.Length == 0 || title == session.Name) return;

        // An open tab renames through its own view-model, which owns the persistence queue.
        if (_tabs.TryGetValue(session.Id, out var tab) && tab.ViewModel is { } vm)
            vm.RenameSession(title);
        else if (_sessions is not null)
            _ = _sessions.RenameAsync(session, title);
    }

    // ---- the shared folders (the footer) ----
    // With a chat shown, its view model makes the change, so a refusal appears as a warning line in that chat; with
    // none, the solution's list changes directly and a refusal goes to the log.

    private ChatSessionViewModel? ShownViewModel =>
        _activeSession is not null && _tabs.TryGetValue(_activeSession.Id, out var tab) ? tab.ViewModel : null;

    private void PickAndAddFolder()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var folders = VsAcpPackage.CurrentExtraFolders();
        if (folders is null) return;

        var picked = FolderPicker.Pick("Choose a folder to share with the agent", ShownViewModel?.WorkingDirectory);
        if (picked is null) return;

        Serilog.Log.Information("[Folders] Adding {Path}", picked);
        if (ShownViewModel is { } vm) vm.AddExtraFolder(picked);
        else if (folders.Add(picked) is { } error) Serilog.Log.Warning("[Folders] {Path} not added: {Reason}", picked, error);
    }

    private void RemoveFolder(string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ShownViewModel is { } vm) vm.RemoveExtraFolder(path);
        else if (VsAcpPackage.CurrentExtraFolders()?.Remove(path) is { } error)
            Serilog.Log.Warning("[Folders] {Path} not removed: {Reason}", path, error);
    }

    private void AddDroppedFolders(string[] paths)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Serilog.Log.Information("[Folders] Items dropped on the folders chip: {Count}", paths.Length);
        if (ShownViewModel is { } vm)
        {
            vm.AddDroppedFolders(paths);
            return;
        }

        if (VsAcpPackage.CurrentExtraFolders() is not { } folders) return;
        var (found, skipped) = ExtraFolders.FoldersAmong(paths);
        foreach (var folder in found)
        {
            if (folders.Add(folder) is { } error) Serilog.Log.Warning("[Folders] {Path} not added: {Reason}", folder, error);
        }
        if (skipped > 0 || paths.Length == 0)
            Serilog.Log.Warning("[Folders] {Count} dropped item(s) are not folders and were not added", skipped);
    }

    // The footer follows the solution's shared folders and the shown chat (whether its agent supports them).
    private void FollowShownChat()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var vm = ShownViewModel;
        if (vm != _shownViewModel)
        {
            if (_shownViewModel is not null) _shownViewModel.PropertyChanged -= OnShownViewModelChanged;
            _shownViewModel = vm;
            if (vm is not null) vm.PropertyChanged += OnShownViewModelChanged;
        }
        SchedulePanel();
    }

    // Coalesces bursts (a turn changes the view model's properties often) into one panel update, as ComposerBridge does.
    // May be called from any thread (a view model's PropertyChanged); the update itself runs on the dispatcher, which is
    // why the handlers below may call it without being on the UI thread (VSTHRD010).
#pragma warning disable VSTHRD001, VSTHRD110, VSTHRD010
    private void OnShownViewModelChanged(object? sender, PropertyChangedEventArgs e) => SchedulePanel();

    private void OnFoldersChanged() => SchedulePanel();

    private void SchedulePanel()
    {
        if (_panelScheduled) return;
        _panelScheduled = true;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            _panelScheduled = false;
            ThreadHelper.ThrowIfNotOnUIThread();
            PushPanel();
        }), DispatcherPriority.Background);
    }
#pragma warning restore VSTHRD001, VSTHRD110, VSTHRD010

    private void PushPanel()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var folders = VsAcpPackage.CurrentExtraFolders();
        if (folders != _folders)
        {
            if (_folders is not null) _folders.Changed -= OnFoldersChanged;
            _folders = folders;
            if (folders is not null) folders.Changed += OnFoldersChanged;
        }

        var json = BridgeJson.Serialize(new
        {
            type = "panel",
            folders = folders is null ? null : ComposerBridge.FoldersStateOf(folders, ShownViewModel?.SupportsAdditionalDirectories)
        });
        if (json == _lastPanelJson || !ChatPage.IsPageReady) return;
        _lastPanelJson = json;
        ChatPage.PostJson(json);
    }

    // ---- theme ----

    private void ApplyThemeColors()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var editor = ChatThemeColors.GetEditorColors();

        var colors = new Dictionary<string, string>();

        void Map(string cssVar, ThemeResourceKey resourceKey)
        {
            var wpfColor = VSColorTheme.GetThemedColor(resourceKey);
            colors[cssVar] = $"#{wpfColor.R:X2}{wpfColor.G:X2}{wpfColor.B:X2}";
        }

        void MapColor(string cssVar, Color color)
        {
            colors[cssVar] = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        // Only the anchors come from Visual Studio; Web/src/theme.css derives every other colour from them
        // (surfaces, muted text, code palette, status colours) so the page is consistent in any theme.
        MapColor("--bg-primary", editor.Background);
        MapColor("--text-primary", editor.Foreground);
        Map("--border", EnvironmentColors.ToolWindowBorderColorKey);
        // Use VS hyperlink color (theme-driven) rather than the OS system
        // highlight, which on Win11 is often violet/purple and clashes with
        // the dark/light VS theme.
        Map("--accent", EnvironmentColors.PanelHyperlinkColorKey);
        // Accent buttons in the banners (permission / revert / elicitation) use the selection colours.
        Map("--button-bg", EnvironmentColors.SystemHighlightColorKey);
        Map("--button-fg", EnvironmentColors.SystemHighlightTextColorKey);
        // The tab strip, as the WPF strip drew it: the command bar's background and border.
        Map("--strip-bg", EnvironmentColors.CommandBarGradientBeginColorKey);
        Map("--strip-border", EnvironmentColors.CommandBarBorderColorKey);

        // Scrollbar: derive from gray/muted text for subtle appearance
        var gray = VSColorTheme.GetThemedColor(EnvironmentColors.ScrollBarThumbBackgroundColorKey);
        colors["--scrollbar-thumb"] = $"rgba({gray.R}, {gray.G}, {gray.B}, 0.5)";
        var grayHover = VSColorTheme.GetThemedColor(EnvironmentColors.ScrollBarThumbMouseOverBackgroundColorKey);
        colors["--scrollbar-thumb-hover"] = $"rgba({grayHover.R}, {grayHover.G}, {grayHover.B}, 0.8)";

        ChatPage.SetChromeBackground(editor.Background);
        // Light or dark follows the editor background (not the shell theme), because that is what the chat
        // sits next to; a Windows high-contrast theme switches the page to outlines and full-contrast text.
        var luminance = (0.299 * editor.Background.R + 0.587 * editor.Background.G + 0.114 * editor.Background.B) / 255.0;
        ChatPage.SetTheme(colors, isLight: luminance > 0.5, highContrast: SystemParameters.HighContrast);

        var editorBrush = new SolidColorBrush(editor.Background);
        editorBrush.Freeze();
        Background = editorBrush;
    }
}
