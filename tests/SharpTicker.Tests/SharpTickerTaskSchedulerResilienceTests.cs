using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpTicker.SharpTickerThreadPool;
using SharpTicker.Utilities.Enums;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// What keeps the task scheduler working when jobs misbehave or pile up: a job that fails does not stop its worker, a
/// full queue makes the caller wait instead of growing without limit, idle workers leave but one stays, and the
/// token a job was queued with is the token it receives.
/// </summary>
public class SharpTickerTaskSchedulerResilienceTests
{
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

    #region Guards

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANumberOfWorkersBelowOne_IsRefused(int maxConcurrency)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new SharpTickerTaskScheduler(maxConcurrency));

    [Fact]
    public async Task QueueingNoWork_IsRefused()
    {
        await using var scheduler = new SharpTickerTaskScheduler(1);

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await scheduler.QueueAsync(null!, TickerTaskPriority.Normal));
    }

    [Fact]
    public async Task TheJobReceivesTheTokenItWasQueuedWith()
    {
        await using var scheduler = new SharpTickerTaskScheduler(1);
        using var source = new CancellationTokenSource();
        var received = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);

        await scheduler.QueueAsync(ct => { received.TrySetResult(ct); return Task.CompletedTask; }, TickerTaskPriority.Normal, source.Token);

        Assert.Equal(source.Token, await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    #endregion

    #region Jobs that fail

    [Fact]
    public async Task AJobThatFailsStraightAway_DoesNotStopTheWorker()
    {
        await using var scheduler = new SharpTickerTaskScheduler(1);
        var ran = 0;

        await scheduler.QueueAsync(_ => throw new InvalidOperationException("fails before returning a task"), TickerTaskPriority.Normal);
        await scheduler.QueueAsync(_ => { Interlocked.Increment(ref ran); return Task.CompletedTask; }, TickerTaskPriority.Normal);
        await scheduler.QueueAsync(_ => { Interlocked.Increment(ref ran); return Task.CompletedTask; }, TickerTaskPriority.Normal);

        Assert.True(await WaitUntilAsync(() => Volatile.Read(ref ran) == 2), "the jobs after a failing job did not run");
    }

    [Fact]
    public async Task AJobThatReturnsAFailedTask_DoesNotStopTheWorker()
    {
        await using var scheduler = new SharpTickerTaskScheduler(1);
        var ran = 0;

        await scheduler.QueueAsync(_ => Task.FromException(new InvalidOperationException("already failed")), TickerTaskPriority.Normal);
        await scheduler.QueueAsync(_ => { Interlocked.Increment(ref ran); return Task.CompletedTask; }, TickerTaskPriority.Normal);

        Assert.True(await WaitUntilAsync(() => Volatile.Read(ref ran) == 1));
    }

    [Fact]
    public async Task AJobThatFailsLater_DoesNotStopTheWorker()
    {
        await using var scheduler = new SharpTickerTaskScheduler(1);
        var ran = 0;

        await scheduler.QueueAsync(async _ => { await Task.Delay(20); throw new InvalidOperationException("fails after awaiting"); }, TickerTaskPriority.Normal);
        await scheduler.QueueAsync(_ => { Interlocked.Increment(ref ran); return Task.CompletedTask; }, TickerTaskPriority.Normal);

        Assert.True(await WaitUntilAsync(() => Volatile.Read(ref ran) == 1));
    }

    [Fact]
    public async Task ALongRunningJobThatFails_IsSwallowed_AndTheSchedulerKeepsWorking()
    {
        await using var scheduler = new SharpTickerTaskScheduler(1);
        var ran = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await scheduler.QueueAsync(_ => throw new InvalidOperationException("a long running job failed"), TickerTaskPriority.LongRunning);
        await scheduler.QueueAsync(_ => { ran.TrySetResult(true); return Task.CompletedTask; }, TickerTaskPriority.Normal);

        Assert.True(await ran.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task TheQueuedCounter_ReturnsToZero_AfterFailedAndSkippedJobs()
    {
        await using var scheduler = new SharpTickerTaskScheduler(2);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        for (var i = 0; i < 20; i++)
        {
            await scheduler.QueueAsync(_ => throw new InvalidOperationException("fails"), TickerTaskPriority.Normal);
            await scheduler.QueueAsync(_ => Task.CompletedTask, TickerTaskPriority.Normal, cancelled.Token);
            await scheduler.QueueAsync(_ => Task.CompletedTask, TickerTaskPriority.Normal);
        }

        Assert.True(await WaitUntilAsync(() => scheduler.TotalQueuedTasks == 0), $"counter stuck at {scheduler.TotalQueuedTasks}");
        Assert.DoesNotContain("DISCREPANCY", scheduler.GetDiagnostics());
    }

    #endregion

    #region A full queue

    /// <summary>One worker stuck in a job that blocks, so the queue behind it fills up to its limit (1024).</summary>
    private static async Task<(SharpTickerTaskScheduler Scheduler, ManualResetEventSlim Release, int Queued)> FillTheQueueAsync(Action? onEachRun = null)
    {
        var scheduler = new SharpTickerTaskScheduler(1);
        var release = new ManualResetEventSlim(false);
        var blockerStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await scheduler.QueueAsync(_ => { blockerStarted.TrySetResult(true); release.Wait(); return Task.CompletedTask; }, TickerTaskPriority.Normal);
        await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        for (var i = 0; i < 1024; i++)
            await scheduler.QueueAsync(_ => { onEachRun?.Invoke(); return Task.CompletedTask; }, TickerTaskPriority.Normal);

        return (scheduler, release, 1024);
    }

    [Fact]
    public async Task WhenTheQueueIsFull_TheCallerWaits_AndGoesOnOnceThereIsRoom()
    {
        var ran = 0;
        var (scheduler, release, queued) = await FillTheQueueAsync(() => Interlocked.Increment(ref ran));
        await using var _ = scheduler;
        try
        {
            var overflow = scheduler.QueueAsync(_ => { Interlocked.Increment(ref ran); return Task.CompletedTask; }, TickerTaskPriority.Normal).AsTask();

            await Task.Delay(300);
            Assert.False(overflow.IsCompleted, "the caller did not wait for room in a full queue");

            release.Set();
            await overflow.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(await WaitUntilAsync(() => Volatile.Read(ref ran) == queued + 1), $"{Volatile.Read(ref ran)} of {queued + 1} jobs ran");
        }
        finally
        {
            release.Set();
            release.Dispose();
        }
    }

    [Fact]
    public async Task ACallerWaitingForRoom_CanBeCancelled()
    {
        var (scheduler, release, _) = await FillTheQueueAsync();
        await using var _ = scheduler;
        try
        {
            using var giveUp = new CancellationTokenSource();
            var overflow = scheduler.QueueAsync(_ => Task.CompletedTask, TickerTaskPriority.Normal, giveUp.Token).AsTask();
            await Task.Delay(100);
            Assert.False(overflow.IsCompleted);

            giveUp.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => overflow.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            release.Set();
            release.Dispose();
        }
    }

    #endregion

    #region Workers

    // The scheduler tries to keep one worker, but several idle workers can leave at the same moment and take the count
    // to zero. That costs nothing: the next job that is queued starts a worker again.
    [Fact]
    public async Task IdleWorkersLeave_AndTheNextJobStartsOneAgain()
    {
        await using var scheduler = new SharpTickerTaskScheduler(4, idleWorkerTimeout: TimeSpan.FromMilliseconds(50));
        var ran = 0;

        // Enough quick jobs that several workers start.
        for (var i = 0; i < 400; i++)
            await scheduler.QueueAsync(_ => { Interlocked.Increment(ref ran); return Task.CompletedTask; }, TickerTaskPriority.Normal);
        Assert.True(await WaitUntilAsync(() => Volatile.Read(ref ran) == 400));

        Assert.True(await WaitUntilAsync(() => scheduler.ActiveWorkers <= 1, seconds: 15), $"{scheduler.ActiveWorkers} workers still running");

        var again = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await scheduler.QueueAsync(_ => { again.TrySetResult(true); return Task.CompletedTask; }, TickerTaskPriority.Normal);
        Assert.True(await again.Task.WaitAsync(TimeSpan.FromSeconds(10)), "no worker was started for the job queued after the workers left");
    }

    [Fact]
    public async Task EveryJobRunsExactlyOnce_WhenSeveralCallersQueueAtTheSameTime()
    {
        await using var scheduler = new SharpTickerTaskScheduler(4);
        var seen = new ConcurrentDictionary<int, int>();
        const int callers = 8, perCaller = 250;

        await Task.WhenAll(Enumerable.Range(0, callers).Select(caller => Task.Run(async () =>
        {
            for (var i = 0; i < perCaller; i++)
            {
                var id = caller * perCaller + i;
                await scheduler.QueueAsync(_ => { seen.AddOrUpdate(id, 1, (_, count) => count + 1); return Task.CompletedTask; }, TickerTaskPriority.Normal);
            }
        })));

        Assert.True(await WaitUntilAsync(() => seen.Count == callers * perCaller), $"{seen.Count} of {callers * perCaller} jobs ran");
        Assert.All(seen.Values, count => Assert.Equal(1, count));
        Assert.True(await WaitUntilAsync(() => scheduler.TotalQueuedTasks == 0));
    }

    #endregion
}
