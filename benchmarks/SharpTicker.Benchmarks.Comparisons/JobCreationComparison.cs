using BenchmarkDotNet.Attributes;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Interfaces.Managers;

namespace SharpTicker.Benchmarks.Comparisons;

/// <summary>
/// Scheduling one one-off job, due in one hour, into each library's in-memory store, with the same no-op job:
/// - SharpTicker: ITimeTickerManager.AddAsync with the in-memory store (background services off).
/// - Hangfire: IBackgroundJobClient.Schedule on Hangfire.InMemory (expression, job serialization, storage write).
/// - Quartz: IScheduler.ScheduleJob with a job and a one-shot trigger on RAMJobStore (scheduler not started).
/// Nothing runs the jobs. Each iteration starts with empty stores and adds a fixed number of jobs (InvocationCount),
/// so the stores stay bounded; the stores are emptied in cleanup, which is not measured.
/// </summary>
[MemoryDiagnoser]
[InvocationCount(2048, 1)]
public class JobCreationComparison
{
    private ServiceProvider _sharpTicker = null!;
    private ITimeTickerManager<TimeTickerEntity> _timeTickerManager = null!;
    private readonly List<Guid> _sharpTickerIds = new(2048);

    private InMemoryStorage _hangfireStorage = null!;
    private BackgroundJobClient _hangfireClient = null!;

    private IScheduler _quartzScheduler = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _sharpTicker = NoopJobs.CreateSharpTickerInMemory();
        _timeTickerManager = _sharpTicker.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
        _quartzScheduler = NoopJobs.CreateQuartzScheduler();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _sharpTicker.Dispose();
        _quartzScheduler.Shutdown(false).GetAwaiter().GetResult();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _hangfireStorage = new InMemoryStorage();
        _hangfireClient = new BackgroundJobClient(_hangfireStorage);
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        if (_sharpTickerIds.Count > 0)
        {
            _timeTickerManager.DeleteBatchAsync(_sharpTickerIds).GetAwaiter().GetResult();
            _sharpTickerIds.Clear();
        }

        _hangfireStorage.Dispose();
        _quartzScheduler.Clear().GetAwaiter().GetResult();
    }

    [Benchmark(Baseline = true, Description = "SharpTicker: ITimeTickerManager.AddAsync (in-memory store)")]
    public async Task<Guid> SharpTicker_AddAsync()
    {
        var result = await _timeTickerManager.AddAsync(new TimeTickerEntity
        {
            Function = NoopJobs.SharpTickerFunction,
            ExecutionTime = DateTime.UtcNow.AddHours(1)
        });

        if (!result.IsSucceeded)
            throw new InvalidOperationException("AddAsync failed", result.Exception);

        _sharpTickerIds.Add(result.Result.Id);
        return result.Result.Id;
    }

    [Benchmark(Description = "Hangfire: BackgroundJobClient.Schedule (Hangfire.InMemory)")]
    public string Hangfire_Schedule() =>
        _hangfireClient.Schedule(() => NoopJobs.HangfireNoop(), TimeSpan.FromHours(1));

    [Benchmark(Description = "Quartz: IScheduler.ScheduleJob, one-shot trigger (RAMJobStore)")]
    public Task<DateTimeOffset> Quartz_ScheduleJob()
    {
        var job = JobBuilder.Create<NoopJobs.QuartzNoopJob>().Build();
        var trigger = TriggerBuilder.Create().StartAt(DateTimeOffset.UtcNow.AddHours(1)).Build();
        return _quartzScheduler.ScheduleJob(job, trigger);
    }
}
