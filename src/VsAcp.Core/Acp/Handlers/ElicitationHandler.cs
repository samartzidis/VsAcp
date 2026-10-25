using System.Text.Json;
using Microsoft.Extensions.Logging;
using VsAcp.Core.Abstractions;
using AcpProtocol;

namespace VsAcp.Core.Acp.Handlers;

/// <summary>
/// Handles <c>elicitation/create</c>: translates the agent's JSON-Schema form (or URL) into an
/// <see cref="AcpElicitationRequest"/> the UI can render, waits for the user's answer through
/// <see cref="IElicitationBroker"/>, and maps it back to a <see cref="CreateElicitationResponse"/>.
/// </summary>
public sealed class ElicitationHandler
{
    readonly IElicitationBroker _broker;
    readonly ILogger<ElicitationHandler> _logger;

    public ElicitationHandler(IElicitationBroker broker, ILogger<ElicitationHandler> logger)
    {
        _broker = broker;
        _logger = logger;
    }

    public async ValueTask<CreateElicitationResponse> HandleAsync(CreateElicitationRequest request, CancellationToken cancellationToken)
    {
        // Only URL-mode elicitations carry an agent id (they can be completed out of band); form
        // elicitations are plain request/response, so give them a local id for the broker.
        var requestId = request.ElicitationId ?? Guid.NewGuid().ToString("N");

        _logger.LogInformation("Elicitation request: mode={Mode} id={Id} message='{Message}'", request.Mode, requestId, request.Message);

        AcpElicitationRequest uiRequest;
        switch (request.Mode)
        {
            case "form" when request.RequestedSchema is not null:
                uiRequest = new AcpElicitationRequest
                {
                    RequestId = requestId,
                    Message = request.Message,
                    Title = request.RequestedSchema.Title,
                    Description = request.RequestedSchema.Description,
                    ToolCallId = request.ToolCallId,
                    Fields = BuildFields(request.RequestedSchema)
                };
                break;

            case "url" when request.Url is not null:
                uiRequest = new AcpElicitationRequest
                {
                    RequestId = requestId,
                    Message = request.Message,
                    ToolCallId = request.ToolCallId,
                    Url = request.Url
                };
                break;

            default:
                // Unknown or malformed mode: the spec says not to render it as a known mode.
                _logger.LogWarning("Elicitation mode '{Mode}' not supported; cancelling", request.Mode);
                return new CreateElicitationResponse { Action = "cancel" };
        }

        var result = await _broker.SubmitAsync(uiRequest, cancellationToken);

        switch (result.Action)
        {
            case AcpElicitationAction.Accept:
                _logger.LogInformation("Elicitation {Id} accepted with {Count} value(s)", requestId, result.Values?.Count ?? 0);
                return new CreateElicitationResponse
                {
                    Action = "accept",
                    Content = result.Values is not null ? JsonSerializer.SerializeToElement(result.Values) : null
                };

            case AcpElicitationAction.Decline:
                _logger.LogInformation("Elicitation {Id} declined by user", requestId);
                return new CreateElicitationResponse { Action = "decline" };

            default:
                _logger.LogInformation("Elicitation {Id} cancelled", requestId);
                return new CreateElicitationResponse { Action = "cancel" };
        }
    }

    public ValueTask CompleteAsync(CompleteElicitationNotification notification)
    {
        _logger.LogInformation("Elicitation {Id} completed by agent", notification.ElicitationId);
        _broker.Dismiss(notification.ElicitationId);
        return default;
    }

