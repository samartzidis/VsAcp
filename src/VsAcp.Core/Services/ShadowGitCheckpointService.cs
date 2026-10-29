using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VsAcp.Core.Abstractions;
using VsAcp.Core.Configuration;

namespace VsAcp.Core.Services;

/// <summary>
/// Implements workspace checkpointing via a private git repository stored in AppData.
/// No project-side git repo is required or touched. No git.exe dependency.
/// </summary>
public sealed class ShadowGitCheckpointService : ICheckpointService
{
    static readonly HashSet<string> ExcludedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".idea", ".vscode",
        ".pnp", "dist", "out", "__pycache__", ".mypy_cache", ".vsacp"
    };

    static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdb", ".nupkg", ".suo", ".user"
    };

    const string RefPrefix = "refs/checkpoints/";
    const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB per file

    readonly string _workspaceFolder;
    readonly string? _shadowRepoPath;
    readonly ILogger<ShadowGitCheckpointService> _logger;

    public ShadowGitCheckpointService(
        IOptions<VsAcpOptions> options,
        ILogger<ShadowGitCheckpointService> logger)
    {
        _workspaceFolder = options.Value.WorkingDirectory;
        _logger = logger;
        _shadowRepoPath = (options.Value.HasProjectDirectory && !VsAcpPaths.IsNetworkPath(_workspaceFolder))
            ? VsAcpPaths.CheckpointRepoPath(_workspaceFolder)
            : null;

        _logger.LogDebug("[Checkpoint] Shadow repo path: {Path}", _shadowRepoPath ?? "(disabled - no local project)");
    }

    // -------------------------------------------------------------------------
    // ICheckpointService
    // -------------------------------------------------------------------------

    public Task<string?> CreateCheckpointAsync(CancellationToken ct = default)
        => Task.Run(() => CreateCheckpointCore(ct), ct);

    public Task<RestoreResult> RestoreAsync(string checkpointId, IReadOnlyCollection<string>? onlyPaths = null, CancellationToken ct = default)
        => Task.Run(() => RestoreCore(checkpointId, ToFilter(onlyPaths), ct), ct);

    public Task<RestorePreview> PreviewRestoreAsync(string checkpointId, IReadOnlyCollection<string>? onlyPaths = null, CancellationToken ct = default)
        => Task.Run(() => PreviewRestoreCore(checkpointId, ToFilter(onlyPaths), ct), ct);

    /// <summary>The files a filtered restore may touch, by full path ignoring case; null = every file.</summary>
    private static HashSet<string>? ToFilter(IReadOnlyCollection<string>? onlyPaths)
    {
        if (onlyPaths is null) return null;
        var filter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in onlyPaths)
            filter.Add(FullPath(path));
        return filter;
    }

    /// <summary>Whether a restore with <paramref name="filter"/> may touch the file. Shared by restore and preview.</summary>
    private static bool InFilter(HashSet<string>? filter, string absPath) => filter is null || filter.Contains(FullPath(absPath));

    private static string FullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception) { return path; }
    }

    public Task<RestorePreview?> PreviewChangesSinceLatestCheckpointAsync(CancellationToken ct = default)
        => Task.Run(() => PreviewChangesSinceLatestCheckpointCore(ct), ct);

    public Task DeleteCheckpointsAsync(IReadOnlyCollection<string> checkpointIds, CancellationToken ct = default)
        => Task.Run(() => DeleteCheckpointsCore(checkpointIds), ct);

    public Task<IReadOnlyDictionary<string, string>> CloneCheckpointsAsync(IReadOnlyCollection<string> checkpointIds, CancellationToken ct = default)
        => Task.Run(() => CloneCheckpointsCore(checkpointIds), ct);

    public string? GetFileAtLatestCheckpoint(string absolutePath)
    {
        if (_shadowRepoPath is null) return null;
        try
        {
            if (!Repository.IsValid(_shadowRepoPath)) return null;

            using var repo = new Repository(_shadowRepoPath);
            var commit = GetLatestCheckpointCommit(repo);
            if (commit is null) return null;

            var gitPath = ToGitPath(absolutePath, _workspaceFolder);

            // Tree[path] resolves nested forward-slash paths through subtrees automatically.
            var entry = commit.Tree[gitPath];
            if (entry?.TargetType != TreeEntryTargetType.Blob) return null;

            var blob = (Blob)entry.Target;
            using var reader = new System.IO.StreamReader(blob.GetContentStream());
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[Checkpoint] GetFileAtLatestCheckpoint failed for {Path}: {Msg}", absolutePath, ex.Message);
            return null;
        }
    }

    public bool WasAbsentAtLatestCheckpoint(string absolutePath)
    {
        if (_shadowRepoPath is null) return false;
        try
        {
            var root = ProjectFolders.Normalize(_workspaceFolder);
            var full = Path.GetFullPath(absolutePath);
            if (root is null || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;

            // Never part of a snapshot, so its absence from the tree proves nothing.
            if (ExcludedExtensions.Contains(Path.GetExtension(full))) return false;
            var segments = full.Substring(root.Length).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Take(segments.Length - 1).Any(ExcludedFolders.Contains)) return false;

            if (!Repository.IsValid(_shadowRepoPath)) return false;
            using var repo = new Repository(_shadowRepoPath);
            var commit = GetLatestCheckpointCommit(repo);
            if (commit is null) return false;

            if (commit.Tree[ToGitPath(full, _workspaceFolder)] is not null) return false;
            return !ReadUnsnapshottedPaths(commit).Contains(full);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[Checkpoint] WasAbsentAtLatestCheckpoint failed for {Path}: {Msg}", absolutePath, ex.Message);
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Core implementations (run on thread pool)
    // -------------------------------------------------------------------------

    private string? CreateCheckpointCore(CancellationToken ct)
    {
        if (_shadowRepoPath is null) return null;
        try
        {
            if (!Directory.Exists(_workspaceFolder))
            {
                _logger.LogWarning("[Checkpoint] Workspace folder not found: {Path}", _workspaceFolder);
                return null;
            }

            EnsureShadowRepo();

            using var repo = new Repository(_shadowRepoPath);

            var checkpointId = $"{DateTime.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}"[..32];

            // Build tree - start from previous checkpoint tree for efficiency
            var prevCommit = GetLatestCheckpointCommit(repo);
            var treeDef = prevCommit is not null
                ? TreeDefinition.From(prevCommit.Tree)
                : new TreeDefinition();

            // The commit is stamped with the time the walk STARTED (see below), and the next snapshot
            // re-reads every file modified at or after it. Stamping the end of the walk would miss a file
            // written between being visited and the commit being created: it would keep stale content forever.
            var walkStart = DateTimeOffset.UtcNow;
            var prevTime = prevCommit?.Author.When.UtcDateTime ?? DateTime.MinValue;

            // Track which git-relative paths are still present on disk
            var presentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Present on disk but not captured in the tree (too large, locked, unreadable). Recorded
            // on the commit so a later restore knows these existed and must not delete them.
            var unsnapshotted = new List<string>();

            foreach (var absPath in EnumerateWorkspaceFiles(_workspaceFolder))
            {
                ct.ThrowIfCancellationRequested();

                var gitPath = ToGitPath(absPath, _workspaceFolder);

                var info = new FileInfo(absPath);
                if (info.Length > MaxFileSizeBytes)
                {
                    unsnapshotted.Add(gitPath);
                    continue;
                }

                presentPaths.Add(gitPath);

                // Only re-blob files that changed since the previous snapshot. A file that is not in the
                // previous tree must be captured whatever its timestamp: copying or unzipping a file keeps
                // its old modified date, and one that was skipped here would look "created later" to a
                // restore, which deletes such files.
                if (prevCommit is null
                    || info.LastWriteTimeUtc >= prevTime
                    || prevCommit.Tree[gitPath] is null)
                {
                    try
                    {
                        var blob = repo.ObjectDatabase.CreateBlob(absPath);
                        treeDef.Add(gitPath, blob, Mode.NonExecutableFile);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("[Checkpoint] Skipping {Path}: {Msg}", absPath, ex.Message);
                        unsnapshotted.Add(gitPath);
                    }
                }
            }

            // Remove tree entries for files that no longer exist on disk.
            // Must walk recursively - commit.Tree only enumerates direct children.
            if (prevCommit is not null)
            {
                foreach (var (gitPath, _) in WalkTreeBlobs(prevCommit.Tree))
                {
                    if (!presentPaths.Contains(gitPath))
                        treeDef.Remove(gitPath);
                }
            }

            var tree = repo.ObjectDatabase.CreateTree(treeDef);
            var sig = new Signature("VsAcp", "vsacp@internal", walkStart);

            var parents = prevCommit is not null
                ? new[] { prevCommit }
                : Array.Empty<Commit>();

            var commit = repo.ObjectDatabase.CreateCommit(
                sig, sig,
                BuildCommitMessage(checkpointId, unsnapshotted),
                tree,
                parents,
                prettifyMessage: false);

            repo.Refs.Add(RefPrefix + checkpointId, commit.Id);

            _logger.LogInformation("[Checkpoint] Created {Id} ({Files} paths, {Unsnapshotted} present but not captured)",
                checkpointId, presentPaths.Count, unsnapshotted.Count);
            return checkpointId;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Checkpoint] CreateCheckpoint failed");
            return null;
        }
    }

    private static Commit LookupCheckpointCommit(Repository repo, string checkpointId)
    {
        var reference = repo.Refs[RefPrefix + checkpointId]
            ?? throw new InvalidOperationException($"Checkpoint not found: {checkpointId}");

        return repo.Lookup<Commit>(reference.TargetIdentifier)
            ?? throw new InvalidOperationException($"Checkpoint commit missing for: {checkpointId}");
    }

    // Commit message layout: first line "checkpoint:{id}", then one "unsnapshotted:{gitPath}" line
    // per file that was on disk at snapshot time but could not be captured in the tree.
    const string UnsnapshottedPrefix = "unsnapshotted:";

    private static string BuildCommitMessage(string checkpointId, List<string> unsnapshotted)
    {
        if (unsnapshotted.Count == 0) return $"checkpoint:{checkpointId}";

        var sb = new System.Text.StringBuilder($"checkpoint:{checkpointId}");
        foreach (var gitPath in unsnapshotted)
            sb.Append('\n').Append(UnsnapshottedPrefix).Append(gitPath);
        return sb.ToString();
    }

    /// <summary>
    /// Absolute paths of files that existed when the checkpoint was taken but are not in its tree.
    /// Restore must leave these alone: their absence from the tree does not mean they were created
    /// after the checkpoint. Commits from before this was recorded simply yield an empty set.
    /// </summary>
    private HashSet<string> ReadUnsnapshottedPaths(Commit commit)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in commit.Message.Split('\n'))
        {
            if (line.StartsWith(UnsnapshottedPrefix, StringComparison.Ordinal))
                result.Add(ToAbsPath(line.Substring(UnsnapshottedPrefix.Length).TrimEnd('\r'), _workspaceFolder));
        }
        return result;
    }

    /// <summary>
    /// Read-only twin of <see cref="RestoreCore"/>: same walk, same "is the file already identical"
    /// test, same set of files considered for deletion, same <paramref name="filter"/> - but it only
    /// records what would happen. With a filter, what the filter leaves alone goes to
    /// <see cref="RestorePreview.Excluded"/>.
    /// </summary>
    private RestorePreview PreviewRestoreCore(string checkpointId, HashSet<string>? filter, CancellationToken ct)
    {
        var included = new PreviewLists();
        var excluded = new PreviewLists();

        if (_shadowRepoPath is not null)
        {
            EnsureShadowRepo();

            using var repo = new Repository(_shadowRepoPath);
            var commit = LookupCheckpointCommit(repo, checkpointId);

            // Files known to have existed at checkpoint time: those in the tree, plus those that
            // were present but could not be captured. Anything else on disk is a candidate for deletion.
            var snapshotPaths = ReadUnsnapshottedPaths(commit);

            foreach (var entry in WalkTreeBlobs(commit.Tree))
            {
                ct.ThrowIfCancellationRequested();

                var absPath = ToAbsPath(entry.GitPath, _workspaceFolder);
                snapshotPaths.Add(absPath);
                var lists = InFilter(filter, absPath) ? included : excluded;

                if (!File.Exists(absPath))
                {
                    lists.Recreated.Add(ToDisplayPath(absPath));
                    continue;
                }

                var info = new FileInfo(absPath);
                if (info.Length != entry.Blob.Size || !FileMatchesBlob(absPath, entry.Blob))
                    lists.Modified.Add(ToDisplayPath(absPath));
            }

            foreach (var absPath in EnumerateWorkspaceFiles(_workspaceFolder))
            {
                ct.ThrowIfCancellationRequested();
                if (!snapshotPaths.Contains(absPath) && IsDeletionCandidate(absPath))
                    (InFilter(filter, absPath) ? included : excluded).Deleted.Add(ToDisplayPath(absPath));
            }
        }

        var preview = included.ToPreview(filter is null ? null : excluded.ToPreview(null));
        _logger.LogInformation("[Checkpoint] Preview {Id}: {Modified} modified, {Recreated} recreated, {Deleted} deleted{Filtered}",
            checkpointId, preview.Modified.Count, preview.Recreated.Count, preview.Deleted.Count,
            filter is null ? "" : $" ({filter.Count} file(s) in the filter, {preview.Excluded!.TotalCount} other change(s) left alone)");
        return preview;
    }

    /// <summary>The three file lists of a preview while it is being built.</summary>
    private sealed class PreviewLists
    {
        public List<string> Modified { get; } = new();
        public List<string> Recreated { get; } = new();
        public List<string> Deleted { get; } = new();

        public RestorePreview ToPreview(RestorePreview? excluded)
        {
            var order = StringComparer.OrdinalIgnoreCase;
            Modified.Sort(order);
            Recreated.Sort(order);
            Deleted.Sort(order);
            return new RestorePreview { Modified = Modified, Recreated = Recreated, Deleted = Deleted, Excluded = excluded };
        }
    }

    private RestorePreview? PreviewChangesSinceLatestCheckpointCore(CancellationToken ct)
    {
        if (_shadowRepoPath is null || !Repository.IsValid(_shadowRepoPath)) return null;

        string? checkpointId;
        using (var repo = new Repository(_shadowRepoPath))
        {
            checkpointId = repo.Refs
                .Where(r => r.CanonicalName.StartsWith(RefPrefix, StringComparison.Ordinal))
                .Select(r => r.CanonicalName)
                .OrderByDescending(n => n, StringComparer.Ordinal)
                .Select(n => n.Substring(RefPrefix.Length))
                .FirstOrDefault();
        }
        if (checkpointId is null) return null;

        var relative = PreviewRestoreCore(checkpointId, null, ct);
        IReadOnlyList<string> Absolute(IReadOnlyList<string> paths) =>
            paths.Select(p => ToAbsPath(p, _workspaceFolder)).ToList();
        return new RestorePreview
        {
            Modified = Absolute(relative.Modified),
            Recreated = Absolute(relative.Recreated),
            Deleted = Absolute(relative.Deleted),
        };
    }

    /// <summary>Workspace-relative path with backslashes, for showing to the user.</summary>
    private string ToDisplayPath(string absPath)
        => absPath.Substring(_workspaceFolder.Length).TrimStart('\\', '/').Replace('/', '\\');

    private RestoreResult RestoreCore(string checkpointId, HashSet<string>? filter, CancellationToken ct)
    {
        if (_shadowRepoPath is null) return RestoreResult.Complete;

        EnsureShadowRepo();

        using var repo = new Repository(_shadowRepoPath);

        var commit = LookupCheckpointCommit(repo, checkpointId);

        // Collect paths in the snapshot and write blobs back to disk.
        // Must walk recursively - commit.Tree only enumerates direct children.
        // Seeded with files that existed but were not captured, so they survive the delete pass.
        var snapshotPaths = ReadUnsnapshottedPaths(commit);
        int written = 0, skipped = 0, leftAlone = 0;
        var failed = new List<string>();

        foreach (var entry in WalkTreeBlobs(commit.Tree))
        {
            ct.ThrowIfCancellationRequested();

            var absPath = ToAbsPath(entry.GitPath, _workspaceFolder);
            snapshotPaths.Add(absPath);

            // Outside the filter: not this restore's to touch (it still counts as "existed", so it is never deleted).
            if (!InFilter(filter, absPath))
            {
                leftAlone++;
                continue;
            }

            // Skip writing if the on-disk file is already identical to the blob.
            // This avoids spurious timestamp updates that cause VS to detect
            // "external changes" and trigger a solution reload mid-revert.
            if (File.Exists(absPath))
            {
                var info = new FileInfo(absPath);
                if (info.Length == entry.Blob.Size && FileMatchesBlob(absPath, entry.Blob))
                {
                    skipped++;
                    continue;
                }
            }

            var dir = Path.GetDirectoryName(absPath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // One file another process will not let go of must not leave the rest of the workspace half-restored.
            if (TryWriteBlob(absPath, entry.Blob, ct, out var error)) written++;
            else
            {
                failed.Add(ToDisplayPath(absPath));
                _logger.LogWarning("[Checkpoint] Could not restore {Path}: {Msg}", absPath, error);
            }
        }

        // Delete files that didn't exist at snapshot time (agent-created files)
        int deleted = 0, keptOversize = 0;
        foreach (var absPath in EnumerateWorkspaceFiles(_workspaceFolder))
        {
            ct.ThrowIfCancellationRequested();
            if (!snapshotPaths.Contains(absPath))
            {
                if (!InFilter(filter, absPath)) continue;
                if (!IsDeletionCandidate(absPath))
                {
                    keptOversize++;
                    continue;
                }

                try
                {
                    File.Delete(absPath);
                    deleted++;
                }
                catch (Exception ex)
                {
                    failed.Add(ToDisplayPath(absPath));
                    _logger.LogWarning("[Checkpoint] Could not delete {Path}: {Msg}", absPath, ex.Message);
                }
            }
        }

        _logger.LogInformation("[Checkpoint] Restored {Id} ({Written} written, {Skipped} unchanged, {Deleted} deleted, {Kept} oversize kept, {Failed} failed{Filtered})",
            checkpointId, written, skipped, deleted, keptOversize, failed.Count,
            filter is null ? "" : $", filtered to {filter.Count} file(s), {leftAlone} snapshot file(s) outside it");
        return new RestoreResult { Failed = failed };
    }

    // Waits before each retry of a write another process blocks (about 1.2 s in all): a scan, an indexer or a
    // virus scanner usually lets go of a file within milliseconds.
    private static readonly int[] SharingRetryDelaysMs = [20, 40, 80, 160, 320, 640];

    /// <summary>
    /// Writes the blob over the file. A sharing violation (another process has the file open) is retried; any other
    /// failure, and a violation that outlasts the retries, returns false with the reason and leaves the file as it was
    /// (the file is truncated only once it is open).
    /// </summary>
    private static bool TryWriteBlob(string absPath, Blob blob, CancellationToken ct, out string? error)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = blob.GetContentStream();
                using var fs = new FileStream(absPath, FileMode.Create, FileAccess.Write, FileShare.None);
                stream.CopyTo(fs);
                error = null;
                return true;
            }
            catch (IOException ex) when (IsSharingViolation(ex) && attempt < SharingRetryDelaysMs.Length)
            {
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(SharingRetryDelaysMs[attempt]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    // ERROR_SHARING_VIOLATION (32) and ERROR_LOCK_VIOLATION (33): the low 16 bits of the HRESULT are the Win32 code.
    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    /// <summary>
    /// A file missing from the snapshot may only be deleted if the snapshot could have contained it.
    /// Files over <see cref="MaxFileSizeBytes"/> are never snapshotted, so their absence says nothing
    /// about whether they existed at checkpoint time (think databases, media, data dumps); deleting
    /// them would be unrecoverable. Shared by restore and preview so the list the user confirms
    /// matches what actually happens.
    /// </summary>
    private bool IsDeletionCandidate(string absPath)
    {
        try
        {
            return new FileInfo(absPath).Length <= MaxFileSizeBytes;
        }
        catch (Exception ex)
        {
            // Can't size it, so can't prove it was snapshot-eligible: leave it alone.
            _logger.LogDebug("[Checkpoint] Not deleting {Path}: {Msg}", absPath, ex.Message);
            return false;
        }
    }

    private void DeleteCheckpointsCore(IReadOnlyCollection<string> checkpointIds)
    {
        if (_shadowRepoPath is null || checkpointIds.Count == 0) return;
        if (!Repository.IsValid(_shadowRepoPath)) return;

        using var repo = new Repository(_shadowRepoPath);

        foreach (var id in checkpointIds)
        {
            var name = RefPrefix + id;
            if (repo.Refs[name] is null) continue;

            repo.Refs.Remove(name);
            _logger.LogDebug("[Checkpoint] Deleted {Ref}", name);
        }
    }

    private IReadOnlyDictionary<string, string> CloneCheckpointsCore(IReadOnlyCollection<string> checkpointIds)
    {
        var copies = new Dictionary<string, string>();
        if (_shadowRepoPath is null || checkpointIds.Count == 0 || !Repository.IsValid(_shadowRepoPath)) return copies;

        using var repo = new Repository(_shadowRepoPath);

        foreach (var id in checkpointIds.Distinct())
        {
            if (repo.Refs[RefPrefix + id] is not { } reference) continue;

            // The latest checkpoint is the last ref by name, and a name starts with its time ("yyyyMMddTHHmmssZ"):
            // the copy keeps that prefix, so it sorts beside its original instead of becoming the newest.
            var copyId = $"{id.Substring(0, Math.Min(id.Length, 16))}-{Guid.NewGuid():N}"[..32];
            repo.Refs.Add(RefPrefix + copyId, new ObjectId(reference.TargetIdentifier));
            copies[id] = copyId;
        }

        _logger.LogInformation("[Checkpoint] Copied {Count} of {Requested} checkpoint(s)", copies.Count, checkpointIds.Count);
        return copies;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns true if the on-disk file at <paramref name="absPath"/> has identical
    /// byte content to <paramref name="blob"/>. Caller must have already confirmed
    /// that the file exists and its length equals <see cref="Blob.Size"/>.
    /// </summary>
    private static bool FileMatchesBlob(string absPath, Blob blob)
    {
        const int BufferSize = 81920;
        try
        {
            using var blobStream = blob.GetContentStream();
            using var fileStream = new FileStream(absPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
            var blobBuf = new byte[BufferSize];
            var fileBuf = new byte[BufferSize];
            int blobRead;
            while ((blobRead = blobStream.Read(blobBuf, 0, BufferSize)) > 0)
            {
                var fileRead = fileStream.Read(fileBuf, 0, blobRead);
                if (fileRead != blobRead) return false;
                if (!blobBuf.AsSpan(0, blobRead).SequenceEqual(fileBuf.AsSpan(0, blobRead)))
                    return false;
            }
            return true;
        }
        catch
        {
            // Any I/O error - fall through to overwrite
            return false;
        }
    }

    private void EnsureShadowRepo()
    {
        if (_shadowRepoPath is null) return;
        if (!Repository.IsValid(_shadowRepoPath))
        {
            Directory.CreateDirectory(_shadowRepoPath);
            Repository.Init(_shadowRepoPath, isBare: true);
            _logger.LogInformation("[Checkpoint] Initialised shadow repo at {Path}", _shadowRepoPath);
        }
    }

    private static Commit? GetLatestCheckpointCommit(Repository repo)
    {
        return repo.Refs
            .Where(r => r.CanonicalName.StartsWith(RefPrefix, StringComparison.Ordinal))
            .OrderByDescending(r => r.CanonicalName, StringComparer.Ordinal)
            .Select(r => repo.Lookup<Commit>(r.TargetIdentifier))
            .FirstOrDefault(c => c is not null);
    }

    private static IEnumerable<string> EnumerateWorkspaceFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { continue; }

            foreach (var f in files)
            {
                var ext = Path.GetExtension(f);
                if (ExcludedExtensions.Contains(ext)) continue;
                yield return f;
            }

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { continue; }

            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (!ExcludedFolders.Contains(name))
                    stack.Push(sub);
            }
        }
    }

    /// <summary>
    /// Recursively enumerates all blob (file) entries in a git <see cref="Tree"/>,
    /// yielding (gitRelativePath, blob) pairs for every file at any depth.
    /// <para>
    /// <see cref="Tree.GetEnumerator"/> only yields direct children; subdirectory entries
    /// have <see cref="TreeEntryTargetType.Tree"/> and must be descended into explicitly.
    /// </para>
    /// </summary>
    private static IEnumerable<(string GitPath, Blob Blob)> WalkTreeBlobs(Tree tree, string prefix = "")
    {
        foreach (var entry in tree)
        {
            var path = string.IsNullOrEmpty(prefix) ? entry.Name : prefix + "/" + entry.Name;
            if (entry.TargetType == TreeEntryTargetType.Blob)
                yield return (path, (Blob)entry.Target);
            else if (entry.TargetType == TreeEntryTargetType.Tree)
                foreach (var child in WalkTreeBlobs((Tree)entry.Target, path))
                    yield return child;
        }
    }

    /// <summary>Converts an absolute path to a forward-slash path relative to the workspace root.</summary>
    private static string ToGitPath(string absPath, string workspaceRoot)
    {
        var rel = absPath.Substring(workspaceRoot.Length).TrimStart('\\', '/');
        return rel.Replace('\\', '/');
    }

    /// <summary>Converts a git-relative forward-slash path back to an absolute path.</summary>
    private static string ToAbsPath(string gitPath, string workspaceRoot)
        => Path.Combine(workspaceRoot, gitPath.Replace('/', Path.DirectorySeparatorChar));

}
