using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SharpTicker.Exceptions;
using SharpTicker.Provider;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Instrumentation;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Managers;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Tests;

/// <summary>
/// What a run does when the node's fence is raised while it runs (#46): with the in-memory store, the real manager and the
/// real handler. The fence is raised by the test, the way the store package that watches the node's heartbeat will.
/// The entity types are private, so the store is separate from other tests.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class NodeFenceRunTests : IAsyncLifetime
{
    private sealed class FenceTimeTicker : TimeTickerEntity<FenceTimeTicker> { }
    private sealed class FenceCronTicker : CronTickerEntity { }

    private sealed class RecordingExceptionHandler : ITickerExceptionHandler
    {
        public ConcurrentQueue<string> Calls { get; } = new();

        public Task HandleExceptionAsync(Exception exception, Guid tickerId, TickerType tickerType)
        {
            Calls.Enqueue($"exception {tickerId}: {exception.GetType().Name}");
            return Task.CompletedTask;
        }

        public Task HandleCanceledExceptionAsync(Exception exception, Guid tickerId, TickerType tickerType)
        {
            Calls.Enqueue($"cancellation {tickerId}: {exception.GetType().Name}");
            return Task.CompletedTask;
        }
    }

    private const string Node = "node-A";
    private static readonly DateTime Now = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private readonly TickerInMemoryPersistenceProvider<FenceTimeTicker, FenceCronTicker> _store;
    private readonly IInternalTickerManager _manager;
    private readonly TickerExecutionTaskHandler _handler;
    private readonly NodeFence _fence = new();
    private readonly ClaimPutBackRetryList _putBacks = new();
    private readonly LostOutcomeRetryList _lostOutcomes = new();
    private readonly RecordingExceptionHandler _exceptionHandler = new();
    private readonly CapturedLogs _logs = new();
    private readonly List<Guid> _rows = [];
    private long _marker;

    public NodeFenceRunTests()
    {
        var clock = Substitute.For<ITickerClock>();
        clock.UtcNow.Returns(Now);
        clock.NextClaimMarker().Returns(_ => NextMarker());
        var services = new ServiceCollection();
        services.AddSingleton(clock);
        services.AddSingleton(new SchedulerOptionsBuilder { NodeIdentifier = Node });
        _store = new TickerInMemoryPersistenceProvider<FenceTimeTicker, FenceCronTicker>(services.BuildServiceProvider());

        _manager = new InternalTickerManager<FenceTimeTicker, FenceCronTicker>(_store, clock, Substitute.For<ISharpTickerNotificationHubSender>());
        var instrumentation = Substitute.For<ISharpTickerInstrumentation>();
        var handlerServices = new ServiceCollection();
        handlerServices.AddSingleton(_manager);
        handlerServices.AddSingleton(instrumentation);
        handlerServices.AddSingleton(Substitute.For<ISharpTickerDispatcher>());
        handlerServices.AddSingleton(_fence);
        handlerServices.AddSingleton(_putBacks);
        handlerServices.AddSingleton(_lostOutcomes);
        handlerServices.AddSingleton<ITickerExceptionHandler>(_exceptionHandler);
        handlerServices.AddSingleton<ILoggerFactory>(new LoggerFactory([_logs]));
        _handler = new TickerExecutionTaskHandler(handlerServices.BuildServiceProvider(), clock, instrumentation, _manager);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        TickerCancellationTokenManager.CleanUpTickerCancellationTokens();
        await _store.RemoveTimeTickers(_rows.ToArray(), CancellationToken.None);
    }

    private DateTime NextMarker() => Now.AddMilliseconds(Interlocked.Increment(ref _marker));

    // A row this node has claimed: InProgress, held by the node, its claim marker in UpdatedAt.
    private FenceTimeTicker ClaimedRow(DateTime marker, int retries = 0)
        => Track(new FenceTimeTicker
        {
            Id = Guid.NewGuid(), Function = "fn", ExecutionTime = Now.AddMinutes(-1), Status = TickerStatus.InProgress,
            LockHolder = Node, LockedAt = Now, Retries = retries, RetryIntervals = [],
            CreatedAt = Now.AddMinutes(-10), UpdatedAt = marker
        });

    // A chain child without a time of its own that still waits for its start.
    private FenceTimeTicker WaitingChild(RunCondition condition)
        => Track(new FenceTimeTicker
        {
            Id = Guid.NewGuid(), Function = "child", ExecutionTime = null, Status = TickerStatus.Idle, RunCondition = condition,
            RetryIntervals = [], CreatedAt = Now.AddMinutes(-10), UpdatedAt = Now.AddMinutes(-5)
        });

    private FenceTimeTicker Track(FenceTimeTicker row)
    {
        _rows.Add(row.Id);
        return row;
    }

    private static InternalFunctionContext Context(Guid id, TickerFunctionDelegate run, DateTime? claimMarker = null,
        RunCondition condition = RunCondition.OnAnyCompletedStatus, int retries = 0)
        => new()
        {
            TickerId = id, FunctionName = "fn", Type = TickerType.TimeTicker, Retries = retries, RetryIntervals = [],
            RunCondition = condition, Status = TickerStatus.InProgress, CachedDelegate = run, ClaimMarker = claimMarker,
            TimeTickerChildren = []
        };

    private async Task<FenceTimeTicker> StoredAsync(Guid id) => await _store.GetTimeTickerById(id, CancellationToken.None);

    // A job that says when it has started and then waits for its token or for the test.
    private sealed class Gate
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Starts;
    }

    // Honours its token: ends with the cancellation.
    private static TickerFunctionDelegate Cooperative(Gate gate) => async (token, _, _) =>
    {
        Interlocked.Increment(ref gate.Starts);
        gate.Started.TrySetResult();
        await gate.Release.Task.WaitAsync(token);
    };

    // Ignores its token: ends only when the test lets it.
    private static TickerFunctionDelegate Stubborn(Gate gate) => async (_, _, _) =>
    {
        Interlocked.Increment(ref gate.Starts);
        gate.Started.TrySetResult();
        await gate.Release.Task;
    };

    private void AssertNothingWasReportedOrStored(FenceTimeTicker stored, DateTime marker)
    {
        Assert.Equal(TickerStatus.InProgress, stored.Status);
        Assert.Equal(Node, stored.LockHolder);
        Assert.Equal(marker, stored.UpdatedAt);
        Assert.Null(stored.ExecutedAt);
        Assert.Null(stored.ExceptionMessage);
        Assert.Empty(_exceptionHandler.Calls);
        Assert.True(_lostOutcomes.IsEmpty, "an outcome was listed to be written again");
    }

    // The row is put back the way the scheduler's queue of claimed work does it for a listed claim.
    private async Task<bool> PutBackAsync(NotPutBackClaim claim)
        => await _manager.TryReleaseClaim(new InternalFunctionContext { TickerId = claim.Id, Type = claim.Type }, claim.ClaimMarker, CancellationToken.None);

    // A job that honours its token: no Cancelled, no outcome, no handler call; its claim is listed and the put-back makes the
    // row Idle again. Until the run has ended it stays among the running jobs, so a stop of the host still waits for it.
    [Fact]
    public async Task AFencedJobThatHonoursItsToken_StoresNoOutcome_AndItsClaimIsListedToBePutBack()
    {
        var marker = NextMarker();
        var row = ClaimedRow(marker);
        await _store.AddTimeTickers([row], CancellationToken.None);
        var gate = new Gate();
        var context = Context(row.Id, Cooperative(gate), marker);

        var run = _handler.ExecuteTaskAsync(context, isDue: false);
        await gate.Started.Task.WaitAsync(Limit);
        Assert.Equal(1, _fence.Raise(_handler));
        await run.WaitAsync(Limit);

        AssertNothingWasReportedOrStored(await StoredAsync(row.Id), marker);
        Assert.Empty(TickerCancellationTokenManager.GetRunningJobs(_handler));
        var claim = Assert.Single(_putBacks.TakeAll());
        Assert.Equal(new NotPutBackClaim(row.Id, marker, TickerType.TimeTicker, "fn"), claim);
        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(row.Id.ToString()) && e.Message.Contains("puts its row back"));

        Assert.True(await PutBackAsync(claim));
        var putBack = await StoredAsync(row.Id);
        Assert.Equal(TickerStatus.Idle, putBack.Status);
        Assert.Null(putBack.LockHolder);
    }

    // A run that was let start just before the fence was raised and reaches the handler after it: the fence's cancel did
    // not find it, so the handler does not start it, and its claim is listed like that of a stopped run.
    [Fact]
    public async Task ARunThatReachesTheHandlerAfterTheFenceWasRaised_DoesNotStart_AndItsClaimIsListedToBePutBack()
    {
        var marker = NextMarker();
        var row = ClaimedRow(marker);
        await _store.AddTimeTickers([row], CancellationToken.None);
        var gate = new Gate();
        Assert.Equal(0, _fence.Raise(_handler));

        await _handler.ExecuteTaskAsync(Context(row.Id, Stubborn(gate), marker), isDue: false).WaitAsync(Limit);

        Assert.Equal(0, gate.Starts);
        AssertNothingWasReportedOrStored(await StoredAsync(row.Id), marker);
        Assert.Equal(new NotPutBackClaim(row.Id, marker, TickerType.TimeTicker, "fn"), Assert.Single(_putBacks.TakeAll()));
        Assert.Empty(TickerCancellationTokenManager.GetRunningJobs(_handler));
    }

    // Raising the fence does not take the job out of the running jobs: it is there until its run has ended.
    [Fact]
    public async Task AFencedJob_StaysAmongTheRunningJobs_UntilItsRunHasEnded()
    {
        var marker = NextMarker();
        var row = ClaimedRow(marker);
        await _store.AddTimeTickers([row], CancellationToken.None);
        var gate = new Gate();

        var run = _handler.ExecuteTaskAsync(Context(row.Id, Stubborn(gate), marker), isDue: false);
        await gate.Started.Task.WaitAsync(Limit);
        _fence.Raise(_handler);

        Assert.Equal([row.Id], TickerCancellationTokenManager.GetRunningJobs(_handler).Select(x => x.TickerId));
        gate.Release.TrySetResult();
        await run.WaitAsync(Limit);
        Assert.Empty(TickerCancellationTokenManager.GetRunningJobs(_handler));
    }

    // The fence may be cleared before the job reacts to its token: the run was still stopped by the fence.
    [Fact]
    public async Task AJobThatEndsAfterTheFenceWasClearedAgain_IsStillPutBack_NotRecordedAsCancelled()
    {
        var marker = NextMarker();
        var row = ClaimedRow(marker);
        await _store.AddTimeTickers([row], CancellationToken.None);
        var gate = new Gate();
        var seen = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Sees the cancellation, and throws it only when the test lets it.
        var context = Context(row.Id, async (token, _, _) =>
        {
            gate.Started.TrySetResult();
            seen.TrySetResult(token);
            await gate.Release.Task;
            token.ThrowIfCancellationRequested();
        }, marker);

        var run = _handler.ExecuteTaskAsync(context, isDue: false);
        await gate.Started.Task.WaitAsync(Limit);
        _fence.Raise(_handler);
        _fence.Clear();
        Assert.True((await seen.Task.WaitAsync(Limit)).IsCancellationRequested);
        gate.Release.TrySetResult();
        await run.WaitAsync(Limit);

        AssertNothingWasReportedOrStored(await StoredAsync(row.Id), marker);
        Assert.Equal(row.Id, Assert.Single(_putBacks.TakeAll()).Id);
    }

    // A job that throws something else once it was asked to stop: no failed attempt, no further attempt, put back.
    [Fact]
    public async Task AFencedJobThatThrowsSomethingElse_IsPutBack_WithoutAFurtherAttempt_AndWithoutCountingTheAttempt()
    {
        var marker = NextMarker();
        var row = ClaimedRow(marker, retries: 2);
        await _store.AddTimeTickers([row], CancellationToken.None);
        var gate = new Gate();
        var context = Context(row.Id, async (_, _, _) =>
        {
            Interlocked.Increment(ref gate.Starts);
            gate.Started.TrySetResult();
            await gate.Release.Task;
            throw new InvalidOperationException("the connection was closed");
        }, marker, retries: 2);

        var run = _handler.ExecuteTaskAsync(context, isDue: false);
        await gate.Started.Task.WaitAsync(Limit);
        _fence.Raise(_handler);
        gate.Release.TrySetResult();
        await run.WaitAsync(Limit);

        var stored = await StoredAsync(row.Id);
        AssertNothingWasReportedOrStored(stored, marker);
        Assert.Equal(0, stored.RetryCount);
        Assert.Equal(1, gate.Starts);
        Assert.Equal(row.Id, Assert.Single(_putBacks.TakeAll()).Id);
    }

    // A job that ends normally although it was asked to stop has done its work: its outcome is stored as always.
    [Fact]
    public async Task AFencedJobThatReturnsNormally_StoresItsOutcomeAsAlways()
    {
        var marker = NextMarker();
        var row = ClaimedRow(marker);
        await _store.AddTimeTickers([row], CancellationToken.None);
        var gate = new Gate();

        var run = _handler.ExecuteTaskAsync(Context(row.Id, Stubborn(gate), marker), isDue: false);
        await gate.Started.Task.WaitAsync(Limit);
        _fence.Raise(_handler);
        gate.Release.TrySetResult();
        await run.WaitAsync(Limit);

        Assert.Equal(TickerStatus.Done, (await StoredAsync(row.Id)).Status);
        Assert.True(_putBacks.IsEmpty);
        Assert.Empty(_exceptionHandler.Calls);
    }

    // A job that ends itself with TerminateExecutionException decided its own outcome: it is stored as always.
    [Fact]
    public async Task AFencedJobThatTerminatesItself_StoresThatOutcomeAsAlways()
    {
        var marker = NextMarker();
        var row = ClaimedRow(marker);
        await _store.AddTimeTickers([row], CancellationToken.None);
        var gate = new Gate();
        var context = Context(row.Id, async (_, _, _) =>
        {
            gate.Started.TrySetResult();
            await gate.Release.Task;
            throw new TerminateExecutionException(TickerStatus.Skipped, "nothing to do");
        }, marker);

        var run = _handler.ExecuteTaskAsync(context, isDue: false);
        await gate.Started.Task.WaitAsync(Limit);
        _fence.Raise(_handler);
        gate.Release.TrySetResult();
        await run.WaitAsync(Limit);

        Assert.Equal(TickerStatus.Skipped, (await StoredAsync(row.Id)).Status);
        Assert.True(_putBacks.IsEmpty);
    }

    // Control: without the fence a cancelled job is recorded as Cancelled and the application's handler is told, as before.
    [Fact]
    public async Task WithoutTheFence_ACancelledJob_IsRecordedAsCancelled_AndTheHandlerIsTold()
    {
        var marker = NextMarker();
        var row = ClaimedRow(marker);
        await _store.AddTimeTickers([row], CancellationToken.None);
        var gate = new Gate();

        var run = _handler.ExecuteTaskAsync(Context(row.Id, Cooperative(gate), marker), isDue: false);
        await gate.Started.Task.WaitAsync(Limit);
        Assert.True(TickerCancellationTokenManager.RequestTickerCancellationById(row.Id));
        await run.WaitAsync(Limit);

        Assert.Equal(TickerStatus.Cancelled, (await StoredAsync(row.Id)).Status);
        Assert.Single(_exceptionHandler.Calls);
        Assert.True(_putBacks.IsEmpty);
    }

    // The parent is stopped by the fence; its child that started with it (RunCondition.InProgress) is not asked to stop,
    // ends on its own and stores its outcome. The child that follows the parent's outcome is neither run nor skipped.
    [Fact]
    public async Task WhenAParentIsFenced_TheChildThatStartedWithIt_RunsOn_AndNothingBelowTheParentIsRunOrSkipped()
    {
        var marker = NextMarker();
        var withParent = WaitingChild(RunCondition.InProgress);
        var afterParent = WaitingChild(RunCondition.OnAnyCompletedStatus);
        var parent = ClaimedRow(marker);
        parent.Children = [withParent, afterParent];
        await _store.AddTimeTickers([parent], CancellationToken.None);
        var parentGate = new Gate();
        var childGate = new Gate();
        var afterGate = new Gate();
        var parentContext = Context(parent.Id, Cooperative(parentGate), marker);
        parentContext.TimeTickerChildren.Add(Context(withParent.Id, Cooperative(childGate), condition: RunCondition.InProgress));
        parentContext.TimeTickerChildren.Add(Context(afterParent.Id, Cooperative(afterGate)));

        var run = _handler.ExecuteTaskAsync(parentContext, isDue: false);
        await parentGate.Started.Task.WaitAsync(Limit);
        await childGate.Started.Task.WaitAsync(Limit);
        Assert.Equal(1, _fence.Raise(_handler));

        // The parent's run has ended; the chain's run waits for the child, which was not asked to stop.
        await WaitUntilAsync(() => !_putBacks.IsEmpty, "the parent's claim is listed");
        Assert.False(run.IsCompleted);
        Assert.Equal(TickerStatus.InProgress, (await StoredAsync(withParent.Id)).Status);
        childGate.Release.TrySetResult();
        await run.WaitAsync(Limit);

        Assert.Equal(TickerStatus.Done, (await StoredAsync(withParent.Id)).Status);
        Assert.Equal(1, childGate.Starts);
        Assert.Equal(TickerStatus.Idle, (await StoredAsync(afterParent.Id)).Status);
        Assert.Equal(0, afterGate.Starts);
        Assert.Equal(parent.Id, Assert.Single(_putBacks.TakeAll()).Id);
        Assert.Empty(_exceptionHandler.Calls);

        // The parent runs again (here: the same contexts, a new claim): the child that already ran is not started again.
        Assert.True(await PutBackAsync(new NotPutBackClaim(parent.Id, marker, TickerType.TimeTicker)));
        var again = Assert.Single(await _store.AcquireImmediateTimeTickersAsync([parent.Id], CancellationToken.None)).UpdatedAt;
        _fence.Clear();
        var secondChildGate = new Gate();
        var rerun = Context(parent.Id, (_, _, _) => Task.CompletedTask, again);
        rerun.TimeTickerChildren.Add(Context(withParent.Id, Cooperative(secondChildGate), condition: RunCondition.InProgress));
        await _handler.ExecuteTaskAsync(rerun, isDue: false).WaitAsync(Limit);

        Assert.Equal(0, secondChildGate.Starts);
        Assert.Equal(TickerStatus.Done, (await StoredAsync(parent.Id)).Status);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"not within {Limit}: {what}");
            await Task.Delay(10);
        }
    }

    // A child that follows its parent's outcome and is running when the fence is raised is a run like any other: it was
    // claimed by its inline start, under a marker of its own, and that marker puts its row back so the fallback takes it.
    [Fact]
    public async Task AFencedChildThatFollowsItsParent_IsListedUnderItsOwnMarker_AndThePutBackLeavesItWaiting()
    {
        var marker = NextMarker();
        var child = WaitingChild(RunCondition.OnSuccess);
        var parent = ClaimedRow(marker);
        parent.Children = [child];
        await _store.AddTimeTickers([parent], CancellationToken.None);
        var childGate = new Gate();
        var parentContext = Context(parent.Id, (_, _, _) => Task.CompletedTask, marker);
        parentContext.TimeTickerChildren.Add(Context(child.Id, Cooperative(childGate), condition: RunCondition.OnSuccess));

        var run = _handler.ExecuteTaskAsync(parentContext, isDue: false);
        await childGate.Started.Task.WaitAsync(Limit);
        var childMarker = (await StoredAsync(child.Id)).UpdatedAt;
        Assert.Equal(1, _fence.Raise(_handler));
        await run.WaitAsync(Limit);

        Assert.Equal(TickerStatus.Done, (await StoredAsync(parent.Id)).Status);
        var claim = Assert.Single(_putBacks.TakeAll());
        Assert.Equal(new NotPutBackClaim(child.Id, childMarker, TickerType.TimeTicker, "fn"), claim);
        Assert.NotEqual(marker, childMarker);
        AssertNothingWasReportedOrStored(await StoredAsync(child.Id), childMarker);

        Assert.True(await PutBackAsync(claim));
        var putBack = await StoredAsync(child.Id);
        Assert.Equal(TickerStatus.Idle, putBack.Status);
        Assert.Null(putBack.LockHolder);
        Assert.Null(putBack.ExecutionTime);
    }

    // While the fence is raised a run starts nothing new: a child that follows its parent's outcome stays waiting (it is not
    // skipped either), and the fallback takes it later.
    [Fact]
    public async Task WhileTheFenceIsRaised_AChildThatFollowsItsParent_IsNotStarted_AndStaysWaiting()
    {
        var marker = NextMarker();
        var child = WaitingChild(RunCondition.OnSuccess);
        var parent = ClaimedRow(marker);
        parent.Children = [child];
        await _store.AddTimeTickers([parent], CancellationToken.None);
        var parentGate = new Gate();
        var childGate = new Gate();
        var parentContext = Context(parent.Id, Stubborn(parentGate), marker);
        parentContext.TimeTickerChildren.Add(Context(child.Id, Cooperative(childGate), condition: RunCondition.OnSuccess));

        var run = _handler.ExecuteTaskAsync(parentContext, isDue: false);
        await parentGate.Started.Task.WaitAsync(Limit);
        _fence.Raise(_handler);
        parentGate.Release.TrySetResult();
        await run.WaitAsync(Limit);

        Assert.Equal(TickerStatus.Done, (await StoredAsync(parent.Id)).Status);
        Assert.Equal(TickerStatus.Idle, (await StoredAsync(child.Id)).Status);
        Assert.Equal(0, childGate.Starts);
    }

    // Control for the test above: with the fence down the child is started by its parent's run.
    [Fact]
    public async Task WithTheFenceDown_AChildThatFollowsItsParent_IsStartedByTheParentsRun()
    {
        var marker = NextMarker();
        var child = WaitingChild(RunCondition.OnSuccess);
        var parent = ClaimedRow(marker);
        parent.Children = [child];
        await _store.AddTimeTickers([parent], CancellationToken.None);
        var ran = 0;
        var parentContext = Context(parent.Id, (_, _, _) => Task.CompletedTask, marker);
        parentContext.TimeTickerChildren.Add(Context(child.Id, (_, _, _) => { ran++; return Task.CompletedTask; }, condition: RunCondition.OnSuccess));

        await _handler.ExecuteTaskAsync(parentContext, isDue: false).WaitAsync(Limit);

        Assert.Equal(1, ran);
        Assert.Equal(TickerStatus.Done, (await StoredAsync(child.Id)).Status);
    }

    // A run whose writes no marker fences could not be put back, so the fence leaves it alone.
    [Fact]
    public async Task ARunWithoutAMarker_IsNotAskedToStop()
    {
        var row = ClaimedRow(NextMarker());
        await _store.AddTimeTickers([row], CancellationToken.None);
        var gate = new Gate();
        var cancelled = false;
        var context = Context(row.Id, async (token, _, _) =>
        {
            gate.Started.TrySetResult();
            await gate.Release.Task;
            cancelled = token.IsCancellationRequested;
        });

        var run = _handler.ExecuteTaskAsync(context, isDue: false);
        await gate.Started.Task.WaitAsync(Limit);
        Assert.Equal(0, _fence.Raise(_handler));
        gate.Release.TrySetResult();
        await run.WaitAsync(Limit);

        Assert.False(cancelled);
        Assert.True(_putBacks.IsEmpty);
    }

    // A job of another host in the same process is not this host's to stop.
    [Fact]
    public async Task TheJobsOfAnotherOwner_AreNotAskedToStop()
    {
        var marker = NextMarker();
        var row = ClaimedRow(marker);
        await _store.AddTimeTickers([row], CancellationToken.None);
        var gate = new Gate();

        var run = _handler.ExecuteTaskAsync(Context(row.Id, Stubborn(gate), marker), isDue: false);
        await gate.Started.Task.WaitAsync(Limit);

        Assert.Equal(0, new NodeFence().Raise(new object()));
        gate.Release.TrySetResult();
        await run.WaitAsync(Limit);
        Assert.Equal(TickerStatus.Done, (await StoredAsync(row.Id)).Status);
    }

    // A callback a job registered on its token throws: raising the fence does not throw, the failure is handed to the
    // caller, and the other jobs are still asked to stop.
    [Fact]
    public async Task ATokenCallbackThatThrows_DoesNotStopTheRaise_AndTheOtherJobsAreStillAskedToStop()
    {
        var firstMarker = NextMarker();
        var secondMarker = NextMarker();
        var first = ClaimedRow(firstMarker);
        var second = ClaimedRow(secondMarker);
        await _store.AddTimeTickers([first, second], CancellationToken.None);
        var firstGate = new Gate();
        var secondGate = new Gate();
        TickerFunctionDelegate throwing(Gate gate) => async (token, _, _) =>
        {
            // Not disposed: the wait below ends inside the cancel, before the cancel reaches this callback.
            token.Register(() => throw new InvalidOperationException("callback"));
            gate.Started.TrySetResult();
            await gate.Release.Task.WaitAsync(token);
        };

        var runs = Task.WhenAll(
            _handler.ExecuteTaskAsync(Context(first.Id, throwing(firstGate), firstMarker), isDue: false),
            _handler.ExecuteTaskAsync(Context(second.Id, throwing(secondGate), secondMarker), isDue: false));
        await Task.WhenAll(firstGate.Started.Task, secondGate.Started.Task).WaitAsync(Limit);
        var failures = new ConcurrentQueue<Guid>();

        Assert.Equal(2, _fence.Raise(_handler, (id, _) => failures.Enqueue(id)));
        await runs.WaitAsync(Limit);

        Assert.Equal(new[] { first.Id, second.Id }.Order(), failures.Order());
        Assert.Equal(new[] { first.Id, second.Id }.Order(), _putBacks.TakeAll().Select(x => x.Id).Order());
    }

    // An occurrence of a cron ticker is a claimed job like any other.
    [Fact]
    public async Task AFencedCronOccurrence_IsListedToBePutBack_WithItsType()
    {
        var marker = NextMarker();
        var gate = new Gate();
        var context = Context(Guid.NewGuid(), Cooperative(gate), marker);
        context.Type = TickerType.CronTickerOccurrence;

        var run = _handler.ExecuteTaskAsync(context, isDue: false);
        await gate.Started.Task.WaitAsync(Limit);
        _fence.Raise(_handler);
        await run.WaitAsync(Limit);

        Assert.Equal(new NotPutBackClaim(context.TickerId, marker, TickerType.CronTickerOccurrence, "fn"), Assert.Single(_putBacks.TakeAll()));
        Assert.Empty(_exceptionHandler.Calls);
        Assert.True(_lostOutcomes.IsEmpty);
    }
}
