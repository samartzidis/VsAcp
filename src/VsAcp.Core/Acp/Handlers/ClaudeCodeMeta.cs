using System.Text.Json;

namespace VsAcp.Core.Acp.Handlers;

/// <summary>
/// Reads claude-agent-acp's private data in a tool call update's <c>_meta</c>. Not part of ACP: other agents never
/// send it, and its shape may change, so every read is defensive and an unexpected shape yields nothing.
/// </summary>
public static class ClaudeCodeMeta
{
    /// <summary>
    /// The files a shell command changed, as the agent saw them:
    /// <c>_meta.claudeCode.toolResponse.bashEditDiff.changedFiles</c> (absolute paths), else the <c>filePath</c> of
    /// each entry of <c>bashEditDiff.files</c>. Empty when the update carries no such data.
    /// </summary>
    public static IReadOnlyList<string> ShellChangedFiles(JsonElement? meta)
    {
        if (meta is not { ValueKind: JsonValueKind.Object } root
            || !TryGetObject(root, "claudeCode", out var claudeCode)
            || !TryGetObject(claudeCode, "toolResponse", out var toolResponse)
            || !TryGetObject(toolResponse, "bashEditDiff", out var bashEditDiff))
            return Array.Empty<string>();

        var paths = new List<string>();
        if (bashEditDiff.TryGetProperty("changedFiles", out var changed) && changed.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in changed.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } path)
                    paths.Add(path);
        }
        if (paths.Count == 0 && bashEditDiff.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in files.EnumerateArray())
                if (file.ValueKind == JsonValueKind.Object
                    && file.TryGetProperty("filePath", out var filePath)
                    && filePath.ValueKind == JsonValueKind.String
                    && filePath.GetString() is { Length: > 0 } path)
                    paths.Add(path);
        }
        return paths;
    }

    static bool TryGetObject(JsonElement element, string name, out JsonElement value) =>
        element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
}
