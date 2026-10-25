using VsAcp.Core.Abstractions;
using VsAcp.Core.Models;

namespace VsAcp.Core.Services;

/// <summary>What a reverted message puts back into the composer.</summary>
public sealed record RestoredDraft(
    string Text,
    string? CommandName,
    IReadOnlyList<string> FilePaths,
    IReadOnlyList<string> FolderPaths,
    IReadOnlyList<CodeSelection> Selections,
    IReadOnlyList<ImageAttachment> Images);

/// <summary>Which of some dropped files to attach, and whether the active editor file is now covered by one of them.</summary>
public sealed record DroppedFiles(IReadOnlyList<string> ToAttach, bool DisableActiveContext);

/// <summary>
/// The composer's rules without its controls: how the input box and its attachments become a
/// <see cref="TurnSubmission"/>, and how a reverted message goes back into the input box. The view-model keeps the
/// bindable collections and calls these, so the rules can be tested without WPF.
/// </summary>
public static class Composer
{
    /// <summary>
    /// Builds the submission for the current composer contents. The text is trimmed; a picked slash command is
    /// put in front as <c>/name rest</c>, which is how ACP agents receive commands. The active editor file is
    /// sent along when it is enabled and still exists, followed by the dropped files. Dropped folders are sent
    /// as a plain reference (no active-context equivalent). Empty attachment lists become null.
    /// </summary>
    public static TurnSubmission BuildSubmission(
        string inputText,
        string? commandName,
        IReadOnlyList<ImageAttachment> images,
        IReadOnlyList<FileAttachment> files,
        IReadOnlyList<FolderAttachment> folders,
        IReadOnlyList<CodeSelection> selections,
        string? activeContextFile,
        bool activeContextEnabled,
        Func<string, bool>? fileExists = null)
    {
        fileExists ??= System.IO.File.Exists;

        var message = (inputText ?? "").Trim();
        if (commandName is not null)
            message = ("/" + commandName + " " + message).TrimEnd();

        var imageList = images.Count > 0 ? images.ToList() : null;
        var imageDataUris = imageList?.Select(ToDataUri).ToList();
        var fileNames = files.Count > 0 ? files.Select(f => f.FileName).ToList() : null;
        var droppedPaths = files.Count > 0 ? files.Select(f => f.FilePath).ToList() : null;
        var folderNames = folders.Count > 0 ? folders.Select(f => f.FolderName).ToList() : null;
        var folderPaths = folders.Count > 0 ? folders.Select(f => f.FolderPath).ToList() : null;

        var filePaths = new List<string>();
        if (activeContextFile is not null && activeContextEnabled && fileExists(activeContextFile))
            filePaths.Add(activeContextFile);
        filePaths.AddRange(files.Select(f => f.FilePath));

        return new TurnSubmission(
            message,
            imageList,
            imageDataUris,
            fileNames,
            filePaths.Count > 0 ? filePaths : null,
            droppedPaths,
            folderNames,
            folderPaths,
            selections.Count > 0 ? selections.ToList() : null);
    }

    /// <summary>
    /// Works out what a reverted user message puts back in the composer so it can be resubmitted or edited:
    /// a leading <c>/known-command</c> becomes the command chip, attached files and folders come back if they
    /// still exist (once each, and a file is skipped when it is the active editor file, which is attached
    /// anyway), and snippets and images come back as they were. Images that cannot be decoded are dropped.
    /// </summary>
    public static RestoredDraft Restore(
        PersistedMessage reverted,
        Func<string, bool> isKnownCommand,
        string? activeContextFile,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? folderExists = null)
    {
        fileExists ??= System.IO.File.Exists;
        folderExists ??= System.IO.Directory.Exists;

        var content = reverted.Content ?? "";
        string? commandName = null;
        var match = System.Text.RegularExpressions.Regex.Match(content, @"^/(\S+)(?:\s+|$)");
        if (match.Success && isKnownCommand(match.Groups[1].Value))
        {
            commandName = match.Groups[1].Value;
            content = content.Substring(match.Length);
        }

        var files = new List<string>();
        foreach (var path in reverted.FilePaths ?? Enumerable.Empty<string>())
        {
            if (!fileExists(path)) continue;
            if (files.Contains(path)) continue;
            if (string.Equals(path, activeContextFile, StringComparison.OrdinalIgnoreCase)) continue;
            files.Add(path);
        }

        var folders = new List<string>();
        foreach (var path in reverted.FolderPaths ?? Enumerable.Empty<string>())
        {
            if (!folderExists(path)) continue;
            if (folders.Contains(path)) continue;
            folders.Add(path);
        }

        var images = new List<ImageAttachment>();
        foreach (var uri in reverted.ImageDataUris ?? Enumerable.Empty<string>())
        {
            var image = TryParseImageDataUri(uri);
            if (image is not null)
                images.Add(image);
        }

        return new RestoredDraft(
            content,
            commandName,
            files,
            folders,
            reverted.Selections?.ToList() ?? new List<CodeSelection>(),
            images);
    }

