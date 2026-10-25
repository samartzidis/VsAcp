using System.Text.Json;
using AcpProtocol;
using VsAcp.Core.Abstractions;
using VsAcp.Core.Acp.Handlers;

namespace VsAcp.Core.Tests;

/// <summary>Display titles and body rendering for tool calls, which is what the user reads in the chat.</summary>
public class ToolCallAccumulatorTests
{
    private readonly ToolCallAccumulator _acc = new();

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static ToolCallLocation[] At(string path) => new[] { new ToolCallLocation { Path = path } };

    private static ToolCallContent[] Text(params string[] texts) =>
        texts.Select(t => (ToolCallContent)new ToolCallContentContent { Content = new ContentBlockText { Text = t } }).ToArray();

    private static OutputItem NewItem() => new() { Id = "t1", ToolName = "x", Title = "x" };

    private OutputItem Body(ToolCallContent[]? content = null, JsonElement? input = null, JsonElement? output = null,
        ToolCallLocation[]? locations = null, string id = "t1")
    {
        var item = NewItem();
        _acc.PopulateBody(item, id, content, input, output, locations);
        return item;
    }

    // ---- titles ----

    [Fact]
    public void An_explicit_title_is_used_as_is()
    {
        Assert.Equal("Reading the config", _acc.ResolveCallTitle("t1", "Reading the config", null, ToolKind.Read, null, null));
    }

    [Theory]
    [InlineData(ToolKind.Read, "Read")]
    [InlineData(ToolKind.Edit, "Edit")]
    [InlineData(ToolKind.Delete, "Delete")]
    [InlineData(ToolKind.Move, "Move")]
    [InlineData(ToolKind.Search, "Search")]
    [InlineData(ToolKind.Execute, "Terminal")]
    [InlineData(ToolKind.Think, "Thinking")]
    [InlineData(ToolKind.Fetch, "Fetch")]
    [InlineData(ToolKind.Other, "Tool")]
    [InlineData(ToolKind.SwitchMode, "Tool")]
    public void Without_a_title_or_name_the_kind_gives_a_generic_label(ToolKind kind, string expected)
    {
        Assert.Equal(expected, _acc.ResolveCallTitle("t1", "", null, kind, null, null));
    }

    [Theory]
    [InlineData("read_file", "Read file")]
    [InlineData("Bash", "Bash")]
    [InlineData("mcp__github__create_issue", "Create issue (github)")]
    [InlineData("mcp__only_two", "Only two")]
    public void Without_a_title_the_tool_name_is_humanized_ahead_of_the_kind(string name, string expected)
    {
        Assert.Equal(expected, _acc.ResolveCallTitle("t1", "", name, ToolKind.Execute, null, null));
    }

    [Fact]
    public void The_first_location_adds_its_file_name_to_the_title()
    {
        Assert.Equal("Read Program.cs", _acc.ResolveCallTitle("t1", "Read", null, ToolKind.Read, At(@"C:\src\app\Program.cs"), null));
    }

    [Fact]
    public void The_file_name_is_not_repeated_when_the_title_already_has_it()
    {
        Assert.Equal("Edit Program.cs", _acc.ResolveCallTitle("t1", "Edit Program.cs", null, ToolKind.Edit, At(@"C:\src\Program.cs"), null));
    }

    [Theory]
    [InlineData("""{"command":"dotnet build"}""", "Terminal dotnet build")]
    [InlineData("""{"file_path":"src/a.ts"}""", "Terminal src/a.ts")]
    [InlineData("""{"pattern":"TODO"}""", "Terminal TODO")]
    [InlineData("""{"unrelated":"x"}""", "Terminal")]
    [InlineData("""{"command":"   "}""", "Terminal")]
    public void Raw_input_supplies_a_detail_when_there_is_no_location(string rawInput, string expected)
    {
        Assert.Equal(expected, _acc.ResolveCallTitle("t1", "Terminal", null, ToolKind.Execute, null, Json(rawInput)));
    }

    [Fact]
    public void A_long_input_detail_is_shortened()
    {
        var command = new string('x', 120);

        var title = _acc.ResolveCallTitle("t1", "Run", null, ToolKind.Execute, null, Json($$"""{"command":"{{command}}"}"""));

        Assert.Equal("Run " + new string('x', 77) + "...", title);
    }

