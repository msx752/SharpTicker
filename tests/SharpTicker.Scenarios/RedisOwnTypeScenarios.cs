using System.Text.Json;
using SharpTicker.Scenarios.Harness;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace SharpTicker.Scenarios;

/// <summary>
/// An application with a time ticker type of its own on Redis (the node runs with <c>SCENARIO_OWN_TYPES=1</c>): a list
/// property, and a property of an abstract type. The jobs are added through the manager; the scenarios read the store
/// directly, because a row the node cannot read is not in its lists.
/// </summary>
public class RedisOwnTypeScenarios(ITestOutputHelper output)
{
    private static readonly IReadOnlyDictionary<string, string> OwnTypes = new Dictionary<string, string> { ["SCENARIO_OWN_TYPES"] = "1" };

    private static Task<ScenarioEnvironment> StartAsync(string name)
        => ScenarioEnvironment.StartAsync(name, Store.Redis, nodes: 1, nodeEnvironment: _ => OwnTypes);

    private static async Task<TickerStatus?> StatusAsync(IDatabase db, Guid id)
    {
        var raw = (string?)await db.StringGetAsync($"tq:tt:{id}");
        if (raw == null) return null;
        using var doc = JsonDocument.Parse(raw);
        return (TickerStatus)doc.RootElement.GetProperty("Status").GetInt32();
    }

    private static async Task<bool> EndedAsync(IDatabase db, Guid id) => await StatusAsync(db, id) is TickerStatus.Done or TickerStatus.DueDone;

    private static async Task<string> DescribeAsync(ScenarioEnvironment env, IDatabase db, params (string Name, Guid Id)[] jobs)
    {
        var parts = new List<string>();
        foreach (var (name, id) in jobs)
            parts.Add($"{name}: {(await StatusAsync(db, id))?.ToString() ?? "no row"}, starts {env.Runs.Starts(id)}");
        return string.Join("; ", parts);
    }

    // #146: the claim script rewrites the row, and that rewrite stores an empty list as an empty object. A job whose own
    // list is empty then came back from its claim unreadable: it was claimed and never handed out. Now it runs like the
    // job whose list is filled.
    [Fact]
    public async Task AJobOfAnOwnTypeWithAnEmptyList_Runs_LikeOneWhoseListIsFilled()
    {
        await using var env = await StartAsync("own-empty-list");
        await using var redis = await ConnectionMultiplexer.ConnectAsync(env.DirectStoreConnection);
        var db = redis.GetDatabase();

        var ids = await env.Nodes[0].Api.AddOwnTimeTickersAsync(
            new OwnSeed { ExecutionTime = DateTime.UtcNow.AddSeconds(3) },
            new OwnSeed { ExecutionTime = DateTime.UtcNow.AddSeconds(3), Tags = ["a"] });
        var (empty, filled) = (ids[0], ids[1]);

        await env.UntilAsync(() => EndedAsync(db, filled), () => "the job whose list is filled did not run", TimeSpan.FromSeconds(40));
        try
        {
            await env.UntilAsync(() => EndedAsync(db, empty), () => "", TimeSpan.FromSeconds(30));
        }
        catch (Exception)
        {
            // Reported below with what the store holds.
        }

        // Long enough for the fallback to take one of them again if it could.
        await Task.Delay(TimeSpan.FromSeconds(6));
        var description = await DescribeAsync(env, db, ("empty list", empty), ("filled list", filled));
        output.WriteLine(description);
        Assert.True(await EndedAsync(db, empty), $"the job whose list is empty did not run: {description}");
        Assert.Equal([1, 1], new[] { empty, filled }.Select(id => env.Runs.Starts(id)));
    }

    // #150: a job that holds a value of an abstract type is stored and cannot be read back. One such row made every read
    // that loaded it throw, and the scheduler loop and the fallback stopped for all jobs. Now the other jobs run, the ones
    // due later and the ones already overdue, and the row that cannot be read is never started.
    [Fact]
    public async Task OneJobThatCannotBeReadBack_DoesNotKeepTheOtherJobsFromRunning()
    {
        await using var env = await StartAsync("own-unreadable");
        await using var redis = await ConnectionMultiplexer.ConnectAsync(env.DirectStoreConnection);
        var db = redis.GetDatabase();

        var ids = await env.Nodes[0].Api.AddOwnTimeTickersAsync(
            // Its list is filled: an empty one would make the row unreadable in the way of #146 as well, and that path
            // leaves the row out by itself.
            new OwnSeed { ExecutionTime = DateTime.UtcNow.AddSeconds(4), WithShape = true, Tags = ["unreadable"] },
            new OwnSeed { ExecutionTime = DateTime.UtcNow.AddSeconds(3), Tags = ["before"] },
            new OwnSeed { ExecutionTime = DateTime.UtcNow.AddSeconds(4), Tags = ["same second"] },
            new OwnSeed { ExecutionTime = DateTime.UtcNow.AddSeconds(8), Tags = ["after"] });
        var (unreadable, before, same, after) = (ids[0], ids[1], ids[2], ids[3]);
        Guid[] others = [before, same, after];

        try
        {
            await env.UntilAsync(async () =>
            {
                foreach (var id in others)
                    if (!await EndedAsync(db, id)) return false;
                return true;
            }, () => "", TimeSpan.FromSeconds(60));
        }
        catch (Exception)
        {
            // Reported below with what the store holds.
        }

        // Long enough for several fallback passes to meet the row that cannot be read.
        await Task.Delay(TimeSpan.FromSeconds(8));
        var description = await DescribeAsync(env, db, ("unreadable", unreadable), ("before", before), ("same second", same), ("after", after));
        output.WriteLine(description);
        foreach (var id in others)
            Assert.True(await EndedAsync(db, id), $"a job next to the row that cannot be read did not run: {description}");
        Assert.Equal([0, 1, 1, 1], new[] { unreadable, before, same, after }.Select(id => env.Runs.Starts(id)));
        // The row is there, not ended: the node could not read it.
        Assert.True(await StatusAsync(db, unreadable) is TickerStatus.Idle or TickerStatus.Queued or TickerStatus.InProgress, description);
    }
}
