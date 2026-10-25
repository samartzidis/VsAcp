using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using VsAcp.Core.Abstractions;
using VsAcp.Controls;
using VsAcp.InlineDiff;
using VsAcp.ViewModels;

namespace VsAcp.Bridge;

/// <summary>
/// Connects one chat tab's <see cref="ChatSessionViewModel"/> to the composer in the page (input box,
/// attachment chips, toolbar, busy line, pending-changes bar). The view model stays the source of
/// truth: state changes are coalesced into whole snapshots pushed to the page, and page messages are
/// translated into view-model calls. The text being typed is owned by the page while the user types
/// (<c>composer.changed</c> only mirrors it into the view model); the host sets it explicitly through
/// <c>composer.set</c> when it changes for another reason (cleared after send, restored after revert).
/// </summary>
internal sealed class ComposerBridge : IDisposable
{
    private const string PlaceholderReady = "Ask a question or describe a task...";
    private const string PlaceholderConnecting = "Connecting to agent...";

    private readonly ChatSessionViewModel _vm;
    private readonly ChatWebView _web;
    private readonly InlineDiffManager? _diff;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly ConditionalWeakTable<object, string> _ids = new();

    private int _nextId;
    private bool _applyingFromPage;
    private bool _stateScheduled;
    private bool _textScheduled;
    private bool _disposed;
    private string? _lastStateJson;

    public ComposerBridge(ChatSessionViewModel vm, ChatWebView web, InlineDiffManager? diff)
    {
        _vm = vm;
        _web = web;
        _diff = diff;

        _vm.PropertyChanged += OnVmPropertyChanged;
        foreach (var collection in new INotifyCollectionChanged[]
                 { _vm.PendingImages, _vm.PendingFiles, _vm.PendingFolders, _vm.PendingSelections, _vm.AvailableModes, _vm.AvailableModels })
            collection.CollectionChanged += OnCollectionChanged;

        if (_diff is not null) _diff.PendingChanged += OnPendingChanged;

        _web.BridgeMessage += OnPageMessage;
        _web.FilesDropped += OnFilesDropped;
        _web.FoldersDropped += OnFoldersDropped;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        foreach (var collection in new INotifyCollectionChanged[]
                 { _vm.PendingImages, _vm.PendingFiles, _vm.PendingFolders, _vm.PendingSelections, _vm.AvailableModes, _vm.AvailableModels })
            collection.CollectionChanged -= OnCollectionChanged;
        if (_diff is not null) _diff.PendingChanged -= OnPendingChanged;
        _web.BridgeMessage -= OnPageMessage;
        _web.FilesDropped -= OnFilesDropped;
        _web.FoldersDropped -= OnFoldersDropped;
    }

    /// <summary>Puts the caret in the input box (after files are dropped, for example).</summary>
    public void Focus() => _web.PostBridgeMessage(new { type = "composer.focus" });

    // ---- view model -> page ----

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatSessionViewModel.InputText) or nameof(ChatSessionViewModel.PendingCommandName)
            && !_applyingFromPage)
            ScheduleText();
        ScheduleState();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleState();

    private void OnPendingChanged() => ScheduleState();

    // May be called from any thread; everything else runs on the dispatcher. BeginInvoke at Background
    // priority (not a JoinableTaskFactory switch) is deliberate: it lets a burst of property changes
    // (restoring a reverted message touches many) collapse into one snapshot.
#pragma warning disable VSTHRD001, VSTHRD110
    private void ScheduleState()
    {
        if (_stateScheduled) return;
        _stateScheduled = true;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            _stateScheduled = false;
            if (!_disposed) PushState();
        }), DispatcherPriority.Background);
    }

    private void ScheduleText()
    {
        if (_textScheduled) return;
        _textScheduled = true;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            _textScheduled = false;
            if (!_disposed) PushText();
        }), DispatcherPriority.Background);
    }

