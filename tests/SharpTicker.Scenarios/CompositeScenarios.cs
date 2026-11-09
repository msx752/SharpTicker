using SharpTicker.Scenarios.Harness;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace SharpTicker.Scenarios;

/// <summary>
/// The fixes together. Mixed load with random faults, and pairs of faults that meet in one place.
/// After the faults stop, the invariants must hold; every documented exception is matched to its cause.
/// </summary>
public class CompositeScenarios(ITestOutputHelper output)
{
    private static readonly TimeSpan Settle = TimeSpan.FromMinutes(3);

    [Theory]
    [InlineData(Store.EfCorePostgres)]
    [InlineData(Store.MongoDb)]
    [InlineData(Store.Redis)]
    public async Task MixedLoadWithChaos_KeepsTheInvariants(Store store)
    {
        await using var env = await ScenarioEnvironment.StartAsync("mixed-chaos", store, nodes: 3);
        var reader = env.Nodes[0];
        var monkey = new ChaosMonkey(env);
        monkey.Spared.Add(0); // the node the test seeds through and reads from
        output.WriteLine($"seed {monkey.Seed}");

        var crons = new[]
        {
            await reader.Api.AddCronAsync(Functions.Record, "*/5 * * * * *"),
            await reader.Api.AddCronAsync(Functions.OneAtATime, "*/7 * * * * *"),
        };
        var farCron = await reader.Api.AddCronAsync(Functions.Record, Crons.Far);

        var duration = TimeSpan.FromMinutes(5);
        var due = await monkey.RunWhileAsync(duration, () => SeedMixedLoadAsync(reader, farCron, duration, output));
        // The crons keep running; their occurrences due well before the load ended must have ended too.
        await AssertInvariantsAfterSettling(env, reader, due, crons.Append(farCron).ToList(), monkey.Faults, store, output,
            occurrencesDueBefore: DateTime.UtcNow.AddSeconds(-10));
        output.WriteLine($"{due.Count} due jobs, {monkey.Faults.Count} faults: {string.Join(", ", monkey.Faults.GroupBy(f => f.Type).Select(g => $"{g.Key} x{g.Count()}"))}");
    }

