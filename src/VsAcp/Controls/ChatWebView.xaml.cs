using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using VsAcp.Core.Abstractions;
using VsAcp.Core.Diagnostics;
using VsAcp.ViewModels;

namespace VsAcp.Controls;

public partial class ChatWebView : UserControl
{
    /// <summary>
    /// Raised when the user clicks a file path link in rendered content.
    /// The string argument is the raw path (possibly with :line suffix).
    /// </summary>
    public static event Action<string>? FileOpenRequested;

    /// <summary>
    /// Raised when the user clicks "Revert to here" on a user message bubble of this page.
    /// The string argument is the checkpointId stored on the message. Per instance: a static event made
    /// every open tab revert (and wipe its own history) on any tab's click.
    /// </summary>
    public event Action<string>? RevertRequested;

    /// <summary>
    /// Raised for page messages other than the chat-message ones above (<c>openFile</c>, <c>revert</c>):
    /// the composer protocol (see Web/src/bridge/protocol.ts). Per instance, so each tab only sees its own page.
    /// </summary>
    public event Action<string, JsonElement>? BridgeMessage;

    /// <summary>
    /// Raised when files are dropped on the page. The paths come from WebView2's additional objects;
    /// an empty array means the drag carried no files (e.g. Solution Explorer items).
    /// </summary>
    public event Action<string[]>? FilesDropped;

    /// <summary>
    /// Raised when items are dropped on the shared-folders chip. Same source and meaning as
    /// <see cref="FilesDropped"/>; the receiver keeps the entries that are folders.
    /// </summary>
    public event Action<string[]>? FoldersDropped;

    /// <summary>
    /// Set by <c>VsAcpPackage</c> from the <c>VsAcpSettings.EnableChatDevTools</c> setting (startup and on change).
    /// When true, the WebView2 context menu and DevTools are enabled for diagnostics (for pages created after the
    /// change). Page logging does not depend on it.
    /// </summary>
    public static bool DevToolsEnabled { get; set; }

    /// <summary>Names this tab in page log lines (the chat's session id); set before the page loads.</summary>
    public string LogTag { get; set; } = "?";

    private readonly PageLogLimiter _pageLogLimiter = new();
    private bool _initialized;
    private bool _pageReady;
    private bool _navigated;

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
            // Shows what the page sent and when (e.g. whether a Stop click arrived); the per-keystroke one is left out.
            if (type != "composer.changed")
                Serilog.Log.Debug("[ChatWebView {Tab:l}] Page sent {Type:l}", LogTag, type);

