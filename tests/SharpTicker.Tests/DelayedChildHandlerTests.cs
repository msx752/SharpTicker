using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Instrumentation;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Tests;

// A delayed child is a chain child with an execution time of its own. It is a row of its own that the store hands out once
// its parent has ended with a status its RunCondition accepts; the parent's run must never run it.
[Collection("TickerCancellationTokenState")]
public class DelayedChildHandlerTests : IDisposable
{
    private readonly IInternalTickerManager _internalManager = Substitute.For<IInternalTickerManager>();
    private readonly ISharpTickerDispatcher _dispatcher = Substitute.For<ISharpTickerDispatcher>();
    private readonly ISharpTickerHostScheduler _hostScheduler = Substitute.For<ISharpTickerHostScheduler>();
    private readonly DateTime _now = DateTime.UtcNow;
    private readonly TickerExecutionTaskHandler _handler;

    public DelayedChildHandlerTests()
    {
        var clock = Substitute.For<ITickerClock>();
        clock.UtcNow.Returns(_now);
        var instrumentation = Substitute.For<ISharpTickerInstrumentation>();
        _dispatcher.IsEnabled.Returns(true);
        // The store has every write, unless a test says otherwise.
        _internalManager.UpdateTickerAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>()).Returns(true);
        _internalManager.AcquireDelayedChildrenAsync(Arg.Any<Guid[]>(), Arg.Any<CancellationToken>())
            .Returns(call => ((Guid[])call[0]).Select(id => new InternalFunctionContext { TickerId = id, FunctionName = "fn", ClaimMarker = _now }).ToArray());
        var services = new ServiceCollection();
        services.AddSingleton(_internalManager);
        services.AddSingleton(instrumentation);
        services.AddSingleton(_dispatcher);
        services.AddSingleton(_hostScheduler);
        _handler = new TickerExecutionTaskHandler(services.BuildServiceProvider(), clock, instrumentation, _internalManager);
    }

    public void Dispose() => TickerCancellationTokenManager.CleanUpTickerCancellationTokens();

    private static InternalFunctionContext Job(TickerFunctionDelegate run, RunCondition condition = RunCondition.OnAnyCompletedStatus, bool delayed = false)
        => new()
        {
            TickerId = Guid.NewGuid(),
            FunctionName = "fn",
            Type = TickerType.TimeTicker,
            ExecutionTime = delayed ? DateTime.UtcNow.AddHours(1) : default,
            IsDelayed = delayed,
            RetryIntervals = [],
            RunCondition = condition,
            Status = TickerStatus.Idle,
            CachedDelegate = run,
            TimeTickerChildren = []
        };

    private static TickerFunctionDelegate Succeeds => (_, _, _) => Task.CompletedTask;
    private static TickerFunctionDelegate Fails => (_, _, _) => throw new InvalidOperationException("boom");

    // A delayed child due at the given offset from now.
    private InternalFunctionContext DelayedChild(TimeSpan dueIn, RunCondition condition, TickerFunctionDelegate? run = null)
    {
        var child = Job(run ?? Succeeds, condition, delayed: true);
        child.ExecutionTime = _now + dueIn;
        return child;
    }

    private Guid[] HandedOffIds()
        => _internalManager.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IInternalTickerManager.AcquireDelayedChildrenAsync))
            .SelectMany(c => (Guid[])c.GetArguments()[0]!)
            .ToArray();

    private Guid[] SkippedIds()
        => _internalManager.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IInternalTickerManager.UpdateSkipTimeTickersWithUnifiedContextAsync))
            .SelectMany(c => ((InternalFunctionContext[])c.GetArguments()[0]!).Select(x => x.TickerId))
            .ToArray();

    [Theory]
    [InlineData(RunCondition.OnSuccess)]
    [InlineData(RunCondition.OnAnyCompletedStatus)]
    [InlineData(RunCondition.InProgress)]
    public async Task ADelayedChild_IsNeverRunWithItsParent_AndIsLeftToTheStoreWhenTheOutcomeMatches(RunCondition condition)
    {
        var ran = false;
        var parent = Job(Succeeds);
        var child = Job((_, _, _) => { ran = true; return Task.CompletedTask; }, condition, delayed: true);
        parent.TimeTickerChildren.Add(child);

        await _handler.ExecuteTaskAsync(parent, isDue: false);

        Assert.Equal(TickerStatus.Done, parent.Status);
        Assert.False(ran);
        Assert.Empty(SkippedIds());
    }

    // A parent that failed or was cancelled has started, so an InProgress delayed child may still run.
    [Fact]
    public async Task AnInProgressDelayedChild_IsLeftToTheStore_WhenItsParentFails()
    {
        var ran = false;
        var parent = Job(Fails);
        parent.TimeTickerChildren.Add(Job((_, _, _) => { ran = true; return Task.CompletedTask; }, RunCondition.InProgress, delayed: true));

        await _handler.ExecuteTaskAsync(parent, isDue: false);

        Assert.Equal(TickerStatus.Failed, parent.Status);
        Assert.False(ran);
        Assert.Empty(SkippedIds());
    }

    [Fact]
    public async Task ADelayedChildWhoseConditionTheOutcomeDoesNotMatch_IsSkippedWithWhatItCarries()
    {
        var ran = false;
        var parent = Job(Fails);
        var child = Job((_, _, _) => { ran = true; return Task.CompletedTask; }, RunCondition.OnSuccess, delayed: true);
        var grandchild = Job(Succeeds);
        child.TimeTickerChildren.Add(grandchild);
        parent.TimeTickerChildren.Add(child);

        await _handler.ExecuteTaskAsync(parent, isDue: false);

        Assert.Equal(TickerStatus.Failed, parent.Status);
        Assert.False(ran);
        Assert.Equal(new[] { child.TickerId, grandchild.TickerId }.Order(), SkippedIds().Order());
    }

    [Fact]
    public async Task ADelayedGrandchild_IsNotRunWithTheChildThatCarriesIt_AndIsSkippedWhenTheChildsOutcomeDoesNotMatch()
    {
        var delayedRan = false;
        var matchingRan = false;
        var parent = Job(Succeeds);
        var child = Job(Fails, RunCondition.OnSuccess);
        var delayedOnSuccess = Job((_, _, _) => { delayedRan = true; return Task.CompletedTask; }, RunCondition.OnSuccess, delayed: true);
        var delayedOnFailure = Job((_, _, _) => { matchingRan = true; return Task.CompletedTask; }, RunCondition.OnFailure, delayed: true);
        child.TimeTickerChildren.Add(delayedOnSuccess);
        child.TimeTickerChildren.Add(delayedOnFailure);
        parent.TimeTickerChildren.Add(child);

        await _handler.ExecuteTaskAsync(parent, isDue: false);

        Assert.Equal(TickerStatus.Failed, child.Status);
        Assert.False(delayedRan);
        Assert.False(matchingRan);
        Assert.Equal([delayedOnSuccess.TickerId], SkippedIds());
    }

    [Fact]
    public async Task AChildWithoutATimeOfItsOwn_StillRunsAfterItsParent()
    {
        var ran = false;
        var parent = Job(Succeeds);
        parent.TimeTickerChildren.Add(Job((_, _, _) => { ran = true; return Task.CompletedTask; }, RunCondition.OnSuccess));

        await _handler.ExecuteTaskAsync(parent, isDue: false);

        Assert.True(ran);
    }

    // A delayed child whose time has come is claimed and started as soon as its parent's outcome is written, not at the
    // next fallback pass.
    [Fact]
    public async Task ADueDelayedChild_IsClaimedAndStarted_RightAfterItsParentsOutcomeIsWritten()
    {
        var steps = new List<string>();
        var ran = false;
        var parent = Job(Succeeds);
        var child = DelayedChild(TimeSpan.FromMinutes(-1), RunCondition.OnSuccess, (_, _, _) => { ran = true; return Task.CompletedTask; });
        parent.TimeTickerChildren.Add(child);
        _internalManager.When(m => m.UpdateTickerAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>()))
            .Do(c => steps.Add($"write {((InternalFunctionContext)c[0]).Status}"));
        _internalManager.When(m => m.AcquireDelayedChildrenAsync(Arg.Any<Guid[]>(), Arg.Any<CancellationToken>()))
            .Do(_ => steps.Add("claim"));

        await _handler.ExecuteTaskAsync(parent, isDue: false);

        Assert.Equal(["write Done", "claim"], steps);
        Assert.Equal([child.TickerId], HandedOffIds());
        await _dispatcher.Received(1).DispatchAsync(
            Arg.Is<InternalFunctionContext[]>(x => x.Length == 1 && x[0].TickerId == child.TickerId), Arg.Any<CancellationToken>());
        Assert.False(ran);
        _hostScheduler.DidNotReceive().RestartIfNeeded(Arg.Any<DateTime?>());
    }

    // Never before its own time: the store's claim does not look at the time, so a child due later is not claimed; the
    // scheduler loop is woken for it instead, as it did not plan it while the child waited for its parent.
    [Theory]
    [InlineData(-60_000, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(300_000, false)]
    public async Task ADelayedChild_IsClaimedWhenItsTimeHasCome_OtherwiseTheSchedulerIsWokenForIt(int dueInMs, bool claimed)
    {
        var parent = Job(Succeeds);
        var child = DelayedChild(TimeSpan.FromMilliseconds(dueInMs), RunCondition.OnSuccess);
        parent.TimeTickerChildren.Add(child);

        await _handler.ExecuteTaskAsync(parent, isDue: false);

        Assert.Equal(claimed ? [child.TickerId] : [], HandedOffIds());
        if (claimed)
            _hostScheduler.DidNotReceive().RestartIfNeeded(Arg.Any<DateTime?>());
        else
            _hostScheduler.Received(1).RestartIfNeeded(child.ExecutionTime);
    }

    [Fact]
    public async Task TheSchedulerIsWokenForTheEarliestDelayedChildDueLater()
    {
        var parent = Job(Succeeds);
        var later = DelayedChild(TimeSpan.FromMinutes(10), RunCondition.OnSuccess);
        var sooner = DelayedChild(TimeSpan.FromMinutes(5), RunCondition.OnAnyCompletedStatus);
        parent.TimeTickerChildren.Add(later);
        parent.TimeTickerChildren.Add(sooner);

        await _handler.ExecuteTaskAsync(parent, isDue: false);

        _hostScheduler.Received(1).RestartIfNeeded(sooner.ExecutionTime);
        _hostScheduler.Received(1).RestartIfNeeded(Arg.Any<DateTime?>());
    }

    [Fact]
    public async Task ADelayedChildTheOutcomeDoesNotLetRun_IsNotHandedOff()
    {
        var parent = Job(Fails);
        var due = DelayedChild(TimeSpan.FromMinutes(-1), RunCondition.OnSuccess);
        var later = DelayedChild(TimeSpan.FromMinutes(5), RunCondition.OnSuccess);
        parent.TimeTickerChildren.Add(due);
        parent.TimeTickerChildren.Add(later);

        await _handler.ExecuteTaskAsync(parent, isDue: false);

        Assert.Empty(HandedOffIds());
        _hostScheduler.DidNotReceive().RestartIfNeeded(Arg.Any<DateTime?>());
        Assert.Equal(new[] { due.TickerId, later.TickerId }.Order(), SkippedIds().Order());
    }

    // An InProgress delayed child may run once its parent has started, so it is handed off while the parent still runs.
    [Fact]
    public async Task ADueInProgressDelayedChild_IsClaimedWhileItsParentRuns()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parent = Job(async (_, _, _) => await release.Task);
        var child = DelayedChild(TimeSpan.FromMinutes(-1), RunCondition.InProgress);
        parent.TimeTickerChildren.Add(child);

        var run = _handler.ExecuteTaskAsync(parent, isDue: false);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (HandedOffIds().Length == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.False(run.IsCompleted);
        Assert.Equal([child.TickerId], HandedOffIds());
        release.SetResult();
        await run;
    }

    // A delayed grandchild depends on the child that carries it: it is handed off once that child's outcome is written.
    [Fact]
    public async Task ADueDelayedGrandchild_IsHandedOffAfterTheChildThatCarriesItEnds()
    {
        var parent = Job(Succeeds);
        var child = Job(Succeeds, RunCondition.OnSuccess);
        var grandchild = DelayedChild(TimeSpan.FromMinutes(-1), RunCondition.OnSuccess);
        child.TimeTickerChildren.Add(grandchild);
        parent.TimeTickerChildren.Add(child);

        await _handler.ExecuteTaskAsync(parent, isDue: false);

        Assert.Equal(TickerStatus.Done, child.Status);
        Assert.Equal([grandchild.TickerId], HandedOffIds());
    }

    // The hand-off is best effort: the parent's outcome is written, the other children still run or are skipped, and the
    // fallback runs what the hand-off missed.
    [Fact]
    public async Task AFailedHandOff_DoesNotFailTheParentsRun()
    {
        _internalManager.AcquireDelayedChildrenAsync(Arg.Any<Guid[]>(), Arg.Any<CancellationToken>())
            .Returns<InternalFunctionContext[]>(_ => throw new InvalidOperationException("store failed"));
        var inlineRan = false;
        var parent = Job(Succeeds);
        parent.TimeTickerChildren.Add(DelayedChild(TimeSpan.FromMinutes(-1), RunCondition.OnSuccess));
        var notMatching = DelayedChild(TimeSpan.FromMinutes(-1), RunCondition.OnFailure);
        parent.TimeTickerChildren.Add(notMatching);
        parent.TimeTickerChildren.Add(Job((_, _, _) => { inlineRan = true; return Task.CompletedTask; }, RunCondition.OnSuccess));

        await _handler.ExecuteTaskAsync(parent, isDue: false);

        Assert.Equal(TickerStatus.Done, parent.Status);
        Assert.True(inlineRan);
        Assert.Equal([notMatching.TickerId], SkippedIds());
        await _dispatcher.DidNotReceive().DispatchAsync(Arg.Any<InternalFunctionContext[]>(), Arg.Any<CancellationToken>());
    }

    // The hand-off comes before the skip write, which can throw, and before the inline children, which can take long.
    [Fact]
    public async Task ADueDelayedChild_IsHandedOff_EvenWhenSkippingItsSiblingsFails()
    {
        _internalManager.UpdateSkipTimeTickersWithUnifiedContextAsync(Arg.Any<InternalFunctionContext[]>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("store failed"));
        var parent = Job(Succeeds);
        var due = DelayedChild(TimeSpan.FromMinutes(-1), RunCondition.OnSuccess);
        parent.TimeTickerChildren.Add(due);
        parent.TimeTickerChildren.Add(DelayedChild(TimeSpan.FromMinutes(-1), RunCondition.OnFailure));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _handler.ExecuteTaskAsync(parent, isDue: false));

        Assert.Equal([due.TickerId], HandedOffIds());
    }

    // Waking the scheduler for a child due later must not keep a due sibling from being claimed.
    [Fact]
    public async Task AFailedSchedulerWake_DoesNotKeepADueChildFromBeingClaimed()
    {
        _hostScheduler.When(s => s.RestartIfNeeded(Arg.Any<DateTime?>())).Do(_ => throw new InvalidOperationException("boom"));
        var parent = Job(Succeeds);
        var due = DelayedChild(TimeSpan.FromMinutes(-1), RunCondition.OnSuccess);
        parent.TimeTickerChildren.Add(due);
        parent.TimeTickerChildren.Add(DelayedChild(TimeSpan.FromMinutes(5), RunCondition.OnSuccess));

        await _handler.ExecuteTaskAsync(parent, isDue: false);

        Assert.Equal([due.TickerId], HandedOffIds());
        await _dispatcher.Received(1).DispatchAsync(Arg.Any<InternalFunctionContext[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailedDispatch_DoesNotFailTheParentsRun()
    {
        _dispatcher.DispatchAsync(Arg.Any<InternalFunctionContext[]>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("boom"));
        var inlineRan = false;
        var parent = Job(Succeeds);
        parent.TimeTickerChildren.Add(DelayedChild(TimeSpan.FromMinutes(-1), RunCondition.OnSuccess));
        parent.TimeTickerChildren.Add(Job((_, _, _) => { inlineRan = true; return Task.CompletedTask; }, RunCondition.OnSuccess));

        await _handler.ExecuteTaskAsync(parent, isDue: false);

        Assert.Equal(TickerStatus.Done, parent.Status);
        Assert.True(inlineRan);
    }

    // An InProgress delayed child due later wakes the scheduler as soon as its parent starts.
    [Fact]
    public async Task AnInProgressDelayedChildDueLater_WakesTheSchedulerWhileItsParentRuns()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parent = Job(async (_, _, _) => await release.Task);
        var child = DelayedChild(TimeSpan.FromMinutes(5), RunCondition.InProgress);
        parent.TimeTickerChildren.Add(child);

        var run = _handler.ExecuteTaskAsync(parent, isDue: false);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!_hostScheduler.ReceivedCalls().Any() && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.False(run.IsCompleted);
        _hostScheduler.Received().RestartIfNeeded(child.ExecutionTime);
        Assert.Empty(HandedOffIds());
        release.SetResult();
        await run;
    }

    // A cancelled parent has an outcome too: its OnCancelled delayed child is handed off.
    [Fact]
    public async Task ADueOnCancelledDelayedChild_IsHandedOff_WhenItsParentIsCancelled()
    {
        var parent = Job((_, _, _) => throw new TaskCanceledException());
        var onCancelled = DelayedChild(TimeSpan.FromMinutes(-1), RunCondition.OnCancelled);
        var onSuccess = DelayedChild(TimeSpan.FromMinutes(-1), RunCondition.OnSuccess);
        parent.TimeTickerChildren.Add(onCancelled);
        parent.TimeTickerChildren.Add(onSuccess);

        await _handler.ExecuteTaskAsync(parent, isDue: false);

        Assert.Equal(TickerStatus.Cancelled, parent.Status);
        Assert.Equal([onCancelled.TickerId], HandedOffIds());
        Assert.Equal([onSuccess.TickerId], SkippedIds());
    }

    // The writes of an inline child, in order: its start (recorded InProgress) and its other writes with their status.
    private void RecordChildWrites(InternalFunctionContext child, List<string> steps)
    {
        _internalManager.When(m => m.RecordChildStartedAsync(Arg.Is<InternalFunctionContext>(c => c.TickerId == child.TickerId), Arg.Any<CancellationToken>()))
            .Do(_ => { lock (steps) steps.Add("write InProgress"); });
        _internalManager.When(m => m.UpdateTickerAsync(Arg.Is<InternalFunctionContext>(c => c.TickerId == child.TickerId), Arg.Any<CancellationToken>()))
            .Do(c => { lock (steps) steps.Add($"write {((InternalFunctionContext)c[0]).Status}"); });
    }

    // A chain child without a time of its own runs inside its parent's run. Its row must be recorded InProgress before its
    // function starts, as the parent's is, so the dashboard shows it running and an InProgress delayed child below it can
    // start while it runs.
    [Theory]
    [InlineData(RunCondition.OnSuccess)]
    [InlineData(RunCondition.InProgress)]
    public async Task AnInlineChild_IsRecordedInProgress_BeforeItsFunctionStarts(RunCondition condition)
    {
        var steps = new List<string>();
        var parent = Job(Succeeds);
        var child = Job((_, _, _) => { lock (steps) steps.Add("child runs"); return Task.CompletedTask; }, condition);
        parent.TimeTickerChildren.Add(child);
        RecordChildWrites(child, steps);

        await _handler.ExecuteTaskAsync(parent, isDue: false);

        Assert.Equal(["write InProgress", "child runs", "write Done"], steps);
    }

    // The InProgress delayed child of an inline child is handed off once that child is recorded InProgress, so the store's
    // gate is open when it is claimed.
    [Fact]
    public async Task AnInProgressDelayedGrandchild_IsHandedOffAfterTheInlineChildIsRecordedInProgress()
    {
        var steps = new List<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parent = Job(Succeeds);
        var child = Job(async (_, _, _) => await release.Task, RunCondition.OnSuccess);
        var grandchild = DelayedChild(TimeSpan.FromMinutes(-1), RunCondition.InProgress);
        child.TimeTickerChildren.Add(grandchild);
        parent.TimeTickerChildren.Add(child);
        RecordChildWrites(child, steps);
        _internalManager.When(m => m.AcquireDelayedChildrenAsync(Arg.Any<Guid[]>(), Arg.Any<CancellationToken>()))
            .Do(_ => { lock (steps) steps.Add("claim grandchild"); });

        var run = _handler.ExecuteTaskAsync(parent, isDue: false);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (HandedOffIds().Length == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        // Claimed while the child still runs, after it was recorded InProgress.
        Assert.False(run.IsCompleted);
        lock (steps) Assert.Equal(["write InProgress", "claim grandchild"], steps);
        release.SetResult();
        await run;
    }

    // A failed start write does not stop the child, nothing else would run it; its outcome is still written, by Id, as the
    // start gave the child no run marker to fence its writes with.
    [Fact]
    public async Task AnInlineChild_StillRuns_WhenItCannotBeRecordedInProgress()
    {
        var ran = false;
        var parent = Job(Succeeds);
        var child = Job((_, _, _) => { ran = true; return Task.CompletedTask; }, RunCondition.OnSuccess);
        parent.TimeTickerChildren.Add(child);
        _internalManager.RecordChildStartedAsync(Arg.Is<InternalFunctionContext>(c => c.TickerId == child.TickerId), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("store failed"));
        var finalWrites = new List<(TickerStatus Status, DateTime? RunMarker)>();
        _internalManager.UpdateTickerAsync(Arg.Is<InternalFunctionContext>(c => c.TickerId == child.TickerId), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                var written = (InternalFunctionContext)c[0];
                finalWrites.Add((written.Status, written.RunMarker));
                return Task.FromResult(true);
            });

        await _handler.ExecuteTaskAsync(parent, isDue: false);

        Assert.True(ran);
        Assert.Equal([(TickerStatus.Done, (DateTime?)null)], finalWrites);
    }

    // With the background services off nothing would start a claimed row, so nothing is claimed.
    [Fact]
    public async Task WithoutARunningDispatcher_NoDelayedChildIsClaimed()
    {
        _dispatcher.IsEnabled.Returns(false);
        var parent = Job(Succeeds);
        parent.TimeTickerChildren.Add(DelayedChild(TimeSpan.FromMinutes(-1), RunCondition.OnSuccess));

        await _handler.ExecuteTaskAsync(parent, isDue: false);

        Assert.Empty(HandedOffIds());
    }

    // The handler and the stores use one table, so a child runs on the same parent outcomes whether it is delayed or not.
    [Theory]
    [InlineData(RunCondition.OnSuccess, new[] { TickerStatus.Done, TickerStatus.DueDone })]
    [InlineData(RunCondition.OnFailure, new[] { TickerStatus.Failed })]
    [InlineData(RunCondition.OnCancelled, new[] { TickerStatus.Cancelled })]
    [InlineData(RunCondition.OnFailureOrCancelled, new[] { TickerStatus.Failed, TickerStatus.Cancelled })]
    [InlineData(RunCondition.OnAnyCompletedStatus, new[] { TickerStatus.Done, TickerStatus.DueDone, TickerStatus.Failed, TickerStatus.Cancelled })]
    [InlineData(RunCondition.InProgress, new[] { TickerStatus.InProgress, TickerStatus.Done, TickerStatus.DueDone, TickerStatus.Failed, TickerStatus.Cancelled })]
    public void TheGate_LetsAChildRunOnExactlyTheseParentStatuses(RunCondition condition, TickerStatus[] allowed)
    {
        foreach (var status in Enum.GetValues<TickerStatus>())
            Assert.True(allowed.Contains(status) == ChainRunGate.Allows(condition, status), $"{condition} with parent {status}");
    }

    [Fact]
    public void TheGate_TreatsAChildStoredWithoutAConditionAsOnAnyCompletedStatus()
    {
        foreach (var status in Enum.GetValues<TickerStatus>())
            Assert.Equal(ChainRunGate.Allows(RunCondition.OnAnyCompletedStatus, status), ChainRunGate.Allows(null, status));
    }
}
