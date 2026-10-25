using System.Text.Json;
using System.Text.Json.Serialization;

namespace AcpProtocol;

// Hand-written types for unstable ACP methods. Generated/AcpTypes.g.cs comes from the stable schema only, so these
// follow schema.unstable.json (same release) by hand, in the generated style. Each has to be dropped here once the
// method is stabilized and generated.

/// <summary>
/// <c>session/fork</c> (unstable): copies an existing session into a new one the client can prompt at once. Offered
/// when the agent advertises <c>sessionCapabilities.fork</c>, which lands in <see cref="SessionCapabilities.ExtensionData"/>.
/// </summary>
public record ForkSessionRequest
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("cwd")]
    public required string Cwd { get; init; }

    [JsonPropertyName("additionalDirectories")]
    public string[]? AdditionalDirectories { get; init; }

    [JsonPropertyName("mcpServers")]
    public McpServer[]? McpServers { get; init; }

    [JsonPropertyName("_meta")]
    public JsonElement? Meta { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public record ForkSessionResponse
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("modes")]
    public SessionModeState? Modes { get; init; }

    [JsonPropertyName("configOptions")]
    public SessionConfigOption[]? ConfigOptions { get; init; }

    [JsonPropertyName("_meta")]
    public JsonElement? Meta { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

// A context of their own: the generator does not take [JsonSerializable] from a second partial declaration of
// AcpJsonSerializerContext. Same options as the generated one.
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(ForkSessionRequest))]
[JsonSerializable(typeof(ForkSessionResponse))]
public partial class AcpUnstableJsonSerializerContext : JsonSerializerContext;
