using SharpTicker.Scenarios.Harness;
using Xunit.Abstractions;

namespace SharpTicker.Scenarios;

/// <summary>What a node that comes back under its own name does with the jobs its earlier run left running.</summary>
public class RestartScenarios(ITestOutputHelper output)
{
    // With the Redis store only another node released the jobs of a node that stopped, once its heartbeat had been missing
    // long enough. A node that is killed and started again under the same name writes its heartbeat again within seconds,
    // so no other node ever takes it for stopped, and a single node has no other node at all: the job its earlier run
    // left InProgress stayed InProgress and never ran again. Now the starting node releases what its earlier run left.
    // The job must run again exactly once: the restarted node must not release it a second time while it runs, and the
    // other node must not take the restarted node for dead (its checks span two heartbeat TTLs of 25 s here).
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ARedisNodeKilledWhileAJobRuns_AndStartedAgainUnderItsName_RunsTheJobAgainOnce(int nodes)
    {
        await using var env = await ScenarioEnvironment.StartAsync($"restart-same-name-{nodes}", Store.Redis, nodes: nodes, startedNodes: 1);
        var reader = env.Nodes[nodes - 1];

        var id = await env.Nodes[0].Api.AddTimeTickerAsync(new TickerSeed
            { Function = Functions.Slow, ExecutionTime = DateTime.UtcNow.AddSeconds(3), Request = new WaitRequest { Seconds = 30 } });
        await env.UntilAsync(() => Task.FromResult(env.Runs.Starts(id) == 1), () => "node 0 did not start the job", TimeSpan.FromSeconds(30));
        if (nodes == 2)
            await env.StartNodeAsync(1);

        var down = System.Diagnostics.Stopwatch.StartNew();
        await env.KillNodeAsync(0);
        await env.StartNodeAsync(0);
        // The case this scenario is about: back before another node could have confirmed it as stopped (two TTLs of 25 s).
        // A slower restart would be released by the other node and prove nothing about the restarted one.
        Assert.True(down.Elapsed < TimeSpan.FromSeconds(40), $"inconclusive: the restart took {down.Elapsed}");
        Assert.Equal(0, env.Runs.Ends(id));

        var status = TickerStatus.InProgress;
        await env.UntilAsync(
            async () => (status = (await reader.Dashboard.GetAllTimeTickersAsync()).Single(t => t.Id == id).Status) is TickerStatus.Done or TickerStatus.DueDone,
            () => $"the job the killed run left was not run to its end: {status}, starts {env.Runs.Starts(id)}; {Describe(env, id)}",
            TimeSpan.FromSeconds(120));

        // Past two heartbeat TTLs after the kill, so a release by the other node would have come by now.
        await Task.Delay(TimeSpan.FromSeconds(30));
        var row = (await reader.Dashboard.GetAllTimeTickersAsync()).Single(t => t.Id == id);
        Assert.True(row.Status is TickerStatus.Done or TickerStatus.DueDone, $"the row changed after its job ended: {row.Status}; {Describe(env, id)}");
        Assert.True(env.Runs.Starts(id) == 2, $"expected the killed run and one more, got {env.Runs.Starts(id)} starts: {Describe(env, id)}");
        Assert.True(env.Runs.Ends(id) == 1, $"expected one end, got {env.Runs.Ends(id)}: {Describe(env, id)}");
        output.WriteLine($"{nodes} node(s): {Describe(env, id)}");
    }

    private static string Describe(ScenarioEnvironment env, Guid id)
        => string.Join("; ", env.Runs.For(id).Select(e => $"{e.Event} {e.Node} {e.At:HH:mm:ss.fff} {e.Status}"));
}
