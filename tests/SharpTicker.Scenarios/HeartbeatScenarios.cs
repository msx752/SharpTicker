using System.Diagnostics;
using SharpTicker.Scenarios.Harness;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace SharpTicker.Scenarios;

/// <summary>What the Redis heartbeat does with the jobs of nodes that are still running.</summary>
public class HeartbeatScenarios(ITestOutputHelper output)
{
    // The scenario host's heartbeat interval (5 s) plus 20 s.
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(25);

    // After a Redis outage longer than the heartbeat TTL every heartbeat entry is gone, the live nodes' too. A node took
    // every registered node without an entry for stopped, at once: the node that reached Redis first after the outage
    // released the jobs of a node that was still running and had only not written its heartbeat again yet. The released
    // job was claimed and started a second time while its first run went on. Now a missing node is released only when it
    // stayed missing for one more TTL, and a live node writes again within seconds of reaching Redis.
    // The order is pinned (each node has its own proxy): the node that runs the job reaches Redis again 7 s after the
    // observer has written its own heartbeat. The old code checked the other nodes at most one heartbeat interval (5 s)
    // after that write, so it has released by then. The time the observer needs to write, the 7 s and the time the runner
    // needs to write must together stay inside one TTL; the scenario measures that and is inconclusive otherwise.
    // With Redis as the job store, and with EF Core as the job store and Redis next to it: the heartbeat then releases
    // the EF Core rows, and only Redis is out of reach.
    [Theory]
    [InlineData(Store.Redis)]
    [InlineData(Store.EfCorePostgres)]
    public async Task AfterARedisOutagePastTheHeartbeatTtl_TheNodeBackFirst_DoesNotReleaseTheRunningJobOfALiveNode(Store store)
    {
        const int runner = 0, observer = 1;
        await using var env = store == Store.Redis
            ? await ScenarioEnvironment.StartAsync("outage-past-ttl", store, nodes: 2, startedNodes: 1, ownStoreProxy: [observer])
            : await ScenarioEnvironment.StartAsync("outage-past-ttl", store, nodes: 2, startedNodes: 1, heartbeatRedis: true);
        Task SetRedisAsync(int node, bool reachable)
            => store == Store.Redis ? env.SetStoreProxyAsync(node, reachable) : env.SetHeartbeatProxyAsync(node, reachable);
        // Not through a proxy: the heartbeat entries are read while the nodes cannot reach Redis.
        await using var redis = await ConnectionMultiplexer.ConnectAsync(env.DirectHeartbeatConnection);
        var db = redis.GetDatabase();
        Task<bool> HasHeartbeat(int node) => db.KeyExistsAsync($"{env.RedisInstanceName}hb:{env.Nodes[node].Identifier}");
        var poll = TimeSpan.FromMilliseconds(200);

        // 100 s: still running when a release of the runner would have shown (checked at the end).
        var id = await env.Nodes[runner].Api.AddTimeTickerAsync(new TickerSeed
            { Function = Functions.Slow, ExecutionTime = DateTime.UtcNow.AddSeconds(3), Request = new WaitRequest { Seconds = 100 } });
        await env.UntilAsync(() => Task.FromResult(env.Runs.Starts(id) == 1), () => "the runner did not start the job", TimeSpan.FromSeconds(30));
        // The running job is a row of the store under test: with EF Core, of PostgreSQL and not of the Redis next to it.
        var inProgress = await env.CountTimeTickersAsync(TickerStatus.InProgress);
        Assert.True(inProgress == 1, $"inconclusive: the store holds {inProgress} jobs InProgress, expected the running one");
        await env.StartNodeAsync(observer);
        await env.UntilAsync(async () => await HasHeartbeat(runner) && await HasHeartbeat(observer),
            () => "the two nodes did not both write a heartbeat", TimeSpan.FromSeconds(30));

        // The outage: neither node reaches Redis until both entries have expired.
        await SetRedisAsync(runner, reachable: false);
        await SetRedisAsync(observer, reachable: false);
        var outage = Stopwatch.StartNew();
        await Eventually.UntilAsync(async () => !await HasHeartbeat(runner) && !await HasHeartbeat(observer),
            Ttl + TimeSpan.FromSeconds(15), () => "the heartbeat entries did not expire during the outage", env, poll);

        // The observer is back first; it has written its own heartbeat when its entry is there again.
        await SetRedisAsync(observer, reachable: true);
        var observerBack = Stopwatch.StartNew();
        await Eventually.UntilAsync(() => HasHeartbeat(observer), TimeSpan.FromSeconds(30),
            () => "the observer did not write its heartbeat after the outage", env, poll);
        var observerWrote = observerBack.Elapsed;
        await Task.Delay(TimeSpan.FromSeconds(7));

        await SetRedisAsync(runner, reachable: true);
        var outageLasted = outage.Elapsed;
        await Eventually.UntilAsync(() => HasHeartbeat(runner), TimeSpan.FromSeconds(30),
            () => "the runner did not write its heartbeat after the outage", env, poll);
        var runnerWrote = observerBack.Elapsed;
        // The case this scenario is about. A node that stays missing for a whole TTL while another node reaches Redis is
        // released, by design; the observer cannot have seen the runner missing before it could reach Redis itself.
        Assert.True(runnerWrote < Ttl, $"inconclusive: the runner wrote its heartbeat {runnerWrote} after the observer could reach Redis, not inside one TTL");
        Assert.True(env.Runs.Ends(id) == 0, $"inconclusive: the job ended before the outage was over: {Describe(env, id)}");

        var status = TickerStatus.InProgress;
        await env.UntilAsync(
            async () => env.Runs.Starts(id) > 1
                        || (status = (await env.Nodes[observer].Dashboard.GetAllTimeTickersAsync()).Single(t => t.Id == id).Status) is not TickerStatus.InProgress,
            () => $"the job did not end: {status}; {Describe(env, id)}", TimeSpan.FromSeconds(150));

        Assert.True(env.Runs.Starts(id) == 1,
            $"the running job of a live node was released and started again ({env.Runs.Starts(id)} starts): {Describe(env, id)}");
        // DueDone when the runner's fallback made the first start.
        Assert.True(status is TickerStatus.Done or TickerStatus.DueDone, $"the job ended {status}: {Describe(env, id)}");
        Assert.True(env.Runs.Ends(id) == 1, $"expected one end, got {env.Runs.Ends(id)}: {Describe(env, id)}");
        // The job ran on past the moment a second start would have shown. The observer can first have missed the runner
        // up to the moment the runner wrote again; a release comes one TTL after the first miss at the next check (5 s),
        // and the pass that claims a released job runs every 2 s.
        Assert.True(observerBack.Elapsed > runnerWrote + Ttl + TimeSpan.FromSeconds(7),
            $"inconclusive: the job ended {observerBack.Elapsed} after the observer was back (the runner wrote at {runnerWrote}), before a release could have shown");
        output.WriteLine($"{store}: outage {outageLasted.TotalSeconds:0.0} s; after the observer could reach Redis it wrote its heartbeat at {observerWrote.TotalSeconds:0.0} s, " +
                         $"the runner at {runnerWrote.TotalSeconds:0.0} s; {Describe(env, id)}");
    }

    private static string Describe(ScenarioEnvironment env, Guid id)
        => string.Join("; ", env.Runs.For(id).Select(e => $"{e.Event} {e.Node} {e.At:HH:mm:ss.fff} {e.Status}"));
}
