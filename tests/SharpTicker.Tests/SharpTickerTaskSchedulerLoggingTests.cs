using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpTicker.SharpTickerThreadPool;
using SharpTicker.Utilities.Enums;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// What the task scheduler writes to the log. A job normally reports its own failure through the ticker handler, so an
/// exception that reaches the scheduler is a bug somewhere else, and without a log line it would vanish. Cancelling a job is
/// ordinary and is not logged.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class SharpTickerTaskSchedulerLoggingTests
{
    private readonly CapturedLogs _logs = new();

    private SharpTickerTaskScheduler NewScheduler(int maxConcurrency)
        => new(maxConcurrency, TimeSpan.FromSeconds(60), null, _logs.CreateLogger("SharpTicker.TaskScheduler"));

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    private CapturedLogs.Entry[] Errors() => _logs.Entries.Where(e => e.Level == LogLevel.Error).ToArray();

    [Fact]
    public async Task AJobThatReturnsAFailedTask_IsLogged()
    {
        await using var scheduler = NewScheduler(1);

        await scheduler.QueueAsync(_ => Task.FromException(new InvalidOperationException("boom-returned")), TickerTaskPriority.Normal);

        Assert.True(await WaitUntilAsync(() => Errors().Any(e => e.Exception?.Message == "boom-returned")), "the failure was not logged");
    }

    [Fact]
    public async Task AJobThatFailsAfterItHasStarted_IsLogged()
    {
        await using var scheduler = NewScheduler(1);

        await scheduler.QueueAsync(async _ => { await Task.Delay(20); throw new InvalidOperationException("boom-later"); }, TickerTaskPriority.Normal);

        Assert.True(await WaitUntilAsync(() => Errors().Any(e => e.Exception?.Message == "boom-later")), "the failure was not logged");
    }

    [Fact]
    public async Task AJobThatThrowsInsteadOfReturningATask_IsLogged()
    {
        await using var scheduler = NewScheduler(1);

        await scheduler.QueueAsync(_ => throw new InvalidOperationException("boom-thrown"), TickerTaskPriority.Normal);

        Assert.True(await WaitUntilAsync(() => Errors().Any(e => e.Exception?.Message == "boom-thrown")), "the failure was not logged");
    }

    [Fact]
    public async Task ALongRunningJobThatFails_IsLogged()
    {
        await using var scheduler = NewScheduler(1);

        await scheduler.QueueAsync(_ => throw new InvalidOperationException("boom-long"), TickerTaskPriority.LongRunning);

        Assert.True(await WaitUntilAsync(() => Errors().Any(e => e.Exception?.Message == "boom-long")), "the failure was not logged");
    }

    [Fact]
    public async Task ACancelledJob_IsNotLogged()
    {
        await using var scheduler = NewScheduler(1);
        var after = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await scheduler.QueueAsync(_ => throw new OperationCanceledException(), TickerTaskPriority.Normal);
        await scheduler.QueueAsync(_ => Task.FromCanceled(new CancellationToken(true)), TickerTaskPriority.Normal);
        await scheduler.QueueAsync(_ => { after.TrySetResult(true); return Task.CompletedTask; }, TickerTaskPriority.Normal);
        await after.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100);

        Assert.Empty(_logs.Entries);
    }

    /// <summary>
    /// Disposing waits five seconds for the workers and drops what is still queued. The jobs that never ran are the thing a
    /// host operator would look for afterwards, so their number is logged.
    /// </summary>
    [Fact]
    public async Task DisposingWithJobsStillQueued_LogsHowManyWereDropped()
    {
        var scheduler = NewScheduler(1);
        using var holdJob = new ManualResetEventSlim(false);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await scheduler.QueueAsync(_ => { started.TrySetResult(true); holdJob.Wait(); return Task.CompletedTask; }, TickerTaskPriority.Normal);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 3; i++)
            await scheduler.QueueAsync(_ => Task.CompletedTask, TickerTaskPriority.Normal);

        try { await scheduler.DisposeAsync(); }
        finally { holdJob.Set(); }

        var warning = Assert.Single(_logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("3", warning.Message);
    }
}
