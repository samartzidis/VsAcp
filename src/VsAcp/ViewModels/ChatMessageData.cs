using VsAcp.Core.Abstractions;
using VsAcp.Core.Models;

namespace VsAcp.ViewModels;

/// <summary>
/// Lightweight DTO for passing message data to the page (serialised camelCase). Property names must match
/// <c>ChatMessage</c> in Web/src/bridge/protocol.ts.
/// </summary>
public class ChatMessageData
{
    /// <summary>The single ChatMessage -> WebView DTO mapping for the whole app.</summary>
    public static ChatMessageData From(ChatMessage m) => new()
    {
        Id = m.Id,
        Type = m.Type.ToString(),
        Content = m.Content,
        ToolName = m.ToolName,
        Title = m.Title,
        Status = m.Status.ToString(),
        ExpanderTitle = m.ExpanderTitle,
        BodyMode = m.BodyMode.ToString(),
        Body = m.Body,
        Language = m.Language,
        IsStreaming = m.IsStreaming,
        ImageDataUris = m.ImageDataUris is null ? null : new List<string>(m.ImageDataUris),
        FileNames = m.FileNames is null ? null : new List<string>(m.FileNames),
        FolderNames = m.FolderNames is null ? null : new List<string>(m.FolderNames),
        Selections = m.Selections?.Select(s => new SelectionChip { Label = s.Label, Tooltip = s.FilePath }).ToList(),
        CheckpointId = m.CheckpointId
    };

    public string Id { get; init; } = "";
    public string Type { get; init; } = "";
    public string Content { get; init; } = "";
    public string? ToolName { get; init; }
    public string Title { get; init; } = "";
    public string Status { get; init; } = "Pending";
    public string ExpanderTitle { get; init; } = "";
    public string BodyMode { get; init; } = "Markdown";
    public string? Body { get; init; }
    public string? Language { get; init; }
    public bool IsStreaming { get; init; }
    public List<string>? ImageDataUris { get; init; }
    public List<string>? FileNames { get; init; }

    /// <summary>Folder attachment names shown as chips on a user message. Display-only.</summary>
    public List<string>? FolderNames { get; init; }

    /// <summary>Pasted editor selections shown as chips (label + file path tooltip) on a user message.</summary>
    public List<SelectionChip>? Selections { get; init; }

    /// <summary>
    /// Workspace snapshot ID taken before this user message was sent.
    /// Populated only on user messages; carried to JS so the "Revert to here" button can pass it back.
    /// </summary>
    public string? CheckpointId { get; init; }
}

public class SelectionChip
{
    public string Label { get; init; } = "";
    public string Tooltip { get; init; } = "";
}
