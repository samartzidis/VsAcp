using System.Text.Json;
using VsAcp.Core.Abstractions;
using VsAcp.Core.Services;

namespace VsAcp.Bridge;

/// <summary>The answer to the revert confirmation: Revert or Cancel, and whether "Also revert other changes" was ticked.</summary>
internal readonly record struct RevertAnswer(bool Confirmed, bool IncludeOther);

/// <summary>
/// Builds the <c>banner.show</c> payloads the page renders (permission, revert confirmation, elicitation,
/// error) and parses the answers it posts back. Shapes match Web/src/bridge/protocol.ts.
/// </summary>
internal static class BannerMessages
{
    /// <summary>Keeps the page responsive on huge reverts; the summary counts are always exact.</summary>
    internal const int MaxRevertFilesListed = 500;

    internal static object Show(object banner) => new { type = "banner.show", banner };

    internal static object Permission(string id, AcpPermissionRequest request) => new
    {
        kind = "permission",
        id,
        title = request.Title,
        options = request.Options.Select(o => new { optionId = o.OptionId, name = o.Name, kind = o.Kind }).ToList(),
    };

    internal static object Error(string id, string message) => new { kind = "error", id, message };

    /// <param name="preview">Null when the file list could not be computed (the page shows a generic prompt).</param>
    internal static object Revert(string id, RevertPreview? preview)
    {
        if (preview is null) return new { kind = "revert", id, preview = (object?)null };

        return new
        {
            kind = "revert",
            id,
            preview = new
            {
                agent = RevertGroup(preview.Agent),
                other = preview.Other.HasChanges ? RevertGroup(preview.Other) : null,
                agentFilesKnown = preview.AgentFilesKnown,
            },
        };
    }

    private static object RevertGroup(RestorePreview group)
    {
        // Deletions first: they are the destructive ones.
        var files = group.Deleted.Select(p => new { action = "delete", path = p })
            .Concat(group.Modified.Select(p => new { action = "overwrite", path = p }))
            .Concat(group.Recreated.Select(p => new { action = "recreate", path = p }))
            .ToList();

        return new
        {
            deleted = group.Deleted.Count,
            overwritten = group.Modified.Count,
            recreated = group.Recreated.Count,
            files = files.Take(MaxRevertFilesListed).ToList(),
            omitted = Math.Max(0, files.Count - MaxRevertFilesListed),
        };
    }

    internal static object Elicitation(string id, AcpElicitationRequest request) => new
    {
        kind = "elicitation",
        id,
        title = request.Title,
        message = request.Message,
        description = request.Description,
        url = request.IsUrlMode ? request.Url : null,
        fields = request.Fields.Select(Field).ToList(),
    };

    private static object Field(AcpElicitationField f) => new
    {
        name = f.Name,
        kind = f.Kind switch
        {
            AcpElicitationFieldKind.Boolean => "boolean",
            AcpElicitationFieldKind.Integer => "integer",
            AcpElicitationFieldKind.Number => "number",
            AcpElicitationFieldKind.Select => "select",
            AcpElicitationFieldKind.MultiSelect => "multiSelect",
            _ => "text",
        },
        title = f.Title,
        description = f.Description,
        required = f.Required,
        format = f.Format,
        pattern = f.Pattern,
        minLength = f.MinLength,
        maxLength = f.MaxLength,
        minimum = f.Minimum,
        maximum = f.Maximum,
        minItems = f.MinItems,
        maxItems = f.MaxItems,
        options = f.Options?.Select(o => new { value = o.Value, title = o.Title, description = o.Description }).ToList(),
        defaultText = f.DefaultText,
        defaultBoolean = f.DefaultBoolean,
        defaultNumber = f.DefaultNumber,
        defaultValues = f.DefaultValues,
    };

    // ---- answers ----

    internal static string? ParsePermission(JsonElement result) =>
        result.TryGetProperty("optionId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;

    internal static RevertAnswer ParseRevert(JsonElement result) => new(
        result.TryGetProperty("confirmed", out var c) && c.ValueKind == JsonValueKind.True,
        result.TryGetProperty("includeOther", out var o) && o.ValueKind == JsonValueKind.True);

    /// <summary>Unknown or missing actions are treated as a cancel, the safe answer.</summary>
    internal static AcpElicitationResult ParseElicitation(JsonElement result)
    {
        var action = result.TryGetProperty("action", out var a) ? a.GetString() : null;
        switch (action)
        {
            case "accept":
                // A form answers with its values (possibly an empty object: the agent still gets `content: {}`);
                // URL mode has none and answers with a bare accept.
                if (!result.TryGetProperty("values", out var v) || v.ValueKind != JsonValueKind.Object)
                    return AcpElicitationResult.Accepted();
                var values = new Dictionary<string, JsonElement>();
                foreach (var p in v.EnumerateObject())
                    values[p.Name] = p.Value.Clone();
                return AcpElicitationResult.Accepted(values);
            case "decline":
                return AcpElicitationResult.Declined();
            default:
                return AcpElicitationResult.Cancelled();
        }
    }
}
