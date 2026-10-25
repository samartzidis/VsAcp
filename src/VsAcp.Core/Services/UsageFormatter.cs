using AcpProtocol;

namespace VsAcp.Core.Services;

/// <summary>How context usage is shown: a compact toolbar readout and a longer tooltip.</summary>
public static class UsageFormatter
{
    /// <summary>
    /// For example text "12.3k / 200k (6%)  $0.42" and a two-line tooltip. With no known context size only the
    /// used tokens are shown; the cost is added when the agent reports one.
    /// </summary>
    public static (string Text, string ToolTip) Format(UsageUpdate usage)
    {
        var text = usage.Size > 0
            ? $"{FormatTokens(usage.Used)} / {FormatTokens(usage.Size)} ({Math.Round(100.0 * usage.Used / usage.Size)}%)"
            : FormatTokens(usage.Used);
        var tip = usage.Size > 0
            ? $"Context: {usage.Used:N0} of {usage.Size:N0} tokens used"
            : $"Context: {usage.Used:N0} tokens used";

        if (usage.Cost is { } cost)
        {
            text += $"  {FormatCost(cost.Amount, cost.Currency)}";
            tip += $"\nCost: {FormatCost(cost.Amount, cost.Currency)}";
        }

        return (text, tip);
    }

    public static string FormatTokens(ulong n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 1_000 => $"{n / 1_000.0:0.#}k",
        _ => n.ToString()
    };

    public static string FormatCost(double amount, string currency) =>
        string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase) ? $"${amount:0.00}" : $"{amount:0.00} {currency}";
}
