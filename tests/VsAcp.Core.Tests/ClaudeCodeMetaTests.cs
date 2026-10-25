using System.Text.Json;
using VsAcp.Core.Acp.Handlers;

namespace VsAcp.Core.Tests;

public class ClaudeCodeMetaTests
{
    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void A_shell_command_reports_the_files_it_changed()
    {
        // trimmed from a real claude-agent-acp tool_call_update for a Bash call running sed -i
        var meta = Json("""
            {"claudeCode":{"toolResponse":{"stdout":"","bashEditDiff":{
              "files":[{"filePath":"C:\\ws\\IHello.cs","hunks":[]},{"filePath":"C:\\ws\\README.md","hunks":[]}],
              "moreFiles":0,
              "changedFiles":["C:\\ws\\IHello.cs","C:\\ws\\README.md"]}},"toolName":"Bash"}}
            """);

        Assert.Equal(new[] { @"C:\ws\IHello.cs", @"C:\ws\README.md" }, ClaudeCodeMeta.ShellChangedFiles(meta));
    }

    [Fact]
    public void Without_changedFiles_the_paths_of_the_file_entries_are_used()
    {
        var meta = Json("""{"claudeCode":{"toolResponse":{"bashEditDiff":{"files":[{"filePath":"C:\\ws\\a.txt"}]}}}}""");

        Assert.Equal(new[] { @"C:\ws\a.txt" }, ClaudeCodeMeta.ShellChangedFiles(meta));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{}""")]
    [InlineData("""{"claudeCode":{"toolName":"Grep"}}""")]
    [InlineData("""{"claudeCode":{"toolResponse":"text"}}""")]
    [InlineData("""{"claudeCode":{"toolResponse":{"bashEditDiff":{"changedFiles":"C:\\ws\\a.txt"}}}}""")]
    [InlineData("""{"claudeCode":{"toolResponse":{"bashEditDiff":{"changedFiles":[1, null, ""]}}}}""")]
    [InlineData("""[1, 2]""")]
    public void Anything_else_yields_no_files(string? json)
    {
        Assert.Empty(ClaudeCodeMeta.ShellChangedFiles(json is null ? null : Json(json)));
    }
}
