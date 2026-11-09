using SharpTicker.Scenarios.Harness;
using Xunit.Abstractions;

namespace SharpTicker.Scenarios;

/// <summary>The store goes away, hangs or drops replies while the nodes work.</summary>
public class StoreFaultScenarios(ITestOutputHelper output)
{
    private static readonly TimeSpan Recovery = TimeSpan.FromSeconds(150);

    // #47: a store that refuses or hangs does not stop the nodes, and the jobs due meanwhile run once after it.
    [Theory]
    [InlineData(Store.EfCorePostgres, "refused")]
    [InlineData(Store.EfCorePostgres, "hanging")]
    [InlineData(Store.MongoDb, "refused")]
    [InlineData(Store.MongoDb, "hanging")]
    [InlineData(Store.Redis, "refused")]
    [InlineData(Store.Redis, "hanging")]
    public async Task AStoreOutage_DoesNotStopTheNodes_AndDueJobsRunOnceAfterIt(Store store, string fault)
    {
        await using var env = await ScenarioEnvironment.StartAsync($"outage-{fault}", store, nodes: 2);

        // 20 jobs due from 3 s to 13 s from now; the store is gone from 1 s to 16 s.
        var start = DateTime.UtcNow;
        var ids = await env.Nodes[0].Api.AddTimeTickersAsync(Enumerable.Range(0, 20)
            .Select(i => new TickerSeed { Function = Functions.Record, ExecutionTime = start.AddSeconds(3 + i * 0.5) }).ToArray());

        await Task.Delay(TimeSpan.FromSeconds(1));
        var window = new FaultEvent(fault == "refused" ? "cut" : "latency") { At = DateTimeOffset.UtcNow, Until = DateTimeOffset.UtcNow.AddSeconds(15) };
        if (fault == "refused")
        {
            await env.CutStoreAsync(TimeSpan.FromSeconds(15));
        }
        else
        {
            // timeout 0: the data is held and the connection never closed.
            await env.AddStoreToxicAsync("timeout", new { timeout = 0 }, "downstream");
            await env.AddStoreToxicAsync("timeout", new { timeout = 0 }, "upstream");
            await Task.Delay(TimeSpan.FromSeconds(15));
            await env.RemoveStoreToxicsAsync();
        }

        // #47: the nodes are still running once the store is back (before the fix the scheduler's failure stopped the host).
        // The node containers have no restart policy, so a stopped host shows as a container that is not running.
        await Task.Delay(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 2; i++)
        {
            var (status, _) = await env.NodeStateAsync(i);
            Assert.True(status == "running", $"node {i} is {status} after the store outage; see {env.LogFolder}");
        }

        await AssertEveryJobRanOnceAndEnded(env, ids, finalOk: [TickerStatus.Done, TickerStatus.DueDone], window);
    }

    [Theory]
    [InlineData(Store.EfCorePostgres)]
    [InlineData(Store.MongoDb)]
    [InlineData(Store.Redis)]
    public async Task AFallbackPassThatFailsPartway_RunsEachOverdueRowOnce(Store store)
    {
        await using var env = await ScenarioEnvironment.StartAsync("fallback-partway", store, nodes: 2, startedNodes: 1);

        // #54, #100: the fallback claims overdue rows one by one while it reads them; before #54 a store call failing partway
        // dropped the rows it had claimed so far, and they stayed InProgress with no worker; before #100 a row whose put-back
        // failed inside that claim call stayed InProgress the same way (seen on EF Core). Seeded through node 0, which
        // stops before they are due, so they are overdue when node 1 starts and its fallback takes them. The store is slow
        // (each claim is a round trip). The pass starts the jobs only once it has read them all, so "partway" is: some rows
        // claimed (InProgress) and none started yet; the store connections are reset then.
        var start = DateTime.UtcNow;
        var ids = await env.Nodes[0].Api.AddTimeTickersAsync(Enumerable.Range(0, 300)
            .Select(_ => new TickerSeed { Function = Functions.Record, ExecutionTime = start.AddSeconds(3) }).ToArray());
        await env.StopNodeAsync(0);
        var overdue = start.AddSeconds(6) - DateTime.UtcNow;
        if (overdue > TimeSpan.Zero)
            await Task.Delay(overdue);

        // A slow store still lets the node start; a node started while the store fails does not come up.
        var slowFrom = DateTimeOffset.UtcNow;
        await env.AddStoreToxicAsync("latency", new { latency = 200 }, "downstream");
        await env.StartNodeAsync(1);
        // Read from the store directly: a read through the slow proxy (or the dashboard) takes longer than the pass.
        var claimed = 0;
        await Eventually.UntilAsync(async () => (claimed = await env.CountTimeTickersAsync(TickerStatus.InProgress)) > 0,
            TimeSpan.FromSeconds(60), () => "node 1's fallback claimed none of them", env, TimeSpan.FromMilliseconds(100));
        var started = ids.Count(id => env.Runs.Starts(id) > 0);
        Assert.True(started == 0 && claimed < ids.Count,
            $"not partway: {claimed} of {ids.Count} claimed and {started} started when the reset was due");
        await env.AddStoreToxicAsync("reset_peer", new { timeout = 0 }, "downstream");
        await Task.Delay(TimeSpan.FromSeconds(3));
        await env.RemoveStoreToxicsAsync();
        var window = new FaultEvent("latency") { At = slowFrom, Until = DateTimeOffset.UtcNow };
        output.WriteLine($"{claimed} of {ids.Count} claimed, none started, when the store connections were reset");

        await AssertEveryJobRanOnceAndEnded(env, ids, finalOk: null, window, reader: 1);
    }

