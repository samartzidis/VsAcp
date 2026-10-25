using System.IO;

namespace VsAcp.Core.Abstractions;

public sealed class FileAttachment
{
    public required string FilePath { get; set; }
    public string FileName => Path.GetFileName(FilePath);
}
