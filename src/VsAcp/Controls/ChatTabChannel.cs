using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using VsAcp.Core.Abstractions;
using VsAcp.ViewModels;

namespace VsAcp.Controls;

/// <summary>
/// One chat tab's half of the page (Web/src/chat.ts): what the tab's session, composer and banners send goes out
/// with the tab's id, and what the page sends for the tab comes back here (routed by <see cref="ChatWebView"/>).
/// Everything a chat needs from the page goes through this, so the page can hold several chats.
/// </summary>
public sealed class ChatTabChannel
{
    private readonly ChatWebView _page;
    // `{"tab":"<id>",` - what PostBridgeJson puts in front of every message's own properties.
    private readonly string _tabPrefix;

    internal ChatTabChannel(ChatWebView page, string tabId)
    {
        _page = page;
        TabId = tabId;
        _tabPrefix = "{\"tab\":" + JsonSerializer.Serialize(tabId) + ",";
    }

    public string TabId { get; }

    /// <summary>
    /// Raised when the user clicks "Revert to here" on a user message of this chat, with the message's checkpointId.
    /// </summary>
    public event Action<string>? RevertRequested;

    /// <summary>Raised when the user clicks "Fork from here" on a message of this chat, with the message's id.</summary>
    public event Action<string>? ForkRequested;

    /// <summary>
    /// Raised for this chat's page messages other than the ones handled here (<c>revert</c>, <c>fork</c>, banners,
    /// syncs, drops): the composer protocol (see Web/src/bridge/protocol.ts).
    /// </summary>
    public event Action<string, JsonElement>? BridgeMessage;

    /// <summary>
    /// Raised when files are dropped on this chat. The paths come from WebView2's additional objects; an empty array
    /// means the drag carried no files (e.g. Solution Explorer items).
    /// </summary>
    public event Action<string[]>? FilesDropped;

    /// <summary>True once the chat's view is mounted and has asked for its state (it posts <c>ready</c>).</summary>
    public bool IsPageReady { get; private set; }

    /// <summary>Handles a page message for this chat. Runs on the UI thread.</summary>
    internal void Receive(string type, JsonElement root, string[] droppedPaths)
    {
        switch (type)
        {
            case "revert":
                var checkpointId = root.GetProperty("checkpointId").GetString();
                if (!string.IsNullOrEmpty(checkpointId))
                    RevertRequested?.Invoke(checkpointId!);
                return;
            case "fork":
                var messageId = root.GetProperty("messageId").GetString();
                if (!string.IsNullOrEmpty(messageId))
                    ForkRequested?.Invoke(messageId!);
                return;
            case "banner.resolve":
                OnBannerResolved(root);
                return;
            case "banner.openUrl":
                OnBannerOpenUrl(root);
                return;
            case "messages.synced":
                var syncId = root.TryGetProperty("syncId", out var sid) ? sid.GetString() : null;
                if (syncId is not null && _syncs.TryGetValue(syncId, out var pending)) pending.TrySetResult(true);
                return;
            case "composer.drop":
                FilesDropped?.Invoke(droppedPaths);
                return;
            case "ready":
                // A (re)mounted view starts empty: give it the banner that is waiting, if any; the composer and the
                // message list answer the same message with their own state.
                IsPageReady = true;
                if (_activeBanner is { } waiting) PostBridgeJson(waiting.Json);
                break;
        }
        BridgeMessage?.Invoke(type, root);
    }

    /// <summary>Posts a protocol message to this chat. Dropped until WebView2 has been created.</summary>
    public void PostBridgeMessage(object message) => PostBridgeJson(Bridge.BridgeJson.Serialize(message));

    /// <summary>Posts a serialized protocol message (a JSON object) to this chat, adding the tab's id.</summary>
    public void PostBridgeJson(string json) => _page.PostJson(_tabPrefix + json.Substring(1));

    // The message list. Live updates are fire-and-forget; the view asks for the full session snapshot when it
    // mounts (the projector answers `ready` with LoadMessages), so nothing needs to be queued while it loads.

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
    /// Reconciles the view against the full message snapshot (end-of-turn self-heal) and waits for the page
    /// to confirm it applied it. Live updates are fire-and-forget, so a lost one could leave a message missing
    /// or stale while the model and disk stay correct; re-sending the snapshot converges the view without a
    /// flicker. Returns false when the page did not confirm in time, so the caller can escalate. Returns true
    /// (no-op) while the view is not up yet: it asks for the snapshot itself once it is.
    /// </summary>
    public async Task<bool> SyncMessagesAsync(IEnumerable<ChatMessageData> messages)
    {
        if (!IsPageReady) return true;

        var syncId = Guid.NewGuid().ToString("N");
        var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _syncs[syncId] = ack;
        try
        {
            var list = messages.ToList();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            PostBridgeMessage(new { type = "messages.sync", syncId, messages = list });
            Serilog.Log.Debug("[ChatWebView {Tab:l}] Sync {SyncId:l} sent ({Count} messages)", TabId, syncId, list.Count);
            if (await Task.WhenAny(ack.Task, Task.Delay(SyncAckTimeout)) == ack.Task)
            {
                Serilog.Log.Debug("[ChatWebView {Tab:l}] Sync {SyncId:l} confirmed after {Ms} ms", TabId, syncId, clock.ElapsedMilliseconds);
                return true;
            }

            Serilog.Log.Warning("[ChatWebView {Tab:l}] The page did not confirm the message sync within {Seconds}s", TabId, SyncAckTimeout.TotalSeconds);
            return false;
        }
        finally
        {
            _syncs.TryRemove(syncId, out _);
        }
    }

    // ── Banners (rendered by the page; see Web/src/banners) ──────────────

    /// <summary>The banner the chat is currently showing, kept so it can be re-sent if its view remounts.</summary>
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
