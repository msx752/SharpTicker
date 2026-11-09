using SharpTicker.Scenarios.Harness;

namespace SharpTicker.Scenarios;

/// <summary>Chains with inline and delayed children and grandchildren.</summary>
public class ChainScenarios
{
    private static TickerSeed Step(int seconds, DateTime? at = null, RunCondition? condition = RunCondition.OnSuccess, params TickerSeed[] children)
        => new()
        {
            Function = Functions.ChainStep,
            ExecutionTime = at,
            Request = new WaitRequest { Seconds = seconds },
            RunCondition = condition,
            Children = children.ToList(),
        };

    // #50: a delayed child (a child with a time of its own) waits for its parent and for its time. One child is due while
    // the parent still runs, one later. Before the fix a delayed child ran right after its parent (time ignored) or was
    // handed out at its own time without waiting for the parent.
    [Theory]
    [InlineData(Store.EfCorePostgres)]
    [InlineData(Store.MongoDb)]
    [InlineData(Store.Redis)]
    public async Task DelayedChildren_StartAfterTheirParentEnded_AndNotBeforeTheirTime(Store store)
    {
        await using var env = await ScenarioEnvironment.StartAsync("chain-delayed", store, nodes: 2);
        var node = env.Nodes[0];

        var now = DateTime.UtcNow;
        var (rootId, tree) = await node.Api.AddChainAsync(Step(6, at: now, condition: null, children:
        [
            Step(1, at: now.AddSeconds(2)),  // due while the parent runs
            Step(1, at: now.AddSeconds(15)), // due after the parent ended
        ]));
        var (early, late) = (tree[1], tree[2]);

        IReadOnlyList<TickerView> all = await WaitAllFinal(env, node, tree);
        foreach (var id in tree)
        {
            Assert.Contains(all.Single(t => t.Id == id).Status, new[] { TickerStatus.Done, TickerStatus.DueDone });
            Assert.Equal(1, env.Runs.Starts(id));
        }

        var parentEnd = env.Runs.For(rootId).Single(e => e.IsEnd).At;
        foreach (var delayed in new[] { early, late })
        {
            var started = env.Runs.For(delayed).Single(e => e.IsStart).At;
            var own = new DateTimeOffset(DateTime.SpecifyKind(all.Single(t => t.Id == delayed).ExecutionTime!.Value, DateTimeKind.Utc));
            Assert.True(started >= parentEnd, $"delayed child {delayed} started at {started:O}, before its parent ended at {parentEnd:O}");
            Assert.True(started >= own, $"delayed child {delayed} started at {started:O}, before its own time {own:O}");
        }
    }

    // #51: grandchildren run (MongoDB never ran them before the fix). An inline child with an inline grandchild.
    [Theory]
    [InlineData(Store.EfCorePostgres)]
    [InlineData(Store.MongoDb)]
    [InlineData(Store.Redis)]
    public async Task AGrandchild_RunsAfterItsParent(Store store)
    {
        await using var env = await ScenarioEnvironment.StartAsync("chain-grandchild", store, nodes: 2);
        var node = env.Nodes[0];

        var (_, tree) = await node.Api.AddChainAsync(Step(1, at: DateTime.UtcNow, condition: null, children:
            [Step(1, children: [Step(1)])]));
        var (child, grandchild) = (tree[1], tree[2]);

        var all = await WaitAllFinal(env, node, tree);
        Assert.All(tree, id => Assert.Contains(all.Single(t => t.Id == id).Status, new[] { TickerStatus.Done, TickerStatus.DueDone }));
        Assert.Equal(1, env.Runs.Starts(grandchild));
        Assert.True(env.Runs.For(grandchild).Single(e => e.IsStart).At >= env.Runs.For(child).Single(e => e.IsEnd).At);
    }

