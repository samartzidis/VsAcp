using System.Text.Json;
using AcpProtocol;
using VsAcp.Core.Acp;

namespace VsAcp.Core.Tests;

public class AcpErrorTextTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Uses_the_agents_details_when_the_error_carries_them()
    {
        var ex = new AcpException("Internal error",
            Json("""{"details":"API error: 429 Usage credits are required for this model · model not changed"}"""), -32603);

        Assert.Equal("API error: 429 Usage credits are required for this model · model not changed", AcpErrorText.Describe(ex));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{}""")]
    [InlineData("""{"details":""}""")]
    [InlineData("""{"details":42}""")]
    [InlineData("\"just text\"")]
    public void Falls_back_to_the_message_without_usable_details(string? data)
    {
        var ex = new AcpException("Internal error", data is null ? null : Json(data), -32603);

        Assert.Equal("Internal error", AcpErrorText.Describe(ex));
    }

    [Fact]
    public void Other_exceptions_use_their_message() =>
        Assert.Equal("boom", AcpErrorText.Describe(new InvalidOperationException("boom")));
}
