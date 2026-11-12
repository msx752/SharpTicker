using SharpTicker.Scenarios.Harness;
using Xunit.Abstractions;

namespace SharpTicker.Scenarios;

/// <summary>
/// The start of a chain child without a time of its own (#83, #84): it is a claim on the child's stored row, and a child
/// whose start never came is taken by the fallback once its parent ended 30 s ago.
/// </summary>
public class ChainStartScenarios(ITestOutputHelper output)
{
    // The time the fallback leaves a child without a time to its parent's own run (ChainRunGate.FallbackGraceForChildWithoutTime).
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Recovery = TimeSpan.FromSeconds(150);

    private static TickerSeed Step(int seconds, DateTime? at = null, RunCondition? condition = RunCondition.OnSuccess, params TickerSeed[] children)
        => new()
        {
            Function = Functions.ChainStep,
            ExecutionTime = at,
            Request = new WaitRequest { Seconds = seconds },
            RunCondition = condition,
            Children = children.ToList(),
        };

    // #84: a node is killed while it runs a child without a time, after the child's parent ended. The killed node's rows are
    // released (EF Core: when the node starts again; Redis: by its next start or by another node), which leaves the child
    // waiting with no time. Before the fix nothing took such a row: it stayed InProgress (no lock holder, so no release saw
    // it) or, once the start had become a claim, Idle for good. Now the fallback takes it once its parent ended 30 s ago.
    // Not MongoDB: it has no release step for a node that stopped, so the child stays InProgress there.
    [Theory]
    [InlineData(Store.EfCorePostgres)]
    [InlineData(Store.Redis)]
    public async Task AChildWithoutATime_WhoseNodeWasKilledWhileItRan_IsRunAgainByTheFallback(Store store)
    {
        await using var env = await ScenarioEnvironment.StartAsync("chain-kill-inline", store, nodes: 2);

        var (root, tree) = await env.Nodes[0].Api.AddChainAsync(Step(1, at: DateTime.UtcNow, condition: null, children: [Step(15)]));
        var child = tree[1];
        await env.UntilAsync(() => Task.FromResult(env.Runs.Starts(child) == 1), () => "the child did not start");
        var first = env.Runs.For(child).Single(e => e.IsStart);
        var runner = env.Nodes.Single(n => n.Identifier == first.Node);
        var other = env.Nodes.Single(n => n != runner);
        Assert.Equal(1, env.Runs.Ends(root));

        await env.KillNodeAsync(runner.Index);
        var killedAt = DateTimeOffset.UtcNow;
        Assert.Equal(0, env.Runs.Ends(child));
        await env.StartNodeAsync(runner.Index);

        IReadOnlyList<TickerView> all = [];
        await env.UntilAsync(async () =>
        {
            all = await other.Dashboard.GetAllTimeTickersAsync();
            return all.Single(t => t.Id == child).Status is TickerStatus.Done or TickerStatus.DueDone or TickerStatus.Failed or TickerStatus.Cancelled or TickerStatus.Skipped;
        }, () => $"the child is {all.SingleOrDefault(t => t.Id == child)?.Status} (lock {all.SingleOrDefault(t => t.Id == child)?.LockHolder ?? "none"}), starts {env.Runs.Starts(child)}", Recovery);

        // Let anything that would start it a third time do so before counting.
        await Task.Delay(TimeSpan.FromSeconds(5));
        all = await other.Dashboard.GetAllTimeTickersAsync();
        Assert.Equal(TickerStatus.DueDone, all.Single(t => t.Id == child).Status);
        var starts = env.Runs.For(child).Where(e => e.IsStart).OrderBy(e => e.At).ToList();
        Assert.True(starts.Count == 2, $"the child started {starts.Count} times, expected the killed run and one more");
        Assert.True(starts[1].At > killedAt, $"the second start ({starts[1].At:O}) is not after the kill ({killedAt:O})");
        var parentEnd = env.Runs.For(root).Single(e => e.IsEnd).At;
        Assert.True(starts[1].At >= parentEnd + Grace - TimeSpan.FromSeconds(1),
            $"the second start came {(starts[1].At - parentEnd).TotalSeconds:0.0} s after the parent's end, inside the fallback's grace time");
        Assert.Equal(1, env.Runs.Ends(child));
        output.WriteLine($"the child ran again {(starts[1].At - parentEnd).TotalSeconds:0.0} s after its parent's end, on {starts[1].Node}");
    }