    /// <summary>
    /// Decides which dropped files to attach: those that exist and are not already attached (or repeated in the
    /// drop). Dropping the active editor file turns its automatic attachment off so it is not sent twice.
    /// </summary>
    public static DroppedFiles AcceptDroppedFiles(
        IEnumerable<string> dropped,
        IReadOnlyCollection<string> alreadyAttached,
        string? activeContextFile,
        bool activeContextEnabled,
        Func<string, bool>? fileExists = null)
    {
        fileExists ??= System.IO.File.Exists;

        var attached = new HashSet<string>(alreadyAttached);
        var toAttach = new List<string>();
        var disableActive = false;
        foreach (var path in dropped)
        {
            if (!fileExists(path)) continue;
            if (!attached.Add(path)) continue;
            if (activeContextEnabled && string.Equals(path, activeContextFile, StringComparison.OrdinalIgnoreCase))
                disableActive = true;
            toAttach.Add(path);
        }
        return new DroppedFiles(toAttach, disableActive);
    }

    /// <summary>
    /// Which of some dropped folders to attach: those that exist and are not already attached (or repeated in
    /// the drop). A one-off reference for this message only - unlike the shared extra-folders list, it grants
    /// the agent no session-wide access.
    /// </summary>
    public static IReadOnlyList<string> AcceptDroppedFolders(
        IEnumerable<string> dropped,
        IReadOnlyCollection<string> alreadyAttached,
        Func<string, bool>? folderExists = null)
    {
        folderExists ??= System.IO.Directory.Exists;

        var attached = new HashSet<string>(alreadyAttached);
        var toAttach = new List<string>();
        foreach (var path in dropped)
        {
            if (!folderExists(path)) continue;
            if (!attached.Add(path)) continue;
            toAttach.Add(path);
        }
        return toAttach;
    }

    /// <summary><c>data:{mime};base64,{data}</c> for an attached image, as stored with the message.</summary>
    public static string ToDataUri(ImageAttachment image) =>
        $"data:{image.MimeType};base64,{Convert.ToBase64String(image.Data)}";

    /// <summary>
    /// Parses a <c>data:{mime};base64,{data}</c> URI back into an <see cref="ImageAttachment"/> so a reverted
    /// inline image can be resubmitted. Returns null if the URI is not a valid base64 data URI.
    /// </summary>
    public static ImageAttachment? TryParseImageDataUri(string uri)
    {
        if (string.IsNullOrEmpty(uri) || !uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return null;

        var comma = uri.IndexOf(',');
        if (comma < 0) return null;

        var header = uri.Substring(5, comma - 5); // between "data:" and ","
        if (!header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            return null;

        var mime = header.Substring(0, header.Length - ";base64".Length);
        if (string.IsNullOrEmpty(mime)) mime = "image/png";

        try
        {
            var data = Convert.FromBase64String(uri.Substring(comma + 1));
            var ext = mime.Split('/').LastOrDefault() is { Length: > 0 } e ? e : "png";
            return new ImageAttachment { Data = data, MimeType = mime, FileName = $"image.{ext}" };
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
