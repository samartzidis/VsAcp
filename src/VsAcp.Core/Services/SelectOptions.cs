using System.Text.Json;
using AcpProtocol;

namespace VsAcp.Core.Services;

/// <summary>Reads the choices of an ACP select config option (for example the model picker).</summary>
public static class SelectOptions
{
    /// <summary>
    /// Flattens the <c>options</c> of a select config option into one list. The agent may send plain options,
    /// groups of options, or a mix; groups are flattened in order. Best effort: an entry (or a whole group) that
    /// cannot be read is skipped and the others are kept, and a value that is not an array gives an empty list.
    /// </summary>
    public static List<SessionConfigSelectOption> Parse(JsonElement optionsElement)
    {
        var result = new List<SessionConfigSelectOption>();
        if (optionsElement.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in optionsElement.EnumerateArray())
        {
            try
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                if (item.TryGetProperty("options", out _))
                {
                    var group = JsonSerializer.Deserialize<SessionConfigSelectGroup>(
                        item.GetRawText(), AcpJsonSerializerContext.Default.Options);
                    if (group?.Options is not null)
                        result.AddRange(group.Options.Where(o => o is not null));
                }
                else
                {
                    var opt = JsonSerializer.Deserialize<SessionConfigSelectOption>(
                        item.GetRawText(), AcpJsonSerializerContext.Default.Options);
                    if (opt is not null)
                        result.Add(opt);
                }
            }
            catch (JsonException)
            {
                // Skip just this entry; the rest of the list is still usable.
            }
        }
        return result;
    }
}