    /// <summary>
    /// Waits until every job is settled, then checks each started exactly once with attempt 0. A row that stays InProgress
    /// although its job logged its end is the open issue #74 (the final status write failed and nothing retries it); it is
    /// tolerated, and reported, only when the job ended inside the scenario's fault <paramref name="window"/>. Any other row
    /// left InProgress fails: without an end line it is a claim left without a worker.
    /// </summary>
    private async Task AssertEveryJobRanOnceAndEnded(ScenarioEnvironment env, IReadOnlyList<Guid> ids, TickerStatus[]? finalOk, FaultEvent window, int reader = 0)
    {
        IReadOnlyList<TickerView> tickers = [];
        bool Settled(Guid id) => tickers.Any(t => t.Id == id && (IsFinal(t.Status) || (t.Status == TickerStatus.InProgress && env.Runs.Ends(id) == 1)));
        await env.UntilAsync(async () =>
        {
            tickers = await env.Nodes[reader].Dashboard.GetAllTimeTickersAsync();
            return ids.All(Settled);
        }, () =>
        {
            var open = tickers.Where(t => ids.Contains(t.Id) && !Settled(t.Id)).ToList();
            var missing = ids.Count(id => tickers.All(t => t.Id != id));
            return $"{open.Count + missing} of {ids.Count} not settled ({missing} not listed by the dashboard; " +
                   $"{string.Join(", ", open.Select(t => $"{t.Id} {t.Status}, starts {env.Runs.Starts(t.Id)}"))})";
        }, Recovery);

        // Let anything that would start a second time do so before counting.
        await Task.Delay(TimeSpan.FromSeconds(5));
        tickers = await env.Nodes[reader].Dashboard.GetAllTimeTickersAsync();
        var known74 = new List<Guid>();
        foreach (var id in ids)
        {
            Assert.True(env.Runs.Starts(id, 0) == 1, $"ticker {id}: {env.Runs.Starts(id, 0)} starts with attempt 0");
            var status = tickers.Single(t => t.Id == id).Status;
            if (status == TickerStatus.InProgress)
            {
                Assert.True(env.Runs.Ends(id) == 1, $"ticker {id} is InProgress and its job never ended: a claim without a worker");
                var end = env.Runs.For(id).Single(e => e.IsEnd);
                Assert.True(Invariants.FinalWriteCouldFail(end, [window]),
                    $"ticker {id} is InProgress although its job ended at {end.At:O}, outside the fault ({window.At:O} to {window.Until:O})");
                known74.Add(id);
            }
            else if (finalOk != null)
            {
                Assert.Contains(status, finalOk);
            }
        }

        if (known74.Count > 0)
            output.WriteLine($"Tolerated open issue #74: {known74.Count} job(s) ended during the fault but stay InProgress (final status write failed): {string.Join(", ", known74)}");
    }

    private static bool IsFinal(TickerStatus status)
        => status is TickerStatus.Done or TickerStatus.DueDone or TickerStatus.Failed or TickerStatus.Cancelled or TickerStatus.Skipped;
}
