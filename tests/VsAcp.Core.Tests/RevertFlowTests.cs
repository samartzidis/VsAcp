using VsAcp.Core.Abstractions;
using VsAcp.Core.Services;
using VsAcp.Core.Tests.Support;

namespace VsAcp.Core.Tests;

/// <summary>Checkpoint + "Revert to here" end to end: agent edits files, user reverts, conversation is rebuilt.</summary>
public class RevertFlowTests
{
    private static object Scenario() => new
    {
        turns = new object[]
        {
            new
            {
                when = "one",
                steps = new object[]
                {
                    new { write = new { path = "a.txt", content = "one" } },
                    new { say = "wrote one" }
                }
            },
            new
            {
                when = "two",
                steps = new object[]
                {
                    new { write = new { path = "a.txt", content = "two" } },
                    new { write = new { path = "new.txt", content = "created by two" } },
                    new { say = "wrote two" }
                }
            },
            new { steps = new object[] { new { say = "ok" } } }
        }
    };

    [Fact]
    public async Task Files_written_by_the_agent_through_the_client_land_in_the_workspace()
    {
        using var h = await ChatHarness.CreateAsync(Scenario());

        await h.SendAsync("one");

        Assert.Equal("one", h.ReadFile("a.txt"));
    }

    [Fact]
    public async Task Revert_restores_files_and_truncates_the_conversation()
    {
        using var h = await ChatHarness.CreateAsync(Scenario());
        h.WriteFile("a.txt", "orig");

        await h.SendAsync("one");
        await h.SendAsync("two");
        Assert.Equal("two", h.ReadFile("a.txt"));
        Assert.True(h.FileExists("new.txt"));

        var reverted = await h.RevertToUserMessageAsync(1);

        Assert.Equal("two", reverted!.Content);
        Assert.Equal("one", h.ReadFile("a.txt"));
        Assert.False(h.FileExists("new.txt"));
        Assert.Equal(new[] { "one", "wrote one" }, h.Messages.Select(m => m.Content).ToArray());

        var persisted = await h.PersistedAsync();
        Assert.Equal(new[] { "one", "wrote one" }, persisted.Select(p => p.Content).ToArray());

        await h.RevertToUserMessageAsync(0);

        Assert.Equal("orig", h.ReadFile("a.txt"));
        Assert.Empty(h.Messages);
    }

    [Fact]
    public async Task Side_effects_made_directly_on_disk_are_reverted_too()
    {
        // Terminal-style changes: the agent edits and deletes files itself, bypassing fs/write_text_file.
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
        h.WriteFile("keep.txt", "original");
        h.WriteFile("gone.txt", "precious");

        await h.SendAsync("shell stuff");
        Assert.True(h.FileExists("made.txt"));
        Assert.False(h.FileExists("gone.txt"));

        await h.RevertToUserMessageAsync(0);

        Assert.False(h.FileExists("made.txt"));
        Assert.Equal("original", h.ReadFile("keep.txt"));
        Assert.Equal("precious", h.ReadFile("gone.txt"));
    }

    // The agent edits a.txt with a tool call that reports the diff, and its command also leaves side.txt behind
    // without saying so.
    private static object ReportedEditScenario() => new
    {
        turns = new[]
        {
            new
            {
                steps = new object[]
                {
                    new { update = new { sessionUpdate = "tool_call", toolCallId = "e", title = "Edit a.txt", kind = "edit", status = "pending" } },
                    new { disk = new { path = "a.txt", content = "agent" } },
                    new { disk = new { path = "side.txt", content = "side effect" } },
                    new { update = new
                    {
                        sessionUpdate = "tool_call_update",
                        toolCallId = "e",
                        status = "completed",
                        content = new[] { new { type = "diff", path = "{cwd}\\a.txt", newText = "agent" } }
                    } },
                    new { say = "done" }
                }
            }
        }
    };

    [Fact]
    public async Task By_default_a_revert_restores_only_the_files_the_agent_reported()
    {
        using var h = await ChatHarness.CreateAsync(ReportedEditScenario());
        h.WriteFile("a.txt", "orig");
        h.WriteFile("mine.txt", "orig");

        await h.SendAsync("edit it");
        h.WriteFile("mine.txt", "my own edit");

        var preview = await h.PreviewRevertAsync(0);
        Assert.True(preview.AgentFilesKnown);
        Assert.Equal(new[] { "a.txt" }, preview.Agent.Modified.ToArray());
        Assert.Equal(1, preview.Agent.TotalCount);
        Assert.Equal(new[] { "mine.txt" }, preview.Other.Modified.ToArray());
        Assert.Equal(new[] { "side.txt" }, preview.Other.Deleted.ToArray());

        await h.RevertToUserMessageAsync(0, includeOther: false);

        Assert.Equal("orig", h.ReadFile("a.txt"));
        Assert.Equal("my own edit", h.ReadFile("mine.txt"));
        Assert.Equal("side effect", h.ReadFile("side.txt"));
        Assert.Empty(h.Messages);
    }

