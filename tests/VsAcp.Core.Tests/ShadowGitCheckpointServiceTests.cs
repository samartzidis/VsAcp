using Microsoft.Extensions.Options;
using VsAcp.Core.Abstractions;
using VsAcp.Core.Configuration;
using VsAcp.Core.Services;
using VsAcp.Core.Tests.Support;

namespace VsAcp.Core.Tests;

public sealed class ShadowGitCheckpointServiceTests : IDisposable
{
    // Checkpoint ids start with a one-second timestamp and the newest one wins by name, so spacing the
    // snapshots keeps "previous checkpoint" deterministic.
    private const int SpacingMs = 1100;

    private readonly TempDirectory _dir = new("ws");
    private readonly TestLog _log = new();

    public void Dispose() => _dir.Dispose();

    private ShadowGitCheckpointService NewService() =>
        new(Options.Create(new VsAcpOptions { WorkingDirectory = _dir.Path, HasProjectDirectory = true }),
            new TestLogger<ShadowGitCheckpointService>(_log));

    private static async Task<string> SnapshotAsync(ShadowGitCheckpointService service)
    {
        await Task.Delay(SpacingMs);
        return (await service.CreateCheckpointAsync())!;
    }

    [Fact]
    public async Task Restore_undoes_edits_creations_and_deletions()
    {
        var service = NewService();
        _dir.Write("edit.txt", "before");
        _dir.Write("delete.txt", "keep me");
        _dir.Write("sub/nested.txt", "nested");
        var checkpoint = await SnapshotAsync(service);

        _dir.Write("edit.txt", "after");
        File.Delete(_dir.Combine("delete.txt"));
        _dir.Write("created.txt", "new");
        _dir.Write("sub/created-too.txt", "new");

        await service.RestoreAsync(checkpoint);

        Assert.Equal("before", _dir.Read("edit.txt"));
        Assert.Equal("keep me", _dir.Read("delete.txt"));
        Assert.Equal("nested", _dir.Read("sub/nested.txt"));
        Assert.False(_dir.Exists("created.txt"));
        Assert.False(_dir.Exists("sub/created-too.txt"), _log.ToString());
    }

    [Fact]
    public async Task Preview_lists_exactly_what_restore_will_do()
    {
        var service = NewService();
        _dir.Write("edit.txt", "before");
        _dir.Write("delete.txt", "keep me");
        _dir.Write("same.txt", "unchanged");
        var checkpoint = await SnapshotAsync(service);

        _dir.Write("edit.txt", "after");
        File.Delete(_dir.Combine("delete.txt"));
        _dir.Write("created.txt", "new");

        var preview = await service.PreviewRestoreAsync(checkpoint);

        Assert.Equal(new[] { "edit.txt" }, preview.Modified.ToArray());
        Assert.Equal(new[] { "delete.txt" }, preview.Recreated.ToArray());
        Assert.Equal(new[] { "created.txt" }, preview.Deleted.ToArray());

        // Preview changes nothing.
        Assert.Equal("after", _dir.Read("edit.txt"));
        Assert.True(_dir.Exists("created.txt"));

        await service.RestoreAsync(checkpoint);
        Assert.Empty((await service.PreviewRestoreAsync(checkpoint)).Modified);
    }

    [Fact]
    public async Task A_filtered_restore_touches_only_the_listed_files()
    {
        var service = NewService();
        _dir.Write("agent-edit.txt", "before");
        _dir.Write("agent-delete.txt", "agent removed me");
        _dir.Write("my-edit.txt", "before");
        _dir.Write("my-delete.txt", "I removed me");
        var checkpoint = await SnapshotAsync(service);

        _dir.Write("agent-edit.txt", "agent");
        File.Delete(_dir.Combine("agent-delete.txt"));
        _dir.Write("agent-new.txt", "agent made me");
        _dir.Write("my-edit.txt", "mine");
        File.Delete(_dir.Combine("my-delete.txt"));
        _dir.Write("my-new.txt", "I made me");

        await service.RestoreAsync(checkpoint, new[]
        {
            _dir.Combine("agent-edit.txt"), _dir.Combine("agent-delete.txt"), _dir.Combine("agent-new.txt")
        });

        Assert.Equal("before", _dir.Read("agent-edit.txt"));
        Assert.Equal("agent removed me", _dir.Read("agent-delete.txt"));
        Assert.False(_dir.Exists("agent-new.txt"));
        Assert.Equal("mine", _dir.Read("my-edit.txt"));
        Assert.False(_dir.Exists("my-delete.txt"));
        Assert.True(_dir.Exists("my-new.txt"));
    }

