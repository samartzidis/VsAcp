using System.IO;

namespace VsAcp.Core.Abstractions;

public sealed class FolderAttachment
{
    public required string FolderPath { get; set; }
    public string FolderName => Path.GetFileName(FolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}
