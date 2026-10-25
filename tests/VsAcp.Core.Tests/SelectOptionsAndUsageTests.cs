using System.Globalization;
using System.Text.Json;
using AcpProtocol;
using VsAcp.Core.Services;

namespace VsAcp.Core.Tests;

public class SelectOptionsTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string[] Values(string json) => SelectOptions.Parse(Json(json)).Select(o => o.Value).ToArray();

    [Fact]
    public void Plain_options_are_listed_in_order()
    {
        var options = SelectOptions.Parse(Json("""[{"value":"a","name":"Model A"},{"value":"b","name":"Model B","description":"fast"}]"""));

        Assert.Equal(new[] { "a", "b" }, options.Select(o => o.Value).ToArray());
        Assert.Equal("Model B", options[1].Name);
        Assert.Equal("fast", options[1].Description);
    }

    [Fact]
    public void Grouped_options_are_flattened()
    {
        var json = """
            [
              {"group":"g1","name":"Fast","options":[{"value":"a","name":"A"},{"value":"b","name":"B"}]},
              {"group":"g2","name":"Smart","options":[{"value":"c","name":"C"}]}
            ]
            """;

        Assert.Equal(new[] { "a", "b", "c" }, Values(json));
    }

    [Fact]
    public void Plain_and_grouped_options_can_be_mixed()
    {
        var json = """
            [
              {"value":"a","name":"A"},
              {"group":"g","name":"G","options":[{"value":"b","name":"B"}]},
              {"value":"c","name":"C"}
            ]
            """;

        Assert.Equal(new[] { "a", "b", "c" }, Values(json));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("\"models\"")]
    [InlineData("42")]
    public void Anything_that_is_not_a_list_gives_no_options(string json)
    {
        Assert.Empty(SelectOptions.Parse(Json(json)));
    }

    [Fact]
    public void An_unreadable_entry_is_skipped_and_the_others_are_kept()
    {
        var json = """[{"value":"a","name":"A"},{"name":"no value"},{"value":"c","name":"C"}]""";

        Assert.Equal(new[] { "a", "c" }, Values(json));
    }

    [Fact]
    public void An_unreadable_group_is_skipped_and_the_others_are_kept()
    {
        var json = """
            [
              {"group":"g1","name":"Broken","options":[{"value":"x"}]},
              {"group":"g2","name":"Fine","options":[{"value":"b","name":"B"}]},
              {"value":"c","name":"C"}
            ]
            """;

        Assert.Equal(new[] { "b", "c" }, Values(json));
    }

    [Fact]
    public void Entries_that_are_not_objects_are_skipped()
    {
        var json = """[null, 5, "text", {"value":"a","name":"A"}, []]""";

        Assert.Equal(new[] { "a" }, Values(json));
    }

    [Fact]
    public void Several_unreadable_entries_in_a_row_do_not_stop_the_parse()
    {
        var json = """[{"name":"bad1"},{"value":1,"name":"bad2"},{"value":"ok","name":"OK"}]""";

        Assert.Equal(new[] { "ok" }, Values(json));
    }

    [Fact]
    public void A_group_with_no_options_adds_nothing()
    {
        var json = """[{"group":"g","name":"G","options":[]},{"value":"a","name":"A"}]""";

        Assert.Equal(new[] { "a" }, Values(json));
    }

    [Fact]
    public void Options_parsed_from_a_real_select_option_are_the_same_as_from_its_raw_json()
    {
        var option = JsonSerializer.Deserialize<SessionConfigOption>(
            """{"type":"select","id":"model","name":"Model","currentValue":"a","options":[{"value":"a","name":"A"}]}""",
            AcpJsonSerializerContext.Default.Options);

        var select = Assert.IsType<SessionConfigOptionSelect>(option);

        Assert.Equal(new[] { "a" }, SelectOptions.Parse(select.Options).Select(o => o.Value).ToArray());
    }
}

public class UsageFormatterTests
{
    // The readout follows the user's culture ("12,3k" in a Greek locale), so these pin the invariant one.
    private static T WithInvariantCulture<T>(Func<T> action)
    {
        var thread = Thread.CurrentThread;
        var previous = thread.CurrentCulture;
        thread.CurrentCulture = CultureInfo.InvariantCulture;
        try { return action(); }
        finally { thread.CurrentCulture = previous; }
    }

    private static UsageUpdate Usage(ulong used, ulong size, Cost? cost = null) =>
        new() { Used = used, Size = size, Cost = cost };

    [Theory]
    [InlineData(0UL, "0")]
    [InlineData(999UL, "999")]
    [InlineData(1000UL, "1k")]
    [InlineData(1500UL, "1.5k")]
    [InlineData(12345UL, "12.3k")]
    [InlineData(999_999UL, "1000k")]
    [InlineData(1_000_000UL, "1M")]
    [InlineData(2_500_000UL, "2.5M")]
    public void Token_counts_are_abbreviated(ulong tokens, string expected)
    {
        Assert.Equal(expected, WithInvariantCulture(() => UsageFormatter.FormatTokens(tokens)));
    }

    [Theory]
    [InlineData(0.42, "USD", "$0.42")]
    [InlineData(0.4, "usd", "$0.40")]
    [InlineData(12.0, "USD", "$12.00")]
    [InlineData(1.5, "EUR", "1.50 EUR")]
    public void Costs_show_a_dollar_sign_for_usd_and_the_code_otherwise(double amount, string currency, string expected)
    {
        Assert.Equal(expected, WithInvariantCulture(() => UsageFormatter.FormatCost(amount, currency)));
    }

    [Fact]
    public void The_readout_shows_used_of_size_with_a_percentage()
    {
        var (text, tip) = WithInvariantCulture(() => UsageFormatter.Format(Usage(12_300, 200_000)));

        Assert.Equal("12.3k / 200k (6%)", text);
        Assert.Equal("Context: 12,300 of 200,000 tokens used", tip);
    }

    [Fact]
    public void Without_a_context_size_only_the_used_tokens_are_shown()
    {
        var (text, tip) = WithInvariantCulture(() => UsageFormatter.Format(Usage(5_000, 0)));

        Assert.Equal("5k", text);
        Assert.Equal("Context: 5,000 tokens used", tip);
    }

    [Fact]
    public void A_reported_cost_is_added_to_both_texts()
    {
        var (text, tip) = WithInvariantCulture(() =>
            UsageFormatter.Format(Usage(100_000, 200_000, new Cost { Amount = 0.42, Currency = "USD" })));

        Assert.Equal("100k / 200k (50%)  $0.42", text);
        Assert.Equal("Context: 100,000 of 200,000 tokens used\nCost: $0.42", tip);
    }

    [Fact]
    public void A_full_context_shows_one_hundred_percent_and_an_overfull_one_goes_over()
    {
        Assert.Equal("1k / 1k (100%)", WithInvariantCulture(() => UsageFormatter.Format(Usage(1000, 1000)).Text));
        Assert.Equal("2k / 1k (200%)", WithInvariantCulture(() => UsageFormatter.Format(Usage(2000, 1000)).Text));
    }
}