    /// <summary>
    /// Converts the schema's <c>properties</c> map into fields, dispatching on each property's JSON
    /// Schema <c>type</c> and deserializing the matching generated record for the details.
    /// </summary>
    List<AcpElicitationField> BuildFields(ElicitationSchema schema)
    {
        var fields = new List<AcpElicitationField>();
        if (schema.Properties.ValueKind != JsonValueKind.Object)
            return fields;

        var required = new HashSet<string>(schema.Required ?? []);
        var options = AcpJsonSerializerContext.Default.Options;

        foreach (var prop in schema.Properties.EnumerateObject())
        {
            var el = prop.Value;
            var type = el.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var isRequired = required.Contains(prop.Name);

            try
            {
                switch (type)
                {
                    case "string":
                    {
                        var s = el.Deserialize<StringPropertySchema>(options)!;
                        var choices = s.OneOf?.Select(o => new AcpElicitationOption { Value = o.Const, Title = o.Title, Description = o.Description }).ToList()
                                   ?? s.Enum?.Select(v => new AcpElicitationOption { Value = v, Title = v }).ToList();
                        fields.Add(new AcpElicitationField
                        {
                            Name = prop.Name,
                            Kind = choices is not null ? AcpElicitationFieldKind.Select : AcpElicitationFieldKind.Text,
                            Title = s.Title,
                            Description = s.Description,
                            Required = isRequired,
                            Format = s.Format?.ToString().ToLowerInvariant(),
                            Pattern = s.Pattern,
                            MinLength = s.MinLength,
                            MaxLength = s.MaxLength,
                            Options = choices,
                            DefaultText = s.Default
                        });
                        break;
                    }
                    case "boolean":
                    {
                        var b = el.Deserialize<BooleanPropertySchema>(options)!;
                        fields.Add(new AcpElicitationField
                        {
                            Name = prop.Name,
                            Kind = AcpElicitationFieldKind.Boolean,
                            Title = b.Title,
                            Description = b.Description,
                            Required = isRequired,
                            DefaultBoolean = b.Default
                        });
                        break;
                    }
                    case "integer":
                    {
                        var i = el.Deserialize<IntegerPropertySchema>(options)!;
                        fields.Add(new AcpElicitationField
                        {
                            Name = prop.Name,
                            Kind = AcpElicitationFieldKind.Integer,
                            Title = i.Title,
                            Description = i.Description,
                            Required = isRequired,
                            Minimum = i.Minimum,
                            Maximum = i.Maximum,
                            DefaultNumber = i.Default
                        });
                        break;
                    }
                    case "number":
                    {
                        var n = el.Deserialize<NumberPropertySchema>(options)!;
                        fields.Add(new AcpElicitationField
                        {
                            Name = prop.Name,
                            Kind = AcpElicitationFieldKind.Number,
                            Title = n.Title,
                            Description = n.Description,
                            Required = isRequired,
                            Minimum = n.Minimum,
                            Maximum = n.Maximum,
                            DefaultNumber = n.Default
                        });
                        break;
                    }
                    case "array":
                    {
                        var m = el.Deserialize<MultiSelectPropertySchema>(options)!;
                        var choices = m.Items switch
                        {
                            TitledMultiSelectItems titled => titled.AnyOf.Select(o => new AcpElicitationOption { Value = o.Const, Title = o.Title, Description = o.Description }).ToList(),
                            StringMultiSelectItems plain => plain.Enum.Select(v => new AcpElicitationOption { Value = v, Title = v }).ToList(),
                            _ => new List<AcpElicitationOption>()
                        };
                        fields.Add(new AcpElicitationField
                        {
                            Name = prop.Name,
                            Kind = AcpElicitationFieldKind.MultiSelect,
                            Title = m.Title,
                            Description = m.Description,
                            Required = isRequired,
                            MinItems = m.MinItems,
                            MaxItems = m.MaxItems,
                            Options = choices,
                            DefaultValues = m.Default
                        });
                        break;
                    }
                    default:
                        _logger.LogWarning("Elicitation field '{Name}' has unsupported type '{Type}'; rendering as text", prop.Name, type);
                        fields.Add(new AcpElicitationField { Name = prop.Name, Kind = AcpElicitationFieldKind.Text, Title = prop.Name, Required = isRequired });
                        break;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Elicitation field '{Name}' schema could not be parsed; rendering as text", prop.Name);
                fields.Add(new AcpElicitationField { Name = prop.Name, Kind = AcpElicitationFieldKind.Text, Title = prop.Name, Required = isRequired });
            }
        }

        return fields;
    }
}
