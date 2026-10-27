using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using VsAcp.Controls;
using VsAcp.ViewModels;

namespace VsAcp.ToolWindows;

public partial class TabbedChatControl : UserControl
{
    private readonly Dictionary<string, ChatSessionControl> _tabControls = new();
    private SessionInfo? _activeSession;
    private bool _initialized;

    public TabbedChatControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        GotKeyboardFocus += OnGotKeyboardFocus;
    }

    /// <summary>
    /// The chat page is the only keyboard target of this tool window (besides the tab rename box), so keyboard focus
    /// that lands anywhere else in it is passed on to the active page. Needed because WPF never sees the focus inside
    /// a page (WebView2 is a Chromium window of another process): when a click into the page's input activates the
    /// VS window, VS restores the focus a few milliseconds later to what it believes is focused, the tab strip, and
    /// the input loses the focus the click just gave it.
    /// </summary>
    private void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is not DependencyObject target
            || FindAncestor<ChatWebView>(target) is not null
            || FindAncestor<TextBox>(target) is not null
            || _activeSession is null
            || !_tabControls.TryGetValue(_activeSession.Id, out var control))
            return;

        Serilog.Log.Debug("[TabbedChat] [Focus] Keyboard focus landed on {Element:l}; passing it to the chat page", target.GetType().Name);
        // After this focus change has finished (moving the focus from inside its own event is unreliable), and only
        // if nothing has moved it since.
        UiThread.Post("Focus chat page", () =>
        {
            if (Keyboard.FocusedElement == target) control.ChatWebView.FocusPage();
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;

        if (!BindIfNeeded())
            VsAcpPackage.Initialized += OnPackageInitialized;
    }

    private void OnPackageInitialized()
    {
        VsAcpPackage.Initialized -= OnPackageInitialized;
        BindIfNeeded();
    }

    public bool BindIfNeeded()
    {
        if (_initialized) return true;

        var vm = VsAcpPackage.SessionListVM;
        if (vm is null) return false;

        DataContext = vm;
        _initialized = true;

        vm.SessionOpenRequested += OnSessionOpenRequested;
        return true;
    }

    private void OnSessionOpenRequested(SessionInfo session)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        ActivateTab(session);
    }

    public void ActivateTab(SessionInfo session)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_activeSession == session && _tabControls.ContainsKey(session.Id))
            return;

        if (_activeSession is not null)
            _activeSession.IsActive = false;

        session.IsActive = true;
        _activeSession = session;

        if (!_tabControls.TryGetValue(session.Id, out var control))
        {
            control = new ChatSessionControl();
            _tabControls[session.Id] = control;
            ContentArea.Children.Add(control);

            VsAcpPackage.InitializeChatControl(control, session);
        }

        foreach (var child in ContentArea.Children)
        {
            if (child is UIElement el)
                el.Visibility = Visibility.Collapsed;
        }
        control.Visibility = Visibility.Visible;
    }

    public void CloseTab(SessionInfo session)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_tabControls.TryGetValue(session.Id, out var control))
        {
            ContentArea.Children.Remove(control);
            _tabControls.Remove(session.Id);
        }

        session.IsActive = false;

        if (_activeSession == session)
        {
            _activeSession = null;
            var vm = DataContext as SessionListViewModel;
            var next = vm?.Sessions.FirstOrDefault(s => s.IsActive)
                       ?? vm?.Sessions.FirstOrDefault();
            if (next is not null)
                ActivateTab(next);
        }
    }

    /// <summary>
    /// Closes all tabs that are not currently busy. Used during same-directory reloads
    /// where in-flight work should not be interrupted.
    /// </summary>
    public void CloseAllIdleTabs()
    {
        var toClose = _tabControls.Keys
            .Where(id => _tabControls.TryGetValue(id, out var ctrl)
                         && !(ctrl.DataContext is ChatSessionViewModel vm && vm.IsBusy))
            .ToList();

        CloseTabs(toClose);
    }

    /// <summary>
    /// Closes every open tab regardless of busy state. Used when switching to a different
    /// workspace so no stale tabs from the previous workspace survive.
    /// </summary>
    public void CloseAllTabs()
    {
        CloseTabs(_tabControls.Keys.ToList());
    }

    private void CloseTabs(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            Serilog.Log.Debug("[Tab] Closing tab {Id}", id);
            if (_tabControls.TryGetValue(id, out var control))
            {
                ContentArea.Children.Remove(control);
                _tabControls.Remove(id);
                Serilog.Log.Debug("[Tab] Removed UI control for {Id}", id);
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

        if (_activeSession is not null && !_tabControls.ContainsKey(_activeSession.Id))
            _activeSession = null;
    }

    private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        // Don't activate when the close button is being clicked: closing a background tab must not bring it
        // forward first. The click can land on the button's border or on its glyph (a text element with no
        // templated parent), so look for the button among the click's ancestors.
        if (e.OriginalSource is DependencyObject clicked && FindAncestor<Button>(clicked) is not null)
            return;

        // Clicks inside the rename box are for editing the text
        if (e.OriginalSource is DependencyObject source && FindAncestor<TextBox>(source) is not null)
            return;

        if (sender is FrameworkElement fe && fe.DataContext is SessionInfo session)
        {
            ActivateTab(session);

            if (e.ClickCount == 2)
                session.IsRenaming = true;
        }
    }

    private void RenameTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SessionInfo session })
            session.IsRenaming = true;
    }

    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox { IsVisible: true } box || box.DataContext is not SessionInfo session)
            return;

        box.Text = session.Name;
        // Focus after this pass: a context menu returns focus to its owner as it closes, which would
        // otherwise take it straight back from the box and end the rename.
        UiThread.Post("Tab rename focus", () =>
        {
            box.Focus();
            box.SelectAll();
        });
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not SessionInfo session)
            return;

        if (e.Key == Key.Enter)
        {
            CommitRename(box, session);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            session.IsRenaming = false;
            e.Handled = true;
        }
    }

    private void RenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box && box.DataContext is SessionInfo session)
            CommitRename(box, session);
    }

    private void CommitRename(TextBox box, SessionInfo session)
    {
        // Enter, Escape and losing focus all end up here or clear the flag; only the first of them acts.
        if (!session.IsRenaming) return;
        session.IsRenaming = false;

        var title = box.Text.Trim();
        if (title.Length == 0 || title == session.Name) return;

        if (_tabControls.TryGetValue(session.Id, out var control) && control.DataContext is ChatSessionViewModel vm)
            vm.RenameSession(title);
        else if (DataContext is SessionListViewModel list)
            _ = list.RenameAsync(session, title);
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var node = start; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is T match) return match;
        }
        return null;
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SessionInfo session)
        {
            if (DataContext is SessionListViewModel vm)
                vm.RemoveSessionCommand.Execute(session);
            e.Handled = true;
        }
    }

    private void NewTab_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SessionListViewModel vm)
        {
            _ = vm.NewSessionCommand.ExecuteAsync(null);
        }
    }
}
