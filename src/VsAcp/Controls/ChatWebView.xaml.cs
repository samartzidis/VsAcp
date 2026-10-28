using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using VsAcp.Core.Diagnostics;

namespace VsAcp.Controls;

public partial class ChatWebView : UserControl
{
    /// <summary>
    /// Raised when the user clicks a file path link in rendered content.
    /// The string argument is the raw path (possibly with :line suffix).
    /// </summary>
    public static event Action<string>? FileOpenRequested;

    /// <summary>
    /// Raised for the panel's own page messages (the tab strip, the settings button, the shared folders; see
    /// <c>PanelWebMessage</c> in Web/src/bridge/protocol.ts), with the paths of dropped items for <c>folders.drop</c>.
    /// </summary>
    public event Action<string, JsonElement, string[]>? PanelMessage;

    /// <summary>Raised when the page has (re)loaded and asked for its state: the panel answers with its own.</summary>
    public event Action? PageReady;

    /// <summary>
    /// Set by <c>VsAcpPackage</c> from the <c>VsAcpSettings.EnableChatDevTools</c> setting (startup and on change).
    /// When true, the WebView2 context menu and DevTools are enabled for diagnostics (read when the page is created,
    /// so a change applies after Visual Studio restarts). Page logging does not depend on it.
    /// </summary>
    public static bool DevToolsEnabled { get; set; }

    /// <summary>Names the page in log lines that belong to no chat.</summary>
    public string LogTag { get; set; } = "panel";

    private readonly PageLogLimiter _pageLogLimiter = new();
    private bool _initialized;
    private bool _pageReady;
    private bool _navigated;
    // The chats this page shows (one per open tab), and the tab list as last set: the page creates a chat view for each
    // open tab (see Web/src/panel.ts).
    private readonly List<ChatTabChannel> _tabs = new();
    private string? _tabsJson;