    [Fact]
    public async Task A_filtered_preview_splits_what_the_filter_restores_from_what_it_leaves_alone()
    {
        var service = NewService();
        _dir.Write("agent-edit.txt", "before");
        _dir.Write("my-edit.txt", "before");
        _dir.Write("my-delete.txt", "I removed me");
        var checkpoint = await SnapshotAsync(service);

        _dir.Write("agent-edit.txt", "agent");
        _dir.Write("agent-new.txt", "agent made me");
        _dir.Write("my-edit.txt", "mine");
        File.Delete(_dir.Combine("my-delete.txt"));
        _dir.Write("my-new.txt", "I made me");
        // Paths are matched ignoring case and slash style.
        var agentFiles = new[] { _dir.Combine("AGENT-EDIT.txt"), _dir.Path + "/agent-new.txt" };

        var preview = await service.PreviewRestoreAsync(checkpoint, agentFiles);

        Assert.Equal(new[] { "agent-edit.txt" }, preview.Modified.ToArray());
        Assert.Equal(new[] { "agent-new.txt" }, preview.Deleted.ToArray());
        Assert.Empty(preview.Recreated);
        Assert.Equal(new[] { "my-edit.txt" }, preview.Excluded!.Modified.ToArray());
        Assert.Equal(new[] { "my-delete.txt" }, preview.Excluded.Recreated.ToArray());
        Assert.Equal(new[] { "my-new.txt" }, preview.Excluded.Deleted.ToArray());

        // ...and the filtered restore does exactly the first half.
        await service.RestoreAsync(checkpoint, agentFiles);
        var after = await service.PreviewRestoreAsync(checkpoint, agentFiles);
        Assert.False(after.HasChanges);
        Assert.Equal(preview.Excluded.TotalCount, after.Excluded!.TotalCount);
        Assert.Null((await service.PreviewRestoreAsync(checkpoint)).Excluded);
    }

    [Fact]
    public async Task A_filtered_restore_still_never_deletes_a_file_too_big_to_snapshot()
    {
        var service = NewService();
        var checkpoint = await SnapshotAsync(service);
        _dir.Write("huge.bin", new string('x', 10 * 1024 * 1024 + 1));

        var preview = await service.PreviewRestoreAsync(checkpoint, new[] { _dir.Combine("huge.bin") });
        await service.RestoreAsync(checkpoint, new[] { _dir.Combine("huge.bin") });

        Assert.Empty(preview.Deleted);
        Assert.True(_dir.Exists("huge.bin"));
    }

