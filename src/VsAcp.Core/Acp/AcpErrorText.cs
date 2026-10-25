using System.Text.Json;
using AcpProtocol;

namespace VsAcp.Core.Acp;

/// <summary>User-facing text for a failed agent request.</summary>
public static class AcpErrorText
{
    /// <summary>
    /// The agent's own explanation when its JSON-RPC error carries one (<c>data.details</c>, e.g. claude-agent-acp's
    /// "API error: 429 Usage credits are required for this model"), else the exception message ("Internal error").
    /// </summary>
    public static string Describe(Exception ex)
    {
        if (ex is AcpException { ErrorData: { ValueKind: JsonValueKind.Object } data }
            && data.TryGetProperty("details", out var details)
            && details.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(details.GetString()))
            return details.GetString()!;
        return ex.Message;
    }
}
