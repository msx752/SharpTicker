using SharpTicker.Scenarios.Harness;
using Xunit.Abstractions;

namespace SharpTicker.Scenarios;

/// <summary>The seed of the crons defined in code, at a node's start.</summary>
public class CronSeedScenarios(ITestOutputHelper output)
{
    // #92 (#102): a user's cron next to the seeded cron of the same function. A node started with a new expression in code
    // brings only the seeded cron to it. Before the fix Redis failed to start on the second cron of a function, and EF Core
    // and MongoDB updated whichever cron of the function came first, which could be the user's. Redis only: on EF Core and
    // MongoDB the seeded cron, stored first, also comes first, so the code before the fix passes there as well (see
    // deferred-scenarios).
    [Theory]
    [InlineData(Store.Redis)]
    public async Task ANewExpressionInCode_LeavesAUsersCronOfTheSameFunctionAlone(Store store)
    {
        // Far away (about half a year), so none of them runs during the scenario. Node 0 has the old code, node 1 the new.
        var tail = Crons.Far[5..];
        string oldCode = "0 0 0" + tail, newCode = "0 30 0" + tail, users = "0 15 0" + tail;
        await using var env = await ScenarioEnvironment.StartAsync("cron-seed", store, nodes: 2, startedNodes: 1,
            nodeEnvironment: i => new Dictionary<string, string> { ["SCENARIO_SEEDED_CRON"] = i == 0 ? oldCode : newCode });

        await env.UntilAsync(async () => (await SeededCrons(env, 0)).Any(c => c.Expression == oldCode),
            () => "node 0 did not seed its cron", TimeSpan.FromSeconds(30));
        await env.Nodes[0].Api.AddCronAsync(Functions.Seeded, users);
        await env.StopNodeAsync(0);

        // The new code starts. Before the fix a Redis node throws here, at the seed, on every start.
        await env.StartNodeAsync(1);

        var crons = await SeededCrons(env, 1);
        output.WriteLine(string.Join("; ", crons.Select(c => $"{c.Id} {c.Expression}")));
        Assert.Equal(2, crons.Count);
        Assert.Single(crons, c => c.Expression == newCode);
        Assert.Single(crons, c => c.Expression == users);
    }

    private static async Task<List<CronView>> SeededCrons(ScenarioEnvironment env, int node)
        => (await env.Nodes[node].Dashboard.GetCronsAsync()).Where(c => c.Function == Functions.Seeded).ToList();
}
