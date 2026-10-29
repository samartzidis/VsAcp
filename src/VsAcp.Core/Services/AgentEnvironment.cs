namespace VsAcp.Core.Services;

/// <summary>
/// The extra environment variables a preset's agent process starts with: the name / value rows of the preset's
/// list in Tools &gt; Options &gt; ACP Agent.
/// </summary>
public static class AgentEnvironment
{
    /// <summary>
    /// Turns the rows into variables. Names and values are trimmed. A row with an empty name, or a name containing
    /// '=' (which Windows cannot hold), is ignored, and a later row replaces an earlier one with the same name
    /// (Windows names ignore case). An empty value is kept: it sets the variable to the empty string.
    /// </summary>
    public static Dictionary<string, string> Build(IEnumerable<KeyValuePair<string?, string?>>? rows)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (rows is null)
            return result;

        foreach (var row in rows)
        {
            var name = row.Key?.Trim();
            if (string.IsNullOrEmpty(name) || name!.IndexOf('=') >= 0)
                continue;
            result[name] = row.Value?.Trim() ?? "";
        }
        return result;
    }
}