    [Fact]
    public void A_location_wins_over_raw_input_detail()
    {
        var title = _acc.ResolveCallTitle("t1", "Read", null, ToolKind.Read, At("a/b.txt"), Json("""{"path":"other.txt"}"""));

        Assert.Equal("Read b.txt", title);
    }

    [Fact]
    public void A_trailing_update_without_a_title_keeps_the_resolved_title()
    {
        _acc.ResolveCallTitle("t1", "Edit Program.cs", null, ToolKind.Edit, null, null);

        var title = _acc.ResolveUpdateTitle("t1", null, null, null, null, null);

        Assert.Equal("Edit Program.cs", title);
    }

    [Fact]
    public void An_update_with_a_new_title_replaces_the_old_one()
    {
        _acc.ResolveCallTitle("t1", "Searching", null, ToolKind.Search, null, null);

        Assert.Equal("Found 3 matches", _acc.ResolveUpdateTitle("t1", "Found 3 matches", null, null, null, null));
        Assert.Equal("Found 3 matches", _acc.ResolveUpdateTitle("t1", null, null, null, null, null));
    }

    [Fact]
    public void An_update_for_an_unknown_call_synthesizes_a_title()
    {
        Assert.Equal("Tool", _acc.ResolveUpdateTitle("unseen", null, null, null, null, null));
    }

    [Fact]
    public void A_blank_update_title_reuses_the_title_synthesized_for_the_original_call()
    {
        _acc.ResolveCallTitle("t1", "", "read_file", null, null, null);

        Assert.Equal("Read file", _acc.ResolveUpdateTitle("t1", "", null, null, null, null));
        // A different call that never had a name does not inherit it.
        Assert.Equal("Tool", _acc.ResolveUpdateTitle("t2", "", null, null, null, null));
    }

    [Fact]
    public void Reset_forgets_titles_and_bodies()
    {
        _acc.ResolveCallTitle("t1", "Edit Program.cs", null, null, null, null);
        Body(input: Json("""{"command":"ls"}"""));

        _acc.Reset();

        Assert.Equal("Tool", _acc.ResolveUpdateTitle("t1", null, null, null, null, null));
        Assert.Null(Body().Body);
    }

    // ---- diff bodies ----

    [Fact]
    public void A_diff_renders_removed_and_added_lines_under_the_path()
    {
        var item = NewItem();
        var content = new ToolCallContent[] { new ToolCallContentDiff { Path = "a.txt", OldText = "one\ntwo", NewText = "one\nthree" } };

        var fromDiff = _acc.PopulateBody(item, "t1", content, null, null, null);

        Assert.True(fromDiff);
        Assert.Equal(OutputBodyMode.Markdown, item.BodyMode);
        var lines = item.Body!.Replace("\r\n", "\n").Split('\n');
        Assert.Equal(new[] { "**a.txt**", "```diff", "- one", "- two", "+ one", "+ three", "```", "" }, lines);
    }

    [Fact]
    public void A_diff_of_a_new_file_has_only_added_lines()
    {
        var item = NewItem();
        _acc.PopulateBody(item, "t1", new ToolCallContent[] { new ToolCallContentDiff { Path = "new.txt", NewText = "hello" } }, null, null, null);

        Assert.DoesNotContain("- ", item.Body);
        Assert.Contains("+ hello", item.Body);
    }

    [Fact]
    public void Windows_line_endings_in_a_diff_are_normalized()
    {
        var item = NewItem();
        _acc.PopulateBody(item, "t1", new ToolCallContent[] { new ToolCallContentDiff { Path = "a", OldText = "a\r\nb", NewText = "a\r\nc" } }, null, null, null);

        // Each source line becomes exactly one diff line; a stray "\r" would show up as an extra blank line.
        var lines = item.Body!.Replace("\r\n", "\n").Split('\n');
        Assert.Equal(new[] { "**a**", "```diff", "- a", "- b", "+ a", "+ c", "```", "" }, lines);
    }

    [Fact]
    public void Several_diffs_are_rendered_one_after_another()
    {
        var item = NewItem();
        _acc.PopulateBody(item, "t1", new ToolCallContent[]
        {
            new ToolCallContentDiff { Path = "a.txt", NewText = "A" },
            new ToolCallContentDiff { Path = "b.txt", NewText = "B" }
        }, null, null, null);

        Assert.True(item.Body!.IndexOf("**a.txt**", StringComparison.Ordinal) < item.Body.IndexOf("**b.txt**", StringComparison.Ordinal));
    }

