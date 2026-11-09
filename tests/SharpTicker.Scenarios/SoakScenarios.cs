using System.Globalization;
using SharpTicker.Scenarios.Harness;
using Xunit.Abstractions;

namespace SharpTicker.Scenarios;

/// <summary>
/// The mixed load of the composite scenarios for an hour without faults, then half an hour with faults. Runs only with SCENARIO_SOAK=1
/// (about 1.5 h per store; the three stores are rows of one theory and run one after another, about 4.5 h).
/// </summary>
public class SoakScenarios(ITestOutputHelper output)
{
    private static readonly TimeSpan Calm = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan WithFaults = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Warm = TimeSpan.FromMinutes(10);

    [SoakTheory]
    [Trait("Category", "Soak")]
    [InlineData(Store.EfCorePostgres)]
    [InlineData(Store.MongoDb)]
    [InlineData(Store.Redis)]
    public async Task MixedLoad_OneHourCalmThenHalfAnHourWithFaults(Store store)
    {
        await using var env = await ScenarioEnvironment.StartAsync("soak", store, nodes: 3);
        var reader = env.Nodes[0];
        var crons = new[]
        {
            await reader.Api.AddCronAsync(Functions.Record, "*/5 * * * * *"),
            await reader.Api.AddCronAsync(Functions.OneAtATime, "*/7 * * * * *"),
        };
        var farCron = await reader.Api.AddCronAsync(Functions.Record, Crons.Far);

        using var sampling = new CancellationTokenSource();
        var samples = new List<(TimeSpan At, int Node, string Container, long Bytes)>();
        var sampler = SampleMemoryAsync(env, samples, sampling.Token);

        List<Guid> due;
        TimeSpan calmEnded;
        ChaosMonkey monkey;
        try
        {
            // Calm: no faults, so every node keeps its first container and its memory can be compared.
            due = await CompositeScenarios.SeedMixedLoadAsync(reader, farCron, Calm, output);
            calmEnded = Elapsed(env);

            monkey = new ChaosMonkey(env);
            monkey.Spared.Add(0);
            output.WriteLine($"seed {monkey.Seed}");
            due.AddRange(await monkey.RunWhileAsync(WithFaults, () => CompositeScenarios.SeedMixedLoadAsync(reader, farCron, WithFaults, output)));

            await CompositeScenarios.AssertInvariantsAfterSettling(env, reader, due, crons.Append(farCron).ToList(), monkey.Faults, store, output,
                occurrencesDueBefore: DateTime.UtcNow.AddSeconds(-10));
        }
        finally
        {
            // Also when anything above throws: the sampler must not go on polling a disposed environment.
            await sampling.CancelAsync();
            await sampler;
        }

        // Memory: in the calm hour, growth after the warm-up below half of the warm-up value, per node.
        List<(TimeSpan At, int Node, string Container, long Bytes)> calm;
        lock (samples)
            calm = samples.Where(s => s.At <= calmEnded).ToList();
        foreach (var node in calm.GroupBy(s => s.Node))
        {
            var warm = node.Where(s => s.At >= Warm).OrderBy(s => s.At).First();
            var last = node.Where(s => s.Container == warm.Container).OrderBy(s => s.At).Last();
            output.WriteLine($"node {node.Key}: {Mb(warm.Bytes)} at {warm.At.TotalMinutes:0} min, {Mb(last.Bytes)} at {last.At.TotalMinutes:0} min");
            Assert.True(last.Bytes - warm.Bytes < warm.Bytes / 2,
                $"node {node.Key} grew from {Mb(warm.Bytes)} ({warm.At.TotalMinutes:0} min) to {Mb(last.Bytes)} ({last.At.TotalMinutes:0} min) in the calm hour");
        }

        // No row left Idle with a lock holder: a release clears the holder.
        var all = (await reader.Dashboard.GetAllTimeTickersAsync()).ToList();
        var held = all.Where(t => t.Status == TickerStatus.Idle && !string.IsNullOrEmpty(t.LockHolder)).ToList();
        Assert.True(held.Count == 0, $"{held.Count} rows Idle with a lock holder: {string.Join(", ", held.Take(10).Select(t => $"{t.Id} {t.LockHolder}"))}");

        if (store == Store.Redis)
        {
            var problems = await RedisIndexCheck.LastingProblemsAsync(env.DirectStoreConnection, TimeSpan.FromSeconds(5));
            Assert.True(problems.Count == 0, $"{problems.Count} Redis index problems: {string.Join("; ", problems.Take(20))}");
        }

        output.WriteLine($"{due.Count} due jobs, {monkey.Faults.Count} faults");
    }

    private static TimeSpan Elapsed(ScenarioEnvironment env) => DateTimeOffset.UtcNow - env.StartedAt;

    private static string Mb(long bytes) => $"{bytes / (1024.0 * 1024):0.0} MiB";

    // Every 30 s, each node; written to memory.csv in the log folder as it goes.
    private static async Task SampleMemoryAsync(ScenarioEnvironment env, List<(TimeSpan, int, string, long)> samples, CancellationToken ct)
    {
        var file = Path.Combine(env.LogFolder, "memory.csv");
        await File.WriteAllTextAsync(file, "minutes,node,container,bytes" + Environment.NewLine, CancellationToken.None);
        while (!ct.IsCancellationRequested)
        {
            for (var i = 0; i < env.Nodes.Count; i++)
            {
                try
                {
                    var (container, bytes) = await env.NodeMemoryAsync(i);
                    var at = Elapsed(env);
                    lock (samples)
                        samples.Add((at, i, container, bytes));
                    await File.AppendAllTextAsync(file, string.Create(CultureInfo.InvariantCulture,
                        $"{at.TotalMinutes:0.0},{i},{container[..12]},{bytes}{Environment.NewLine}"), CancellationToken.None);
                }
                catch (Exception)
                {
                    // A node between a stop and its start has no container: no sample this round.
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
