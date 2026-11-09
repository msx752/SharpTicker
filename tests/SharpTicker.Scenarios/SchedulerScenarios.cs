using SharpTicker.Scenarios.Harness;
using Xunit.Abstractions;

namespace SharpTicker.Scenarios;

/// <summary>Priority, the scheduler's own threads, and workers that come and go.</summary>
public class SchedulerScenarios(ITestOutputHelper output)
{
    private static Func<int, IReadOnlyDictionary<string, string>> Scheduler(int maxConcurrency, double? idleSeconds = null)
        => _ =>
        {
            var env = new Dictionary<string, string> { ["SCENARIO_MAX_CONCURRENCY"] = maxConcurrency.ToString() };
            if (idleSeconds != null)
                env["SCENARIO_IDLE_WORKER_TIMEOUT"] = idleSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return env;
        };

    // #4: one worker, held by a blocker; 30 jobs queue behind it, seeded Low first (earliest), then Normal, then High, so a
    // plain first-in, first-out queue would start L, N, H. Rule: after 10 higher priority jobs in a row while a Low one
    // waits, one Low job goes next. The order is decided in the process, not by the store; EF Core is used because it runs
    // on the code before the #4 fix (Redis could not read its rows there).
    [Theory]
    [InlineData(10, "H*10 L N*10 L*9")]
    [InlineData(5, "H*5 N*5 L*5")]
    public async Task PriorityDecidesTheStartOrder(int perPriority, string expected)
    {
        await using var env = await ScenarioEnvironment.StartAsync($"priority-{perPriority}", Store.EfCorePostgres, nodes: 1, nodeEnvironment: Scheduler(1));
        var node = env.Nodes[0];

        var blocker = await node.Api.AddTimeTickerAsync(new TickerSeed
        {
            Function = Functions.Blocker, ExecutionTime = DateTime.UtcNow,
        });
        await env.UntilAsync(() => Task.FromResult(env.Runs.Starts(blocker) == 1), () => "the blocker did not start");

        var due = DateTime.UtcNow.AddMilliseconds(500);
        var order = 0;
        foreach (var function in new[] { Functions.Low, Functions.Normal, Functions.High })
        {
            await node.Api.AddTimeTickersAsync(Enumerable.Range(0, perPriority)
                .Select(_ => new TickerSeed { Function = function, ExecutionTime = due.AddMilliseconds(10 * order++) }).ToArray());
        }

        await env.UntilAsync(() => Task.FromResult(env.Runs.Events.Count(e => e.IsStart && e.TickerId != blocker) == perPriority * 3),
            () => $"{env.Runs.Events.Count(e => e.IsStart && e.TickerId != blocker)} of {perPriority * 3} started");

        var blockerEnd = env.Runs.For(blocker).Single(e => e.IsEnd).At;
        var starts = env.Runs.Events.Where(e => e.IsStart && e.TickerId != blocker).OrderBy(e => e.Seq).ToList();
        Assert.All(starts, s => Assert.True(s.At >= blockerEnd, $"{s.Function} started before the blocker let go"));

        Assert.Equal(expected, Compress(starts.Select(s => s.Function switch
        {
            Functions.High => "H",
            Functions.Normal => "N",
            _ => "L",
        })));
    }

    // #61: the fix promises that a job's synchronous start runs on a SharpTicker.Worker-N thread, not on the .NET pool. The jobs
    // are claimed and queued (InProgress) behind a blocker before the pool is starved; the blocker lets go during the
    // starvation, and every queued job must start then.
    [Fact]
    public async Task JobsStartOnTheSchedulersOwnThreads_WhileThePoolIsStarved()
    {
        const int jobs = 5;
        await using var env = await ScenarioEnvironment.StartAsync("own-threads", Store.Redis, nodes: 1, nodeEnvironment: Scheduler(1));
        var node = env.Nodes[0];

        var blocker = await node.Api.AddTimeTickerAsync(new TickerSeed
        {
            Function = Functions.Blocker, ExecutionTime = DateTime.UtcNow,
        });
        await env.UntilAsync(() => Task.FromResult(env.Runs.Starts(blocker) == 1), () => "the blocker did not start",
            TimeSpan.FromSeconds(30));
        var seen = DateTime.UtcNow;

        var ids = await node.Api.AddTimeTickersAsync(Enumerable.Range(0, jobs)
            .Select(_ => new TickerSeed { Function = Functions.Record, ExecutionTime = DateTime.UtcNow }).ToArray());
        IReadOnlyList<TickerView> tickers = [];
        await Eventually.UntilAsync(async () =>
        {
            tickers = await node.Dashboard.GetAllTimeTickersAsync();
            return ids.All(id => tickers.Single(t => t.Id == id).Status == TickerStatus.InProgress);
        }, TimeSpan.FromSeconds(5), () => $"not all {jobs} jobs claimed: {string.Join(", ", tickers.Where(t => ids.Contains(t.Id)).Select(t => t.Status))}",
            env, TimeSpan.FromMilliseconds(100));

        // The blocker holds the worker for 8 s from its start; the starvation must begin before it lets go (2 s margin).
        Assert.True(DateTime.UtcNow - seen < TimeSpan.FromSeconds(6), "setup too slow: the pool would be starved after the blocker let go");
        var (from, until) = await node.Api.StarvePoolAsync(10);

        // While the pool is starved the node cannot answer HTTP: only the logs are read.
        await env.UntilAsync(() => Task.FromResult(ids.All(id => env.Runs.Starts(id) == 1)),
            () => $"{ids.Count(id => env.Runs.Starts(id) == 1)} of {jobs} started", TimeSpan.FromSeconds(30));

        var starts = ids.Select(id => env.Runs.For(id).Single(e => e.IsStart)).ToList();
        foreach (var s in starts)
        {
            output.WriteLine($"{s.TickerId}: started {s.At:O} on {s.Thread} (pool {s.Pool}), late {(s.At - s.ScheduledFor!.Value).TotalSeconds:0.000} s");
            Assert.True(s.At >= from && s.At < until, $"{s.TickerId} started at {s.At:O}, outside the starvation {from:O} - {until:O}");
            Assert.False(s.Pool, $"{s.TickerId} started on a pool thread");
            Assert.StartsWith("SharpTicker.Worker-", s.Thread);
        }

        output.WriteLine($"Lateness while the pool is starved: max {starts.Max(s => (s.At - s.ScheduledFor!.Value).TotalSeconds):0.000} s");
    }

