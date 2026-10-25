using System.Text.Json.Nodes;
using AcpProtocol;
using VsAcp.Core.Abstractions;
using VsAcp.Core.Services;
using VsAcp.Core.Tests.Support;

namespace VsAcp.Core.Tests;

/// <summary>Session setup, modes, config options and prompt building, seen from the agent's side of the wire.</summary>
public class AcpSessionServiceTests
{
    private static object WithSessionCaps(object sessionCapabilities, bool loadSession = false, object? promptCapabilities = null) => new
    {
        agentCapabilities = new { loadSession, sessionCapabilities, promptCapabilities },
    };

    private static JsonObject Prompt(ChatHarness h) => h.Received("session/prompt").Last();

    private static JsonArray Blocks(JsonObject prompt) => (JsonArray)prompt["prompt"]!;

    // ---- additional directories (ADR-005) ----

    [Fact]
    public async Task Extra_project_folders_are_shared_when_the_agent_supports_them()
    {
        using var h = await ChatHarness.CreateAsync(
            WithSessionCaps(new { additionalDirectories = new { } }),
            o => o.AdditionalDirectories = new[] { @"C:\other\project" });

        await h.SendAsync("hi");

        var dirs = (JsonArray)h.Received("session/new").Single()["additionalDirectories"]!;
        Assert.Equal(new[] { @"C:\other\project" }, dirs.Select(d => (string?)d).ToArray());
    }

    [Fact]
    public async Task Extra_project_folders_are_left_out_when_the_agent_does_not_support_them()
    {
        using var h = await ChatHarness.CreateAsync(
            WithSessionCaps(new { }),
            o => o.AdditionalDirectories = new[] { @"C:\other\project" });

        await h.SendAsync("hi");

        Assert.False(h.Received("session/new").Single().ContainsKey("additionalDirectories"));
    }

    [Fact]
    public async Task No_extra_folders_means_the_field_is_omitted_even_when_supported()
    {
        using var h = await ChatHarness.CreateAsync(WithSessionCaps(new { additionalDirectories = new { } }));

        await h.SendAsync("hi");

        Assert.False(h.Received("session/new").Single().ContainsKey("additionalDirectories"));
    }

    [Fact]
    public async Task Extra_project_folders_are_also_sent_when_loading_a_saved_session()
    {
        using var h = await ChatHarness.CreateAsync(
            WithSessionCaps(new { additionalDirectories = new { } }, loadSession: true),
            o => o.AdditionalDirectories = new[] { @"C:\other\project" });
        h.Service.RestoreHistory("fake-session-1");

        await h.SendAsync("hi");

        var dirs = (JsonArray)h.Received("session/load").Single()["additionalDirectories"]!;
        Assert.Equal(@"C:\other\project", (string?)dirs.Single());
    }

    // ---- loading a saved session ----