    [Fact]
    public async Task Also_reverting_other_changes_restores_every_file()
    {
        using var h = await ChatHarness.CreateAsync(ReportedEditScenario());
        h.WriteFile("a.txt", "orig");
        h.WriteFile("mine.txt", "orig");

        await h.SendAsync("edit it");
        h.WriteFile("mine.txt", "my own edit");

        await h.RevertToUserMessageAsync(0, includeOther: true);

        Assert.Equal("orig", h.ReadFile("a.txt"));
        Assert.Equal("orig", h.ReadFile("mine.txt"));
        Assert.False(h.FileExists("side.txt"));
    }

    [Fact]
    public async Task The_files_a_step_changed_are_saved_with_it()
    {
        using var h = await ChatHarness.CreateAsync(ReportedEditScenario());
        h.WriteFile("a.txt", "orig");

        await h.SendAsync("edit it");

        var persisted = await h.PersistedAsync();
        Assert.True(Assert.Single(persisted, p => p.ItemType == "User").TracksChangedFiles);
        Assert.Equal(new[] { h.PathOf("a.txt") }, Assert.Single(persisted, p => p.ItemType == "ToolStep").ChangedFiles);
    }

    [Fact]
    public async Task After_a_revert_the_first_send_carries_the_prior_conversation_once()
    {
        using var h = await ChatHarness.CreateAsync(Scenario());

        await h.SendAsync("one");
        await h.SendAsync("two");
        await h.RevertToUserMessageAsync(1);

        await h.SendAsync("three");
        await h.SendAsync("four");

        var prompts = h.PromptTexts();
        Assert.Equal(4, prompts.Count);
        Assert.DoesNotContain("<prior_conversation_context>", prompts[0]);
        Assert.DoesNotContain("<prior_conversation_context>", prompts[1]);
        Assert.Contains("<prior_conversation_context>", prompts[2]);
        Assert.Contains("wrote one", prompts[2]);
        Assert.DoesNotContain("wrote two", prompts[2]);
        Assert.EndsWith("three", prompts[2]);
        Assert.DoesNotContain("<prior_conversation_context>", prompts[3]);

        // The revert drops the agent's session; the next send starts a fresh one.
        Assert.Equal(2, h.Received("session/new").Count);
    }

    [Fact]
    public async Task Notices_and_compactions_are_left_out_of_the_prior_conversation_context()
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            turns = new object[]
            {
                new
                {
                    when = "first",
                    steps = new object[]
                    {
                        new { update = new { sessionUpdate = "notice", severity = "info", title = "NOTICE-MARKER" } },
                        new { update = new { sessionUpdate = "compaction_update", compactionId = "c1", status = "in_progress" } },
                        new { update = new { sessionUpdate = "compaction_summary_chunk", compactionId = "c1", content = new { type = "text", text = "SUMMARY-MARKER" } } },
                        new { update = new { sessionUpdate = "compaction_update", compactionId = "c1", status = "completed" } },
                        new { say = "first answer" }
                    }
                },
                new { steps = new object[] { new { say = "ok" } } }
            }
        });

        await h.SendAsync("first");
        await h.SendAsync("second");
        await h.RevertToUserMessageAsync(1);
        await h.SendAsync("third");

        var prefixed = h.PromptTexts().Last();
        Assert.Contains("<prior_conversation_context>", prefixed);
        Assert.Contains("first answer", prefixed);
        Assert.DoesNotContain("NOTICE-MARKER", prefixed);
        Assert.DoesNotContain("SUMMARY-MARKER", prefixed);

        // ...but the compaction itself is still part of the persisted transcript.
        Assert.Contains(await h.PersistedAsync(), p => p.ItemType == "Compaction");
    }

    [Fact]
    public async Task Reverting_to_the_first_message_sends_no_prefix()
    {
        using var h = await ChatHarness.CreateAsync(Scenario());

        await h.SendAsync("one");
        await h.RevertToUserMessageAsync(0);
        await h.SendAsync("again");

        Assert.DoesNotContain("<prior_conversation_context>", h.PromptTexts().Last());
    }
}
