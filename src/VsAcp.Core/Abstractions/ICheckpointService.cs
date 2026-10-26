namespace VsAcp.Core.Abstractions;

/// <summary>
/// What <see cref="ICheckpointService.RestoreAsync"/> would do to the workspace, without doing it.
/// Paths are relative to the workspace root, using backslashes, sorted.
/// </summary>
public sealed class RestorePreview
{
    /// <summary>Files that exist now but differ from the snapshot; their content will be overwritten.</summary>
    public required IReadOnlyList<string> Modified { get; init; }

    /// <summary>Files in the snapshot that are missing from disk now; they will be recreated.</summary>
    public required IReadOnlyList<string> Recreated { get; init; }

    /// <summary>Files on disk that were not in the snapshot (created since); they will be deleted.</summary>
    public required IReadOnlyList<string> Deleted { get; init; }

    public int TotalCount => Modified.Count + Recreated.Count + Deleted.Count;
    public bool HasChanges => TotalCount > 0;

    /// <summary>
    /// With a path filter: what a full restore would also change but this one leaves alone (same walk, same
    /// comparisons). Null without a filter.
    /// </summary>
    public RestorePreview? Excluded { get; init; }
}

public interface ICheckpointService
{
    /// <summary>
    /// Snapshots the workspace into the shadow git repository.
    /// Returns a checkpointId to store on the user's <see cref="Models.PersistedMessage"/>.
    /// Returns <see langword="null"/> on failure (best-effort - caller should tolerate null).
    /// </summary>
    Task<string?> CreateCheckpointAsync(CancellationToken ct = default);

    /// <summary>
    /// Restores the workspace to the state captured by the given checkpoint.
    /// Writes all tracked files back to disk and deletes files not present in the snapshot.
    /// With <paramref name="onlyPaths"/> (absolute paths), only those files are written, recreated or deleted;
    /// every other file is left as it is.
    /// </summary>
    Task RestoreAsync(string checkpointId, IReadOnlyCollection<string>? onlyPaths = null, CancellationToken ct = default);

    /// <summary>
    /// Computes which files <see cref="RestoreAsync"/> would overwrite, recreate or delete, without
    /// touching the workspace. Uses exactly the same comparison (and the same <paramref name="onlyPaths"/>
    /// filter) as the restore, so the preview and the real restore agree. With a filter,
    /// <see cref="RestorePreview.Excluded"/> lists what the filter leaves alone.
    /// </summary>
    Task<RestorePreview> PreviewRestoreAsync(string checkpointId, IReadOnlyCollection<string>? onlyPaths = null, CancellationToken ct = default);

    /// <summary>
    /// Compares the workspace with the latest checkpoint (the state when the current turn started), the
    /// way <see cref="PreviewRestoreAsync"/> does: <c>Modified</c> = changed on disk, <c>Recreated</c> =
    /// removed since, <c>Deleted</c> = created since. Unlike <see cref="PreviewRestoreAsync"/> the paths are
    /// <b>absolute</b>. Null when there is no checkpoint (or the service is disabled for this workspace).
    /// </summary>
    Task<RestorePreview?> PreviewChangesSinceLatestCheckpointAsync(CancellationToken ct = default);

    /// <summary>
    /// Deletes exactly the given checkpoints (unknown ids are ignored). Call after a successful
    /// restore with the ids owned by the history that was just discarded. Deliberately not
    /// "everything after X": the shadow repo is shared by every chat tab of the workspace, so
    /// a name-ordered prune would also delete other tabs' checkpoints.
    /// </summary>
    Task DeleteCheckpointsAsync(IReadOnlyCollection<string> checkpointIds, CancellationToken ct = default);

    /// <summary>
    /// Gives each of the given checkpoints a second id naming the same snapshot, for a chat forked from another one:
    /// the shadow repo is shared, so the forked chat's messages need checkpoints the other chat's reverts do not
    /// delete. A copy keeps its original's time, so it never becomes the latest checkpoint. Returns original id to
    /// copy id; unknown ids are left out.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> CloneCheckpointsAsync(IReadOnlyCollection<string> checkpointIds, CancellationToken ct = default);

    /// <summary>
    /// Returns the text content of <paramref name="absolutePath"/> as stored in the latest
    /// checkpoint, or <see langword="null"/> if the file was not tracked at checkpoint time
    /// (new file) or no checkpoint exists yet.
    /// This is a fast synchronous read - LibGit2Sharp blob reads do not require I/O scheduling.
    /// </summary>
    string? GetFileAtLatestCheckpoint(string absolutePath);

    /// <summary>
    /// True only when the latest checkpoint <em>proves</em> the file did not exist yet: a checkpoint
    /// exists, the path is inside the workspace, it is of a kind the snapshot captures (not in an
    /// excluded folder, not an excluded extension), it is not recorded as present-but-uncaptured
    /// (oversize or locked), and it is not in the snapshot. False in every doubtful case (no
    /// checkpoint, excluded path, ...), so callers can treat a "true" as safe grounds to delete the file.
    /// </summary>
    bool WasAbsentAtLatestCheckpoint(string absolutePath);
}
