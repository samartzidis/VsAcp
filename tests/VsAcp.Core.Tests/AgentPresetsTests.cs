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
}
