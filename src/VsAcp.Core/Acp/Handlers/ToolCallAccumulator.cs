using System.Text.Json;
using AcpProtocol;
using VsAcp.Core.Abstractions;

namespace VsAcp.Core.Acp.Handlers;

/// <summary>
/// Accumulates per-tool-call presentation state (display titles and merged body content) across the
/// streaming <c>tool_call</c> / <c>tool_call_update</c> notifications, and renders the body for the
/// outgoing <see cref="OutputItem"/>. State is per prompt and cleared via <see cref="Reset"/>.
/// </summary>
public sealed class ToolCallAccumulator
{
    readonly Dictionary<string, string> _toolCallTitles = new();
    readonly Dictionary<string, string> _toolCallNames = new();
    readonly Dictionary<string, ToolCallBodyState> _toolCallBodies = new();

    public void Reset()
    {
        _toolCallTitles.Clear();
        _toolCallNames.Clear();
        _toolCallBodies.Clear();
    }

    /// <summary>Resolves and records the display title for an initial tool call.</summary>
    public string ResolveCallTitle(string id, string? title, string? name, ToolKind? kind, ToolCallLocation[]? locations, JsonElement? rawInput)
    {
        if (!string.IsNullOrWhiteSpace(name))
            _toolCallNames[id] = name!;

        var displayTitle = BuildDisplayTitle(title, name, kind, locations, rawInput);
        _toolCallTitles[id] = displayTitle;
        return displayTitle;
    }

    /// <summary>
    /// Resolves the display title for a tool-call update. When the agent sends an update with no
    /// real title (e.g. the trailing completed update carries <c>title=null, kind=null</c>), keep
    /// the title already resolved for this call rather than downgrading to a synthesized
    /// placeholder ("Tool" / "Tool &lt;file&gt;").
    /// </summary>
    public string ResolveUpdateTitle(string id, string? title, string? name, ToolKind? kind, ToolCallLocation[]? locations, JsonElement? rawInput)
    {
        if (string.IsNullOrWhiteSpace(title) && _toolCallTitles.TryGetValue(id, out var previousTitle))
            return previousTitle;

        if (!string.IsNullOrWhiteSpace(name))
            _toolCallNames[id] = name!;
        else
            _toolCallNames.TryGetValue(id, out name);

        var displayTitle = BuildDisplayTitle(title, name, kind, locations, rawInput);
        _toolCallTitles[id] = displayTitle;
        return displayTitle;
    }

    /// <summary>
    /// Populates <paramref name="item"/>'s Body/BodyMode/Language. Returns true when a file diff
    /// drove the body (caller may log this), false when merged input/output/content text was used.
    /// </summary>
    public bool PopulateBody(OutputItem item, string id, ToolCallContent[]? content,
        JsonElement? rawInput, JsonElement? rawOutput, ToolCallLocation[]? locations)
    {
        var diffBody = BuildDiffBody(content);
        if (diffBody is not null)
        {
            item.Body = diffBody;
            item.BodyMode = OutputBodyMode.Markdown;
            return true;
        }

        var state = GetOrCreateBodyState(id);
        state.Merge(content, rawInput, rawOutput);
        item.Body = state.Render();
        item.BodyMode = state.Mode;
        if (state.Mode == OutputBodyMode.PlainText)
            item.Language = InferLanguage(locations, rawInput);
        return false;
    }

    ToolCallBodyState GetOrCreateBodyState(string toolCallId)
    {
        if (!_toolCallBodies.TryGetValue(toolCallId, out var state))
        {
            state = new ToolCallBodyState();
            _toolCallBodies[toolCallId] = state;
        }
        return state;
    }

    static string? BuildDiffBody(ToolCallContent[]? content)
    {
        if (content is null) return null;

        var diffs = content.OfType<ToolCallContentDiff>().ToArray();
        if (diffs.Length == 0) return null;

        var sb = new System.Text.StringBuilder();
        foreach (var diff in diffs)
        {
            sb.AppendLine($"**{diff.Path}**");
            sb.AppendLine("```diff");
            if (diff.OldText is not null)
            {
                foreach (var line in diff.OldText.Replace("\r\n", "\n").Split('\n'))
                    sb.AppendLine($"- {line}");
            }
            foreach (var line in diff.NewText.Replace("\r\n", "\n").Split('\n'))
                sb.AppendLine($"+ {line}");
            sb.AppendLine("```");
        }

        return sb.ToString();
    }

    static string BuildDisplayTitle(string? title, string? name, ToolKind? kind, ToolCallLocation[]? locations, JsonElement? rawInput)
    {
        var baseTitle = string.IsNullOrWhiteSpace(title) ? null : title;

        // No human-readable title: prefer the agent's tool name over a generic label from `kind`.
        baseTitle ??= HumanizeToolName(name);

        baseTitle ??= kind switch
        {
            ToolKind.Read => "Read",
            ToolKind.Edit => "Edit",
            ToolKind.Delete => "Delete",
            ToolKind.Move => "Move",
            ToolKind.Search => "Search",
            ToolKind.Execute => "Terminal",
            ToolKind.Think => "Thinking",
            ToolKind.Fetch => "Fetch",
            _ => "Tool"
        };

        var location = locations?.FirstOrDefault()?.Path;
        if (location is not null)
        {
            var fileName = System.IO.Path.GetFileName(location);
            if (!string.IsNullOrEmpty(fileName) && !baseTitle.Contains(fileName))
                return $"{baseTitle} {fileName}";
        }

        if (rawInput is JsonElement input && input.ValueKind == JsonValueKind.Object)
        {
            var detail = TryExtractInputDetail(input);
            if (detail is not null && !baseTitle.Contains(detail))
                return $"{baseTitle} {detail}";
        }

        return baseTitle;
    }

