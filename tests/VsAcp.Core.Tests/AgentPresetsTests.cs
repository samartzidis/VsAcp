using VsAcp.Core.Services;

namespace VsAcp.Core.Tests;

public class AgentPresetsTests
{
    [Theory]
    [InlineData(AgentPresets.ClaudeCode, "claude")]
    [InlineData(AgentPresets.Cursor, "cursor")]
    [InlineData(AgentPresets.Custom, null)]
    [InlineData("somethingNew", null)]
    [InlineData(null, null)]
    public void Only_the_built_in_presets_have_a_logo(string? preset, string? logo) =>
        Assert.Equal(logo, AgentPresets.Logo(preset));

    [Theory]
    // Blank fields take the preset's command line.
    [InlineData(AgentPresets.Cursor, "", "", AgentPresets.Cursor, "agent", "acp")]
    [InlineData(AgentPresets.ClaudeCode, "", "", AgentPresets.ClaudeCode, "npx", "@agentclientprotocol/claude-agent-acp --yes")]
    [InlineData(AgentPresets.ClaudeCode, null, null, AgentPresets.ClaudeCode, "npx", "@agentclientprotocol/claude-agent-acp --yes")]
    [InlineData(AgentPresets.ClaudeCode, "  ", "\t", AgentPresets.ClaudeCode, "npx", "@agentclientprotocol/claude-agent-acp --yes")]
    // Each field is resolved on its own.
    [InlineData(AgentPresets.ClaudeCode, @"C:\tools\npx.cmd", "", AgentPresets.ClaudeCode, @"C:\tools\npx.cmd", "@agentclientprotocol/claude-agent-acp --yes")]
    [InlineData(AgentPresets.ClaudeCode, "", "@agentclientprotocol/claude-agent-acp --yes --verbose", AgentPresets.ClaudeCode, "npx", "@agentclientprotocol/claude-agent-acp --yes --verbose")]
    [InlineData(AgentPresets.Cursor, "my-agent", "serve", AgentPresets.Cursor, "my-agent", "serve")]
    // Values are trimmed.
    [InlineData(AgentPresets.Cursor, " agent2 ", " acp ", AgentPresets.Cursor, "agent2", "acp")]
    // Custom has no values of its own: blank fields take Cursor's, and it keeps its preset (so no logo).
    [InlineData(AgentPresets.Custom, "", "", AgentPresets.Custom, "agent", "acp")]
    [InlineData(AgentPresets.Custom, "my-agent", "", AgentPresets.Custom, "my-agent", "acp")]
    [InlineData(AgentPresets.Custom, "my-agent", "serve", AgentPresets.Custom, "my-agent", "serve")]
    // An unknown or missing preset reads as Cursor.
    [InlineData("somethingNew", "", "", AgentPresets.Cursor, "agent", "acp")]
    [InlineData(null, "", "x", AgentPresets.Cursor, "agent", "x")]
    public void Resolve_fills_each_blank_field_from_the_preset(
        string? preset, string? command, string? arguments, string expectedPreset, string expectedCommand, string expectedArguments) =>
        Assert.Equal((expectedPreset, expectedCommand, expectedArguments), AgentPresets.Resolve(preset, command, arguments));
}
