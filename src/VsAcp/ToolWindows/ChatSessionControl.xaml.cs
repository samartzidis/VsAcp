using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AcpProtocol;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using VsAcp.Core.Abstractions;
using VsAcp.Core.Services;
using VsAcp.Bridge;
using VsAcp.Controls;
using VsAcp.ViewModels;
using VsAcp.InlineDiff;

namespace VsAcp.ToolWindows;

public partial class ChatSessionControl : UserControl
{
    private ChatSessionViewModel? _viewModel;
    private Action<string>? _revertHandler;
    private TaskCompletionSource<bool?>? _pendingRevertConfirm;
    private ChatSessionProjector? _projector;
    private ComposerBridge? _composer;

    public ChatSessionControl()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _composer?.Dispose();
        _composer = null;
        if (_revertHandler is not null)
        {
            ChatWebView.RevertRequested -= _revertHandler;
            _revertHandler = null;
        }
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
    /// Asks the user, inline in the banner row, to confirm a revert that will change files on disk,
    /// listing them. Returns true to proceed. Reverts that change no files go ahead without a prompt;
    /// if the file list cannot be computed the user is still asked (generic message) rather than
    /// reverting blind. Ignored (false) while a turn is running - the banner slot may be holding a
    /// permission or elicitation prompt the agent is waiting on - or while another confirmation is open.
    /// </summary>
    /// <summary>
    /// Asks before "Revert to here" changes files. Returns null when the user cancels, else whether to restore
    /// every file (true: the user ticked "Also revert other changes", or the generic prompt was confirmed after
    /// the preview failed) or only the agent's (false).
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
            ChatWebView.ShowRevertConfirmBanner(preview, result =>
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
    // rebuilds the WebView from the source of truth rather than touching any data.
    private void OnProjectorRenderFailed()
    {
        UiThread.Post("Render-failed banner", () =>
            ChatWebView.ShowErrorBanner(
                "The chat view failed to update and may be out of sync. Reload to restore it.",
                () => _projector?.Rebuild()));
    }

    internal void Initialize(ChatSessionViewModel viewModel, InlineDiffManager? manager, string sessionId)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _viewModel = viewModel;
        ChatWebView.LogTag = sessionId;
        DataContext = viewModel;

        // Route revert requests from the WebView to this session's view model
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
        ChatWebView.RevertRequested += _revertHandler;

        // Composer (input box, chips, toolbar, status bars) lives in the page; the bridge mirrors the
        // view model into it and turns its messages into view-model calls.
        _composer?.Dispose();
        _composer = new ComposerBridge(viewModel, ChatWebView, manager);

        // Single projection seam: the session raises change events, the projector renders them to
        // the WebView. The view-model no longer fires per-message UI events.
        if (_projector is not null)
        {
            _projector.RenderFailed -= OnProjectorRenderFailed;
            _projector.Dispose();
        }
        _projector = new ChatSessionProjector(viewModel.Session, ChatWebView);
        _projector.RenderFailed += OnProjectorRenderFailed;

        viewModel.PermissionPromptRequested += (request, resolve) =>
            ChatWebView.ShowPermissionBanner(request, resolve);

        viewModel.PermissionPromptDismissed += () =>
            ChatWebView.HideBanner();

        viewModel.ElicitationPromptRequested += (request, resolve) =>
            ChatWebView.ShowElicitationBanner(request, resolve);

        viewModel.ElicitationPromptDismissed += () =>
            ChatWebView.HideBanner();

        // Apply VS theme colors to the WebView
        ApplyThemeColors();

        // Re-apply when VS theme changes
        VSColorTheme.ThemeChanged += OnThemeChanged;
    }

    private void OnThemeChanged(ThemeChangedEventArgs e)
    {
        UiThread.Post("Apply theme colours", ApplyThemeColors);
    }

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

        // Scrollbar: derive from gray/muted text for subtle appearance
        var gray = VSColorTheme.GetThemedColor(EnvironmentColors.ScrollBarThumbBackgroundColorKey);
        colors["--scrollbar-thumb"] = $"rgba({gray.R}, {gray.G}, {gray.B}, 0.5)";
        var grayHover = VSColorTheme.GetThemedColor(EnvironmentColors.ScrollBarThumbMouseOverBackgroundColorKey);
        colors["--scrollbar-thumb-hover"] = $"rgba({grayHover.R}, {grayHover.G}, {grayHover.B}, 0.8)";

        ChatWebView.SetChromeBackground(editor.Background);
        // Light or dark follows the editor background (not the shell theme), because that is what the chat
        // sits next to; a Windows high-contrast theme switches the page to outlines and full-contrast text.
        var luminance = (0.299 * editor.Background.R + 0.587 * editor.Background.G + 0.114 * editor.Background.B) / 255.0;
        ChatWebView.SetTheme(colors, isLight: luminance > 0.5, highContrast: SystemParameters.HighContrast);

        var editorBrush = new SolidColorBrush(editor.Background);
        editorBrush.Freeze();
        RootPanel.Background = editorBrush;
        Background = editorBrush;
    }
}
