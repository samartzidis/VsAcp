using System.Diagnostics;
using VsAcp.Core.Abstractions;
using VsAcp.Core.Configuration;
using VsAcp.Core.Services;
using VsAcp.Core.Tests.Support;
using Xunit.Abstractions;

namespace VsAcp.Core.Tests;

/// <summary>
/// Skipped unless <c>VSACP_REAL_AGENT=1</c>: the test talks to the real agent on the user's account, spending
/// real quota, and runs <c>npx @agentclientprotocol/claude-agent-acp --yes</c>, which fetches and executes
/// whatever that package resolves to over the network.
/// <para>
/// <b>Never set VSACP_REAL_AGENT in CI, a shell profile, a task, or any other automated or persistent place.</b>
/// It must only ever be set by hand for a single local run. Nothing in this repo does today - keep it that way;
/// if you're about to wire it into a workflow, task, or script, don't - run the test by hand instead.
/// </para>
/// </summary>
public sealed class RealAgentFactAttribute : FactAttribute
{
    public RealAgentFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VSACP_REAL_AGENT") != "1")
            Skip = "Runs the real claude-agent-acp (npx) on the user's Claude account; set VSACP_REAL_AGENT=1 to run it.";
    }
}

/// <summary>
/// Opt-in checks against the real claude-agent-acp, for agent behaviour the fake agent can only assume.
/// Needs Node/npx and a signed-in Claude Code; each test makes a few short prompts. See
/// <see cref="RealAgentFactAttribute"/> for why this must stay a manual, local-only run.
/// </summary>
public class RealAgentTests
{
    private const int TurnTimeoutMs = 180000;
    private readonly ITestOutputHelper _output;

    public RealAgentTests(ITestOutputHelper output)
    {
        _output = output;
        // xunit makes a fresh instance per test method, so this fires once per real-agent test - every test
        // gets the warning without having to print it itself.
        if (Environment.GetEnvironmentVariable("VSACP_REAL_AGENT") == "1")
            _output.WriteLine("*** VSACP_REAL_AGENT=1: calling the real claude-agent-acp on the signed-in account. ***");
    }

    private static void ClaudeAgentAcp(VsAcpOptions o)
    {
        o.AgentCommand = "npx";
        o.AgentArgs = ["@agentclientprotocol/claude-agent-acp", "--yes"];
    }

    private static string LastAnswer(ChatHarness h) =>
        h.Messages.Last(m => m.Type == ChatItemType.Assistant).Content ?? "";

    private async Task<TimeSpan> TimedSendAsync(ChatHarness h, string text)
    {
        var clock = Stopwatch.StartNew();
        Assert.Equal(TurnResult.Completed, await h.SendWithinAsync(text, TurnTimeoutMs));
        _output.WriteLine($"{clock.Elapsed.TotalSeconds,6:F1} s  {text}\n         -> {LastAnswer(h)}");
        return clock.Elapsed;
    }

    [RealAgentFact]
    public async Task After_a_revert_the_agent_keeps_running_and_still_knows_the_kept_conversation()
    {
        using var h = await ChatHarness.CreateAsync(configure: ClaudeAgentAcp);

        var cold = await TimedSendAsync(h, "Remember the word pelican. Do not use any tools. Reply with just: OK");
        await TimedSendAsync(h, "Now remember the word walrus instead. Do not use any tools. Reply with just: OK");
        await h.RevertToUserMessageAsync(1);
        var afterRevert = await TimedSendAsync(h,
            "Which word did I ask you to remember? Do not use any tools. Reply with just that word.");

        _output.WriteLine($"cold start + first turn {cold.TotalSeconds:F1} s, first turn after revert {afterRevert.TotalSeconds:F1} s");
        Assert.Contains("pelican", LastAnswer(h), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("walrus", LastAnswer(h), StringComparison.OrdinalIgnoreCase);
        Assert.Single(h.Log.Lines, l => l.Contains("Starting agent process"));
        Assert.Contains(h.Log.Lines, l => l.Contains("Closed ACP session"));
        Assert.DoesNotContain(h.Log.Lines, l => l.Contains("did not open a new session"));
    }

    [RealAgentFact]
    public async Task Reverts_fork_the_agent_session_and_the_fork_of_a_fork_still_knows_only_the_kept_conversation()
    {
        using var h = await ChatHarness.CreateAsync(configure: ClaudeAgentAcp);

        await TimedSendAsync(h, "Remember the word pelican. Do not use any tools. Reply with just: OK");
        await TimedSendAsync(h, "Now remember the word walrus instead. Do not use any tools. Reply with just: OK");
        await h.RevertToUserMessageAsync(1);
        // A turn in the forked session, then a revert of it: the second fork starts from the first one's copy.
        await TimedSendAsync(h, "Now remember the word heron instead. Do not use any tools. Reply with just: OK");
        await h.RevertToUserMessageAsync(1);
        await TimedSendAsync(h, "Which word did I ask you to remember? Do not use any tools. Reply with just that word.");

        var entry = await h.IndexEntryAsync();
        _output.WriteLine($"chat title: {entry.Title}, agent session: {entry.AcpSessionId}");
        Assert.Contains("pelican", LastAnswer(h), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("walrus", LastAnswer(h), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("heron", LastAnswer(h), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, h.Log.Lines.Count(l => l.Contains("forked from")));
        Assert.DoesNotContain(h.Log.Lines, l => l.Contains("prepending pending context prefix"));
        Assert.Single(h.Log.Lines, l => l.Contains("Starting agent process"));
        Assert.DoesNotContain("(fork)", entry.Title);
    }
}
