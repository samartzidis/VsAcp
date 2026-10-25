using VsAcp.Core.Abstractions;

namespace VsAcp.Core.Models;

/// <summary>
/// Everything the user sent in one turn: the message and its attachments. <see cref="ImageDataUris"/> and
/// <see cref="FileNames"/> are the display forms stored with the message; <see cref="FilePaths"/> are the files
/// sent to the agent (the active editor file plus dropped files) and <see cref="DroppedFilePaths"/> only the
/// dropped ones, which is what a revert puts back in the composer. <see cref="FolderPaths"/> are dropped folders:
/// a one-off reference sent to the agent the same way (no active-context equivalent, so there is no separate
/// "dropped" list - the whole list is what a revert puts back, too).
/// </summary>
public sealed record TurnSubmission(
    string Message,
    IReadOnlyList<ImageAttachment>? Images = null,
    IReadOnlyList<string>? ImageDataUris = null,
    IReadOnlyList<string>? FileNames = null,
    IReadOnlyList<string>? FilePaths = null,
    IReadOnlyList<string>? DroppedFilePaths = null,
    IReadOnlyList<string>? FolderNames = null,
    IReadOnlyList<string>? FolderPaths = null,
    IReadOnlyList<CodeSelection>? Selections = null);