    // #68: an inline child (no time of its own) is recorded InProgress while it runs, so the dashboard shows it.
    // Before the fix only its final status was written: it showed Idle while it ran.
    [Theory]
    [InlineData(Store.EfCorePostgres)]
    [InlineData(Store.MongoDb)]
    [InlineData(Store.Redis)]
    public async Task AnInlineChild_IsInProgressInTheDashboardWhileItRuns(Store store)
    {
        await using var env = await ScenarioEnvironment.StartAsync("chain-inline-status", store, nodes: 1);
        var node = env.Nodes[0];

        var (_, tree) = await node.Api.AddChainAsync(Step(1, at: DateTime.UtcNow, condition: null, children: [Step(8)]));
        var child = tree[1];
        await env.UntilAsync(() => Task.FromResult(env.Runs.Starts(child) == 1), () => "the inline child did not start");

        // It runs 8 s; read the dashboard twice while it runs.
        await Task.Delay(TimeSpan.FromSeconds(2));
        var seen = new List<TickerStatus>();
        for (var i = 0; i < 2; i++)
        {
            seen.Add((await node.Dashboard.GetAllTimeTickersAsync()).Single(t => t.Id == child).Status);
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(0, env.Runs.Ends(child));
        Assert.All(seen, s => Assert.Equal(TickerStatus.InProgress, s));
    }

    private static async Task<IReadOnlyList<TickerView>> WaitAllFinal(ScenarioEnvironment env, Node node, IReadOnlyList<Guid> tree)
    {
        IReadOnlyList<TickerView> all = [];
        await env.UntilAsync(async () =>
        {
            all = await node.Dashboard.GetAllTimeTickersAsync();
            return tree.All(id => all.Any(t => t.Id == id && t.Status is TickerStatus.Done or TickerStatus.DueDone or TickerStatus.Failed or TickerStatus.Skipped or TickerStatus.Cancelled));
        }, () => string.Join(", ", tree.Select(id => $"{id}: {all.SingleOrDefault(t => t.Id == id)?.Status} starts {env.Runs.Starts(id)}")), TimeSpan.FromSeconds(90));
        return all;
    }

    // #50: a delayed child whose parent ended with an outcome its RunCondition does not accept is skipped, with everything
    // below it, and never starts. Before the fix the delayed child was handed out at its own time whatever its parent did.
    [Theory]
    [InlineData(Store.EfCorePostgres)]
    [InlineData(Store.MongoDb)]
    [InlineData(Store.Redis)]
    public async Task AFailedParent_SkipsAnOnSuccessDelayedChild_WithItsDescendants(Store store)
    {
        await using var env = await ScenarioEnvironment.StartAsync("chain-failed", store, nodes: 2);
        var node = env.Nodes[0];

        var now = DateTime.UtcNow;
        var root = new TickerSeed
        {
            Function = Functions.Fail,
            ExecutionTime = now,
            Children = [Step(1, at: now.AddSeconds(3), children: [Step(1)])],
        };
        var (rootId, tree) = await node.Api.AddChainAsync(root);

        IReadOnlyList<TickerView> all = [];
        await env.UntilAsync(async () =>
        {
            all = await node.Dashboard.GetAllTimeTickersAsync();
            return all.SingleOrDefault(t => t.Id == rootId)?.Status == TickerStatus.Failed
                   && tree.Skip(1).All(id => all.SingleOrDefault(t => t.Id == id)?.Status == TickerStatus.Skipped);
        }, () => string.Join(", ", tree.Select(id => $"{id}: {all.SingleOrDefault(t => t.Id == id)?.Status}")), TimeSpan.FromSeconds(60));

        // Wait past the child's own time: neither it nor its grandchild ever starts.
        await Task.Delay(TimeSpan.FromSeconds(5));
        Assert.All(tree.Skip(1), id => Assert.Equal(0, env.Runs.Starts(id)));
    }

    // #25: deleting a chain on EF Core removes its children and grandchildren; before the fix they stayed in the store.
    [Fact]
    public async Task DeletingAChain_RemovesItsChildren()
    {
        await using var env = await ScenarioEnvironment.StartAsync("chain-delete", Store.EfCorePostgres, nodes: 1);
        var node = env.Nodes[0];

        var later = DateTime.UtcNow.AddHours(1);
        var (rootId, tree) = await node.Api.AddChainAsync(Step(1, at: later, condition: null, children:
            [Step(1, children: [Step(1)]), Step(1, at: later.AddMinutes(1))]));
        Assert.Equal(4, (await node.Dashboard.GetAllTimeTickersAsync()).Count(t => tree.Contains(t.Id)));

        await node.Api.DeleteTimeTickerAsync(rootId);

        var listed = await node.Dashboard.GetAllTimeTickersAsync();
        Assert.DoesNotContain(listed, t => tree.Contains(t.Id));
    }
}
