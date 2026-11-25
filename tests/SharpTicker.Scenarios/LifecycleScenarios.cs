using SharpTicker.Scenarios.Harness;

namespace SharpTicker.Scenarios;

/// <summary>
/// The lifecycle handler (#1) on real nodes. The nodes run with <c>SCENARIO_LIFECYCLE=1</c>, and every handler call is a
/// <c>lifecycle-start</c> or <c>lifecycle-end</c> line in the run log. Each scenario injects the case its code path is for,
/// and checks the calls against the stored rows: one end call per stored final status, made by the node that stored it.
/// </summary>
public class LifecycleScenarios
{
    private static readonly Func<int, IReadOnlyDictionary<string, string>> WithLifecycleHandler =
        _ => new Dictionary<string, string> { ["SCENARIO_LIFECYCLE"] = "1" };

    private static IReadOnlyList<RunEvent> Lifecycle(ScenarioEnvironment env, Guid id, string evt)
        => env.Runs.For(id).Where(e => e.Event == evt).ToList();

    // #1, RecordStoppedRunAsync: a job that ignores its token and outlasts the stop's wait is recorded Cancelled by the stop
    // itself, while it still runs. The stopping node reports that Cancelled once, before it exits.
    [Theory]
    [InlineData(Store.EfCorePostgres)]
    [InlineData(Store.MongoDb)]
    [InlineData(Store.Redis)]
    public async Task AJobTheStopRecordsAsCancelled_IsReportedOnce_ByTheStoppingNode(Store store)
    {
        await using var env = await ScenarioEnvironment.StartAsync("lifecycle-stop", store, nodes: 2, nodeEnvironment: WithLifecycleHandler);

        var id = await env.Nodes[0].Api.AddTimeTickerAsync(new TickerSeed
        {
            Function = Functions.Stubborn, ExecutionTime = DateTime.UtcNow, Request = new WaitRequest { Seconds = 60 },
        });

        await env.UntilAsync(() => Task.FromResult(env.Runs.Starts(id) == 1), () => $"no start line for {id}");
        var runner = env.Nodes.Single(n => n.Identifier == env.Runs.For(id).Single(e => e.IsStart).Node);
        var other = env.Nodes.Single(n => n != runner);
        await env.UntilAsync(() => Task.FromResult(Lifecycle(env, id, "lifecycle-start").Count == 1), () => $"no lifecycle-start line for {id}");

        await Task.Delay(TimeSpan.FromSeconds(1));
        await env.StopNodeAsync(runner.Index);

        TickerView? ticker = null;
        await env.UntilAsync(async () =>
        {
            ticker = (await other.Dashboard.GetAllTimeTickersAsync()).Single(t => t.Id == id);
            return ticker.Status is not (TickerStatus.InProgress or TickerStatus.Queued or TickerStatus.Idle);
        }, () => $"status {ticker?.Status}");
        // The end line is written during the stop; wait for it, then a little longer for a second one.
        await env.UntilAsync(() => Task.FromResult(Lifecycle(env, id, "lifecycle-end").Count > 0), () => $"no lifecycle-end line for {id}");
        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.Equal(TickerStatus.Cancelled, ticker!.Status);
        // The job never ended in the process: the stop's record is the only Cancelled.
        Assert.Equal(0, env.Runs.Ends(id));
        var end = Assert.Single(Lifecycle(env, id, "lifecycle-end"));
        Assert.Equal("Cancelled", end.Status);
        Assert.Equal(runner.Identifier, end.Node);
        Assert.Single(Lifecycle(env, id, "lifecycle-start"));
    }

    // #1, the skip write of children: a root fails on one node of two. Its child that runs only on success and that child's
    // own child are skipped by the root's outcome; the node that ran the root reports each of them once as Skipped, with no
    // start. The child that runs on failure runs and is reported once with its own outcome. The EF Core and MongoDB stores
    // skip the children one by one only because a handler is registered.
    [Theory]
    [InlineData(Store.EfCorePostgres)]
    [InlineData(Store.MongoDb)]
    [InlineData(Store.Redis)]
    public async Task ChildrenThatAFailedRootSkips_AreReportedOnceAsSkipped_ByTheRootsNode(Store store)
    {
        await using var env = await ScenarioEnvironment.StartAsync("lifecycle-skip", store, nodes: 2, nodeEnvironment: WithLifecycleHandler);

        var root = await env.Nodes[0].Api.AddTimeTickerAsync(new TickerSeed
        {
            Function = Functions.Fail,
            ExecutionTime = DateTime.UtcNow,
            Children =
            [
                new TickerSeed
                {
                    Function = Functions.Record, RunCondition = RunCondition.OnSuccess,
                    Children = [new TickerSeed { Function = Functions.Record, RunCondition = RunCondition.OnSuccess }],
                },
                new TickerSeed { Function = Functions.Record, RunCondition = RunCondition.OnFailure },
            ],
        });

        TickerView? stored = null;
        await env.UntilAsync(async () =>
        {
            stored = (await env.Nodes[0].Dashboard.GetAllTimeTickersAsync()).SingleOrDefault(t => t.Id == root);
            return stored is { Status: TickerStatus.Failed } && stored.Children.All(c => c.Status is TickerStatus.Skipped or TickerStatus.Done or TickerStatus.DueDone)
                   && stored.Children.SelectMany(c => c.Children).All(g => g.Status == TickerStatus.Skipped);
        }, () => $"the chain did not end: root {stored?.Status}, children {string.Join(", ", stored?.Children.Select(c => $"{c.Id} {c.Status}") ?? [])}");
        var rootNode = env.Runs.For(root).Single(e => e.IsStart).Node;
        var skipped = Assert.Single(stored!.Children, c => c.Status == TickerStatus.Skipped);
        var ran = Assert.Single(stored.Children, c => c.Status is TickerStatus.Done or TickerStatus.DueDone);
        var grandchild = Assert.Single(skipped.Children);

        // Wait for the four end lines (the root, the two skipped jobs, the child that ran), then a little longer for a second one.
        var expected = new[] { root, skipped.Id, grandchild.Id, ran.Id };
        await env.UntilAsync(() => Task.FromResult(expected.All(x => Lifecycle(env, x, "lifecycle-end").Count > 0)),
            () => $"missing lifecycle-end lines: {string.Join(", ", expected.Where(x => Lifecycle(env, x, "lifecycle-end").Count == 0))}");
        await Task.Delay(TimeSpan.FromSeconds(3));

        foreach (var id in new[] { skipped.Id, grandchild.Id })
        {
            var end = Assert.Single(Lifecycle(env, id, "lifecycle-end"));
            Assert.Equal("Skipped", end.Status);
            Assert.Equal(rootNode, end.Node);
            Assert.Empty(Lifecycle(env, id, "lifecycle-start"));
            Assert.Equal(0, env.Runs.Starts(id));
        }

        Assert.Equal("Failed", Assert.Single(Lifecycle(env, root, "lifecycle-end")).Status);
        var ranEnd = Assert.Single(Lifecycle(env, ran.Id, "lifecycle-end"));
        Assert.True(ranEnd.Status is "Done" or "DueDone", ranEnd.Status);
        Assert.Single(Lifecycle(env, ran.Id, "lifecycle-start"));
    }
}