    /// <summary>
    /// Turns a protocol tool name into a display label: <c>read_file</c> → "Read file",
    /// <c>mcp__server__tool</c> → "Tool (server)".
    /// </summary>
    static string? HumanizeToolName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var parts = name!.Split(new[] { "__" }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        var label = parts[parts.Length - 1].Replace('_', ' ').Trim();
        if (label.Length == 0) return null;

        label = char.ToUpperInvariant(label[0]) + label.Substring(1);
        return parts.Length >= 3 && parts[0] == "mcp" ? $"{label} ({parts[1]})" : label;
    }

    static string? TryExtractInputDetail(JsonElement input)
    {
        string[] pathKeys = ["path", "file_path", "filePath", "file", "glob", "pattern", "command"];
        foreach (var key in pathKeys)
        {
            if (input.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
            {
                var s = val.GetString();
                if (s is not null && !string.IsNullOrWhiteSpace(s))
                    return s.Length > 80 ? s.Substring(0, 77) + "..." : s;
            }
        }
        return null;
    }

    const int MaxBodyLength = 4000;

    static string Truncate(string s) =>
        s.Length > MaxBodyLength ? s.Substring(0, MaxBodyLength) + "\n... (truncated)" : s;

    static string? ExtractJsonStringField(JsonElement? el, params string[] keys)
    {
        if (el is not JsonElement e || e.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in keys)
        {
            if (e.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
            {
                var s = val.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
    }

    static string? InferLanguage(ToolCallLocation[]? locations, JsonElement? rawInput)
    {
        var path = locations?.FirstOrDefault()?.Path
            ?? ExtractJsonStringField(rawInput, "path", "file_path", "filePath", "file");
        if (path is null) return null;
        return System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant() switch
        {
            "cs" => "csharp",
            "ts" => "typescript",
            "tsx" => "typescript",
            "js" => "javascript",
            "jsx" => "javascript",
            "py" => "python",
            "json" => "json",
            "xml" => "xml",
            "md" => "markdown",
            "html" or "htm" => "html",
            "css" => "css",
            "sh" or "bash" => "bash",
            "sql" => "sql",
            "go" => "go",
            "rs" => "rust",
            "java" => "java",
            "cpp" or "cc" or "cxx" => "cpp",
            "c" => "c",
            "rb" => "ruby",
            "php" => "php",
            "swift" => "swift",
            "kt" or "kts" => "kotlin",
            "yaml" or "yml" => "yaml",
            "toml" => "toml",
            "ps1" or "psm1" => "powershell",
            var ext when ext.Length > 0 => ext,
            _ => null
        };
    }

    sealed class ToolCallBodyState
    {
        public string? Input { get; private set; }
        public string? Output { get; private set; }
        public string? ContentText { get; private set; }
        public string? ContentHtml { get; private set; }

        public OutputBodyMode Mode =>
            ContentHtml is not null ? OutputBodyMode.Html : OutputBodyMode.PlainText;

        public void Merge(ToolCallContent[]? content, JsonElement? rawInput, JsonElement? rawOutput)
        {
            if (rawInput is JsonElement ri && ri.ValueKind == JsonValueKind.Object)
            {
                var extracted = ExtractJsonStringField(ri, "command", "content", "text", "pattern", "query");
                if (extracted is not null)
                    Input = extracted;
                else
                {
                    var raw = ri.ToString();
                    if (!string.IsNullOrEmpty(raw) && raw != "{}")
                        Input = raw;
                }
            }

            if (rawOutput is JsonElement ro && ro.ValueKind == JsonValueKind.Object)
            {
                var extracted = ExtractJsonStringField(ro, "content", "output", "text", "result", "error");
                if (extracted is not null)
                    Output = extracted;
                else if (ro.ValueKind == JsonValueKind.Object)
                {
                    var raw = ro.ToString();
                    if (!string.IsNullOrEmpty(raw) && raw != "{}")
                        Output = raw;
                }
            }

            if (content is null) return;
            var blocks = content.OfType<ToolCallContentContent>().Select(c => c.Content).ToArray();
            var texts = blocks.OfType<ContentBlockText>().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
            var images = blocks.OfType<ContentBlockImage>().ToArray();

            if (texts.Length > 0)
                ContentText = string.Join("\n", texts);

            if (images.Length > 0)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var t in texts)
                {
                    sb.Append("<pre style=\"margin:0;white-space:pre-wrap;word-break:break-word\">");
                    sb.Append(System.Net.WebUtility.HtmlEncode(Truncate(t)));
                    sb.Append("</pre>");
                }
                foreach (var img in images)
                    sb.Append($"<img src=\"data:{System.Net.WebUtility.HtmlEncode(img.MimeType)};base64,{img.Data}\" style=\"max-width:100%;border-radius:4px;margin:4px 0\" />");
                ContentHtml = sb.ToString();
            }
        }

        public string? Render()
        {
            if (ContentHtml is not null) return ContentHtml;
            if (ContentText is not null) return Truncate(ContentText);

            var parts = new List<string>();
            if (Input is not null) parts.Add(Truncate(Input));
            if (Output is not null) parts.Add(Truncate(Output));
            return parts.Count > 0 ? string.Join("\n", parts) : null;
        }
    }
}
