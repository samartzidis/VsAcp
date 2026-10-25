namespace VsAcp.Core.Tests.Support;

/// <summary>
/// A unique scratch directory under %TEMP% that is removed (best effort) on dispose. <see cref="Path"/> is the
/// directory to use; with a subfolder it lives inside <see cref="Root"/>, which leaves room beside it for files
/// that must not be part of a snapshotted workspace (the agent scenario and log).
/// </summary>
public sealed class TempDirectory : IDisposable
{
    public string Root { get; }

    public string Path { get; }

    public TempDirectory(string? subfolder = null)
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vsacp-tests", Guid.NewGuid().ToString("N"));
        Path = subfolder is null ? Root : System.IO.Path.Combine(Root, subfolder);
        Directory.CreateDirectory(Path);
    }

    public string Combine(string relativePath) => System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relativePath));

    public void Write(string relativePath, string content)
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public string Read(string relativePath) => File.ReadAllText(Combine(relativePath));

    public bool Exists(string relativePath) => File.Exists(Combine(relativePath));

    public void Dispose() => DeleteBestEffort(Root);

    internal static void DeleteBestEffort(string directory)
    {
        // LibGit2 and a just-killed agent process can hold files for a moment.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory)) return;
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception) when (attempt < 7)
            {
                Thread.Sleep(150);
            }
            catch (Exception)
            {
                // leave it to the OS temp cleanup
            }
        }
    }
}
