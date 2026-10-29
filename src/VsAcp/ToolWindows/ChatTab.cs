using Microsoft.VisualStudio.Shell;
using VsAcp.Core.Services;
using VsAcp.Bridge;
using VsAcp.Controls;
using VsAcp.ViewModels;
using VsAcp.InlineDiff;

namespace VsAcp.ToolWindows;

/// <summary>
/// One open chat tab: its view model wired to its chat in the panel's page (<see cref="ChatTabChannel"/>) through the
/// composer bridge, the message projector and the banners. Draws nothing itself: the page draws every chat. Created
/// when the tab is first shown, disposed when it closes.
/// </summary>
internal sealed class ChatTab : IDisposable
{
    private readonly ChatWebView _page;
    private ChatSessionViewModel? _viewModel;
    private ChatTabChannel? _channel;
    private Action<string>? _revertHandler;
    private Action<string>? _forkHandler;
    private TaskCompletionSource<bool?>? _pendingRevertConfirm;
    private ChatSessionProjector? _projector;
    private ComposerBridge? _composer;

    public ChatTab(ChatWebView page) => _page = page;

    /// <summary>The tab's view model, once <see cref="Initialize"/> has run.</summary>
    public ChatSessionViewModel? ViewModel => _viewModel;

    public void Dispose()
    {
        _composer?.Dispose();
        _composer = null;
        if (_channel is not null)
        {
            if (_revertHandler is not null) _channel.RevertRequested -= _revertHandler;
            if (_forkHandler is not null) _channel.ForkRequested -= _forkHandler;
            _page.CloseTab(_channel.TabId);
        }
        _revertHandler = null;
        _forkHandler = null;
        // Tab going away with the confirmation open: treat as Cancel so the waiting task completes.
        _pendingRevertConfirm?.TrySetResult(null);
        if (_projector is not null)
        {
            _projector.RenderFailed -= OnProjectorRenderFailed;
            _projector.Dispose();
            _projector = null;
        }
    }

    /// <summary>
    /// Asks before "Revert to here" changes files. Returns null when the user cancels, else whether to restore
    /// every file (true: the user ticked "Also revert other changes", or the generic prompt was confirmed after
    /// the preview failed) or only the agent's (false). Reverts that change no files go ahead without a prompt;
    /// if the file list cannot be computed the user is still asked (generic message) rather than reverting blind.
    /// Ignored (null) while a turn is running - the banner slot may be holding a permission or elicitation prompt
    /// the agent is waiting on - or while another confirmation is open.
    /// </summary>
    private async Task<bool?> ConfirmRevertAsync(ChatSessionViewModel viewModel, string checkpointId)
    {
        if (viewModel.IsBusy || viewModel.IsRevertConfirmPending)
        {
            Serilog.Log.Information("[Revert] Ignored revert request for {Id}: busy={Busy} confirmPending={Pending}",
                checkpointId, viewModel.IsBusy, viewModel.IsRevertConfirmPending);
            return null;
        }

        RevertPreview? preview = null;
        try
        {
            var previewTask = viewModel.PreviewRevertAsync(checkpointId);
            if (previewTask is null)
                return false; // no checkpoint service: nothing on disk will be touched

            // Block sending from now until answered, so no turn starts between preview and confirm.
            viewModel.IsRevertConfirmPending = true;
            preview = await previewTask;
            if (!preview.HasChanges)
            {
                viewModel.IsRevertConfirmPending = false;
                return false;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[Revert] Preview failed for checkpoint {Id}; asking for generic confirmation", checkpointId);
        }

        var answer = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRevertConfirm = answer;
        try
        {
            viewModel.IsRevertConfirmPending = true;
            _channel!.ShowRevertConfirmBanner(preview, result =>
                answer.TrySetResult(!result.Confirmed ? null : preview is null || result.IncludeOther));
            return await answer.Task;
        }
        finally
        {
            _pendingRevertConfirm = null;
            viewModel.IsRevertConfirmPending = false;
        }
    }

    // End-of-turn re-projection could not be applied (broken render surface). Escalate to a visible,
    // non-DOM banner; the session model and persisted history are still valid, so "Reload view"
    // rebuilds the chat's view from the source of truth rather than touching any data.
    private void OnProjectorRenderFailed()
    {
        UiThread.Post("Render-failed banner", () =>
            _channel?.ShowErrorBanner(
                "The chat view failed to update and may be out of sync. Reload to restore it.",
                () => _projector?.Rebuild()));
    }

    internal void Initialize(ChatSessionViewModel viewModel, InlineDiffManager? manager, string sessionId)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _viewModel = viewModel;
        var channel = _channel = _page.OpenTab(sessionId);

        // Route revert requests from the page to this session's view model
        _revertHandler = checkpointId =>
        {
            // a failure is logged by UiThread.Post (it used to go to Debug output only)
            UiThread.Post("Revert", async () =>
            {
                if (await ConfirmRevertAsync(viewModel, checkpointId) is not { } includeOther)
                    return;

                await viewModel.RevertToMessageAsync(checkpointId, includeOther);
            });
        };
        channel.RevertRequested += _revertHandler;

        // "Fork from here": a new tab continues the conversation from that message
        _forkHandler = messageId => UiThread.Post("Fork", () => VsAcpPackage.ForkChatAsync(viewModel, messageId));
        channel.ForkRequested += _forkHandler;

        // Composer (input box, chips, toolbar, status bars) lives in the page; the bridge mirrors the
        // view model into it and turns its messages into view-model calls.
        _composer?.Dispose();
        _composer = new ComposerBridge(viewModel, channel, manager);

        // Single projection seam: the session raises change events, the projector renders them to
        // the page. The view-model no longer fires per-message UI events.
        if (_projector is not null)
        {
            _projector.RenderFailed -= OnProjectorRenderFailed;
            _projector.Dispose();
        }
        _projector = new ChatSessionProjector(viewModel.Session, channel);
        _projector.RenderFailed += OnProjectorRenderFailed;

        viewModel.PermissionPromptRequested += (request, resolve) =>
            channel.ShowPermissionBanner(request, resolve);

        viewModel.PermissionPromptDismissed += () =>
            channel.HideBanner();

        viewModel.ElicitationPromptRequested += (request, resolve) =>
            channel.ShowElicitationBanner(request, resolve);

        viewModel.ElicitationPromptDismissed += () =>
            channel.HideBanner();
    }
}
