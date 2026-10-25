using System.Text.Json;

namespace VsAcp.Core.Abstractions;

/// <summary>Input control to render for an elicitation form field.</summary>
public enum AcpElicitationFieldKind
{
    Text,
    Boolean,
    Integer,
    Number,
    /// <summary>Single choice from <see cref="AcpElicitationField.Options"/>.</summary>
    Select,
    /// <summary>Zero or more choices from <see cref="AcpElicitationField.Options"/>.</summary>
    MultiSelect
}

public sealed class AcpElicitationOption
{
    public required string Value { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
}

/// <summary>One form field, translated from the agent's JSON-Schema property so the UI needs no schema knowledge.</summary>
public sealed class AcpElicitationField
{
    public required string Name { get; init; }
    public required AcpElicitationFieldKind Kind { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public bool Required { get; init; }

    /// <summary>Hint such as "email" or "date" for text fields, from the schema <c>format</c>.</summary>
    public string? Format { get; init; }
    public string? Pattern { get; init; }
    public uint? MinLength { get; init; }
    public uint? MaxLength { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public ulong? MinItems { get; init; }
    public ulong? MaxItems { get; init; }

    /// <summary>Choices for <see cref="AcpElicitationFieldKind.Select"/> / <see cref="AcpElicitationFieldKind.MultiSelect"/>.</summary>
    public IReadOnlyList<AcpElicitationOption>? Options { get; init; }

    public string? DefaultText { get; init; }
    public bool? DefaultBoolean { get; init; }
    public double? DefaultNumber { get; init; }
    public IReadOnlyList<string>? DefaultValues { get; init; }
}

/// <summary>
/// An agent request for structured user input (ACP <c>elicitation/create</c>). Either a form
/// (<see cref="Fields"/>) or a URL the user must visit (<see cref="Url"/>).
/// </summary>
public sealed class AcpElicitationRequest
{
    public required string RequestId { get; init; }
    public required string Message { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? ToolCallId { get; init; }

    /// <summary>Set for URL-mode elicitations; <see cref="Fields"/> is then empty.</summary>
    public string? Url { get; init; }
    public IReadOnlyList<AcpElicitationField> Fields { get; init; } = [];

    public bool IsUrlMode => Url is not null;
}

public enum AcpElicitationAction
{
    Accept,
    Decline,
    Cancel
}

public sealed class AcpElicitationResult
{
    public required AcpElicitationAction Action { get; init; }

    /// <summary>Field name → value; only for <see cref="AcpElicitationAction.Accept"/> on a form.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Values { get; init; }

    public static AcpElicitationResult Accepted(IReadOnlyDictionary<string, JsonElement>? values = null)
        => new() { Action = AcpElicitationAction.Accept, Values = values };
    public static AcpElicitationResult Declined() => new() { Action = AcpElicitationAction.Decline };
    public static AcpElicitationResult Cancelled() => new() { Action = AcpElicitationAction.Cancel };
}

/// <summary>
/// Hands elicitation requests from the ACP handler to the UI and returns the user's answer.
/// Same shape as <see cref="IPermissionBroker"/>: the handler awaits <see cref="SubmitAsync"/>,
/// the UI calls <see cref="Resolve"/>.
/// </summary>
public interface IElicitationBroker
{
    event Action<AcpElicitationRequest>? ElicitationRequested;

    /// <summary>Raised when the agent withdraws an elicitation (<c>elicitation/complete</c>) so the UI can close it.</summary>
    event Action<string>? ElicitationDismissed;

    Task<AcpElicitationResult> SubmitAsync(AcpElicitationRequest request, CancellationToken cancellationToken);
    void Resolve(string requestId, AcpElicitationResult result);

    /// <summary>Agent-side completion: resolves the pending request as cancelled and raises <see cref="ElicitationDismissed"/>.</summary>
    void Dismiss(string requestId);

    /// <summary>Resolves every pending request as cancelled (user pressed Stop).</summary>
    void CancelAll();
}
