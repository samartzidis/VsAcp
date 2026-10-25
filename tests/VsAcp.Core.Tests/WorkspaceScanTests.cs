using System.Collections.Concurrent;
using VsAcp.Core.Abstractions;
using VsAcp.Core.Tests.Support;

namespace VsAcp.Core.Tests;

/// <summary>
/// File changes an agent makes behind the client's back (a shell command) are found by comparing the
/// workspace with the turn's checkpoint, with the real checkpoint service and a real agent process.
/// </summary>
public class WorkspaceScanTests
{
    [Fact]
    public async Task A_turn_that_created_edited_and_removed_files_on_disk_reports_each_when_it_ends()
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            turns = new[]
            {
                new
                {
                    steps = new object[]
                    {
                        new { disk = new { path = "made.txt", content = "from a shell" } },
                        new { disk = new { path = "keep.txt", content = "changed" } },
                        new { deleteDisk = "gone.txt" },
                        new { say = "done" }
                    }
                }
            }
        });
        var diffs = new ConcurrentBag<DiffRequest>();
        h.Diffs.DiffCompleted += diffs.Add;
        h.WriteFile("keep.txt", "original");
        h.WriteFile("gone.txt", "precious");
        h.WriteFile("untouched.txt", "same");

        await h.SendAsync("shell stuff");
        await h.WaitUntilAsync(() => diffs.Count >= 3, "three reported changes");
        await Task.Delay(300); // nothing more should arrive

        Assert.Equal(3, diffs.Count);

        var made = Assert.Single(diffs, d => d.FilePath == h.PathOf("made.txt"));
        Assert.Equal(DiffKind.Added, made.Kind);
        Assert.Equal("from a shell", made.ProposedContent);

        var kept = Assert.Single(diffs, d => d.FilePath == h.PathOf("keep.txt"));
        Assert.Equal(DiffKind.Modified, kept.Kind);
        Assert.Equal("original", kept.OriginalContent);
        Assert.Equal("changed", kept.ProposedContent);

        var gone = Assert.Single(diffs, d => d.FilePath == h.PathOf("gone.txt"));
        Assert.Equal(DiffKind.Deleted, gone.Kind);
        Assert.Equal("precious", gone.OriginalContent);

        Assert.All(diffs, d => Assert.Equal(DiffSource.Scan, d.Source));
    }

    [Fact]
    public async Task Files_the_agent_says_its_shell_command_changed_are_listed_as_the_agents()
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            turns = new[]
            {
                new
                {
                    steps = new object[]
                    {
                        new { update = new { sessionUpdate = "tool_call", toolCallId = "sh", title = "sed -i ...", kind = "execute", status = "pending" } },
                        new { disk = new { path = "README.md", content = "GetHi" } },
                        new { disk = new { path = "build.log", content = "built" } },
                        // claude-agent-acp's own report of what the command changed (not part of ACP)
                        new { update = new
                        {
                            sessionUpdate = "tool_call_update",
                            toolCallId = "sh",
                            _meta = new { claudeCode = new { toolResponse = new { bashEditDiff = new { changedFiles = new[] { "{cwd}\\README.md" } } } } }
                        } },
                        new { update = new { sessionUpdate = "tool_call_update", toolCallId = "sh", status = "completed" } },
                        new { say = "renamed" }
                    }
                }
            }
        });
        var diffs = new ConcurrentBag<DiffRequest>();
        h.Diffs.DiffCompleted += diffs.Add;
        h.WriteFile("README.md", "GetHello");
        h.WriteFile("build.log", "old");

        await h.SendAsync("rename it");
        await h.WaitUntilAsync(() => diffs.Count >= 2, "two reported changes");
        await Task.Delay(300); // nothing more should arrive

        var readme = Assert.Single(diffs, d => d.FilePath == h.PathOf("README.md"));
        Assert.Equal(DiffSource.Agent, readme.Source);
        Assert.True(readme.FoundByScan);

        var log = Assert.Single(diffs, d => d.FilePath == h.PathOf("build.log"));
        Assert.Equal(DiffSource.Scan, log.Source);
        Assert.True(log.FoundByScan);
    }

    [Fact]
    public async Task A_new_files_diff_content_arriving_before_the_write_lands_is_still_attributed_to_the_agent()
    {
        // claude-agent-acp's Write tool reports an Edit-kind tool call whose tool_call_update carries the
        // diff content before the file actually exists on disk (mid permission prompt); it never resends
        // the diff once the write lands, so the only thing that later finds the file is the end-of-turn
        // workspace scan. That scan must still credit the agent, not list it under "Other changes".
        using var h = await ChatHarness.CreateAsync(new
        {
            turns = new[]
            {
                new
                {
                    steps = new object[]
                    {
                        new { update = new { sessionUpdate = "tool_call", toolCallId = "w", title = "Preparing file...", kind = "edit", status = "pending" } },
                        new { update = new
                        {
                            sessionUpdate = "tool_call_update",
                            toolCallId = "w",
                            content = new object[] { new { type = "diff", path = "{cwd}\\new.txt", newText = "hi" } }
                        } },
                        // Give the client time to process the diff content (and so try, and fail, to read
                        // the not-yet-written file) before the write actually lands.
                        new { delayMs = 200 },
                        new { disk = new { path = "new.txt", content = "hi" } },
                        new { update = new { sessionUpdate = "tool_call_update", toolCallId = "w", status = "completed" } },
                        new { say = "done" }
                    }
                }
            }
        });
        var diffs = new ConcurrentBag<DiffRequest>();
        h.Diffs.DiffCompleted += diffs.Add;

        await h.SendAsync("make a file");
        await h.WaitUntilAsync(() => diffs.Count >= 1, "the created file reported");
        await Task.Delay(300); // nothing more should arrive

        var created = Assert.Single(diffs, d => d.FilePath == h.PathOf("new.txt"));
        Assert.Equal(DiffKind.Added, created.Kind);
        Assert.Equal("hi", created.ProposedContent);
        Assert.Equal(DiffSource.Agent, created.Source);
    }

    [Fact]
    public async Task A_turn_that_changed_nothing_reports_nothing()
    {
        using var h = await ChatHarness.CreateAsync();
        var diffs = new ConcurrentBag<DiffRequest>();
        h.Diffs.DiffCompleted += diffs.Add;
        h.WriteFile("a.txt", "same");

        await h.SendAsync("hello");
        await Task.Delay(500);

        Assert.Empty(diffs);
    }
}
