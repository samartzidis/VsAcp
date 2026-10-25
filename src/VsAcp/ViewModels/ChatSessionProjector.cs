using System.Linq;
using System.Text.Json;
using VsAcp.Core.Models;
using VsAcp.Controls;

namespace VsAcp.ViewModels;

/// <summary>
/// Projects <see cref="ChatSession"/> change events onto the <see cref="ChatWebView"/>. This is the
/// single place the session-to-page mapping lives, so any session mutation renders automatically -
/// the view-model can no longer mutate state without the UI following. The page asks for the full
/// snapshot when it starts (<c>ready</c>), so live events never need to be queued while it loads.
/// </summary>
public sealed class ChatSessionProjector : IDisposable
{
    private readonly ChatSession _session;
    private readonly ChatWebView _webView;

    /// <summary>
    /// Raised when an end-of-turn re-projection could not be confirmed by the page (e.g. the render
    /// surface is broken). The host escalates this to a visible error so the user can reload the view;
    /// the underlying session and persisted history remain valid.
    /// </summary>
    public event Action? RenderFailed;

    public ChatSessionProjector(ChatSession session, ChatWebView webView)
    {
        _session = session;
        _webView = webView;

        _session.MessageAdded += OnMessageAdded;
        _session.MessageContentUpdated += OnContentUpdated;
        _session.MessageStatusUpdated += OnStatusUpdated;
        _session.MessageBodySet += OnBodySet;
        _session.MessageCompleted += OnCompleted;
        _session.Cleared += OnCleared;
        _session.Restored += OnRestored;
        _session.ResyncRequested += OnResyncRequested;
        _webView.BridgeMessage += OnPageMessage;

        // The page may already be up (this tab was re-initialised): it will not ask again.
        if (_webView.IsPageReady) PushSnapshot();
    }

    private void OnPageMessage(string type, JsonElement message)
    {
        if (type == "ready") PushSnapshot();
    }

    private void PushSnapshot() => _webView.LoadMessages(_session.Messages.Select(ChatMessageData.From));

    private void OnMessageAdded(ChatMessage m)
        => _webView.AddMessage(ChatMessageData.From(m));

    private void OnContentUpdated(string id, string content)
        => _webView.UpdateContent(id, content);

    private void OnStatusUpdated(string id, Core.Abstractions.OutputItemStatus status, string expanderTitle)
        => _webView.UpdateStatus(id, status, expanderTitle);

    private void OnBodySet(string id, string body, Core.Abstractions.OutputBodyMode mode, string? language)
        => _webView.SetBody(id, body, mode, language);

    private void OnCompleted(string id)
        => _webView.CompleteMessage(id);

    private void OnCleared()
        => _webView.ClearMessages();

    private void OnRestored(IReadOnlyList<ChatMessage> messages)
        => _webView.LoadMessages(messages.Select(ChatMessageData.From));

    // The event is an Action, so the handler cannot be async; ResyncAsync catches everything itself, which
    // makes dropping its task safe (an exception thrown by a RenderFailed subscriber is logged, not fatal).
    private void OnResyncRequested(IReadOnlyList<ChatMessage> messages) => _ = ResyncAsync(messages);

    private async Task ResyncAsync(IReadOnlyList<ChatMessage> messages)
    {
        try
        {
            if (await _webView.SyncMessagesAsync(messages.Select(ChatMessageData.From)))
                return;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[ChatSessionProjector] The end-of-turn sync threw");
        }

        Serilog.Log.Error("[ChatSessionProjector] End-of-turn re-projection failed; chat view may be out of sync with the session.");
        try
        {
            RenderFailed?.Invoke();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[ChatSessionProjector] A RenderFailed handler threw");
        }
    }

    /// <summary>
    /// Full rebuild of the page's list from the session snapshot. Used by the "Reload view"
    /// escalation: replaces every message with the source of truth.
    /// </summary>
    public void Rebuild() => PushSnapshot();

    public void Dispose()
    {
        _session.MessageAdded -= OnMessageAdded;
        _session.MessageContentUpdated -= OnContentUpdated;
        _session.MessageStatusUpdated -= OnStatusUpdated;
        _session.MessageBodySet -= OnBodySet;
        _session.MessageCompleted -= OnCompleted;
        _session.Cleared -= OnCleared;
        _session.Restored -= OnRestored;
        _session.ResyncRequested -= OnResyncRequested;
        _webView.BridgeMessage -= OnPageMessage;
    }
}
