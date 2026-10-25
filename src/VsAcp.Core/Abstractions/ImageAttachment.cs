namespace VsAcp.Core.Abstractions;

public sealed class ImageAttachment
{
    public required byte[] Data { get; set; }
    public required string MimeType { get; set; }
    public string? FileName { get; set; }
}