    // #83: a child without a time is given one in the dashboard while its parent runs. The parent's run carries the child as
    // it was claimed (no time) and starts it at the parent's end; the store hands the same child out as a delayed child.
    // Before the fix the parent's run wrote InProgress by id, whatever the row was, and ran the child: with the store's
    // hand-out, twice. Now that start is a claim the row refuses once it has a time.
    // The parent's node reaches the store through a slow proxy, so the other node's fallback takes the child between the
    // parent's stored outcome and the parent's run starting the child; without that the two are milliseconds apart and the
    // code before the fix starts the child once in most runs.
    [Theory]
    [InlineData(Store.EfCorePostgres)]
    [InlineData(Store.MongoDb)]
    [InlineData(Store.Redis)]
    public async Task AChildGivenATimeWhileItsParentRuns_IsStartedOnce(Store store)
    {
        // Node 0's own log shows the refused start (a Debug line).
        await using var env = await ScenarioEnvironment.StartAsync("chain-child-given-time", store, nodes: 2, startedNodes: 1, ownStoreProxy: [1],
            nodeEnvironment: i => i == 0
                ? new Dictionary<string, string> { ["Logging__LogLevel__SharpTicker.TickerExecutionTaskHandler"] = "Debug" }
                : new Dictionary<string, string>());

        var (root, tree) = await env.Nodes[0].Api.AddChainAsync(Step(25, at: DateTime.UtcNow, condition: null, children: [Step(1)]));
        var child = tree[1];
        await env.UntilAsync(() => Task.FromResult(env.Runs.Starts(root) == 1), () => "the parent did not start");

        // Node 1 starts while the parent runs on node 0, so node 0 is the parent's node.
        await env.StartNodeAsync(1);
        await env.Nodes[1].Dashboard.UpdateTimeTickerAsync(child, Functions.ChainStep, """{"Seconds":1}""", DateTime.UtcNow.AddSeconds(1));
        var edited = (await env.Nodes[1].Dashboard.GetAllTimeTickersAsync()).Single(t => t.Id == child);
        Assert.True(edited is { ExecutionTime: not null, ParentId: not null, Status: TickerStatus.Idle },
            $"after the edit the child has time {edited.ExecutionTime:O}, parent {edited.ParentId}, status {edited.Status}");
        Assert.Equal(0, env.Runs.Ends(root));

        // Each request of node 0 reaches the store this much later: the parent's outcome first, the child's start after it.
        var slow = store == Store.Redis ? 3000 : 4000;
        await env.AddStoreToxicAsync("latency", new { latency = slow }, "upstream");

        await env.UntilAsync(() => Task.FromResult(env.Runs.Ends(root) == 1), () => "the parent did not end", TimeSpan.FromSeconds(60));
        IReadOnlyList<TickerView> all = [];
        await env.UntilAsync(async () =>
        {
            all = await env.Nodes[1].Dashboard.GetAllTimeTickersAsync();
            return all.Single(t => t.Id == child).Status is TickerStatus.Done or TickerStatus.DueDone;
        }, () => $"the child is {all.SingleOrDefault(t => t.Id == child)?.Status}, starts {env.Runs.Starts(child)}", TimeSpan.FromSeconds(90));

        // Node 0's start of the child comes at least one slow request after the parent's outcome reached the store.
        await Task.Delay(TimeSpan.FromSeconds(20));
        await env.RemoveStoreToxicsAsync();

        var starts = env.Runs.For(child).Where(e => e.IsStart).ToList();
        Assert.True(starts.Count == 1, $"the child started {starts.Count} times: {string.Join(", ", starts.Select(s => $"{s.Node} at {s.At:O}"))}");
        Assert.Equal(env.Nodes[1].Identifier, starts[0].Node);
        Assert.True(starts[0].At >= env.Runs.For(root).Single(e => e.IsEnd).At, "the child started before its parent ended");

        // The parent's run did try to start the child and was refused: without this the scenario would pass on a run that
        // never reached that start.
        Assert.True(env.CountNodeLogLines("is not started here", node: 0) >= 1, $"node 0 logged no refused start of the child; see {env.LogFolder}");
    }

