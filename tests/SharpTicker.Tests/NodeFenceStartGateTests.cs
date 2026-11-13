using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SharpTicker.BackgroundServices;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Tests;

/// <summary>
/// While the node's fence is raised, a claimed job does not start (#46): its claim is put back instead, whoever claimed
/// it. And the claim of a run the fence stopped is put back by the same queue, with a log entry that says what it was.
/// The manager is a substitute: what is under test is what the queue asks of it.
/// </summary>
public class NodeFenceStartGateTests
{
    private static readonly DateTime Marker = new(2025, 6, 15, 12, 0, 0, 123, DateTimeKind.Utc);

    // Runs the queued work at once, on the caller.
    private sealed class InlineScheduler : ISharpTickerTaskScheduler
    {
        private readonly ConcurrentQueue<Task> _running = new();
        public bool IsFrozen => false;
        public bool IsDisposed => false;
        public int ActiveWorkers => 0;
        public int TotalQueuedTasks => 0;

        public ValueTask QueueAsync(Func<CancellationToken, Task> work, TickerTaskPriority priority, CancellationToken cancellationToken = default)
        {
            _running.Enqueue(work(cancellationToken));
            return ValueTask.CompletedTask;
        }

        public Task WhenAllIdleAsync() => Task.WhenAll(_running.ToArray());
        public void Freeze() { }
        public void Resume() { }
        public string GetDiagnostics() => string.Empty;
        public Task<bool> WaitForRunningTasksAsync(TimeSpan? timeout = null) => Task.FromResult(true);
    }

    private readonly IInternalTickerManager _manager = Substitute.For<IInternalTickerManager>();
    private readonly ITickerExecutionTaskHandler _handler = Substitute.For<ITickerExecutionTaskHandler>();
    private readonly InlineScheduler _scheduler = new();
    private readonly TickerFunctionConcurrencyGate _gate = new();
    private readonly NodeFence _fence = new();
    private readonly ClaimPutBackRetryList _putBacks = new();
    private readonly CapturedLogs _logs = new();
    private readonly ClaimedWorkQueue _work;

    public NodeFenceStartGateTests()
    {
        _handler.ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _manager.TryReleaseClaim(Arg.Any<InternalFunctionContext>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);
        _work = new ClaimedWorkQueue(_manager, _scheduler, _handler, _gate, new LoggerFactory([_logs]).CreateLogger<ClaimedWorkQueue>(), _putBacks, _fence);
    }

    private static InternalFunctionContext Job(string function = "Fence.Gate.Job", int maxConcurrency = 0)
        => new()
        {
            TickerId = Guid.NewGuid(), FunctionName = function, Type = TickerType.TimeTicker, ClaimMarker = Marker,
            Status = TickerStatus.InProgress, CachedMaxConcurrency = maxConcurrency
        };

    private Task Started(InternalFunctionContext job)
        => _handler.Received(1).ExecuteTaskAsync(job, Arg.Any<bool>(), Arg.Any<CancellationToken>());

    private Task NotStarted()
        => _handler.DidNotReceive().ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

    private Task<bool> PutBack(InternalFunctionContext job, int times = 1)
        => _manager.Received(times).TryReleaseClaim(Arg.Is<InternalFunctionContext>(f => f.TickerId == job.TickerId), Marker, Arg.Any<CancellationToken>());

    // Control: with the fence down the claimed job starts and nothing is put back.
    [Fact]
    public async Task WithTheFenceDown_AClaimedJobStarts()
    {
        var job = Job();

        await _work.QueueWork(_work.Record(job, Marker), isDue: false, CancellationToken.None);
        await _scheduler.WhenAllIdleAsync();

        await Started(job);
        await _manager.DidNotReceive().TryReleaseClaim(Arg.Any<InternalFunctionContext>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhileTheFenceIsRaised_AClaimedJobDoesNotStart_AndItsClaimIsPutBack()
    {
        var job = Job();
        _fence.Raise(owner: new object());

        await _work.QueueWork(_work.Record(job, Marker), isDue: false, CancellationToken.None);
        await _scheduler.WhenAllIdleAsync();

        await NotStarted();
        await PutBack(job);
        Assert.Equal(0, _work.Claims.Count);
        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(job.TickerId.ToString()) && e.Message.Contains("put back"));
    }

    // The job was claimed before the fence was raised and waited for its function's only slot; the slot is freed while the
    // fence is raised (the job that held it was asked to stop). It does not start then.
    [Fact]
    public async Task AJobThatGetsItsSlotWhileTheFenceIsRaised_DoesNotStart_AndItsClaimIsPutBack()
    {
        var job = Job("Fence.Gate.OneSlot", maxConcurrency: 1);
        var slot = _gate.GetSemaphoreOrNull(job.FunctionName, 1)!;
        await slot.WaitAsync();

        await _work.QueueWork(_work.Record(job, Marker), isDue: false, CancellationToken.None);
        _fence.Raise(owner: new object());
        slot.Release();
        await _scheduler.WhenAllIdleAsync();

        await NotStarted();
        await PutBack(job);
        Assert.Equal(1, slot.CurrentCount);
    }

    // Once the fence is cleared, jobs start again.
    [Fact]
    public async Task AfterTheFenceWasCleared_AClaimedJobStartsAgain()
    {
        _fence.Raise(owner: new object());
        _fence.Clear();
        var job = Job();

        await _work.QueueWork(_work.Record(job, Marker), isDue: false, CancellationToken.None);
        await _scheduler.WhenAllIdleAsync();

        await Started(job);
    }

    // The store is out of reach, which is when the fence is raised: the put-back fails, the claim stays to be tried again
    // and can no longer start; the scheduler's retry puts it back once the store answers.
    [Fact]
    public async Task WhenThePutBackOfAJobNotStartedFails_ItIsTriedAgain_AndTheJobNeverStartsFromThisClaim()
    {
        var job = Job();
        _manager.TryReleaseClaim(Arg.Any<InternalFunctionContext>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<bool>(new TimeoutException("the store did not answer")), _ => Task.FromResult(true));
        _fence.Raise(owner: new object());

        await _work.QueueWork(_work.Record(job, Marker), isDue: false, CancellationToken.None);
        await _scheduler.WhenAllIdleAsync();
        Assert.True(_work.HasPutBacksToRetry);
        _fence.Clear();

        await _work.RetryFailedPutBacksAsync();

        Assert.False(_work.HasPutBacksToRetry);
        await PutBack(job, times: 2);
        await NotStarted();
    }

    // A store that does not answer at all: the put-back of the job that is not started gives up after the queue's limit, so
    // the worker and the function's slot are free again; the claim stays to be tried again.
    [Fact]
    public async Task WhenTheStoreDoesNotAnswer_ThePutBackOfAJobNotStarted_GivesUpAfterTheLimit_AndIsTriedAgainLater()
    {
        var job = Job("Fence.Gate.SilentStore", maxConcurrency: 1);
        _manager.TryReleaseClaim(Arg.Any<InternalFunctionContext>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                return true;
            });
        _work.PutBackAttemptLimit = TimeSpan.FromMilliseconds(200);
        _fence.Raise(owner: new object());

        await _work.QueueWork(_work.Record(job, Marker), isDue: false, CancellationToken.None);
        await _scheduler.WhenAllIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(_work.HasPutBacksToRetry);
        Assert.Equal(1, _gate.GetSemaphoreOrNull(job.FunctionName, 1)!.CurrentCount);
        await NotStarted();
    }

