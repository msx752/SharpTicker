using SharpTicker.Scenarios.Harness;
using Xunit.Abstractions;

namespace SharpTicker.Scenarios;

/// <summary>The seed of the crons defined in code, at a node's start.</summary>
public class CronSeedScenarios(ITestOutputHelper output)
{
    // #92 (#102): a user's cron next to the seeded cron of the same function. A node started with a new expression in code
    // brings only the seeded cron to it. Before the fix Redis failed to start on the second cron of a function, and EF Core
    // and MongoDB updated whichever cron of the function came first, which could be the user's. Redis only: on EF Core and
    // MongoDB the seeded cron, stored first, is also read first, so the code before the fix passes there as well and the
    // scenario would prove nothing; the unit tests of #102 cover those stores.
    [Fact]
    public async Task ANewExpressionInCode_LeavesAUsersCronOfTheSameFunctionAlone()
    {
        const Store store = Store.Redis;
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

        // Read until node 1's seed shows, not once: the dashboard answering does not by itself say the seed is done.
        List<CronView> crons = [];
        await env.UntilAsync(async () => (crons = await SeededCrons(env, 1)).Any(c => c.Expression == newCode),
            () => $"node 1 did not bring the seeded cron to the new expression: {string.Join("; ", crons.Select(c => c.Expression))}",
            TimeSpan.FromSeconds(30));
        output.WriteLine(string.Join("; ", crons.Select(c => $"{c.Id} {c.Expression}")));
        Assert.Equal(2, crons.Count);
        Assert.Single(crons, c => c.Expression == newCode);
        Assert.Single(crons, c => c.Expression == users);
    }

    // #92: replicas of a new deployment start together, each finds the code-defined cron missing and inserts it. Before
    // the fix every node inserted a row of its own under a random id, and the job then ran once per row on every tick.
    // Node 0 has no cron in code: it creates the schema and reads the store. The other four start at the same moment,
    // with every answer of the store one second late, so that each has read before any of them has written.
    [Theory]
    [InlineData(Store.EfCorePostgres)]
    [InlineData(Store.MongoDb)]
    [InlineData(Store.Redis)]
    public async Task NodesThatStartTogether_SeedACodeDefinedCronOnce(Store store)
    {
        var expression = "0 0 0" + Crons.Far[5..];
        await using var env = await ScenarioEnvironment.StartAsync("cron-seed-together", store, nodes: 5, startedNodes: 1,
            nodeEnvironment: i => i == 0 ? new Dictionary<string, string>() : new Dictionary<string, string> { ["SCENARIO_SEEDED_CRON"] = expression });

        await env.AddStoreToxicAsync("latency", new { latency = 1000 }, "downstream");
        await Task.WhenAll(Enumerable.Range(1, 4).Select(env.StartNodeAsync));
        await env.RemoveStoreToxicsAsync();

        List<CronView> crons = [];
        await env.UntilAsync(async () => (crons = await SeededCrons(env, 0)).Count > 0,
            () => "no node seeded the cron", TimeSpan.FromSeconds(60));
        // A node that is still seeding would add its row within its next store calls; the store answers at once again.
        await Task.Delay(TimeSpan.FromSeconds(10));
        crons = await SeededCrons(env, 0);
        output.WriteLine(string.Join("; ", crons.Select(c => $"{c.Id} {c.Expression}")));
        Assert.Single(crons);
    }

    private static async Task<List<CronView>> SeededCrons(ScenarioEnvironment env, int node)
        => (await env.Nodes[node].Dashboard.GetCronsAsync()).Where(c => c.Function == Functions.Seeded).ToList();
}
