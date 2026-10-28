using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using AcpProtocol;
using Microsoft.Extensions.Logging.Abstractions;
using VsAcp.Core.Abstractions;
using VsAcp.Core.Acp;
using VsAcp.Core.Acp.Handlers;

namespace VsAcp.Core.Tests;

/// <summary>
/// The generated tagless unions whose schema branches carry a <c>type</c> tag (McpServer, AuthMethod,
/// MultiSelectItems, ElicitationPropertySchema): a tagged variant writes its tag, a known tag reads as that
/// variant, no tag reads as the schema's untagged branch, and a tag the schema doesn't list reads as Unknown.
/// </summary>
public class TaggedUnionTests
{
    // The same resolvers ClientSideConnection sends requests with.
    static readonly JsonSerializerOptions Options = new(AcpJsonSerializerContext.Default.Options)
    {
        TypeInfoResolver = JsonTypeInfoResolver.Combine(AcpJsonSerializerContext.Default, AcpUnstableJsonSerializerContext.Default)
    };

    static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;

    static JsonObject Write<T>(T value) => JsonNode.Parse(JsonSerializer.Serialize(value, Options))!.AsObject();

    static McpServerHttp HttpServer() => new()
    {
        Name = "vsacp",
        Url = "http://127.0.0.1:5123/mcp/",
        Headers = [new HttpHeader { Name = "Authorization", Value = "Bearer t" }]
    };

    // --- McpServer (written by us) ---

    [Fact]
    public void An_http_server_is_written_with_its_type_tag()
    {
        var json = Write<McpServer>(HttpServer());

        Assert.Equal("http", (string?)json["type"]);
        Assert.Equal("vsacp", (string?)json["name"]);
        Assert.Equal("Bearer t", (string?)json["headers"]![0]!["value"]);
    }

    [Fact]
    public void An_http_server_written_as_its_own_type_still_carries_the_tag()
    {
        Assert.Equal("http", (string?)Write(HttpServer())["type"]);
    }

    [Fact]
    public void A_stdio_server_is_written_without_a_tag()
    {
        var json = Write<McpServer>(new McpServerStdio { Name = "s", Command = "c", Args = [], Env = [] });

        Assert.False(json.ContainsKey("type"));
    }

    [Fact]
    public void Session_setup_requests_carry_the_http_tag()
    {
        McpServer[] servers = [HttpServer()];

        static string? FirstTag(JsonObject request) => (string?)request["mcpServers"]![0]!["type"];

        Assert.Equal("http", FirstTag(Write(new NewSessionRequest { Cwd = "C:\\w", McpServers = servers })));
        Assert.Equal("http", FirstTag(Write(new LoadSessionRequest { SessionId = "s", Cwd = "C:\\w", McpServers = servers })));
        Assert.Equal("http", FirstTag(Write(new ForkSessionRequest { SessionId = "s", Cwd = "C:\\w", McpServers = servers })));
    }

    [Theory]
    [InlineData("""{"type":"http","name":"n","url":"u","headers":[]}""", typeof(McpServerHttp))]
    [InlineData("""{"type":"sse","name":"n","url":"u","headers":[]}""", typeof(McpServerSse))]
    [InlineData("""{"name":"n","command":"c","args":[],"env":[]}""", typeof(McpServerStdio))]
    [InlineData("""{"type":"ws","name":"n","url":"u"}""", typeof(McpServerUnknown))]
    public void A_server_reads_as_the_variant_its_tag_names(string json, Type expected)
    {
        Assert.IsType(expected, Read<McpServer>(json));
    }

    [Fact]
    public void A_known_tag_is_not_left_in_extension_data()
    {
        var server = (McpServerHttp)Read<McpServer>("""{"type":"http","name":"n","url":"u","headers":[]}""");

        Assert.Null(server.ExtensionData);
    }

    [Fact]
    public void A_server_round_trips()
    {
        var json = Write<McpServer>(Read<McpServer>(JsonSerializer.Serialize<McpServer>(HttpServer(), Options)));

        Assert.Equal("http", (string?)json["type"]);
        Assert.Equal("http://127.0.0.1:5123/mcp/", (string?)json["url"]);
    }

    // --- Unknown ---