            if (type == "openFile")
            {
                var path = root.GetProperty("path").GetString();
                if (!string.IsNullOrEmpty(path))
                    FileOpenRequested?.Invoke(path!);
            }
            else if (type == "revert")
            {
                var checkpointId = root.GetProperty("checkpointId").GetString();
                if (!string.IsNullOrEmpty(checkpointId))
                    RevertRequested?.Invoke(checkpointId!);
            }
            else if (type == "banner.resolve")
            {
                OnBannerResolved(root);
            }
            else if (type == "banner.openUrl")
            {
                OnBannerOpenUrl(root);
            }
            else if (type == "messages.synced")
            {
                var syncId = root.TryGetProperty("syncId", out var sid) ? sid.GetString() : null;
                if (syncId is not null && _syncs.TryGetValue(syncId, out var pending)) pending.TrySetResult(true);
            }
            else if (type == "composer.drop" || type == "folders.drop")
            {
                var paths = e.AdditionalObjects?.OfType<CoreWebView2File>()
                    .Select(f => f.Path)
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToArray() ?? Array.Empty<string>();
                if (type == "folders.drop") FoldersDropped?.Invoke(paths);
                else FilesDropped?.Invoke(paths);
            }
            else if (type is not null)
            {
                if (type == "ready")
                {
                    // A (re)loaded page starts empty: give it the theme and the banner that is waiting, if any;
                    // the composer and the message list answer the same message with their own state.
                    _pageReady = true;
                    if (_themeJson is { } theme) PostBridgeJson(theme);
                    if (_activeBanner is { } waiting) PostBridgeJson(waiting.Json);
                }
                BridgeMessage?.Invoke(type, root);
            }
        }
        catch (Exception ex)
        {
            // Malformed message, or a handler (e.g. a banner callback) threw: never take the page down for it.
            Serilog.Log.Warning(ex, "[ChatWebView] Failed to handle a page message");
        }
    }

    // --- Public API ---

    /// <summary>True once the page has mounted and asked for its initial state (it posts <c>ready</c>).</summary>
    public bool IsPageReady => _pageReady;

    /// <summary>Posts a protocol message to the page. Dropped until WebView2 has been created.</summary>
    public void PostBridgeMessage(object message) => PostBridgeJson(Bridge.BridgeJson.Serialize(message));

    public void PostBridgeJson(string json)
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

    // The message list. Live updates are fire-and-forget; the page asks for the full session snapshot when
    // it starts (the projector answers `ready` with LoadMessages), so nothing needs to be queued while it loads.

    public void AddMessage(ChatMessageData message) =>
        PostBridgeMessage(new { type = "messages.add", message });

    public void UpdateContent(string id, string content) =>
        PostBridgeMessage(new { type = "messages.content", id, content });

    public void UpdateStatus(string id, OutputItemStatus status, string expanderTitle) =>
        PostBridgeMessage(new { type = "messages.status", id, status = status.ToString(), expanderTitle });

    public void SetBody(string id, string body, OutputBodyMode bodyMode, string? language = null) =>
        PostBridgeMessage(new { type = "messages.body", id, body, bodyMode = bodyMode.ToString(), language });

    public void CompleteMessage(string id) =>
        PostBridgeMessage(new { type = "messages.complete", id });

    public void ClearMessages() =>
        PostBridgeMessage(new { type = "messages.clear" });

    /// <summary>Replaces the whole list with these messages.</summary>
    public void LoadMessages(IEnumerable<ChatMessageData> messages) =>
        PostBridgeMessage(new { type = "messages.load", messages = messages.ToList() });

    private static readonly TimeSpan SyncAckTimeout = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _syncs = new();

    /// <summary>
    /// Reconciles the page against the full message snapshot (end-of-turn self-heal) and waits for the page
    /// to confirm it applied it. Live updates are fire-and-forget, so a lost one could leave a message missing
    /// or stale while the model and disk stay correct; re-sending the snapshot converges the view without a
    /// flicker. Returns false when the page did not confirm in time, so the caller can escalate. Returns true
    /// (no-op) while the page is not up yet: it asks for the snapshot itself once it is.
    /// </summary>
    public async Task<bool> SyncMessagesAsync(IEnumerable<ChatMessageData> messages)
    {
        if (!_pageReady) return true;

        var syncId = Guid.NewGuid().ToString("N");
        var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _syncs[syncId] = ack;
        try
        {
            var list = messages.ToList();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            PostBridgeMessage(new { type = "messages.sync", syncId, messages = list });
            Serilog.Log.Debug("[ChatWebView {Tab:l}] Sync {SyncId:l} sent ({Count} messages)", LogTag, syncId, list.Count);
            if (await Task.WhenAny(ack.Task, Task.Delay(SyncAckTimeout)) == ack.Task)
            {
                Serilog.Log.Debug("[ChatWebView {Tab:l}] Sync {SyncId:l} confirmed after {Ms} ms", LogTag, syncId, clock.ElapsedMilliseconds);
                return true;
            }

            Serilog.Log.Warning("[ChatWebView] The page did not confirm the message sync within {Seconds}s", SyncAckTimeout.TotalSeconds);
            return false;
        }
        finally
        {
            _syncs.TryRemove(syncId, out _);
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
        PostBridgeJson(_themeJson);
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

    // ── Banners (rendered by the page; see Web/src/banners) ──────────────

    /// <summary>The banner the page is currently showing, kept so it can be re-sent if the page reloads.</summary>
    private sealed record ActiveBanner(string Id, string Json, string? Url, Action<JsonElement> Resolve);

    private ActiveBanner? _activeBanner;

    /// <summary>
    /// Shows <paramref name="build"/>'s banner, replacing any banner currently shown (the replaced one's
    /// callback is never invoked, as before). <paramref name="onResolved"/> runs once with the page's answer.
    /// </summary>
    private void ShowBanner(Func<string, object> build, string? url, Action<JsonElement> onResolved)
    {
        UiThread.Send(() =>
        {
            var id = Guid.NewGuid().ToString("N");
            var json = Bridge.BridgeJson.Serialize(Bridge.BannerMessages.Show(build(id)));
            _activeBanner = new ActiveBanner(id, json, url, onResolved);
            PostBridgeJson(json);
        });
    }

    /// <summary>
    /// Show the permission banner for a request from the ACP agent. The
    /// supplied callback is invoked once when the user selects an option.
    /// Replaces any banner currently shown.
    /// </summary>
    public void ShowPermissionBanner(AcpPermissionRequest request, Action<string?> onResolved) =>
        ShowBanner(id => Bridge.BannerMessages.Permission(id, request), null,
            result => onResolved(Bridge.BannerMessages.ParsePermission(result)));

    /// <summary>
    /// Show the inline "Revert to here" confirmation: the agent's files, and the other changes with an
    /// "Also revert other changes" checkbox. <paramref name="onResolved"/> is invoked once with the answer
    /// (Revert or Cancel, and the checkbox). Replaces any banner currently shown.
    /// </summary>
    internal void ShowRevertConfirmBanner(VsAcp.Core.Services.RevertPreview? preview, Action<Bridge.RevertAnswer> onResolved) =>
        ShowBanner(id => Bridge.BannerMessages.Revert(id, preview), null,
            result => onResolved(Bridge.BannerMessages.ParseRevert(result)));

    /// <summary>
    /// Show a form or URL elicitation from the ACP agent. The callback is invoked once with the user's
    /// answer. Replaces any banner currently shown.
    /// </summary>
    public void ShowElicitationBanner(AcpElicitationRequest request, Action<AcpElicitationResult> onResolved) =>
        ShowBanner(id => Bridge.BannerMessages.Elicitation(id, request), request.IsUrlMode ? request.Url : null,
            result => onResolved(Bridge.BannerMessages.ParseElicitation(result)));

    /// <summary>
    /// Shows an error banner used to escalate a failed chat re-projection. It is a separate banner kind
    /// (not part of the message DOM the projector renders), so it still shows when message rendering is
    /// what failed. <paramref name="onReload"/> runs when the user clicks "Reload view".
    /// </summary>
    public void ShowErrorBanner(string message, Action onReload) =>
        ShowBanner(id => Bridge.BannerMessages.Error(id, message), null, _ => onReload());

    public void HideBanner()
    {
        UiThread.Send(() =>
        {
            _activeBanner = null;
            PostBridgeMessage(new { type = "banner.hide" });
        });
    }

    /// <summary>The page answered a banner. Answers for a banner that was since replaced or hidden are ignored.</summary>
    private void OnBannerResolved(JsonElement message)
    {
        var id = message.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        if (_activeBanner is not { } active || active.Id != id) return;

        // Take it down before running the callback: the callback may show the next banner straight away.
        _activeBanner = null;
        PostBridgeMessage(new { type = "banner.hide" });
        active.Resolve(message.TryGetProperty("result", out var result) ? result : default);
    }

    /// <summary>
    /// The page asks to open the URL of an elicitation. The page never supplies the URL (it comes from the
    /// agent), only the banner id, and only web links are opened.
    /// </summary>
    private void OnBannerOpenUrl(JsonElement message)
    {
        var id = message.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        if (_activeBanner is not { Url: { } url } active || active.Id != id) return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Serilog.Log.Warning("[Elicitation] Refusing to open non-web URL {Url}", url);
            return;
        }

        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "[Elicitation] failed to open {Url}", url); }
    }
}