    // ---- text bodies ----

    [Fact]
    public void Raw_input_and_output_are_shown_together()
    {
        var item = Body(input: Json("""{"command":"dotnet test"}"""), output: Json("""{"output":"Passed!"}"""));

        Assert.Equal("dotnet test\nPassed!", item.Body);
        Assert.Equal(OutputBodyMode.PlainText, item.BodyMode);
    }

    [Fact]
    public void Input_and_output_arriving_in_separate_updates_are_merged()
    {
        Body(input: Json("""{"command":"ls"}"""));

        var later = Body(output: Json("""{"output":"a.txt"}"""));

        Assert.Equal("ls\na.txt", later.Body);
    }

    [Fact]
    public void Bodies_of_different_calls_do_not_mix()
    {
        Body(input: Json("""{"command":"ls"}"""), id: "one");

        var other = Body(output: Json("""{"output":"x"}"""), id: "two");

        Assert.Equal("x", other.Body);
    }

    [Fact]
    public void Input_without_a_known_field_falls_back_to_its_raw_json()
    {
        var item = Body(input: Json("""{"weird":1}"""));

        Assert.Equal("""{"weird":1}""", item.Body);
    }

    [Fact]
    public void An_empty_object_adds_nothing()
    {
        Assert.Null(Body(input: Json("{}"), output: Json("{}")).Body);
    }

    [Fact]
    public void Content_text_takes_precedence_over_input_and_output()
    {
        var item = Body(Text("first", "second"), Json("""{"command":"ls"}"""), Json("""{"output":"x"}"""));

        Assert.Equal("first\nsecond", item.Body);
    }

    [Fact]
    public void Blank_content_text_is_ignored()
    {
        var item = Body(Text("  ", ""), Json("""{"command":"ls"}"""));

        Assert.Equal("ls", item.Body);
    }

    [Fact]
    public void Long_text_is_truncated()
    {
        var item = Body(Text(new string('a', 5000)));

        Assert.EndsWith("\n... (truncated)", item.Body);
        Assert.Equal(4000 + "\n... (truncated)".Length, item.Body!.Length);
    }

    [Fact]
    public void An_image_switches_the_body_to_html_with_encoded_text()
    {
        var content = new ToolCallContent[]
        {
            new ToolCallContentContent { Content = new ContentBlockText { Text = "<b>caption</b>" } },
            new ToolCallContentContent { Content = new ContentBlockImage { Data = "QUJD", MimeType = "image/png" } }
        };

        var item = Body(content);

        Assert.Equal(OutputBodyMode.Html, item.BodyMode);
        Assert.Contains("&lt;b&gt;caption&lt;/b&gt;", item.Body);
        Assert.DoesNotContain("<b>caption</b>", item.Body);
        Assert.Contains("src=\"data:image/png;base64,QUJD\"", item.Body);
    }

    // ---- language ----

    [Theory]
    [InlineData("a/b.cs", "csharp")]
    [InlineData("a/b.TS", "typescript")]
    [InlineData("a/b.tsx", "typescript")]
    [InlineData("a/b.js", "javascript")]
    [InlineData("a/b.py", "python")]
    [InlineData("a/b.yml", "yaml")]
    [InlineData("a/b.ps1", "powershell")]
    [InlineData("a/b.sh", "bash")]
    [InlineData("a/b.htm", "html")]
    [InlineData("a/b.tf", "tf")]
    public void Language_comes_from_the_file_extension(string path, string expected)
    {
        Assert.Equal(expected, Body(Text("code"), locations: At(path)).Language);
    }

    [Fact]
    public void Language_can_come_from_the_raw_input_path()
    {
        Assert.Equal("go", Body(Text("code"), Json("""{"file_path":"main.go"}""")).Language);
    }

    [Fact]
    public void A_file_without_an_extension_has_no_language()
    {
        Assert.Null(Body(Text("code"), locations: At("Makefile")).Language);
    }

    [Fact]
    public void Html_bodies_carry_no_language()
    {
        var content = new ToolCallContent[]
        {
            new ToolCallContentContent { Content = new ContentBlockImage { Data = "QUJD", MimeType = "image/png" } }
        };

        Assert.Null(Body(content, locations: At("a.cs")).Language);
    }
}