    public ChatWebView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => LogView("unloaded");
        IsVisibleChanged += (_, e) => LogView((bool)e.NewValue ? "shown" : "hidden");
    }

    // The control's life in the WPF tree, with the windows it sits in: a page that stops updating can then be matched
    // against the tab being moved, re-created or reparented (a changed window handle).
    private void LogView(string what)
    {
        var window = PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource source ? source.Handle : IntPtr.Zero;
        Serilog.Log.Debug("[ChatWebView {Tab:l}] View {What:l} (window {Window:X}, WebView window {WebViewWindow:X})",
            LogTag, what, window.ToInt64(), WebView.Handle.ToInt64());
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LogView("loaded");
        if (_initialized) return;
        _initialized = true;
        UiThread.Post("WebView2 init", InitializeWebViewAsync);
    }

    /// <summary>
    /// Gives the keyboard focus to the page. The WebView2 control takes the WPF focus and hands it on to the page as
    /// a programmatic move, which keeps the page's focused element (the input, normally).
    /// </summary>
    public void FocusPage() => WebView.Focus();

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VsAcp", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await WebView.EnsureCoreWebView2Async(env);
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = DevToolsEnabled;
            WebView.CoreWebView2.Settings.AreDevToolsEnabled = DevToolsEnabled;
            WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            WebView.CoreWebView2.Settings.IsZoomControlEnabled = false;

            WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            // A crashed or hung page process (or browser/GPU process) is otherwise silent: the page just stops.
            WebView.CoreWebView2.ProcessFailed += (_, args) =>
                Serilog.Log.Warning("[ChatWebView {Tab:l}] WebView2 process failed: {Kind}, reason {Reason}, exit code {ExitCode}, {Process}",
                    LogTag, args.ProcessFailedKind, args.Reason, args.ExitCode, args.ProcessDescription);

            // The page is a closed app: it never navigates and never opens windows. Links go to the system
            // browser (http/https only); the one allowed navigation is loading the page itself.
            WebView.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                OpenExternal(args.Uri);
            };
            WebView.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (!_navigated) { _navigated = true; return; }
                args.Cancel = true;
                OpenExternal(args.Uri);
            };

            // before the page loads, so an error while it starts is caught too
            await StartPageLoggingAsync(WebView.CoreWebView2);

            var html = LoadHtmlTemplate();
            WebView.NavigateToString(html);
        }
        catch (Exception ex)
        {
            // without WebView2 the whole chat is blank: make sure the reason is in the log, not just Debug output
            Serilog.Log.Error(ex, "[ChatWebView] WebView2 initialisation failed");
        }
    }

    /// <summary>
    /// Copies the page's console output and uncaught exceptions into the extension log through the DevTools
    /// protocol, so a page failure is on record without DevTools being open or enabled. Always on: the page logs
    /// a few lifecycle lines per turn, and the limiter caps the rest. Best effort: a failure here leaves the chat working.
    /// </summary>
    private async Task StartPageLoggingAsync(CoreWebView2 core)
    {
        try
        {
            core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled").DevToolsProtocolEventReceived +=
                (_, e) => LogPageEntry(PageLog.FromConsoleApiCalled(e.ParameterObjectAsJson));
            core.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown").DevToolsProtocolEventReceived +=
                (_, e) => LogPageEntry(PageLog.FromExceptionThrown(e.ParameterObjectAsJson));
            await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[ChatWebView] Page logging could not be started");
        }
    }

    private void LogPageEntry(PageLogEntry? entry)
    {
        if (entry is not { } line) return;
        if (!_pageLogLimiter.TryTake(out var dropped)) return;

        if (dropped > 0)
            Serilog.Log.Warning("[ChatWebView:page {Tab:l}] {Dropped} page log lines dropped (over the rate limit)", LogTag, dropped);
        var level = line.Level switch
        {
            PageLogLevel.Error => Serilog.Events.LogEventLevel.Error,
            PageLogLevel.Warning => Serilog.Events.LogEventLevel.Warning,
            PageLogLevel.Debug => Serilog.Events.LogEventLevel.Debug,
            _ => Serilog.Events.LogEventLevel.Information
        };
        // :l writes the text as is (no quotes); it is already one line and length-capped
        Serilog.Log.Write(level, "[ChatWebView:page {Tab:l}] {Text:l}", LogTag, line.Text);
    }

    private static string LoadHtmlTemplate()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var showdownJs = ReadEmbeddedResource(assembly, "VsAcp.Assets.showdown.min.js");
        var highlightJs = ReadEmbeddedResource(assembly, "VsAcp.Assets.highlight.min.js");
        var templateHtml = ReadEmbeddedResource(assembly, "VsAcp.Assets.chat-template.html");
        // Preact bundle built from Web\ (see the BuildWebUi target in the csproj).
        var spaJs = ReadEmbeddedResource(assembly, "VsAcp.Assets.spa.js");
        var spaCss = ReadEmbeddedResource(assembly, "VsAcp.Assets.spa.css");
        return templateHtml
            .Replace("{{SHOWDOWN_JS}}", showdownJs)
            .Replace("{{HIGHLIGHTJS_JS}}", highlightJs)
            .Replace("{{SPA_CSS}}", spaCss)
            .Replace("{{SPA_JS}}", spaJs);
    }

    private static string ReadEmbeddedResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var json = e.TryGetWebMessageAsString();
        if (json is null) return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            var tab = root.TryGetProperty("tab", out var tabProp) ? tabProp.GetString() : null;
            // Shows what the page sent and when (e.g. whether a Stop click arrived); the per-keystroke one is left out.
            if (type != "composer.changed")
                Serilog.Log.Debug("[ChatWebView {Tab:l}] Page sent {Type:l}", tab ?? LogTag, type);
            if (type is null) return;

            if (tab is not null)
            {
                if (_tabs.FirstOrDefault(t => t.TabId == tab) is not { } channel)
                {
                    Serilog.Log.Warning("[ChatWebView] Page sent {Type:l} for tab {Tab:l}, which is not open here", type, tab);
                    return;
                }

                channel.Receive(type, root, DroppedPaths(type, e));
                return;
            }

            switch (type)
            {
                case "page.ready":
                    // A (re)loaded page starts empty: give it the theme and the tabs; each open tab's view then asks
                    // for its own state.
                    _pageReady = true;
                    if (_themeJson is { } theme) PostJson(theme);
                    if (_tabsJson is { } tabs) PostJson(tabs);
                    PageReady?.Invoke();
                    break;
                case "openFile":
                    var path = root.GetProperty("path").GetString();
                    if (!string.IsNullOrEmpty(path))
                        FileOpenRequested?.Invoke(path!);
                    break;
                default:
                    PanelMessage?.Invoke(type, root, DroppedPaths(type, e));
                    break;
            }
        }
        catch (Exception ex)
        {
            // Malformed message, or a handler (e.g. a banner callback) threw: never take the page down for it.
            Serilog.Log.Warning(ex, "[ChatWebView] Failed to handle a page message");
        }
    }

    // The paths of the items dropped with a drop message (WebView2 additional objects); none for other messages, and
    // none when the drag carried no files (e.g. Solution Explorer items).
    private static string[] DroppedPaths(string type, CoreWebView2WebMessageReceivedEventArgs e) =>
        type is "composer.drop" or "folders.drop"
            ? e.AdditionalObjects?.OfType<CoreWebView2File>()
                .Select(f => f.Path)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray() ?? Array.Empty<string>()
            : Array.Empty<string>();

    // --- Public API ---

    /// <summary>The page has loaded and asked for its state.</summary>
    public bool IsPageReady => _pageReady;

    /// <summary>
    /// Gives a chat its half of the page, before its tab is listed as open (<see cref="SetTabs"/>): once it is, the
    /// page creates the chat's view, and the view asks for its state (<c>ready</c>), which the chat's composer and
    /// message list answer.
    /// </summary>
    public ChatTabChannel OpenTab(string tabId)
    {
        if (_tabs.FirstOrDefault(t => t.TabId == tabId) is { } existing) return existing;

        var channel = new ChatTabChannel(this, tabId);
        _tabs.Add(channel);
        return channel;
    }

    /// <summary>Takes a chat out of the page: messages for it are ignored from now on.</summary>
    public void CloseTab(string tabId) => _tabs.RemoveAll(t => t.TabId == tabId);

    /// <summary>
    /// The tab strip, in order (Web/src/bridge/protocol.ts TabInfo). Sent now, and again when the page (re)loads.
    /// </summary>
    public void SetTabs(IEnumerable<object> tabs)
    {
        _tabsJson = Bridge.BridgeJson.Serialize(new { type = "tabs", tabs = tabs.ToList() });
        if (_pageReady) PostJson(_tabsJson);
    }

    /// <summary>Posts a serialized protocol message to the page. Dropped until WebView2 has been created.</summary>
    internal void PostJson(string json)
    {
        try
        {
            WebView.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[ChatWebView] Failed to post bridge message");
        }
    }

    private string? _themeJson;

    /// <summary>
    /// Pushes the VS theme to the page: the anchor colours as CSS variables plus the light/dark scheme and the
    /// high-contrast flag (see Web/src/theme.css). Re-sent whenever the page (re)starts.
    /// </summary>
    public void SetTheme(Dictionary<string, string> colors, bool isLight, bool highContrast)
    {
        _themeJson = Bridge.BridgeJson.Serialize(new { type = "theme", colors, scheme = isLight ? "light" : "dark", highContrast });
        PostJson(_themeJson);
    }

    public void SetChromeBackground(Color color)
    {
        UiThread.Send(() =>
        {
            WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(
                color.A, color.R, color.G, color.B);
        });
    }

    /// <summary>
    /// Opens a web link in the system browser. The page never navigates itself: rendered content comes
    /// from the agent, so only http(s) links are followed and everything else is dropped.
    /// </summary>
    private static void OpenExternal(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Serilog.Log.Information("[ChatWebView] Ignored navigation to {Url}", url);
            return;
        }

        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "[ChatWebView] failed to open {Url}", url); }
    }
}