#pragma warning restore VSTHRD001, VSTHRD110

    private void PushText() =>
        _web.PostBridgeMessage(new { type = "composer.set", text = _vm.InputText, commandName = _vm.PendingCommandName });

    private void PushState()
    {
        var state = BuildState();
        var json = BridgeJson.Serialize(new { type = "state", state });
        if (json == _lastStateJson) return;
        _lastStateJson = json;
        _web.PostBridgeJson(json);
    }

    private ComposerStateDto BuildState() => new(
        Enabled: _vm.IsInputAllowed,
        Placeholder: _vm.IsInitializing ? PlaceholderConnecting : PlaceholderReady,
        CanSend: _vm.SendCommand.CanExecute(null),
        Busy: _vm.IsBusy,
        Images: _vm.PendingImages.Select(i => new ImageChipDto(IdOf(i), i.FileName ?? "image")).ToList(),
        Files: _vm.PendingFiles.Select(f => new FileChipDto(IdOf(f), f.FileName, f.FilePath)).ToList(),
        AttachedFolders: _vm.PendingFolders.Select(f => new FolderChipDto(IdOf(f), f.FolderName, f.FolderPath)).ToList(),
        Selections: _vm.PendingSelections.Select(s => new SelectionChipDto(IdOf(s), s.Label, s.FilePath)).ToList(),
        ActiveContext: _vm.ActiveContextFilePath is { } path
            ? new ActiveContextDto(_vm.ActiveContextFileName ?? path, path, _vm.IsActiveContextEnabled)
            : null,
        Commands: _vm.SlashCommands.Select(c => new SlashCommandDto(c.Name, c.Description)).ToList(),
        Modes: _vm.AvailableModes.Select(m => new ChoiceDto(m.ModeId, m.DisplayName)).ToList(),
        ModeId: _vm.SelectedMode?.ModeId,
        Models: _vm.AvailableModels.Select(m => new ChoiceDto(ModelId(m), m.DisplayName)).ToList(),
        ModelId: _vm.SelectedModel is { } selected ? ModelId(selected) : null,
        AutoApprove: _vm.AutoApprove,
        Usage: _vm.UsageText is { } usage ? new UsageDto(usage, _vm.UsageToolTip) : null,
        PendingFiles: _diff?.PendingFiles()
            .Select(f => new PendingFileDto(
                System.IO.Path.GetFileName(f.FilePath), f.FilePath,
                VsAcp.Core.Services.ProjectFolders.RelativeTo(_vm.WorkingDirectory, f.FilePath),
                f.Kind.ToString().ToLowerInvariant(), f.Added, f.Removed, f.Source.ToString().ToLowerInvariant()))
            .ToList() ?? new List<PendingFileDto>(),
        Folders: _vm.ExtraFolders is { } folders
            ? new FoldersDto(
                folders.Folders.Select(f => new FolderDto(f.Path, FolderName(f.Path), f.Source.ToString().ToLowerInvariant())).ToList(),
                _vm.SupportsAdditionalDirectories,
                folders.CanEdit)
            : null,
        ActiveFolders: _vm.ActiveAdditionalDirectories?
            .Select(p => new FolderDto(p, FolderName(p), "active"))
            .ToList());

    private static string FolderName(string path)
    {
        var name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static string ModelId(ModelDisplayItem m) => m.ConfigId + "|" + m.Value;

    /// <summary>Stable id for a list item for as long as the item lives, so the page can refer to it later.</summary>
    private string IdOf(object item) => _ids.GetValue(item, _ => "a" + Interlocked.Increment(ref _nextId));

    // ---- page -> view model ----

    private void OnFilesDropped(string[] paths)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            // No file list in the drag: fall back to what is selected in Solution Explorer.
            if (paths.Length == 0) paths = SolutionExplorerSelection.Files();
            Serilog.Log.Information("[Composer] Files dropped: {Count}", paths.Length);
            if (paths.Length == 0) return;
            _vm.AddDroppedFiles(paths);
            Focus();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[Composer] Failed to handle dropped files");
        }
    }

    private void OnFoldersDropped(string[] paths)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            Serilog.Log.Information("[Folders] Items dropped on the folders chip: {Count}", paths.Length);
            _vm.AddDroppedFolders(paths);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[Folders] Failed to handle dropped folders");
        }
    }

    private void OnPageMessage(string type, JsonElement message)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            Handle(type, message);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[Composer] Failed to handle page message '{Type}'", type);
        }
    }

    private void Handle(string type, JsonElement m)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        switch (type)
        {
            case "ready":
                // The page (re)loaded: it has no state and an empty input.
                _lastStateJson = null;
                PushState();
                PushText();
                break;

            case "composer.changed":
                ApplyInput(m);
                break;

            case "composer.send":
                ApplyInput(m);
                if (_vm.SendCommand.CanExecute(null))
                    _vm.SendCommand.Execute(null);
                break;

            case "composer.stop":
                if (_vm.StopCommand.CanExecute(null))
                    _vm.StopCommand.Execute(null);
                break;

            case "composer.paste":
                var hasImage = m.TryGetProperty("hasImage", out var img) && img.GetBoolean();
                var text = m.GetProperty("text").GetString() ?? "";
                // Same precedence as before: an image on the clipboard wins, then a code reference, then plain text.
                if (hasImage && _vm.PasteImageFromClipboard()) break;
                if (text.Length > 0 && !_vm.TryPasteCodeReference(text))
                    _web.PostBridgeMessage(new { type = "composer.insert", text });
                break;

            case "composer.remove":
                Remove(m.GetProperty("kind").GetString(), m.GetProperty("id").GetString());
                break;

            case "composer.attachImage":
                _vm.AttachImageCommand.Execute(null);
                break;

            case "composer.setMode":
                var modeId = m.GetProperty("id").GetString();
                if (_vm.AvailableModes.FirstOrDefault(x => x.ModeId == modeId) is { } mode)
                    _vm.SelectedMode = mode;
                break;

            case "composer.setModel":
                var modelId = m.GetProperty("id").GetString();
                if (_vm.AvailableModels.FirstOrDefault(x => ModelId(x) == modelId) is { } model)
                    _vm.SelectedModel = model;
                break;

            case "composer.setAutoApprove":
                _vm.AutoApprove = m.GetProperty("value").GetBoolean();
                break;

            case "composer.toggleActiveContext":
                _vm.ToggleActiveContextCommand.Execute(null);
                break;

            case "folders.add":
                // Not inside the WebView2 message callback: the folder dialog is modal.
                UiThread.Post("folders.add", PickAndAddFolder);
                break;

            case "folders.remove":
                _vm.RemoveExtraFolder(m.GetProperty("path").GetString() ?? "");
                break;

            case "changes.acceptAll": _diff?.AcceptAll(ChangesSource(m)); break;
            case "changes.rejectAll": _diff?.RejectAll(ChangesSource(m)); break;
            case "changes.open": _diff?.OpenFile(m.GetProperty("path").GetString() ?? ""); break;
            case "changes.keep": _diff?.KeepFile(m.GetProperty("path").GetString() ?? ""); break;
            case "changes.undo": _diff?.UndoFile(m.GetProperty("path").GetString() ?? ""); break;
        }
    }

    /// <summary>Which group of the pending list a Keep All / Undo All is for ("agent" or "scan"); null = the whole list.</summary>
    private static DiffSource? ChangesSource(JsonElement m) =>
        m.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString() switch { "agent" => DiffSource.Agent, "scan" => DiffSource.Scan, _ => null }
            : null;

    private void PickAndAddFolder()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var picked = FolderPicker.Pick("Choose a folder to share with the agent", _vm.WorkingDirectory);
        if (picked is null) return;

        Serilog.Log.Information("[Folders] Adding {Path}", picked);
        _vm.AddExtraFolder(picked);
    }

    private void ApplyInput(JsonElement m)
    {
        _applyingFromPage = true;
        try
        {
            _vm.InputText = m.GetProperty("text").GetString() ?? "";
            _vm.PendingCommandName = m.TryGetProperty("commandName", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;
        }
        finally
        {
            _applyingFromPage = false;
        }
    }

    private void Remove(string? kind, string? id)
    {
        switch (kind)
        {
            case "image" when _vm.PendingImages.FirstOrDefault(x => IdOf(x) == id) is { } image:
                _vm.RemoveImageCommand.Execute(image);
                break;
            case "file" when _vm.PendingFiles.FirstOrDefault(x => IdOf(x) == id) is { } file:
                _vm.RemoveFileCommand.Execute(file);
                break;
            case "folder" when _vm.PendingFolders.FirstOrDefault(x => IdOf(x) == id) is { } folder:
                _vm.RemoveFolderCommand.Execute(folder);
                break;
            case "selection" when _vm.PendingSelections.FirstOrDefault(x => IdOf(x) == id) is { } selection:
                _vm.RemoveSelectionCommand.Execute(selection);
                break;
        }
    }
}
