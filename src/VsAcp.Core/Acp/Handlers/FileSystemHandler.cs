using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using VsAcp.Core.Abstractions;
using AcpProtocol;

namespace VsAcp.Core.Acp.Handlers;

public class FileSystemHandler
{
    readonly IDiffBroker _diffBroker;
    readonly ILogger<FileSystemHandler> _logger;
    readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public FileSystemHandler(IDiffBroker diffBroker, ILogger<FileSystemHandler> logger)
    {
        _diffBroker = diffBroker;
        _logger = logger;
    }

    public virtual async ValueTask<ReadTextFileResponse> ReadAsync(
        ReadTextFileRequest request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("fs/read_text_file: {Path} (line={Line}, limit={Limit})",
            request.Path, request.Line, request.Limit);

        var path = request.Path;

        if (!File.Exists(path))
        {
            _logger.LogWarning("fs/read_text_file: file not found: {Path}", path);
            throw new AcpException($"File not found: {path}", default, -32001);
        }

        var allLines = await Task.Run(() => File.ReadAllLines(path), cancellationToken);

        string content;
        if (request.Line.HasValue || request.Limit.HasValue)
        {
            var startLine = (int)(request.Line ?? 1) - 1;
            var limit = (int)(request.Limit ?? (uint)allLines.Length);
            startLine = Math.Max(0, Math.Min(startLine, allLines.Length));
            var count = Math.Min(limit, allLines.Length - startLine);
            content = string.Join("\n", allLines.Skip(startLine).Take(count));
        }
        else
        {
            content = string.Join("\n", allLines);
        }

        _logger.LogDebug("fs/read_text_file: returning {Lines} lines from {Path}", allLines.Length, path);
        return new ReadTextFileResponse { Content = content };
    }

    public virtual async ValueTask<WriteTextFileResponse> WriteAsync(
        WriteTextFileRequest request, CancellationToken cancellationToken)
    {
        var path = request.Path;
        var sem = _fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));

        await sem.WaitAsync(cancellationToken);
        try
        {
            var isNewFile = !File.Exists(path);
            _logger.LogInformation("fs/write_text_file: {Path} (new={IsNew}, contentLength={Len})",
                path, isNewFile, request.Content?.Length ?? 0);

            // Read original before overwriting (needed for the inline diff overlay)
            var originalContent = isNewFile
                ? string.Empty
                : await Task.Run(() => File.ReadAllText(path), cancellationToken);

            var directory = Path.GetDirectoryName(path);
            if (directory is not null && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            // Write immediately - the inline diff adornment lets the user Keep/Undo afterwards
            await Task.Run(() => File.WriteAllText(path, request.Content ?? string.Empty), cancellationToken);
            _logger.LogInformation("fs/write_text_file: wrote {Path}", path);

            // Notify the inline diff manager so it can overlay the green/red adornment
            if (!isNewFile)
            {
                var diffRequest = new DiffRequest
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    FilePath = path,
                    OriginalContent = originalContent,
                    ProposedContent = request.Content ?? string.Empty
                };
                _diffBroker.FireDiffCompleted(diffRequest);
            }
        }
        finally
        {
            sem.Release();
        }

        return new WriteTextFileResponse();
    }
}
