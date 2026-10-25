using System.Text.Json;
using Serilog;

namespace VsAcp.Core.Services;

/// <summary>
/// Reads and writes the user's extra-folder choices for one solution (<c>folders.json</c>). A missing or
/// unreadable file is treated as "no choices" so a bad file never blocks a chat from starting.
/// </summary>
public sealed class ExtraFoldersStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;

    public ExtraFoldersStore(string filePath) => _filePath = filePath;

    public string FilePath => _filePath;

    public ExtraFoldersData Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new ExtraFoldersData();
            var data = JsonSerializer.Deserialize<ExtraFoldersData>(File.ReadAllText(_filePath), JsonOptions);
            if (data is null) return new ExtraFoldersData();
            return new ExtraFoldersData
            {
                Added = data.Added?.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray() ?? [],
                Removed = data.Removed?.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray() ?? []
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Folders] Could not read {Path}; ignoring it", _filePath);
            return new ExtraFoldersData();
        }
    }

    /// <summary>Writes via a temporary file so a crash never leaves a half-written file. Returns false (and logs) on failure.</summary>
    public bool Save(ExtraFoldersData data)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data, JsonOptions));
            if (File.Exists(_filePath))
                File.Replace(temp, _filePath, null);
            else
                File.Move(temp, _filePath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Folders] Could not write {Path}", _filePath);
            return false;
        }
    }
}
