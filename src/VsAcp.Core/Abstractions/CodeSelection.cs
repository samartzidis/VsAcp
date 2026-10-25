using System.IO;
using System.Text.Json.Serialization;

namespace VsAcp.Core.Abstractions;

/// <summary>
/// A multi-line snippet pasted from an editor, kept as a reference (file + line range) plus the text
/// at paste time instead of being inserted into the input. Persisted with the user message.
/// </summary>
public sealed class CodeSelection
{
    public string FilePath { get; set; } = "";

    /// <summary>1-based first line of the snippet.</summary>
    public int StartLine { get; set; }

    /// <summary>1-based last line of the snippet (inclusive).</summary>
    public int EndLine { get; set; }

    public string Text { get; set; } = "";

    [JsonIgnore]
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>Chip label, e.g. <c>terragrunt.hcl (25-30)</c>.</summary>
    [JsonIgnore]
    public string Label => $"{FileName} ({StartLine}-{EndLine})";
}
