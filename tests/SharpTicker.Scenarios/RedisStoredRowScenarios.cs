using System.Text.Json;
using SharpTicker.Scenarios.Harness;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace SharpTicker.Scenarios;

/// <summary>
/// Rows in Redis that the current code did not write the way it writes today: records a version before 1.3.0 left behind,
/// written here as the raw text such a version stored. Each scenario stores the rows before the node starts, then starts
/// a real node over them and checks what ran.
/// </summary>
public class RedisStoredRowScenarios(ITestOutputHelper output)
{
    private const string Ids = "tq:tt:ids";
    private const string Pending = "tq:tt:pending";

    private static string Key(Guid id) => $"tq:tt:{id}";

    private static string Q(DateTime time) => "\"" + time.ToString("O") + "\"";

    private static async Task<TickerStatus?> StatusAsync(IDatabase db, Guid id)
    {
        var raw = (string?)await db.StringGetAsync(Key(id));
        if (raw == null) return null;
        using var doc = JsonDocument.Parse(raw);
        return (TickerStatus)doc.RootElement.GetProperty("Status").GetInt32();
    }

    private static async Task<string> DescribeAsync(ScenarioEnvironment env, IDatabase db, params Guid[] ids)
    {
        var parts = new List<string>();
        foreach (var id in ids)
            parts.Add($"{id}: {(await StatusAsync(db, id))?.ToString() ?? "no row"}, starts {env.Runs.Starts(id)}");
        return string.Join("; ", parts);
    }

    // #144: a version before 1.3.0 claimed a due time job and left it InProgress with its empty Children written as an
    // object ({}), which is what its claim script stored. A node that starts under the name that holds the row releases
    // what its earlier run left locked, and the job runs. Before the fix the row could not be read, so the start release
    // skipped it and the job stayed InProgress for good.
    [Fact]
    public async Task ATimeJobLeftInProgressByAVersionBefore130_IsReleasedWhenItsNodeStarts_AndRuns()
    {
        await using var env = await ScenarioEnvironment.StartAsync("older-row", Store.Redis, nodes: 1, startedNodes: 0);
        await using var redis = await ConnectionMultiplexer.ConnectAsync(env.DirectStoreConnection);
        var db = redis.GetDatabase();
        var now = DateTime.UtcNow;
        var locked = now.AddMinutes(-5).AddTicks(1234567);

        async Task<Guid> SeedAsync(string children)
        {
            var id = Guid.NewGuid();
            var json = "{\"Description\":null,\"Retries\":0,\"ExecutedAt\":null,\"ExceptionMessage\":null,"
                       + $"\"CreatedAt\":{Q(now.AddHours(-1))},\"Status\":2,\"UpdatedAt\":{Q(locked)},\"Children\":{children},"
                       + $"\"RetryCount\":0,\"SkippedReason\":null,\"ElapsedTime\":0,\"Id\":\"{id}\",\"ParentId\":null,\"RunCondition\":null,"
                       + $"\"Request\":\"\",\"ExecutionTime\":{Q(now.AddMinutes(-6))},\"LockHolder\":\"{env.Nodes[0].Identifier}\",\"LockedAt\":{Q(locked)},"
                       + $"\"Function\":\"{Functions.Record}\",\"RetryIntervals\":null,\"InitIdentifier\":null}}";
            await db.StringSetAsync(Key(id), json);
            await db.SetAddAsync(Ids, id.ToString());
            await db.SortedSetAddAsync(Pending, id.ToString(), now.AddMinutes(-6).Ticks);
            return id;
        }

        // The row of the older version, and the same row as this version writes it (the control inside the scenario).
        var older = await SeedAsync("{}");
        var current = await SeedAsync("[]");

        await env.StartNodeAsync(0);
        await env.UntilAsync(async () => await StatusAsync(db, current) is TickerStatus.Done or TickerStatus.DueDone,
            () => "the row written as this version writes it did not run", TimeSpan.FromSeconds(60));
        try
        {
            await env.UntilAsync(async () => await StatusAsync(db, older) is TickerStatus.Done or TickerStatus.DueDone,
                () => "", TimeSpan.FromSeconds(30));
        }
        catch (Exception)
        {
            // Reported below with what the store holds.
        }

        var description = await DescribeAsync(env, db, older, current);
        output.WriteLine(description);
        Assert.True(await StatusAsync(db, older) is TickerStatus.Done or TickerStatus.DueDone, $"the job the older version left InProgress did not run: {description}");
        Assert.Equal(1, env.Runs.Starts(older));
        Assert.Equal(1, env.Runs.Starts(current));
    }

