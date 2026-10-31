using VsAcp.Core.Services;

namespace VsAcp.Core.Tests;

public class AgentEnvironmentTests
{
    static KeyValuePair<string?, string?> Row(string? name, string? value) => new(name, value);

    [Fact]
    public void Names_and_values_are_trimmed_and_an_empty_value_is_kept()
    {
        var env = AgentEnvironment.Build(new[] { Row("FOO", "bar"), Row("  TOKEN ", "  a=b=c  "), Row("EMPTY", ""), Row("NULLED", null) });

        Assert.Equal("bar", env["FOO"]);
        Assert.Equal("a=b=c", env["TOKEN"]);
        Assert.Equal("", env["EMPTY"]);
        Assert.Equal("", env["NULLED"]);
        Assert.Equal(4, env.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A=B")]
    public void A_row_with_no_usable_name_is_ignored(string? name) =>
        Assert.Empty(AgentEnvironment.Build(new[] { Row(name, "value") }));

    [Fact]
    public void A_later_row_replaces_an_earlier_one_with_the_same_name_ignoring_case()
    {
        var env = AgentEnvironment.Build(new[] { Row("Path", "first"), Row("PATH", "second") });

        Assert.Equal("second", Assert.Single(env).Value);
        Assert.Equal("second", env["path"]);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData("true", true)]
    [InlineData("False", false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("maybe", true)]
    [InlineData(1, true)]
    public void A_row_is_enabled_unless_its_cell_says_false(object? cell, bool expected) =>
        Assert.Equal(expected, AgentEnvironment.IsEnabled(cell));

    [Fact]
    public void No_rows_is_no_variables()
    {
        Assert.Empty(AgentEnvironment.Build(null));
        Assert.Empty(AgentEnvironment.Build(Array.Empty<KeyValuePair<string?, string?>>()));
    }
}
