using BenchmarkDotNet.Attributes;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Interfaces.Managers;

namespace SharpTicker.Benchmarks.Comparisons;

/// <summary>
/// Setting up each library with an empty in-memory store and registering JobCount recurring jobs, with the same no-op job
/// and the same schedules (every k minutes), in one invocation:
/// - SharpTicker: build a service provider with AddSharpTicker (in-memory store, background services off), then
///   ICronTickerManager.AddAsync per job (validates the expression and computes its next occurrence in the scheduler's time zone).
/// - Hangfire: new InMemoryStorage, then RecurringJobManager.AddOrUpdate per job.
/// - Quartz: a new scheduler on RAMJobStore (not started), then ScheduleJob with a cron trigger per job, then Shutdown.
/// Nothing runs the jobs. SharpTicker's function registry is built once per process (as the source generator registers it),
/// not per invocation. SharpTicker's in-memory store is process-wide, so the cron tickers added in an iteration are removed
/// in cleanup, which is not measured.
/// </summary>
[MemoryDiagnoser]
[InvocationCount(16, 1)]
public class StartupRegistrationComparison
{
    private readonly List<Guid> _sharpTickerIds = new();
    private ServiceProvider _cleanupProvider = null!;

    [Params(5, 25, 100)]
    public int JobCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        NoopJobs.EnsureSharpTickerFunction();
        _cleanupProvider = NoopJobs.CreateSharpTickerInMemory();
    }

    [GlobalCleanup]
    public void Cleanup() => _cleanupProvider.Dispose();

    [IterationCleanup]
    public void IterationCleanup()
    {
        if (_sharpTickerIds.Count == 0)
            return;

        _cleanupProvider.GetRequiredService<ICronTickerManager<CronTickerEntity>>()
            .DeleteBatchAsync(_sharpTickerIds).GetAwaiter().GetResult();
        _sharpTickerIds.Clear();
    }

    [Benchmark(Baseline = true, Description = "SharpTicker: AddSharpTicker + ICronTickerManager.AddAsync per job")]
    public async Task SharpTicker_Register()
    {
        await using var provider = NoopJobs.CreateSharpTickerInMemory();
        var manager = provider.GetRequiredService<ICronTickerManager<CronTickerEntity>>();

        for (var i = 0; i < JobCount; i++)
        {
            var result = await manager.AddAsync(new CronTickerEntity
            {
                Function = NoopJobs.SharpTickerFunction,
                Expression = $"0 */{(i % 59) + 1} * * * *"
            });

            if (!result.IsSucceeded)
                throw new InvalidOperationException("AddAsync failed", result.Exception);

            _sharpTickerIds.Add(result.Result.Id);
        }
    }

    [Benchmark(Description = "Hangfire: InMemoryStorage + RecurringJobManager.AddOrUpdate per job")]
    public void Hangfire_Register()
    {
        using var storage = new InMemoryStorage();
        var manager = new RecurringJobManager(storage);

        for (var i = 0; i < JobCount; i++)
            manager.AddOrUpdate($"job-{i}", Job.FromExpression(() => NoopJobs.HangfireNoop()), $"*/{(i % 59) + 1} * * * *");
    }

    [Benchmark(Description = "Quartz: new scheduler (RAMJobStore) + ScheduleJob with cron trigger per job")]
    public async Task Quartz_Register()
    {
        var scheduler = NoopJobs.CreateQuartzScheduler();

        for (var i = 0; i < JobCount; i++)
        {
            var job = JobBuilder.Create<NoopJobs.QuartzNoopJob>().WithIdentity($"job-{i}").Build();
            var trigger = TriggerBuilder.Create()
                .WithIdentity($"trigger-{i}")
                .WithCronSchedule($"0 0/{(i % 59) + 1} * * * ?")
                .Build();

            await scheduler.ScheduleJob(job, trigger);
        }

        await scheduler.Shutdown(false);
    }
}