    // #145: a version before 1.3.0 stored a chain as one record, the children inside the parent's row. The node that takes
    // such a parent writes its children as rows of their own, and they run by their conditions. Before the fix the parent
    // ran and its children were never started.
    [Fact]
    public async Task AChainStoredInsideItsParentsRowByAVersionBefore130_RunsEachNodeByItsCondition()
    {
        await using var env = await ScenarioEnvironment.StartAsync("older-chain", Store.Redis, nodes: 1, startedNodes: 0);
        await using var redis = await ConnectionMultiplexer.ConnectAsync(env.DirectStoreConnection);
        var db = redis.GetDatabase();
        var now = DateTime.UtcNow;

        string Node(Guid id, string executionTime, int? runCondition, string children)
            => "{\"Status\":0,\"LockHolder\":null,\"Request\":\"\",\"ExecutionTime\":" + executionTime
               + ",\"LockedAt\":null,\"ExecutedAt\":null,\"ExceptionMessage\":null,\"SkippedReason\":null,\"ElapsedTime\":0,"
               + "\"Retries\":0,\"RetryCount\":0,\"RetryIntervals\":null,\"ParentId\":null,\"Children\":" + children
               + ",\"RunCondition\":" + (runCondition?.ToString() ?? "null") + ",\"Id\":\"" + id + "\",\"Function\":\"" + Functions.Record + "\","
               + "\"Description\":null,\"InitIdentifier\":null,\"CreatedAt\":" + Q(now.AddHours(-1)) + ",\"UpdatedAt\":" + Q(now.AddHours(-1)) + "}";

        // parent -> onSuccess (OnSuccess) -> grandchild (OnSuccess); onFailure (OnFailure)
        Guid parent = Guid.NewGuid(), onSuccess = Guid.NewGuid(), grandchild = Guid.NewGuid(), onFailure = Guid.NewGuid();
        var parentTime = now.AddMinutes(-1);
        var text = Node(parent, Q(parentTime), null,
            "[" + Node(onSuccess, "null", (int)RunCondition.OnSuccess, "[" + Node(grandchild, "null", (int)RunCondition.OnSuccess, "[]") + "]")
            + "," + Node(onFailure, "null", (int)RunCondition.OnFailure, "[]") + "]");
        await db.StringSetAsync(Key(parent), text);
        await db.SetAddAsync(Ids, parent.ToString());
        await db.SortedSetAddAsync(Pending, parent.ToString(), parentTime.Ticks);

        await env.StartNodeAsync(0);
        await env.UntilAsync(async () => await StatusAsync(db, parent) is TickerStatus.Done or TickerStatus.DueDone,
            () => "the parent did not run", TimeSpan.FromSeconds(60));
        try
        {
            await env.UntilAsync(async () => await StatusAsync(db, grandchild) is TickerStatus.Done or TickerStatus.DueDone
                                             && await StatusAsync(db, onFailure) == TickerStatus.Skipped,
                () => "", TimeSpan.FromSeconds(60));
        }
        catch (Exception)
        {
            // Reported below with what the store holds.
        }

        // Long enough for the fallback to start something a second time if it could.
        await Task.Delay(TimeSpan.FromSeconds(6));
        var description = await DescribeAsync(env, db, parent, onSuccess, grandchild, onFailure);
        output.WriteLine(description);
        Assert.True(await StatusAsync(db, onSuccess) is TickerStatus.Done or TickerStatus.DueDone, $"the child did not run: {description}");
        Assert.True(await StatusAsync(db, grandchild) is TickerStatus.Done or TickerStatus.DueDone, $"the grandchild did not run: {description}");
        Assert.True(await StatusAsync(db, onFailure) == TickerStatus.Skipped, $"the child for a failed parent was not skipped: {description}");
        Assert.Equal([1, 1, 1, 0], new[] { parent, onSuccess, grandchild, onFailure }.Select(id => env.Runs.Starts(id)));
    }
}