    [Fact]
    public void An_unknown_tag_keeps_the_tag_and_the_other_fields()
    {
        var unknown = (McpServerUnknown)Read<McpServer>("""{"type":"ws","name":"n","url":"u","extra":{"a":1}}""");

        Assert.Equal("ws", unknown.RawType);
        Assert.Equal(new[] { "extra", "name", "url" }, unknown.ExtensionData!.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void An_unknown_value_is_written_back_unchanged()
    {
        const string original = """{"type":"ws","name":"n","url":"u","extra":{"a":1}}""";

        var json = Write(Read<McpServer>(original));

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original), json));
    }

    [Fact]
    public void A_tag_that_is_not_a_string_is_rejected()
    {
        Assert.Throws<JsonException>(() => Read<McpServer>("""{"type":1,"name":"n"}"""));
    }

    // --- AuthMethod (read from the agent's initialize response) ---

    [Theory]
    [InlineData("""{"id":"login","name":"Log in"}""", typeof(AuthMethodAgent))]
    [InlineData("""{"type":"terminal","id":"t","name":"Terminal"}""", typeof(AuthMethodTerminal))]
    [InlineData("""{"type":"env_var","id":"k","name":"Key","varName":"FOO"}""", typeof(AuthMethodUnknown))]
    public void An_auth_method_reads_as_the_variant_its_tag_names(string json, Type expected)
    {
        Assert.IsType(expected, Read<AuthMethod>(json));
    }

    [Fact]
    public void An_initialize_response_with_an_unknown_auth_method_still_parses()
    {
        var response = Read<InitializeResponse>("""
            {"protocolVersion":1,"authMethods":[
              {"id":"login","name":"Log in"},
              {"type":"env_var","id":"k","name":"Key"}
            ]}
            """);

        Assert.IsType<AuthMethodAgent>(response.AuthMethods![0]);
        Assert.Equal("env_var", Assert.IsType<AuthMethodUnknown>(response.AuthMethods[1]).RawType);
    }

    // --- MultiSelectItems and ElicitationPropertySchema (elicitation forms) ---

    [Theory]
    [InlineData("""{"type":"string","enum":["a","b"]}""", typeof(StringMultiSelectItems))]
    [InlineData("""{"anyOf":[{"const":"a","title":"A"}]}""", typeof(TitledMultiSelectItems))]
    [InlineData("""{"type":"color","palette":"web"}""", typeof(MultiSelectItemsUnknown))]
    public void Multi_select_items_read_as_the_variant_their_tag_names(string json, Type expected)
    {
        Assert.IsType(expected, Read<MultiSelectItems>(json));
    }

    [Theory]
    [InlineData("""{"type":"string"}""", typeof(StringPropertySchema))]
    [InlineData("""{"type":"boolean"}""", typeof(BooleanPropertySchema))]
    [InlineData("""{"type":"color"}""", typeof(ElicitationPropertySchemaUnknown))]
    public void A_property_schema_reads_as_the_variant_its_tag_names(string json, Type expected)
    {
        Assert.IsType(expected, Read<ElicitationPropertySchema>(json));
    }

    [Fact]
    public void A_property_schema_without_a_tag_is_rejected()
    {
        // ElicitationPropertySchema has no untagged branch: every property schema names its type.
        Assert.Throws<JsonException>(() => Read<ElicitationPropertySchema>("""{"title":"x"}"""));
    }

    [Fact]
    public async Task A_multi_select_with_an_unknown_item_type_is_shown_as_a_text_field()
    {
        var broker = new ElicitationBroker();
        AcpElicitationRequest? shown = null;
        broker.ElicitationRequested += r =>
        {
            shown = r;
            broker.Resolve(r.RequestId, AcpElicitationResult.Cancelled());
        };
        var handler = new ElicitationHandler(broker, NullLogger<ElicitationHandler>.Instance);
        var request = Read<CreateElicitationRequest>("""
            {"mode":"form","message":"Pick","requestedSchema":{"type":"object","required":["colors"],"properties":{
              "colors":{"type":"array","title":"Colours","items":{"type":"color","palette":"web"}}
            }}}
            """);

        await handler.HandleAsync(request, CancellationToken.None);

        var field = Assert.Single(shown!.Fields);
        Assert.Equal(AcpElicitationFieldKind.Text, field.Kind);
        Assert.True(field.Required);
    }
}