    // #23: waves of exactly MaxConcurrency jobs sharing one time, with pauses longer than the idle timeout, so workers leave
    // and start again. No two jobs may overlap on one worker id, and the ids stay below MaxConcurrency. Worker ids are given
    // in the process, not by the store. The code before #23 had no worker thread names (#61 came later), so its control is
    // the current code with only the id taken from the number of running workers again: two jobs then overlap on one id.
    [Fact]
    public async Task WorkersLeavingAndStartingAgain_NeverShareAnId()
    {
        const int maxConcurrency = 4;
        await using var env = await ScenarioEnvironment.StartAsync("workers", Store.EfCorePostgres, nodes: 1,
            nodeEnvironment: Scheduler(maxConcurrency, idleSeconds: 1));
        var node = env.Nodes[0];

        var waves = new List<IReadOnlyList<Guid>>();
        var end = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < end)
        {
            var at = DateTime.UtcNow.AddSeconds(1);
            waves.Add(await node.Api.AddTimeTickersAsync(Enumerable.Range(0, maxConcurrency)
                .Select(_ => new TickerSeed { Function = Functions.Wave, ExecutionTime = at }).ToArray()));
            await Task.Delay(TimeSpan.FromSeconds(3.5));
        }

        var all = waves.SelectMany(w => w).ToList();
        await env.UntilAsync(() => Task.FromResult(all.All(id => env.Runs.Ends(id) == 1)),
            () => $"{all.Count(id => env.Runs.Ends(id) == 1)} of {all.Count} ended");

        foreach (var id in all)
            Assert.Equal(1, env.Runs.Starts(id));

        var runs = all.Select(id => (Start: env.Runs.For(id).Single(e => e.IsStart), End: env.Runs.For(id).Single(e => e.IsEnd))).ToList();
        foreach (var run in runs)
        {
            Assert.StartsWith("SharpTicker.Worker-", run.Start.Thread);
            var workerId = int.Parse(run.Start.Thread!["SharpTicker.Worker-".Length..]);
            Assert.InRange(workerId, 0, maxConcurrency - 1);
            Assert.True((run.Start.At - run.Start.ScheduledFor!.Value) <= TimeSpan.FromSeconds(3),
                $"{run.Start.TickerId} started {(run.Start.At - run.Start.ScheduledFor.Value).TotalSeconds:0.0} s late");
        }

        foreach (var perWorker in runs.GroupBy(r => r.Start.Thread))
        {
            var ordered = perWorker.OrderBy(r => r.Start.At).ToList();
            for (var i = 1; i < ordered.Count; i++)
                Assert.True(ordered[i].Start.At >= ordered[i - 1].End.At, $"two jobs overlap on {perWorker.Key}");
        }

        var lessThanFull = waves.Select(w => w.Select(id => env.Runs.For(id).Single(e => e.IsStart).Thread).Distinct().Count())
            .Count(threads => threads < maxConcurrency);
        output.WriteLine($"{waves.Count} waves, {lessThanFull} used fewer than {maxConcurrency} worker threads");
        Assert.True(lessThanFull == 0, $"{lessThanFull} of {waves.Count} waves used fewer than {maxConcurrency} worker threads");

        var (status, restarts) = await env.NodeStateAsync(0);
        Assert.Equal("running", status);
        Assert.Equal(0, restarts);
    }

    /// <summary>"H H H L N" -> "H*3 L N".</summary>
    private static string Compress(IEnumerable<string> letters)
    {
        var parts = new List<string>();
        foreach (var letter in letters)
        {
            if (parts.Count > 0 && parts[^1].Split('*')[0] == letter)
            {
                var count = parts[^1].Contains('*') ? int.Parse(parts[^1].Split('*')[1]) : 1;
                parts[^1] = $"{letter}*{count + 1}";
            }
            else
            {
                parts.Add(letter);
            }
        }

        return string.Join(' ', parts);
    }
}