    [Fact]
    public void TheQueueSaysThatNoJobStarts_OnlyWhileTheFenceIsRaised()
    {
        Assert.False(_work.StartsNoJob);
        _fence.Raise(owner: new object());
        Assert.True(_work.StartsNoJob);
        _fence.Clear();
        Assert.False(_work.StartsNoJob);
        Assert.False(new ClaimedWorkQueue(_manager, _scheduler, _handler, _gate).StartsNoJob);
    }

    // The claim of a run the fence stopped (listed by the execution handler): the queue puts it back, and the log entry
    // names the function and does not say the job never started.
    [Fact]
    public async Task TheClaimOfARunTheFenceStopped_IsPutBackByTheRetry_AndLoggedAsAStoppedRun()
    {
        var id = Guid.NewGuid();
        _putBacks.Add(new NotPutBackClaim(id, Marker, TickerType.CronTickerOccurrence, "Fence.Gate.Stopped"));
        Assert.True(_work.HasPutBacksToRetry);

        await _work.RetryFailedPutBacksAsync();

        await _manager.Received(1).TryReleaseClaim(
            Arg.Is<InternalFunctionContext>(f => f.TickerId == id && f.Type == TickerType.CronTickerOccurrence), Marker, Arg.Any<CancellationToken>());
        Assert.False(_work.HasPutBacksToRetry);
        var entry = Assert.Single(_logs.Entries, e => e.Level >= LogLevel.Information);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("Fence.Gate.Stopped", entry.Message);
        Assert.Contains(id.ToString(), entry.Message);
        Assert.Contains("was stopped by this node", entry.Message);
        Assert.DoesNotContain("did not start", entry.Message);
    }

    // The scheduler loop may sleep for a long time; a listed claim wakes it.
    [Fact]
    public void ListingTheClaimOfAStoppedRun_WakesWhoeverWaitsForPutBacksToRetry()
    {
        var woken = 0;
        var unsubscribe = _work.WakeOnPutBacksToRetry(() => woken++);

        _putBacks.Add(new NotPutBackClaim(Guid.NewGuid(), Marker, TickerType.TimeTicker, "Fence.Gate.Stopped"));
        unsubscribe();
        _putBacks.Add(new NotPutBackClaim(Guid.NewGuid(), Marker, TickerType.TimeTicker, "Fence.Gate.Stopped"));

        Assert.Equal(1, woken);
    }

    // The row of a stopped run that another node released meanwhile is not this claim any more: nothing is put back, and
    // that is not reported as a problem.
    [Fact]
    public async Task TheClaimOfAStoppedRunWhoseRowMovedOn_IsDropped_WithoutAWarning()
    {
        _manager.TryReleaseClaim(Arg.Any<InternalFunctionContext>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(false);
        _putBacks.Add(new NotPutBackClaim(Guid.NewGuid(), Marker, TickerType.TimeTicker, "Fence.Gate.Stopped"));

        await _work.RetryFailedPutBacksAsync();

        Assert.False(_work.HasPutBacksToRetry);
        Assert.DoesNotContain(_logs.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Contains(_logs.Entries, e => e.Message.Contains("was stopped by this node") && !e.Message.Contains("did not start"));
    }

    // A claim whose put-back failed inside a claim call keeps the texts it had.
    [Fact]
    public async Task AClaimWhosePutBackFailedInsideAClaimCall_IsStillLoggedAsNotStarted()
    {
        var id = Guid.NewGuid();
        _putBacks.Add(new NotPutBackClaim(id, Marker, TickerType.TimeTicker));

        await _work.RetryFailedPutBacksAsync();

        var entry = Assert.Single(_logs.Entries, e => e.Level >= LogLevel.Information);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("was claimed but did not start", entry.Message);
        Assert.Contains("(not read: its claim call failed)", entry.Message);
    }
}