    /// <summary>
    /// The mixed load, through <paramref name="reader"/>, for <paramref name="duration"/>: every 3 s plain jobs, a
    /// one-at-a-time job and a slow one; every third round a chain with an inline child, a grandchild and a delayed child;
    /// every tenth round a run-now of <paramref name="farCron"/>. Returns the jobs added (a round that failed is left out).
    /// </summary>
    internal static async Task<List<Guid>> SeedMixedLoadAsync(Node reader, Guid farCron, TimeSpan duration, ITestOutputHelper output)
    {
        var due = new List<Guid>();
        var end = DateTime.UtcNow + duration;
        var round = 0;
        while (DateTime.UtcNow < end)
        {
            var now = DateTime.UtcNow;
            try
            {
                due.AddRange(await reader.Api.AddTimeTickersAsync(
                    new TickerSeed { Function = Functions.Record, ExecutionTime = now.AddSeconds(1) },
                    new TickerSeed { Function = Functions.Record, ExecutionTime = now.AddSeconds(3) },
                    new TickerSeed { Function = Functions.OneAtATime, ExecutionTime = now.AddSeconds(2), Request = new WaitRequest { Seconds = 3 } },
                    new TickerSeed { Function = Functions.Slow, ExecutionTime = now.AddSeconds(2), Request = new WaitRequest { Seconds = round % 3 == 0 ? 20 : 4 } }));
                if (round % 3 == 0)
                {
                    var (_, tree) = await reader.Api.AddChainAsync(new TickerSeed
                    {
                        Function = Functions.ChainStep, ExecutionTime = now.AddSeconds(1), Request = new WaitRequest { Seconds = 2 },
                        Children =
                        [
                            new TickerSeed { Function = Functions.ChainStep, Request = new WaitRequest { Seconds = 1 }, RunCondition = RunCondition.OnSuccess,
                                Children = [new TickerSeed { Function = Functions.Record, RunCondition = RunCondition.OnSuccess }] },
                            new TickerSeed { Function = Functions.Record, ExecutionTime = now.AddSeconds(5), RunCondition = RunCondition.OnAnyCompletedStatus },
                        ],
                    });
                    due.AddRange(tree);
                }

                if (round % 10 == 0)
                    await reader.Dashboard.RunCronNowAsync(farCron);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException { InnerException: TimeoutException })
            {
                // The store is cut or slow (an error reply, or no reply within the client's timeout): what this round added
                // is unknown; it is not counted as due, the invariants on double starts still cover it.
                output.WriteLine($"round {round}: seeding failed: {ex.Message.Split('\n')[0]}");
            }

            round++;
            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        return due;
    }

    [Theory]
    [InlineData(Store.EfCorePostgres)]
    [InlineData(Store.MongoDb)]
    [InlineData(Store.Redis)]
    public async Task AStoreCutWhileAChainRuns_KeepsTheInvariants(Store store)
    {
        await using var env = await ScenarioEnvironment.StartAsync("pair-cut-chain", store, nodes: 2);
        var node = env.Nodes[0];
        var now = DateTime.UtcNow;
        var (root, tree) = await node.Api.AddChainAsync(new TickerSeed
        {
            Function = Functions.ChainStep, ExecutionTime = now, Request = new WaitRequest { Seconds = 3 },
            Children =
            [
                new TickerSeed { Function = Functions.ChainStep, Request = new WaitRequest { Seconds = 2 }, RunCondition = RunCondition.OnSuccess },
                new TickerSeed { Function = Functions.Record, ExecutionTime = now.AddSeconds(5), RunCondition = RunCondition.OnSuccess },
            ],
        });
        await env.UntilAsync(() => Task.FromResult(env.Runs.Starts(root) == 1), () => "the parent did not start");

        var cutFrom = DateTimeOffset.UtcNow;
        await env.CutStoreAsync(TimeSpan.FromSeconds(10));
        var faults = new List<FaultEvent> { new("cut") { At = cutFrom, Until = DateTimeOffset.UtcNow } };

        await AssertInvariantsAfterSettling(env, node, tree, [], faults, store, output);
    }

    [Theory]
    [InlineData(Store.EfCorePostgres)]
    [InlineData(Store.MongoDb)]
    [InlineData(Store.Redis)]
    public async Task AKillWhileAJobWaitsForItsSlot_KeepsTheInvariants(Store store)
    {
        await using var env = await ScenarioEnvironment.StartAsync("pair-kill-slot", store, nodes: 2, startedNodes: 1);
        var ids = await env.Nodes[0].Api.AddTimeTickersAsync(
            new TickerSeed { Function = Functions.OneAtATime, ExecutionTime = DateTime.UtcNow, Request = new WaitRequest { Seconds = 15 } },
            new TickerSeed { Function = Functions.OneAtATime, ExecutionTime = DateTime.UtcNow, Request = new WaitRequest { Seconds = 15 } });
        IReadOnlyList<TickerView> tickers = [];
        await env.UntilAsync(async () =>
        {
            tickers = await env.Nodes[0].Dashboard.GetAllTimeTickersAsync();
            return ids.Sum(id => env.Runs.Starts(id)) == 1 && ids.All(id => tickers.Single(t => t.Id == id).Status == TickerStatus.InProgress);
        }, () => "node 0 does not hold both jobs");

        await env.StartNodeAsync(1);
        var faults = new List<FaultEvent> { new("kill", env.Nodes[0].Identifier) };
        await env.KillNodeAsync(0);
        await env.StartNodeAsync(0);

        await AssertInvariantsAfterSettling(env, env.Nodes[1], ids, [], faults, store, output);
    }

    [Theory]
    [InlineData(Store.EfCorePostgres)]
    [InlineData(Store.MongoDb)]
    [InlineData(Store.Redis)]
    public async Task RunNowDuringAStoreCut_KeepsTheInvariants(Store store)
    {
        await using var env = await ScenarioEnvironment.StartAsync("pair-runnow-cut", store, nodes: 2);
        var node = env.Nodes[0];
        var cron = await node.Api.AddCronAsync(Functions.Record, Crons.Far);

        var cutFrom = DateTimeOffset.UtcNow;
        var cut = env.CutStoreAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromSeconds(1));
        bool answered;
        try
        {
            await node.Dashboard.RunCronNowAsync(cron);
            answered = true;
            output.WriteLine("run-now answered success during the cut");
        }
        catch (Exception ex)
        {
            answered = false;
            output.WriteLine($"run-now failed during the cut: {ex.Message.Split('\n')[0]}");
        }

        await cut;
        var faults = new[] { new FaultEvent("cut") { At = cutFrom, Until = DateTimeOffset.UtcNow } };
        await AssertInvariantsAfterSettling(env, node, [], [cron], faults, store, output, occurrencesDueBefore: DateTime.UtcNow.AddDays(1));

        // The cron's next regular run is months away, so an occurrence near now is the run-now one. A success reply means it
        // was stored (and, above, ended); a failure may still have stored it before the reply was lost, but never twice.
        var runNow = (await node.Dashboard.GetOccurrencesAsync(cron)).Count(o => o.ExecutionTime < DateTime.UtcNow.AddDays(1));
        Assert.True(answered ? runNow == 1 : runNow <= 1, $"run-now {(answered ? "succeeded" : "failed")} and {runNow} occurrence(s) are near now");
    }