    [Fact]
    public async Task A_saved_session_the_agent_no_longer_has_falls_back_to_a_new_one_and_says_so()
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            agentCapabilities = new { loadSession = true },
            failLoad = true,
            turns = new[] { new { steps = new object[] { new { say = "fresh start" } } } }
        });
        var restoreFailed = 0;
        h.Service.SessionRestoreFailed += () => restoreFailed++;
        h.Service.RestoreHistory("gone-session");

        var result = await h.SendAsync("hello");

        Assert.Equal(TurnResult.Completed, result);
        Assert.Equal(1, restoreFailed);
        Assert.Single(h.Received("session/load"));
        Assert.Single(h.Received("session/new"));
        Assert.Contains(h.Messages, m => m.Type == ChatItemType.Assistant && m.Content == "fresh start");
    }

    [Fact]
    public async Task A_saved_session_is_not_loaded_when_the_agent_cannot_load_sessions_and_the_user_is_told()
    {
        using var h = await ChatHarness.CreateAsync(new { agentCapabilities = new { loadSession = false } });
        var restoreFailed = 0;
        h.Service.SessionRestoreFailed += () => restoreFailed++;
        h.Service.RestoreHistory("old-session");

        await h.SendAsync("hello");
        await h.SendAsync("again");

        Assert.Empty(h.Received("session/load"));
        Assert.Single(h.Received("session/new"));
        Assert.Equal(1, restoreFailed);
    }

    [Fact]
    public async Task A_brand_new_chat_does_not_report_a_failed_restore()
    {
        using var h = await ChatHarness.CreateAsync(new { agentCapabilities = new { loadSession = false } });
        var restoreFailed = false;
        h.Service.SessionRestoreFailed += () => restoreFailed = true;

        await h.SendAsync("hello");

        Assert.False(restoreFailed);
    }

    [Fact]
    public async Task The_session_id_is_serialized_for_persistence_once_a_session_exists()
    {
        using var h = await ChatHarness.CreateAsync();
        Assert.Equal("", h.Service.SerializeHistory());

        await h.SendAsync("hello");

        Assert.Equal("fake-session-1", h.Service.SerializeHistory());
    }

    // ---- modes ----

    private static object Modes() => new
    {
        newSession = new
        {
            modes = new
            {
                currentModeId = "ask",
                availableModes = new[] { new { id = "ask", name = "Ask" }, new { id = "agent", name = "Agent" } }
            }
        }
    };

    [Fact]
    public async Task Modes_offered_by_the_agent_are_exposed_after_the_session_starts()
    {
        using var h = await ChatHarness.CreateAsync(Modes());
        var changed = 0;
        h.Service.ModesChanged += () => changed++;
        Assert.Empty(h.Service.AvailableModes);

        await h.SendAsync("hello");

        Assert.Equal(new[] { "ask", "agent" }, h.Service.AvailableModes.Select(m => m.Id).ToArray());
        Assert.Equal("ask", h.Service.CurrentModeId);
        Assert.True(changed >= 1);
    }

    [Fact]
    public async Task Choosing_a_mode_tells_the_agent()
    {
        using var h = await ChatHarness.CreateAsync(Modes());
        await h.SendAsync("hello");

        await h.Service.SetModeAsync("agent");

        var request = Assert.Single(h.Received("session/set_mode"));
        Assert.Equal("agent", (string?)request["modeId"]);
        Assert.Equal("fake-session-1", (string?)request["sessionId"]);
        Assert.Equal("agent", h.Service.CurrentModeId);
    }

    [Fact]
    public async Task The_agent_can_switch_the_mode_itself()
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            newSession = new { modes = new { currentModeId = "ask", availableModes = new[] { new { id = "ask", name = "Ask" } } } },
            turns = new[] { new { steps = new object[] { new { update = new { sessionUpdate = "current_mode_update", currentModeId = "plan" } }, new { say = "ok" } } } }
        });
        var changed = 0;
        h.Service.ModesChanged += () => changed++;

        await h.SendAsync("hello");

        Assert.Equal("plan", h.Service.CurrentModeId);
        Assert.True(changed >= 2);
    }

    [Fact]
    public async Task Choosing_a_mode_before_any_session_exists_does_nothing()
    {
        using var h = await ChatHarness.CreateAsync(Modes());

        await h.Service.SetModeAsync("agent");

        Assert.Empty(h.AgentLog());
        Assert.Null(h.Service.CurrentModeId);
    }

    // ---- config options ----

    private static object[] ModelOptions(string current) => new object[]
    {
        new
        {
            type = "select", id = "model", name = "Model", currentValue = current,
            options = new[] { new { value = "a", name = "Model A" }, new { value = "b", name = "Model B" } }
        }
    };

    [Fact]
    public async Task Config_options_from_the_agent_are_exposed_and_can_be_changed()
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            newSession = new { configOptions = ModelOptions("a") },
            configOptionsOnSet = ModelOptions("b")
        });
        var changed = 0;
        h.Service.ConfigOptionsChanged += () => changed++;
        await h.SendAsync("hello");

        var initial = Assert.IsType<SessionConfigOptionSelect>(Assert.Single(h.Service.ConfigOptions));
        Assert.Equal("model", initial.Id);
        Assert.Equal("a", initial.CurrentValue);

        await h.Service.SetConfigOptionAsync("model", "b");

        var request = Assert.Single(h.Received("session/set_config_option"));
        Assert.Equal("model", (string?)request["configId"]);
        Assert.Equal("b", (string?)request["value"]);
        Assert.Equal("b", Assert.IsType<SessionConfigOptionSelect>(Assert.Single(h.Service.ConfigOptions)).CurrentValue);
        Assert.True(changed >= 2);
    }

    [Fact]
    public async Task The_agent_can_push_new_config_options()
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            turns = new[]
            {
                new
                {
                    steps = new object[]
                    {
                        new { update = new { sessionUpdate = "config_option_update", configOptions = ModelOptions("b") } },
                        new { say = "ok" }
                    }
                }
            }
        });

        await h.SendAsync("hello");

        Assert.Equal("b", Assert.IsType<SessionConfigOptionSelect>(Assert.Single(h.Service.ConfigOptions)).CurrentValue);
    }

    [Fact]
    public async Task Changing_a_config_option_before_any_session_exists_does_nothing()
    {
        using var h = await ChatHarness.CreateAsync();

        await h.Service.SetConfigOptionAsync("model", "b");

        Assert.Empty(h.AgentLog());
    }

    // ---- slash commands, usage, titles ----

    [Fact]
    public async Task Slash_commands_advertised_by_the_agent_are_exposed()
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            turns = new[]
            {
                new
                {
                    steps = new object[]
                    {
                        new { update = new { sessionUpdate = "available_commands_update", availableCommands = new[] { new { name = "review", description = "Review the changes" } } } },
                        new { say = "ok" }
                    }
                }
            }
        });
        var changed = 0;
        h.Service.CommandsChanged += () => changed++;

        await h.SendAsync("hello");

        Assert.Equal("review", Assert.Single(h.Service.AvailableCommands).Name);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Cost_is_reported_from_usage_updates_and_cleared_with_the_session()
    {
        using var h = await ChatHarness.CreateAsync(new
        {
            turns = new[]
            {
                new
                {
                    steps = new object[]
                    {
                        new { update = new { sessionUpdate = "usage_update", used = 500, size = 1000, cost = new { amount = 0.25, currency = "USD" } } },
                        new { say = "ok" }
                    }
                }
            }
        });
        var changed = 0;
        h.Service.UsageChanged += () => changed++;
        Assert.Null(h.Service.GetSessionCost());

        await h.SendAsync("hello");
        await h.WaitUntilAsync(() => h.Service.Usage is not null, "the usage update");

        Assert.Equal(0.25m, h.Service.GetSessionCost());
        Assert.Equal("USD", h.Service.Usage!.Cost!.Currency);

        h.Service.ClearHistory();

        Assert.Null(h.Service.Usage);
        Assert.Null(h.Service.GetSessionCost());
        Assert.Equal(2, changed);
    }

    [Theory]
    [InlineData("fix the login bug", "fix the login bug")]
    [InlineData("one two three four five six seven eight", "one two three four five six...")]
    [InlineData("  spaced   out  ", "spaced out")]
    public async Task Titles_are_the_first_six_words(string message, string expected)
    {
        using var h = await ChatHarness.CreateAsync();

        Assert.Equal(expected, await h.Service.GenerateTitleAsync(message));
    }

    // ---- deleting the agent's copy of the session ----

    [Fact]
    public async Task The_agent_side_session_can_be_deleted_when_the_agent_supports_it()
    {
        using var h = await ChatHarness.CreateAsync(WithSessionCaps(new { delete = new { } }));
        Assert.False(h.Service.CanDeleteAgentSession);
        await h.SendAsync("hello");
        Assert.True(h.Service.CanDeleteAgentSession);

        await h.Service.DeleteAgentSessionAsync();

        Assert.Equal("fake-session-1", (string?)Assert.Single(h.Received("session/delete"))["sessionId"]);
        Assert.False(h.Service.CanDeleteAgentSession);
    }

    [Fact]
    public async Task Deleting_is_a_no_op_when_the_agent_cannot_delete_sessions()
    {
        using var h = await ChatHarness.CreateAsync(WithSessionCaps(new { }));
        await h.SendAsync("hello");

        await h.Service.DeleteAgentSessionAsync();

        Assert.False(h.Service.CanDeleteAgentSession);
        Assert.Empty(h.Received("session/delete"));
    }

    // ---- what goes into the prompt ----

    private static CodeSelection Selection() =>
        new() { FilePath = @"C:\src\a.cs", StartLine = 3, EndLine = 5, Text = "int x = 1;" };

    [Fact]
    public async Task A_pasted_snippet_is_sent_as_tagged_text_when_the_agent_cannot_take_resources()
    {
        using var h = await ChatHarness.CreateAsync();

        await h.SendRawAsync("explain", selections: new[] { Selection() });

        var blocks = Blocks(Prompt(h));
        Assert.Equal("text", (string?)blocks[0]!["type"]);
        Assert.Equal("explain", (string?)blocks[0]!["text"]);
        var snippet = (string?)blocks[1]!["text"];
        Assert.Equal("text", (string?)blocks[1]!["type"]);
        Assert.Contains("<code_selection path=\"C:\\src\\a.cs\" lines=\"3-5\">", snippet);
        Assert.Contains("int x = 1;", snippet);
    }

    [Fact]
    public async Task A_pasted_snippet_is_sent_as_an_embedded_resource_when_the_agent_supports_it()
    {
        using var h = await ChatHarness.CreateAsync(WithSessionCaps(new { }, promptCapabilities: new { embeddedContext = true }));

        await h.SendRawAsync("explain", selections: new[] { Selection() });

        var resource = (JsonObject)Blocks(Prompt(h))[1]!;
        Assert.Equal("resource", (string?)resource["type"]);
        Assert.EndsWith("a.cs#L3-5", (string?)resource["resource"]!["uri"]);
        Assert.Equal("int x = 1;", (string?)resource["resource"]!["text"]);
    }

    [Fact]
    public async Task Images_are_sent_as_base64_blocks()
    {
        using var h = await ChatHarness.CreateAsync();

        await h.SendRawAsync("what is this", images: new[]
        {
            new ImageAttachment { Data = new byte[] { 1, 2, 3 }, MimeType = "image/png" }
        });

        var image = (JsonObject)Blocks(Prompt(h))[1]!;
        Assert.Equal("image", (string?)image["type"]);
        Assert.Equal("image/png", (string?)image["mimeType"]);
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), (string?)image["data"]);
    }

    [Fact]
    public async Task Attached_files_are_sent_as_resource_links()
    {
        using var h = await ChatHarness.CreateAsync();

        await h.SendRawAsync("look at this", filePaths: new[] { @"C:\src\notes.txt" });

        var link = (JsonObject)Blocks(Prompt(h))[1]!;
        Assert.Equal("resource_link", (string?)link["type"]);
        Assert.Equal("notes.txt", (string?)link["name"]);
        Assert.StartsWith("file:///", (string?)link["uri"]);
        Assert.EndsWith("notes.txt", (string?)link["uri"]);
    }

    [Fact]
    public async Task Attached_folders_are_sent_as_resource_links_with_a_directory_uri()
    {
        using var h = await ChatHarness.CreateAsync();

        await h.SendRawAsync("look at this", folderPaths: new[] { @"C:\src\assets" });

        var link = (JsonObject)Blocks(Prompt(h))[1]!;
        Assert.Equal("resource_link", (string?)link["type"]);
        Assert.Equal("assets", (string?)link["name"]);
        Assert.Null((string?)link["mimeType"]);
        Assert.StartsWith("file:///", (string?)link["uri"]);
        Assert.EndsWith("assets/", (string?)link["uri"]);
    }

    // ---- the post-revert context prefix ----

    private static object ThreeTurns() => new
    {
        turns = new object[]
        {
            new { when = "one", steps = new object[] { new { say = "A1" } } },
            new { when = "two", steps = new object[] { new { say = "A2" } } },
            new { when = "three", steps = new object[] { new { say = "A3" } } },
            new { steps = new object[] { new { say = "ok" } } }
        }
    };

    [Fact]
    public async Task Clearing_the_chat_after_a_revert_drops_the_pending_context()
    {
        using var h = await ChatHarness.CreateAsync(ThreeTurns());
        await h.SendAsync("one");
        await h.SendAsync("two");
        await h.RevertToUserMessageAsync(1);

        h.Service.ClearHistory();
        await h.SendAsync("fresh");

        Assert.DoesNotContain("<prior_conversation_context>", h.PromptTexts().Last());
    }

    [Fact]
    public async Task A_second_revert_replaces_the_context_of_the_first()
    {
        using var h = await ChatHarness.CreateAsync(ThreeTurns());
        await h.SendAsync("one");
        await h.SendAsync("two");
        await h.SendAsync("three");
        await h.RevertToUserMessageAsync(2);
        await h.RevertToUserMessageAsync(1);

        await h.SendAsync("next");

        var prompt = h.PromptTexts().Last();
        Assert.Contains("A1", prompt);
        Assert.DoesNotContain("A2", prompt);
        Assert.DoesNotContain("A3", prompt);
    }

    [Fact]
    public async Task The_context_lists_earlier_messages_by_speaker_and_ends_with_the_new_request()
    {
        using var h = await ChatHarness.CreateAsync(ThreeTurns());
        await h.SendAsync("one");
        await h.SendAsync("two");
        await h.RevertToUserMessageAsync(1);

        await h.SendRawAsync("never mind");
        var prompt = h.PromptTexts().Last();

        Assert.Contains("[User]: one", prompt);
        Assert.Contains("[Assistant]: A1", prompt);
        Assert.EndsWith("My next request:\nnever mind", prompt.Replace("\r\n", "\n"));
    }
}
