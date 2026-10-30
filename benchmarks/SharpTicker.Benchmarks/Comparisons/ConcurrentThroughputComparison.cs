using System.Collections.Frozen;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.States;
using Quartz;
using Quartz.Impl;
using HangfireJob = Hangfire.Common.Job;
using SharpTicker.Utilities;

namespace SharpTicker.Benchmarks.Comparisons;

/// <summary>
/// Simulates concurrent job dispatch throughput across all three frameworks.
/// Measures how many jobs each framework can enqueue/dispatch per second under parallel load.
/// - SharpTicker: FrozenDictionary lookup + delegate invoke (the actual hot path)
/// - Hangfire: expression-tree parse + serialize + InMemory storage write
/// - Quartz: JobBuilder + TriggerBuilder + RAM scheduler write
///
/// Results Overview (Apple M4 Pro, .NET 10.0):
/// ┌───────────────────────────────┬──────┬──────────────┬───────────┬──────────────┐
/// │ Operation                     │ Jobs │ Time         │ Alloc     │ vs SharpTicker   │
/// ├───────────────────────────────┼──────┼──────────────┼───────────┼──────────────┤
/// │ SharpTicker: Parallel dispatch    │ 100  │ 2,876 ns     │ 2.6 KB    │ 1x (baseline)│
/// │ Hangfire: Parallel enqueue    │ 100  │ 323,348 ns   │ 727 KB    │ 112x slower  │
/// │ Quartz: Parallel schedule     │ 100  │ 498,816 ns   │ 278 KB    │ 173x slower  │
/// │ SharpTicker: Sequential dispatch  │ 100  │ 299 ns       │ 0 B       │ 0.1x         │
/// │ Hangfire: Sequential enqueue  │ 100  │ 319,903 ns   │ 722 KB    │ 111x slower  │
/// │ Quartz: Sequential schedule   │ 100  │ 347,123 ns   │ 295 KB    │ 121x slower  │
/// ├───────────────────────────────┼──────┼──────────────┼───────────┼──────────────┤
/// │ SharpTicker: Parallel dispatch    │ 1000 │ 14,046 ns    │ 3.7 KB    │ 1x (baseline)│
/// │ Hangfire: Parallel enqueue    │ 1000 │ 2,805,155 ns │ 7.1 MB    │ 200x slower  │
/// │ Quartz: Parallel schedule     │ 1000 │ 3,672,841 ns │ 2.2 MB    │ 262x slower  │
/// │ SharpTicker: Sequential dispatch  │ 1000 │ 2,986 ns     │ 0 B       │ 0.2x         │
/// │ Hangfire: Sequential enqueue  │ 1000 │ 4,051,634 ns │ 7.1 MB    │ 289x slower  │
/// │ Quartz: Sequential schedule   │ 1000 │ 3,547,540 ns │ 2.5 MB    │ 253x slower  │
/// └───────────────────────────────┴──────┴──────────────┴───────────┴──────────────┘
/// Winner: SharpTicker — 100-289x faster throughput, 1,985x less memory at 1000 jobs.
/// Sequential SharpTicker dispatches 1000 jobs in 2.99 us with zero allocations.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.HostProcess)]
public class ConcurrentThroughputComparison
{
    private FrozenDictionary<string, TickerFunctionDelegate> _sharptickerFunctions = null!;
    private string[] _sharptickerKeys = null!;
    private BackgroundJobClient _hangfireClient = null!;
    private InMemoryStorage _hangfireStorage = null!;
    private IScheduler _quartzScheduler = null!;
    private int _quartzCounter;

    [Params(100, 1000)]
    public int JobCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // SharpTicker: pre-built FrozenDictionary with 10 functions
        var dict = new Dictionary<string, TickerFunctionDelegate>();
        for (int i = 0; i < 10; i++)
            dict[$"MyApp.Jobs.Function_{i}"] = (_, _, _) => Task.CompletedTask;
        _sharptickerFunctions = dict.ToFrozenDictionary();
        _sharptickerKeys = dict.Keys.ToArray();

        // Hangfire
        _hangfireStorage = new InMemoryStorage();
        _hangfireClient = new BackgroundJobClient(_hangfireStorage);

        // Quartz
        _quartzScheduler = new StdSchedulerFactory().GetScheduler().GetAwaiter().GetResult();
        _quartzScheduler.Start().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _hangfireStorage?.Dispose();
        _quartzScheduler?.Shutdown(false).GetAwaiter().GetResult();
    }

    // ── SharpTicker: parallel lookup + invoke ──

    [Benchmark(Baseline = true, Description = "SharpTicker: Parallel dispatch")]
    public void SharpTicker_ParallelDispatch()
    {
        Parallel.For(0, JobCount, i =>
        {
            var key = _sharptickerKeys[i % _sharptickerKeys.Length];
            if (_sharptickerFunctions.TryGetValue(key, out var del))
                del(CancellationToken.None, null!, null!).GetAwaiter().GetResult();
        });
    }

    // ── Hangfire: parallel enqueue ──

    [Benchmark(Description = "Hangfire: Parallel enqueue")]
    public void Hangfire_ParallelEnqueue()
    {
        Parallel.For(0, JobCount, i =>
        {
            _hangfireClient.Create(
                HangfireJob.FromExpression(() => NoopMethod()),
                new EnqueuedState());
        });
    }

    // ── Quartz: parallel schedule ──

    [Benchmark(Description = "Quartz: Parallel schedule")]
    public void Quartz_ParallelSchedule()
    {
        Parallel.For(0, JobCount, i =>
        {
            var id = Interlocked.Increment(ref _quartzCounter);
            var job = JobBuilder.Create<NoopQuartzJob>()
                .WithIdentity($"job-{id}", "throughput")
                .Build();

            var trigger = TriggerBuilder.Create()
                .WithIdentity($"trigger-{id}", "throughput")
                .StartAt(DateTimeOffset.UtcNow.AddHours(1))
                .Build();

            _quartzScheduler.ScheduleJob(job, trigger).GetAwaiter().GetResult();
        });
    }

    // ── Sequential variants for comparison ──

    [Benchmark(Description = "SharpTicker: Sequential dispatch")]
    public void SharpTicker_SequentialDispatch()
    {
        for (int i = 0; i < JobCount; i++)
        {
            var key = _sharptickerKeys[i % _sharptickerKeys.Length];
            if (_sharptickerFunctions.TryGetValue(key, out var del))
                del(CancellationToken.None, null!, null!).GetAwaiter().GetResult();
        }
    }

    [Benchmark(Description = "Hangfire: Sequential enqueue")]
    public void Hangfire_SequentialEnqueue()
    {
        for (int i = 0; i < JobCount; i++)
        {
            _hangfireClient.Create(
                HangfireJob.FromExpression(() => NoopMethod()),
                new EnqueuedState());
        }
    }

    [Benchmark(Description = "Quartz: Sequential schedule")]
    public void Quartz_SequentialSchedule()
    {
        for (int i = 0; i < JobCount; i++)
        {
            var id = Interlocked.Increment(ref _quartzCounter);
            var job = JobBuilder.Create<NoopQuartzJob>()
                .WithIdentity($"job-{id}", "seq")
                .Build();

            var trigger = TriggerBuilder.Create()
                .WithIdentity($"trigger-{id}", "seq")
                .StartAt(DateTimeOffset.UtcNow.AddHours(1))
                .Build();

            _quartzScheduler.ScheduleJob(job, trigger).GetAwaiter().GetResult();
        }
    }

    public static void NoopMethod() { }

    public class NoopQuartzJob : IJob
    {
        public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
    }
}