    [Theory]
    [InlineData(Store.EfCorePostgres)]
    [InlineData(Store.MongoDb)]
    [InlineData(Store.Redis)]
    public async Task AGracefulStopWhileTheFallbackIsInAPass_KeepsTheInvariants(Store store)
    {
        await using var env = await ScenarioEnvironment.StartAsync("pair-stop-fallback", store, nodes: 2, startedNodes: 1);

        // Seeded through node 0, which stops before they are due; they are overdue when node 1 starts, and node 1's
        // fallback pass over them is cut by a graceful stop right after its first start.
        var start = DateTime.UtcNow;
        var ids = await env.Nodes[0].Api.AddTimeTickersAsync(Enumerable.Range(0, 60)
            .Select(_ => new TickerSeed { Function = Functions.Slow, ExecutionTime = start.AddSeconds(3), Request = new WaitRequest { Seconds = 1 } }).ToArray());
        await env.StopNodeAsync(0);
        var wait = start.AddSeconds(6) - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait);

        await env.StartNodeAsync(1);
        await env.UntilAsync(() => Task.FromResult(ids.Any(id => env.Runs.Starts(id) > 0)), () => "node 1 started none of them");
        var faults = new List<FaultEvent> { new("stop", env.Nodes[1].Identifier) };
        await env.StopNodeAsync(1);
        await env.StartNodeAsync(0);

        await AssertInvariantsAfterSettling(env, env.Nodes[0], ids, [], faults, store, output);
    }

    /// <summary>
    /// Waits until every due job ended (or is InProgress for a known cause), then checks all invariants, the cron
    /// occurrences of <paramref name="crons"/> included.
    /// </summary>
    internal static async Task AssertInvariantsAfterSettling(ScenarioEnvironment env, Node reader, IReadOnlyList<Guid> due,
        IReadOnlyList<Guid> crons, IReadOnlyList<FaultEvent> faults, Store store, ITestOutputHelper output, DateTime? occurrencesDueBefore = null)
    {
        IReadOnlyList<TickerView> all = [];
        string last = "";
        List<Guid> DueNow() => due.Concat(all.Where(t => t.Function == "occurrence" && t.ExecutionTime < occurrencesDueBefore).Select(t => t.Id)).ToList();
        await env.UntilAsync(async () =>
        {
            all = await AllAsync(reader, crons);
            try
            {
                Invariants.AssertEveryDueJobEnded(all, DueNow(), env.Runs, faults, store);
                return true;
            }
            catch (XunitException ex)
            {
                last = ex.Message;
                return false;
            }
        }, () => last, Settle);

        // Let a late second start show before counting.
        await Task.Delay(TimeSpan.FromSeconds(5));
        all = await AllAsync(reader, crons);
        Invariants.AssertEveryDueJobEnded(all, DueNow(), env.Runs, faults, store);
        var reruns = Invariants.AssertNoUnexplainedDoubleStart(env.Runs, faults);
        // A cron occurrence due after the cutoff may be running right now; it is not judged.
        var judged = all.Where(t => t.Function != "occurrence" || t.ExecutionTime < occurrencesDueBefore).ToList();
        Invariants.AssertNothingInProgressExceptKnownLimits(judged, env.Runs, faults, store);
        Invariants.AssertDelayedChildrenOrder(all, env.Runs);

        var limits = all.Where(t => t.Status == TickerStatus.InProgress)
            .Select(t => Invariants.KnownLimit(t, all, env.Runs, faults, store)).GroupBy(l => l).Select(g => $"{g.Key} x{g.Count()}");
        output.WriteLine($"re-runs after a kill: {reruns.AfterKill}; re-runs of a job whose final status write failed (#74), after its node restarted: {reruns.After74}; " +
                         $"InProgress for a known cause: {string.Join(", ", limits)}");
    }

    private static async Task<IReadOnlyList<TickerView>> AllAsync(Node reader, IReadOnlyList<Guid> crons)
    {
        var tickers = (await reader.Dashboard.GetAllTimeTickersAsync()).ToList();
        foreach (var cron in crons)
        {
            tickers.AddRange((await reader.Dashboard.GetOccurrencesAsync(cron))
                .Select(o => new TickerView { Id = o.Id, Status = o.Status, LockHolder = o.LockHolder, Function = "occurrence", ExecutionTime = o.ExecutionTime }));
        }

        return tickers;
    }
}
