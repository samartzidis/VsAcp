using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VsAcp.Core.Abstractions;
using VsAcp.Core.Configuration;
using VsAcp.Core.Models;

namespace VsAcp.Core.Services;

public class JsonSessionStore : ISessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Compact (single-line) options for JSONL message records - one PersistedMessage per line.
    private static readonly JsonSerializerOptions JsonLineOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string? _basePath;
    private readonly string _workingDirectory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<JsonSessionStore> _logger;

    public JsonSessionStore(IOptions<VsAcpOptions> options, ILogger<JsonSessionStore> logger)
        : this(options.Value.WorkingDirectory, options.Value.HasProjectDirectory, logger) { }

    public JsonSessionStore(string workingDirectory, bool hasProjectDirectory, ILogger<JsonSessionStore> logger)
    {
        _workingDirectory = workingDirectory;
        _basePath = (hasProjectDirectory && !VsAcpPaths.IsNetworkPath(workingDirectory))
            ? VsAcpPaths.SessionsPath(workingDirectory)
            : null;
        _logger = logger;

        _logger.LogDebug("[SessionStore] Base path: {Path}", _basePath ?? "(disabled - no local project)");
    }

    // --- Workspace ---

    public async Task EnsureWorkspaceAsync()
    {
        if (_basePath is null) return;

        var infoPath = Path.Combine(_basePath, "workspace.json");
        if (File.Exists(infoPath))
        {
            _logger.LogDebug("[SessionStore] Workspace already initialised at {Path}", _basePath);
            return;
        }

        _logger.LogInformation("[SessionStore] Creating workspace at {Path}", _basePath);
        Directory.CreateDirectory(_basePath);

        var info = new WorkspaceInfo
        {
            FolderPath = _workingDirectory,
            CreatedUtc = DateTime.UtcNow
        };
        await WriteJsonAsync(infoPath, info);

        var indexPath = Path.Combine(_basePath, "sessions-index.json");
        if (!File.Exists(indexPath))
            await WriteJsonAsync(indexPath, new List<SessionEntry>());

    }

    // --- Session Index ---

    public async Task<IReadOnlyList<SessionEntry>> GetSessionIndexAsync()
    {
        if (_basePath is null) return Array.Empty<SessionEntry>();

        var indexPath = IndexPath;
        if (!File.Exists(indexPath))
            return Array.Empty<SessionEntry>();

        return await ReadJsonAsync<List<SessionEntry>>(indexPath) ?? new List<SessionEntry>();
    }

    public async Task<SessionEntry> CreateSessionAsync(string title)
    {
        if (_basePath is null)
            return new SessionEntry { Id = Guid.NewGuid().ToString("N"), Title = title, CreatedUtc = DateTime.UtcNow, LastActivityUtc = DateTime.UtcNow };

        await _lock.WaitAsync();
        try
        {
            await EnsureWorkspaceAsync();

            var index = await ReadJsonAsync<List<SessionEntry>>(IndexPath) ?? new List<SessionEntry>();

            var entry = new SessionEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = title,
                Ordinal = index.Count,
                CreatedUtc = DateTime.UtcNow,
                LastActivityUtc = DateTime.UtcNow
            };

            index.Add(entry);
            await WriteJsonAsync(IndexPath, index);

            return entry;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateSessionAsync(SessionEntry entry)
    {
        if (_basePath is null) return;

        await _lock.WaitAsync();
        try
        {
            var index = await ReadJsonAsync<List<SessionEntry>>(IndexPath) ?? new List<SessionEntry>();

            var existing = index.FindIndex(s => s.Id == entry.Id);
            if (existing >= 0)
            {
                index[existing] = entry;
                await WriteJsonAsync(IndexPath, index);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteSessionAsync(string sessionId)
    {
        if (_basePath is null) return;

        await _lock.WaitAsync();
        try
        {
            var index = await ReadJsonAsync<List<SessionEntry>>(IndexPath) ?? new List<SessionEntry>();

            index.RemoveAll(s => s.Id == sessionId);
            await WriteJsonAsync(IndexPath, index);

            var messagesPath = MessagesPath(sessionId);
            if (File.Exists(messagesPath))
                File.Delete(messagesPath);
        }
        finally
        {
            _lock.Release();
        }
    }

    // --- Messages ---

    public async Task<IReadOnlyList<PersistedMessage>> GetMessagesAsync(string sessionId)
    {
        if (_basePath is null) return Array.Empty<PersistedMessage>();

        var messagesPath = MessagesPath(sessionId);
        if (!File.Exists(messagesPath))
            return Array.Empty<PersistedMessage>();

        var messages = await ReadMessagesAsync(messagesPath);
        // Append order is completion order; Ordinal is the creation-order key, so sort by it.
        messages.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
        return messages;
    }

    public async Task AppendMessageAsync(string sessionId, PersistedMessage message)
    {
        if (_basePath is null) return;

        await _lock.WaitAsync();
        try
        {
            var messagesPath = MessagesPath(sessionId);

            // Ordinal is the caller-assigned creation-order sequence (see ChatSession.NextSequence).
            // Defensive fallback only: if unset, append at the end of the current sequence.
            if (message.Ordinal < 0)
            {
                var existing = File.Exists(messagesPath) ? await ReadMessagesAsync(messagesPath) : new List<PersistedMessage>();
                message.Ordinal = existing.Count == 0 ? 0 : existing.Max(m => m.Ordinal) + 1;
            }

            EnsureDirectory(messagesPath);
            var line = JsonSerializer.Serialize(message, JsonLineOptions) + "\n";
            using var writer = new StreamWriter(messagesPath, append: true, Encoding.UTF8);
            await writer.WriteAsync(line);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearMessagesAsync(string sessionId)
    {
        if (_basePath is null) return;

        var messagesPath = MessagesPath(sessionId);
        if (File.Exists(messagesPath))
            File.Delete(messagesPath);
    }

    public async Task TruncateMessagesAsync(string sessionId, int keepUpToOrdinal)
    {
        if (_basePath is null) return;

        await _lock.WaitAsync();
        try
        {
            var messagesPath = MessagesPath(sessionId);
            if (!File.Exists(messagesPath)) return;

            var messages = await ReadMessagesAsync(messagesPath);
            var kept = messages.Where(m => m.Ordinal <= keepUpToOrdinal).ToList();

            if (kept.Count == messages.Count) return;

            if (kept.Count == 0)
            {
                File.Delete(messagesPath);
            }
            else
            {
                kept.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
                await WriteMessagesAsync(messagesPath, kept);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    // --- Path helpers ---

    private string IndexPath => Path.Combine(_basePath!, "sessions-index.json");
    private string MessagesPath(string sessionId) => Path.Combine(_basePath!, $"messages-{sessionId}.jsonl");

    // --- JSONL message helpers ---

    private static async Task<List<PersistedMessage>> ReadMessagesAsync(string path)
    {
        var result = new List<PersistedMessage>();
        using var reader = new StreamReader(path, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var msg = JsonSerializer.Deserialize<PersistedMessage>(line, JsonLineOptions);
                if (msg is not null) result.Add(msg);
            }
            catch (JsonException)
            {
                // Skip a corrupt line rather than losing the whole transcript.
            }
        }
        return result;
    }

    private static async Task WriteMessagesAsync(string path, IEnumerable<PersistedMessage> messages)
    {
        EnsureDirectory(path);
        var sb = new StringBuilder();
        foreach (var m in messages)
            sb.Append(JsonSerializer.Serialize(m, JsonLineOptions)).Append('\n');
        using var writer = new StreamWriter(path, append: false, Encoding.UTF8);
        await writer.WriteAsync(sb.ToString());
    }

    private static void EnsureDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (dir is not null)
            Directory.CreateDirectory(dir);
    }

    private static async Task<T?> ReadJsonAsync<T>(string path)
    {
        var json = await ReadTextAsync(path);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private static async Task WriteJsonAsync<T>(string path, T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        await WriteTextAsync(path, json);
    }

    private static async Task<string> ReadTextAsync(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static async Task WriteTextAsync(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        await writer.WriteAsync(content);
    }
}