    [Fact]
    public async Task A_file_copied_in_with_an_old_timestamp_is_still_captured()
    {
        var service = NewService();
        _dir.Write("seed.txt", "seed");
        var first = await SnapshotAsync(service);

        // Copying or unzipping keeps the original modified date, which predates the previous checkpoint.
        _dir.Write("copied.txt", "from elsewhere");
        File.SetLastWriteTimeUtc(_dir.Combine("copied.txt"), new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var second = await SnapshotAsync(service);

        await service.RestoreAsync(first);
        Assert.False(_dir.Exists("copied.txt"));

        await service.RestoreAsync(second);
        Assert.Equal("from elsewhere", _dir.Read("copied.txt"));
    }

    [Fact]
    public async Task A_file_edited_right_after_a_checkpoint_is_recaptured_by_the_next_one()
    {
        var service = NewService();
        _dir.Write("a.txt", "v1");
        await SnapshotAsync(service);

        _dir.Write("a.txt", "v2");
        var second = await SnapshotAsync(service);

        _dir.Write("a.txt", "v3");
        await service.RestoreAsync(second);

        Assert.Equal("v2", _dir.Read("a.txt"));
    }

    [Fact]
    public async Task Deleting_checkpoints_only_removes_the_ones_named()
    {
        var service = NewService();
        _dir.Write("a.txt", "one");
        var first = await SnapshotAsync(service);
        _dir.Write("a.txt", "two");
        var second = await SnapshotAsync(service);
        _dir.Write("a.txt", "three");
        var third = await SnapshotAsync(service);

        await service.DeleteCheckpointsAsync(new[] { second });

        await service.RestoreAsync(third);
        Assert.Equal("three", _dir.Read("a.txt"));
        await service.RestoreAsync(first);
        Assert.Equal("one", _dir.Read("a.txt"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(second));
    }

    [Fact]
    public async Task A_copied_checkpoint_outlives_its_original_and_never_becomes_the_latest()
    {
        var service = NewService();
        _dir.Write("a.txt", "one");
        var first = await SnapshotAsync(service);
        _dir.Write("a.txt", "two");
        await SnapshotAsync(service);

        var copies = await service.CloneCheckpointsAsync(new[] { first, "20200101T000000Z-unknown" });

        var copy = Assert.Single(copies).Value;
        Assert.Equal(first, Assert.Single(copies).Key);
        Assert.NotEqual(first, copy);
        Assert.Equal(first.Substring(0, 16), copy.Substring(0, 16));
        // Still the second snapshot: the inline diff and the next incremental snapshot build on the latest one.
        Assert.Equal("two", service.GetFileAtLatestCheckpoint(_dir.Combine("a.txt")));

        await service.DeleteCheckpointsAsync(new[] { first });
        _dir.Write("a.txt", "three");
        await service.RestoreAsync(copy);

        Assert.Equal("one", _dir.Read("a.txt"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(first));
    }

    [Fact]
    public async Task Two_tabs_on_one_workspace_keep_each_others_restore_points()
    {
        // Chat tabs of a workspace share one shadow repo; a revert in one must not touch the other's checkpoints.
        var tabA = NewService();
        var tabB = NewService();
        _dir.Write("a.txt", "start");
        var a1 = await SnapshotAsync(tabA);
        var b1 = await SnapshotAsync(tabB);
        _dir.Write("a.txt", "later");
        var a2 = await SnapshotAsync(tabA);

        await tabA.DeleteCheckpointsAsync(new[] { a2 });

        _dir.Write("a.txt", "changed");
        await tabB.RestoreAsync(b1);
        Assert.Equal("start", _dir.Read("a.txt"));
        await tabA.RestoreAsync(a1);
        Assert.Equal("start", _dir.Read("a.txt"));
    }

    [Fact]
    public async Task Files_over_ten_megabytes_are_never_deleted()
    {
        var service = NewService();
        _dir.Write("small.txt", "x");
        var checkpoint = await SnapshotAsync(service);

        // Absent from the snapshot only because it is too big to capture, so it may have predated it.
        using (var stream = File.Create(_dir.Combine("big.bin")))
            stream.SetLength(11 * 1024 * 1024);

        var preview = await service.PreviewRestoreAsync(checkpoint);
        Assert.DoesNotContain("big.bin", preview.Deleted);

        await service.RestoreAsync(checkpoint);
        Assert.True(_dir.Exists("big.bin"));
    }

    [Fact]
    public async Task A_big_file_present_at_snapshot_time_survives_a_restore()
    {
        var service = NewService();
        using (var stream = File.Create(_dir.Combine("data.db")))
            stream.SetLength(11 * 1024 * 1024);
        _dir.Write("a.txt", "one");
        var checkpoint = await SnapshotAsync(service);

        _dir.Write("a.txt", "two");
        await service.RestoreAsync(checkpoint);

        Assert.True(_dir.Exists("data.db"));
        Assert.Equal("one", _dir.Read("a.txt"));
    }

    [Theory]
    [InlineData("node_modules/pkg/index.js")]
    [InlineData("bin/Debug/app.txt")]
    [InlineData("obj/cache.txt")]
    [InlineData(".vs/state.txt")]
    [InlineData("dist/bundle.txt")]
    public async Task Excluded_folders_are_neither_snapshotted_nor_cleaned_up(string relativePath)
    {
        var service = NewService();
        _dir.Write("a.txt", "one");
        var checkpoint = await SnapshotAsync(service);

        _dir.Write(relativePath, "build output");
        var preview = await service.PreviewRestoreAsync(checkpoint);
        await service.RestoreAsync(checkpoint);

        Assert.Empty(preview.Deleted);
        Assert.True(_dir.Exists(relativePath));
    }

    [Fact]
    public async Task Excluded_extensions_are_left_alone()
    {
        var service = NewService();
        _dir.Write("a.txt", "one");
        var checkpoint = await SnapshotAsync(service);

        _dir.Write("tool.dll", "binary");
        await service.RestoreAsync(checkpoint);

        Assert.True(_dir.Exists("tool.dll"));
    }

    // A reader that allows only other readers, as the workspace scan's File.ReadAllText is: the restore's exclusive
    // write to the same file fails while it is open. (2026-10-28: RevertFlowTests failed on a.txt about 1 run in 15.)
    private FileStream HoldOpen(string relativePath) =>
        new(_dir.Combine(relativePath), FileMode.Open, FileAccess.Read, FileShare.Read);

    [Fact]
    public async Task Restore_retries_a_file_another_process_holds_for_a_moment()
    {
        var service = NewService();
        _dir.Write("a.txt", "before");
        var checkpoint = await SnapshotAsync(service);
        _dir.Write("a.txt", "after");

        var held = HoldOpen("a.txt");
        // A dedicated thread, not Task.Run/Task.Delay: on a starved pool the release would come after the retries gave up.
        new Thread(() => { Thread.Sleep(300); held.Dispose(); }) { IsBackground = true }.Start();
        var result = await service.RestoreAsync(checkpoint);

        Assert.Empty(result.Failed);
        Assert.Equal("before", _dir.Read("a.txt"));
    }

    [Fact]
    public async Task A_file_that_stays_locked_is_listed_and_the_rest_of_the_restore_is_done()
    {
        var service = NewService();
        _dir.Write("a.txt", "before");
        _dir.Write("b.txt", "before");
        _dir.Write("sub/c.txt", "before");
        var checkpoint = await SnapshotAsync(service);
        _dir.Write("a.txt", "after");
        _dir.Write("b.txt", "after");
        _dir.Write("sub/c.txt", "after");
        _dir.Write("created.txt", "new");

        RestoreResult result;
        using (HoldOpen("a.txt")) // a.txt comes first in the snapshot, so the others prove the restore went on
            result = await service.RestoreAsync(checkpoint);

        Assert.Equal(new[] { "a.txt" }, result.Failed);
        Assert.Equal("after", _dir.Read("a.txt")); // untouched
        Assert.Equal("before", _dir.Read("b.txt"));
        Assert.Equal("before", _dir.Read("sub/c.txt"));
        Assert.False(_dir.Exists("created.txt"), _log.ToString());
    }

    [Fact]
    public async Task Without_a_project_folder_checkpointing_is_disabled()
    {
        var service = new ShadowGitCheckpointService(
            Options.Create(new VsAcpOptions { WorkingDirectory = _dir.Path, HasProjectDirectory = false }),
            new TestLogger<ShadowGitCheckpointService>(_log));

        Assert.Null(await service.CreateCheckpointAsync());
    }
}
