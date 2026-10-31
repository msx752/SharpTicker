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
[Collection(SharedStaticStateCollection.Name)]
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

    /// <summary>
    /// Two idle workers decide to leave. A job is queued in the moment after that decision and before they are counted
    /// out, when the queueing side still sees two workers and starts none. Once both are gone nobody is left to take the
    /// job unless the last worker to leave looks again.
    /// </summary>
    [Fact]
    public async Task AJobQueuedWhileTheLastWorkersAreLeaving_IsNotLeftWaitingWithoutAWorker()
    {
        await using var scheduler = new SharpTickerTaskScheduler(2, idleWorkerTimeout: TimeSpan.Zero);
        using var holdJobs = new ManualResetEventSlim(false);
        using var holdLeaving = new ManualResetEventSlim(false);
        var started = new CountdownEvent(2);
        var leaving = 0;
        scheduler.OnWorkerLeaving = _ => { Interlocked.Increment(ref leaving); holdLeaving.Wait(); };

        // Two jobs that block, so both workers exist.
        for (var i = 0; i < 2; i++)
            await scheduler.QueueAsync(_ => { started.Signal(); holdJobs.Wait(); return Task.CompletedTask; }, TickerTaskPriority.Normal);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)),
            $"the two workers did not start: {2 - started.CurrentCount} jobs started, {scheduler.ActiveWorkers} workers, {scheduler.TotalQueuedTasks} queued");

        holdJobs.Set();
        Assert.True(await WaitUntilAsync(() => Volatile.Read(ref leaving) == 2),
            $"the two workers did not decide to leave: {Volatile.Read(ref leaving)} did, {scheduler.ActiveWorkers} workers, {scheduler.TotalQueuedTasks} queued");

        var ran = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await scheduler.QueueAsync(_ => { ran.TrySetResult(true); return Task.CompletedTask; }, TickerTaskPriority.Normal);
        holdLeaving.Set();

        var finished = await Task.WhenAny(ran.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(ReferenceEquals(finished, ran.Task),
            $"the job was left in the queue: {scheduler.ActiveWorkers} workers, {scheduler.TotalQueuedTasks} queued");
    }

    /// <summary>
    /// A worker's id picks the queue it owns. When a worker from the middle leaves, the next worker has to take the id that
    /// became free, not the id of a worker that is still running, or two workers share a queue and one queue has no owner.
    /// </summary>
    [Fact]
    public async Task AWorkerStartedAfterOneFromTheMiddleLeft_TakesTheFreeId()
    {
        await using var scheduler = new SharpTickerTaskScheduler(3, idleWorkerTimeout: TimeSpan.Zero);
        using var holdJobs = new ManualResetEventSlim(false);
        using var holdOthers = new ManualResetEventSlim(false);
        using var holdNewJob = new ManualResetEventSlim(false);
        var started = new CountdownEvent(3);
        var startedIds = new ConcurrentQueue<int>();
        // Only worker 1 is let through, so workers 0 and 2 keep running while it leaves.
        scheduler.OnWorkerLeaving = id => { if (id != 1) holdOthers.Wait(); };
        try
        {
            for (var i = 0; i < 3; i++)
                await scheduler.QueueAsync(_ => { started.Signal(); holdJobs.Wait(); return Task.CompletedTask; }, TickerTaskPriority.Normal);
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)), "the three workers did not start");

            scheduler.OnWorkerThreadStarting = startedIds.Enqueue;
            holdJobs.Set();
            Assert.True(await WaitUntilAsync(() => scheduler.ActiveWorkers == 2), $"worker 1 did not leave: {scheduler.ActiveWorkers} workers");

            await scheduler.QueueAsync(_ => { holdNewJob.Wait(); return Task.CompletedTask; }, TickerTaskPriority.Normal);

            Assert.True(await WaitUntilAsync(() => !startedIds.IsEmpty), "no worker was started for the queued job");
            Assert.Equal(1, startedIds.First());
        }
        finally
        {
            holdJobs.Set();
            holdOthers.Set();
            holdNewJob.Set();
        }
    }

    /// <summary>
    /// The last workers leave together and the one that looks at the queue again cannot start a replacement because the
    /// operating system refuses another thread. That must not end the process, the reserved worker slot must be given
    /// back, and the job must still run once a worker can be started.
    /// </summary>
    [Fact]
    public async Task AWorkerThatCannotBeStartedWhenTheLastWorkersLeave_DoesNotEndTheProcessOrShrinkThePool()
    {
        const string failure = "simulated thread start failure";
        var unhandled = new ConcurrentQueue<Exception>();
        UnhandledExceptionEventHandler record = (_, e) => { if (e.ExceptionObject is Exception x && x.Message == failure) unhandled.Enqueue(x); };
        AppDomain.CurrentDomain.UnhandledException += record;
        try
        {
            var logs = new CapturedLogs();
            await using var scheduler = new SharpTickerTaskScheduler(2, TimeSpan.Zero, null, logs.CreateLogger("SharpTicker.TaskScheduler"));
            using var holdJobs = new ManualResetEventSlim(false);
            using var holdLeaving = new ManualResetEventSlim(false);
            var started = new CountdownEvent(2);
            var leaving = 0;
            scheduler.OnWorkerLeaving = _ => { Interlocked.Increment(ref leaving); holdLeaving.Wait(); };

            for (var i = 0; i < 2; i++)
                await scheduler.QueueAsync(_ => { started.Signal(); holdJobs.Wait(); return Task.CompletedTask; }, TickerTaskPriority.Normal);
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)),
            $"the two workers did not start: {2 - started.CurrentCount} jobs started, {scheduler.ActiveWorkers} workers, {scheduler.TotalQueuedTasks} queued");
            holdJobs.Set();
            Assert.True(await WaitUntilAsync(() => Volatile.Read(ref leaving) == 2),
                $"the two workers did not decide to leave: {Volatile.Read(ref leaving)} did, {scheduler.ActiveWorkers} workers, {scheduler.TotalQueuedTasks} queued");

            var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await scheduler.QueueAsync(_ => { first.TrySetResult(true); return Task.CompletedTask; }, TickerTaskPriority.Normal);
            scheduler.OnWorkerThreadStarting = _ => throw new InvalidOperationException(failure);
            holdLeaving.Set();

            Assert.True(await WaitUntilAsync(() => scheduler.ActiveWorkers == 0),
                $"the reserved worker slot was not given back: {scheduler.ActiveWorkers} workers counted");
            await Task.Delay(200);
            Assert.True(unhandled.IsEmpty, $"the leaving worker threw: {unhandled.FirstOrDefault()}");
            Assert.Equal(1, scheduler.TotalQueuedTasks);
            Assert.Contains(logs.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error && e.Exception?.Message == failure);

            scheduler.OnWorkerThreadStarting = null;
            var second = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await scheduler.QueueAsync(_ => { second.TrySetResult(true); return Task.CompletedTask; }, TickerTaskPriority.Normal);

            await Task.WhenAll(first.Task, second.Task).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= record;
        }
    }

    /// <summary>
    /// Disposing waits five seconds for the workers and then disposes the shutdown source. A job that blocks its worker
    /// thread is still running by then, and when it ends the worker looks at the shutdown token once more. That must not
    /// throw: the worker runs on a plain thread, so an exception there takes the whole process down.
    /// </summary>
    [Fact]
    public async Task AWorkerStillInsideAJobWhenTheSchedulerIsDisposed_LeavesQuietlyWhenTheJobEnds()
    {
        var unhandled = new ConcurrentQueue<Exception>();
        UnhandledExceptionEventHandler record = (_, e) => { if (e.ExceptionObject is ObjectDisposedException x) unhandled.Enqueue(x); };
        AppDomain.CurrentDomain.UnhandledException += record;
        try
        {
            var scheduler = new SharpTickerTaskScheduler(2);
            using var holdJob = new ManualResetEventSlim(false);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await scheduler.QueueAsync(_ => { started.TrySetResult(true); holdJob.Wait(); return Task.CompletedTask; }, TickerTaskPriority.Normal);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await scheduler.DisposeAsync();
            Assert.Equal(1, scheduler.ActiveWorkers);

            holdJob.Set();
            Assert.True(await WaitUntilAsync(() => scheduler.ActiveWorkers == 0), "the worker did not leave after its job ended");
            await Task.Delay(200);
            Assert.True(unhandled.IsEmpty, $"the worker threw after the scheduler was disposed: {unhandled.FirstOrDefault()}");
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= record;
        }
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