    // #83, #84: the store is cut between the parent's stored outcome and the start of its child without a time, so the
    // child's start claim fails. Before the fix the child ran anyway, with its row still Idle: a run nothing fenced. Now it is
    // not run by the parent's run, and the fallback takes it after the store is back, once the parent ended 30 s ago.
    // Requests reach the store late (a latency toxic), so the cut can be placed after the parent's outcome is in the store
    // and before the child's start arrives.
    [Theory]
    [InlineData(Store.EfCorePostgres)]
    [InlineData(Store.MongoDb)]
    [InlineData(Store.Redis)]
    public async Task AStoreCutAtTheStartOfAChildWithoutATime_TheChildIsNotRunUntilItsRowCanBeClaimed(Store store)
    {
        await using var env = await ScenarioEnvironment.StartAsync("chain-start-cut", store, nodes: 1);
        var node = env.Nodes[0];

        var (root, tree) = await node.Api.AddChainAsync(Step(8, at: DateTime.UtcNow, condition: null, children: [Step(1)]));
        var child = tree[1];
        await env.UntilAsync(() => Task.FromResult(env.Runs.Starts(root) == 1), () => "the parent did not start");
        await env.AddStoreToxicAsync("latency", new { latency = 3000 }, "upstream");

        // Read from the store directly: the parent is the only row that can be Done.
        await Eventually.UntilAsync(async () => await env.CountTimeTickersAsync(TickerStatus.Done) == 1,
            TimeSpan.FromSeconds(60), () => "the parent's outcome did not reach the store", env, TimeSpan.FromMilliseconds(100));

        // MongoDB 40 s: longer than its driver's server selection wait (30 s), which otherwise waits out the cut. Redis 10 s:
        // inside its heartbeat TTL (25 s).
        var cut = TimeSpan.FromSeconds(store switch { Store.MongoDb => 40, Store.Redis => 10, _ => 15 });
        var window = new FaultEvent("cut") { At = DateTimeOffset.UtcNow, Until = DateTimeOffset.UtcNow + cut };
        await env.SetStoreProxyAsync(0, enabled: false);
        var startedBeforeTheCut = env.Runs.Starts(child);
        try
        {
            await Task.Delay(cut);
        }
        finally
        {
            await env.SetStoreProxyAsync(0, enabled: true);
        }
        var back = DateTimeOffset.UtcNow;
        // Not during the cut: Toxiproxy takes many seconds to remove a toxic from a connection it has closed.
        await env.RemoveStoreToxicsAsync();
        Assert.True(startedBeforeTheCut == 0, "inconclusive: the child had started before the store was cut");

        IReadOnlyList<TickerView> all = [];
        await env.UntilAsync(async () =>
        {
            all = await node.Dashboard.GetAllTimeTickersAsync();
            return all.Single(t => t.Id == child).Status is TickerStatus.Done or TickerStatus.DueDone or TickerStatus.Failed or TickerStatus.Cancelled or TickerStatus.Skipped;
        }, () => $"the child is {all.SingleOrDefault(t => t.Id == child)?.Status}, starts {env.Runs.Starts(child)}; cut {window.At:O} to {back:O}", Recovery);

        await Task.Delay(TimeSpan.FromSeconds(5));
        all = await node.Dashboard.GetAllTimeTickersAsync();
        var starts = env.Runs.For(child).Where(e => e.IsStart).ToList();
        Assert.True(starts.Count == 1, $"the child started {starts.Count} times: {string.Join(", ", starts.Select(s => s.At.ToString("O")))}");
        Assert.True(starts[0].At >= back - TimeSpan.FromMilliseconds(500),
            $"the child started at {starts[0].At:O}, while the store was cut ({window.At:O} to {back:O}): a run without its row");
        Assert.Equal(TickerStatus.DueDone, all.Single(t => t.Id == child).Status);
        Assert.Equal(TickerStatus.Done, all.Single(t => t.Id == root).Status);

        // The cut reached the child's start claim: without this the scenario would pass on a run whose claim was answered.
        Assert.True(env.CountNodeLogLines("could not be started because its claim failed") >= 1,
            $"no failed start claim was logged; see {env.LogFolder}");
        output.WriteLine($"the child started {(starts[0].At - back).TotalSeconds:0.0} s after the store was back");
    }
}
