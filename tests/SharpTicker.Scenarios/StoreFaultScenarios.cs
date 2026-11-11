using System.Diagnostics;
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

    // #74: the store is cut while jobs run, so the final status write of each fails at its end. The node writes the outcome
    // again once the store is back, with no restart. Before the fix the row stayed InProgress for as long as the node lived;
    // a later release (a restart on EF Core) freed it and the finished job ran again.
    [Theory]
    [InlineData(Store.EfCorePostgres)]
    [InlineData(Store.MongoDb)]
    [InlineData(Store.Redis)]
    public async Task AStoreCutAtTheEndOfJobs_TheirOutcomesAreStoredOnceTheStoreIsBack(Store store)
    {
        await using var env = await ScenarioEnvironment.StartAsync("outcome-cut", store, nodes: 2);

        var start = DateTime.UtcNow;
        var ids = await env.Nodes[0].Api.AddTimeTickersAsync(Enumerable.Range(0, 5)
            .Select(_ => new TickerSeed { Function = Functions.Slow, ExecutionTime = start.AddSeconds(3), Request = new WaitRequest { Seconds = 6 } })
            .ToArray());

        // All five run 6 s. They are seen started at most a fallback pass (2 s) after the first start, so the earliest end is
        // at least 4 s away; the cut starts 2 s from here and lasts past the latest end. MongoDB 40 s: longer than its
        // driver's server selection wait (30 s), which otherwise waits out the cut and writes late on its own, so no final
        // write fails there. Redis 10 s: well inside its heartbeat TTL (25 s), counting the last heartbeat (up to 5 s before
        // the cut) and the 5 s retry delay after a failed one; a longer outage can make the nodes release their own running
        // rows when Redis returns (a known Redis behaviour, not fixed yet), which is not what this scenario checks. The
        // jobs end at most 6 s into the cut, so 10 s still fails their final writes.
        await env.UntilAsync(() => Task.FromResult(ids.All(id => env.Runs.Starts(id) == 1)),
            () => $"{ids.Count(id => env.Runs.Starts(id) == 1)} of {ids.Count} started", TimeSpan.FromSeconds(30));
        await Task.Delay(TimeSpan.FromSeconds(2));
        var cut = TimeSpan.FromSeconds(store switch { Store.MongoDb => 40, Store.Redis => 10, _ => 15 });
        var window = new FaultEvent("cut") { At = DateTimeOffset.UtcNow, Until = DateTimeOffset.UtcNow + cut };
        await env.CutStoreAsync(cut);

        // DueDone when a node's fallback took the job.
        await AssertEveryJobRanOnceAndEnded(env, ids, finalOk: [TickerStatus.Done, TickerStatus.DueDone], window);

        // The fault reached the final writes: without this the scenario would pass on code that never meets a failed write
        // (a store client that waits out the cut and writes late on its own).
        var lost = env.CountNodeLogLines("could not be stored");
        Assert.True(lost >= ids.Count, $"{lost} lost final writes logged, expected one per job ({ids.Count}); see {env.LogFolder}");
    }

    // #74 (#104): a node frozen past its heartbeat is taken for dead; the other node releases its rows and runs its job again
    // (the documented at-least-once of a node taken for dead). When the frozen node goes on, its job ends and writes its
    // outcome late. That write must not land on the row the other node now runs: before the fix it was by Id, so the row
    // became Done and unlocked while the other node still ran the job. Redis only: it is the store whose nodes release the
    // rows of a dead node while they run.
    [Fact]
    public async Task APausedNodesLateWrite_DoesNotOverwriteTheRunThatTookItsRow()
    {
        await using var env = await ScenarioEnvironment.StartAsync("late-write", Store.Redis, nodes: 2, startedNodes: 1);
        string frozen = env.Nodes[0].Identifier, taker = env.Nodes[1].Identifier;

        // 30 s: node 0's run outlasts nothing it needs to (its wait has passed when it is let go), and node 1's run, which
        // starts at least 20 s into the pause (heartbeat ttl 25 s minus one 5 s interval), is still going 10 s after it.
        var id = await env.Nodes[0].Api.AddTimeTickerAsync(new TickerSeed
            { Function = Functions.Slow, ExecutionTime = DateTime.UtcNow.AddSeconds(3), Request = new WaitRequest { Seconds = 30 } });
        await env.UntilAsync(() => Task.FromResult(env.Runs.Starts(id) == 1), () => "node 0 did not start the job", TimeSpan.FromSeconds(30));
        await env.StartNodeAsync(1);

        await env.PauseNodeAsync(0, TimeSpan.FromSeconds(40));
        Assert.True(env.Runs.For(id).Any(e => e.IsStart && e.Node == taker),
            $"node 1 did not take the job while node 0 was frozen: {Describe(env, id)}");

        // Node 0's job ends as soon as it runs again, and writes its outcome.
        await env.UntilAsync(() => Task.FromResult(env.Runs.For(id).Any(e => e.IsEnd && e.Node == frozen)),
            () => $"node 0's job did not end after the pause: {Describe(env, id)}", TimeSpan.FromSeconds(20));

        // The row is read once node 0's write is done: with the fence it logs the refusal; without it there is no such
        // line, so the wait ends after 10 s and the row shows what the write did.
        var refusalWait = Stopwatch.StartNew();
        while (env.CountNodeLogLines("was not stored", node: 0) == 0 && refusalWait.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(TimeSpan.FromMilliseconds(200));

        var row = (await env.Nodes[1].Dashboard.GetAllTimeTickersAsync()).Single(t => t.Id == id);
        Assert.False(env.Runs.For(id).Any(e => e.IsEnd && e.Node == taker), $"inconclusive: node 1's run ended already: {Describe(env, id)}");
        Assert.True(row.Status == TickerStatus.InProgress && row.LockHolder == taker,
            $"node 0's late write changed the row node 1 runs: {row.Status}, lock {row.LockHolder ?? "none"}; {Describe(env, id)}");

        // Node 1's run ends it: DueDone, as node 1's fallback took the overdue row.
        var final = TickerStatus.InProgress;
        await env.UntilAsync(async () => (final = (await env.Nodes[1].Dashboard.GetAllTimeTickersAsync()).Single(t => t.Id == id).Status) is TickerStatus.Done or TickerStatus.DueDone,
            () => $"node 1's run did not end the row: {final}; {Describe(env, id)}", TimeSpan.FromSeconds(60));
        Assert.True(env.CountNodeLogLines("was not stored", node: 0) >= 1, $"node 0 logged no refused late write; see {env.LogFolder}");
        output.WriteLine($"starts: {env.Runs.Starts(id)} (node 0, then node 1 after it was taken for dead)");
    }

    private static string Describe(ScenarioEnvironment env, Guid id)
        => string.Join("; ", env.Runs.For(id).Select(e => $"{e.Event} {e.Node} {e.At:HH:mm:ss.fff} {e.Status}"));

    /// <summary>
    /// Waits until every job has a final status, then checks each started exactly once with attempt 0. A row left
    /// InProgress fails: either a claim without a worker, or a final status write that failed and was not written again
    /// (#74). <paramref name="window"/> is kept for the failure message.
    /// </summary>
    private async Task AssertEveryJobRanOnceAndEnded(ScenarioEnvironment env, IReadOnlyList<Guid> ids, TickerStatus[]? finalOk, FaultEvent window, int reader = 0)
    {
        IReadOnlyList<TickerView> tickers = [];
        bool Settled(Guid id) => tickers.Any(t => t.Id == id && IsFinal(t.Status));
        await env.UntilAsync(async () =>
        {
            tickers = await env.Nodes[reader].Dashboard.GetAllTimeTickersAsync();
            return ids.All(Settled);
        }, () =>
        {
            var open = tickers.Where(t => ids.Contains(t.Id) && !Settled(t.Id)).ToList();
            var missing = ids.Count(id => tickers.All(t => t.Id != id));
            return $"{open.Count + missing} of {ids.Count} not settled ({missing} not listed by the dashboard; " +
                   $"{string.Join(", ", open.Select(t => $"{t.Id} {t.Status}, starts {env.Runs.Starts(t.Id)}, ends {env.Runs.Ends(t.Id)}"))}; " +
                   $"fault {window.Type} {window.At:O} to {window.Until:O})";
        }, Recovery);

        // Let anything that would start a second time do so before counting.
        await Task.Delay(TimeSpan.FromSeconds(5));
        tickers = await env.Nodes[reader].Dashboard.GetAllTimeTickersAsync();
        foreach (var id in ids)
        {
            Assert.True(env.Runs.Starts(id, 0) == 1, $"ticker {id}: {env.Runs.Starts(id, 0)} starts with attempt 0");
            if (finalOk != null)
                Assert.Contains(tickers.Single(t => t.Id == id).Status, finalOk);
        }
        output.WriteLine($"{ids.Count} jobs, each started once and ended {string.Join("/", finalOk ?? [])}");
    }

    private static bool IsFinal(TickerStatus status)
        => status is TickerStatus.Done or TickerStatus.DueDone or TickerStatus.Failed or TickerStatus.Cancelled or TickerStatus.Skipped;
}
