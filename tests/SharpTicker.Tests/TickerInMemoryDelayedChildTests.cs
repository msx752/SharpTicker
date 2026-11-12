using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SharpTicker.Provider;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;

namespace SharpTicker.Tests;

// A delayed child (a chain child with an execution time of its own) is a row of its own. No claim path may hand it out
// before its parent has ended with a status its RunCondition accepts; the parent's payload carries it with its time.
[Collection(SharedStaticStateCollection.Name)]
public class TickerInMemoryDelayedChildTests : IAsyncLifetime
{
    private sealed class FakeTimeTicker : TimeTickerEntity<FakeTimeTicker> { }
    private sealed class FakeCronTicker : CronTickerEntity { }

    private static readonly DateTime Now = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    private readonly TickerInMemoryPersistenceProvider<FakeTimeTicker, FakeCronTicker> _provider;
    private readonly List<Guid> _roots = [];
    private long _marker;

    public TickerInMemoryDelayedChildTests()
    {
        var clock = Substitute.For<ITickerClock>();
        clock.UtcNow.Returns(Now);
        clock.NextClaimMarker().Returns(_ => Now.AddMilliseconds(Interlocked.Increment(ref _marker)));
        var services = new ServiceCollection();
        services.AddSingleton(clock);
        services.AddSingleton(new SchedulerOptionsBuilder { NodeIdentifier = "node-A" });
        _provider = new TickerInMemoryPersistenceProvider<FakeTimeTicker, FakeCronTicker>(services.BuildServiceProvider());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _provider.RemoveTimeTickers(_roots.ToArray(), CancellationToken.None);

    private FakeTimeTicker Row(DateTime? executionTime, TickerStatus status = TickerStatus.Idle, RunCondition? condition = null, string function = "fn")
    {
        var row = new FakeTimeTicker
        {
            Id = Guid.NewGuid(),
            Function = function,
            ExecutionTime = executionTime,
            Status = status,
            RunCondition = condition,
            RetryIntervals = [],
            CreatedAt = Now.AddMinutes(-10),
            UpdatedAt = Now.AddMinutes(-5)
        };
        _roots.Add(row.Id); // every row, so a test leaves none of its descendants in the shared store
        return row;
    }

    // A parent that is not due (or already ran) with a delayed child.
    private async Task<(FakeTimeTicker Parent, FakeTimeTicker Child)> Seed(TickerStatus parentStatus, RunCondition? condition, DateTime childTime)
    {
        var child = Row(childTime, condition: condition, function: "delayed-child");
        var parent = Row(Now.AddHours(-1), parentStatus);
        parent.Children = [child];
        await _provider.AddTimeTickers([parent], CancellationToken.None);
        _roots.Add(parent.Id);
        return (parent, child);
    }

    private async Task<Guid[]> ClaimedByAnyPath(Guid id)
    {
        var window = await _provider.GetEarliestTimeTickers(CancellationToken.None);
        var fallback = new List<TimeTickerEntity>();
        await foreach (var row in _provider.QueueTimedOutTimeTickers(CancellationToken.None)) fallback.Add(row);
        var runNow = await _provider.AcquireImmediateTimeTickersAsync([id], CancellationToken.None);
        return window.Concat(fallback).Concat(runNow).Select(x => x.Id).Where(x => x == id).ToArray();
    }

    private async Task<TickerStatus> StatusOf(Guid id) => (await _provider.GetTimeTickerById(id, CancellationToken.None)).Status;

    [Theory]
    [InlineData(TickerStatus.Idle, 30)]
    [InlineData(TickerStatus.Queued, 30)]
    [InlineData(TickerStatus.InProgress, 30)]
    [InlineData(TickerStatus.Idle, -30)]
    [InlineData(TickerStatus.InProgress, -30)]
    public async Task ADelayedChild_IsNotHandedOutWhileItsParentHasNotEnded(TickerStatus parentStatus, int childDueInSeconds)
    {
        var (_, child) = await Seed(parentStatus, RunCondition.OnSuccess, Now.AddSeconds(childDueInSeconds));

        Assert.Empty(await ClaimedByAnyPath(child.Id));
        Assert.Equal(TickerStatus.Idle, await StatusOf(child.Id));
    }

    [Theory]
    [InlineData(TickerStatus.Done, RunCondition.OnSuccess)]
    [InlineData(TickerStatus.DueDone, RunCondition.OnSuccess)]
    [InlineData(TickerStatus.Failed, RunCondition.OnFailure)]
    [InlineData(TickerStatus.Cancelled, RunCondition.OnCancelled)]
    [InlineData(TickerStatus.Failed, RunCondition.OnFailureOrCancelled)]
    [InlineData(TickerStatus.Done, RunCondition.OnAnyCompletedStatus)]
    [InlineData(TickerStatus.Failed, null)]
    [InlineData(TickerStatus.InProgress, RunCondition.InProgress)]
    public async Task ADelayedChild_IsHandedOutOnceItsParentHasAMatchingStatus(TickerStatus parentStatus, RunCondition? condition)
    {
        var (_, child) = await Seed(parentStatus, condition, Now.AddSeconds(30));

        var claimed = await _provider.AcquireImmediateTimeTickersAsync([child.Id], CancellationToken.None);

        Assert.Equal([child.Id], claimed.Select(x => x.Id));
    }

    [Theory]
    [InlineData(TickerStatus.Failed, RunCondition.OnSuccess)]
    [InlineData(TickerStatus.Done, RunCondition.OnFailure)]
    [InlineData(TickerStatus.Skipped, RunCondition.OnAnyCompletedStatus)]
    [InlineData(TickerStatus.Skipped, RunCondition.InProgress)]
    public async Task ADelayedChild_IsNotHandedOutWhenItsParentsOutcomeDoesNotMatch(TickerStatus parentStatus, RunCondition condition)
    {
        var (_, child) = await Seed(parentStatus, condition, Now.AddSeconds(30));

        Assert.Empty(await ClaimedByAnyPath(child.Id));
    }

    // A child without a time of its own normally runs inside its parent's run. Run-now (a claim by id) hands it out only once
    // its parent has ended with an outcome its condition accepts: that is how it follows a parent outcome that was stored
    // late. Never an InProgress child, which starts with its parent, and never by the window. The fallback leaves it too
    // while its parent has not ended at least the grace time ago.
    public static IEnumerable<object?[]> EveryConditionAndParentStatus()
        => from condition in Enum.GetValues<RunCondition>().Select(c => (RunCondition?)c).Append(null)
           from status in Enum.GetValues<TickerStatus>()
           select new object?[] { condition, status };

    [Theory]
    [MemberData(nameof(EveryConditionAndParentStatus))]
    public async Task AChildWithoutATime_IsHandedOutByRunNow_OnlyOnceItsParentEndedWithAMatchingOutcome(RunCondition? condition, TickerStatus parentStatus)
    {
        var child = Row(null, condition: condition, function: "inline-child");
        var parent = Row(Now.AddHours(1), parentStatus); // not due, so no claim below takes the parent itself
        parent.ExecutedAt = Now;
        parent.Children = [child];
        await _provider.AddTimeTickers([parent], CancellationToken.None);

        var window = await _provider.GetEarliestTimeTickers(CancellationToken.None);
        var fallback = new List<TimeTickerEntity>();
        await foreach (var row in _provider.QueueTimedOutTimeTickers(CancellationToken.None)) fallback.Add(row);
        Assert.DoesNotContain(window.Concat(fallback), x => x.Id == child.Id);

        var claimed = await _provider.AcquireImmediateTimeTickersAsync([child.Id], CancellationToken.None);

        var expected = condition != RunCondition.InProgress && ChainRunGate.Allows(condition, parentStatus);
        Assert.Equal(expected, claimed.Any(x => x.Id == child.Id));
        Assert.Equal(expected ? TickerStatus.InProgress : TickerStatus.Idle, await StatusOf(child.Id));
        Assert.Empty(await _provider.AcquireImmediateTimeTickersAsync([child.Id], CancellationToken.None));
    }

    [Fact]
    public async Task TheWindow_HandsOutADelayedChildAtItsTime_OnceItsParentSucceeded()
    {
        var (_, child) = await Seed(TickerStatus.Done, RunCondition.OnSuccess, Now.AddSeconds(30));

        var window = await _provider.GetEarliestTimeTickers(CancellationToken.None);

        Assert.Equal([child.Id], window.Select(x => x.Id));
    }

    [Fact]
    public async Task AWaitingDelayedChildDueFirst_DoesNotHideTheJobsDueAfterIt()
    {
        await Seed(TickerStatus.InProgress, RunCondition.OnSuccess, Now.AddSeconds(10));
        var later = Row(Now.AddSeconds(20));
        await _provider.AddTimeTickers([later], CancellationToken.None);
        _roots.Add(later.Id);

        var window = await _provider.GetEarliestTimeTickers(CancellationToken.None);

        Assert.Equal([later.Id], window.Select(x => x.Id));
    }

    // The parent's run skips a child its outcome does not let run; the fallback does it for a node that stopped first,
    // and the skip reaches the grandchildren one level per pass.
    [Fact]
    public async Task TheFallback_SkipsADelayedChildWhoseParentsOutcomeDoesNotMatch_AndThenItsDelayedGrandchild()
    {
        var grandchild = Row(Now.AddSeconds(-20), condition: RunCondition.OnAnyCompletedStatus, function: "delayed-grandchild");
        var child = Row(Now.AddSeconds(-30), condition: RunCondition.OnSuccess, function: "delayed-child");
        child.Children = [grandchild];
        var parent = Row(Now.AddHours(-1), TickerStatus.Failed);
        parent.Children = [child];
        await _provider.AddTimeTickers([parent], CancellationToken.None);
        _roots.Add(parent.Id);

        for (var pass = 0; pass < 2; pass++)
            await foreach (var row in _provider.QueueTimedOutTimeTickers(CancellationToken.None))
                Assert.Fail($"handed out {row.Id}");

        var skipped = await _provider.GetTimeTickerById(child.Id, CancellationToken.None);
        Assert.Equal(TickerStatus.Skipped, skipped.Status);
        Assert.Equal(ChainRunGate.NotMatchedReason, skipped.SkippedReason);
        Assert.Equal(TickerStatus.Skipped, await StatusOf(grandchild.Id));
    }

    [Fact]
    public async Task TheFallback_LeavesADelayedChildWhoseParentHasNotEnded()
    {
        var (_, child) = await Seed(TickerStatus.InProgress, RunCondition.OnSuccess, Now.AddSeconds(-30));

        await foreach (var _ in _provider.QueueTimedOutTimeTickers(CancellationToken.None)) { }

        Assert.Equal(TickerStatus.Idle, await StatusOf(child.Id));
    }

    // The skip write only touches a row that has not started: a delayed child the store already ran keeps its status.
    [Theory]
    [InlineData(TickerStatus.InProgress)]
    [InlineData(TickerStatus.Done)]
    [InlineData(TickerStatus.Failed)]
    public async Task SkippingChildren_LeavesARowThatAlreadyStarted(TickerStatus status)
    {
        var started = Row(Now.AddSeconds(-30), status);
        var waiting = Row(Now.AddSeconds(-30));
        await _provider.AddTimeTickers([started, waiting], CancellationToken.None);
        var skip = new SharpTicker.Utilities.Models.InternalFunctionContext()
            .SetProperty(x => x.Status, TickerStatus.Skipped)
            .SetProperty(x => x.ExceptionDetails, ChainRunGate.NotMatchedReason);

        await ((SharpTicker.Utilities.Interfaces.IChildSkipWriter)_provider).SkipNotStartedTimeTickersAsync([started.Id, waiting.Id], skip, CancellationToken.None);

        Assert.Equal(status, await StatusOf(started.Id));
        Assert.Equal(TickerStatus.Skipped, await StatusOf(waiting.Id));
    }

    // A grandchild left without its parent row would wait for that parent forever.
    [Fact]
    public async Task RemovingARoot_RemovesItsGrandchildrenToo()
    {
        var grandchild = Row(Now.AddSeconds(30), condition: RunCondition.OnSuccess, function: "delayed-grandchild");
        var child = Row(null, function: "child");
        child.Children = [grandchild];
        var parent = Row(Now.AddHours(-1), TickerStatus.Done);
        parent.Children = [child];
        await _provider.AddTimeTickers([parent], CancellationToken.None);

        Assert.Equal(3, await _provider.RemoveTimeTickers([parent.Id], CancellationToken.None));

        Assert.Null(await _provider.GetTimeTickerById(grandchild.Id, CancellationToken.None));
        Assert.Null(await _provider.GetTimeTickerById(child.Id, CancellationToken.None));
    }

    [Fact]
    public async Task TheParentsPayload_CarriesItsDelayedChildAndGrandchild_WithTheirTimes()
    {
        var delayedGrandchild = Row(Now.AddHours(3), function: "delayed-grandchild");
        var grandchild = Row(null, function: "grandchild");
        var child = Row(null, function: "child");
        child.Children = [grandchild, delayedGrandchild];
        var delayedChild = Row(Now.AddHours(2), function: "delayed-child");
        var parent = Row(Now.AddSeconds(5));
        parent.Children = [child, delayedChild];
        await _provider.AddTimeTickers([parent], CancellationToken.None);
        _roots.Add(parent.Id);

        var payload = Assert.Single(await _provider.GetEarliestTimeTickers(CancellationToken.None));

        Assert.Equal(parent.Id, payload.Id);
        Assert.Equal(Now.AddHours(2), Assert.Single(payload.Children, x => x.Id == delayedChild.Id).ExecutionTime);
        var carried = Assert.Single(payload.Children, x => x.Id == child.Id);
        Assert.Null(carried.ExecutionTime);
        Assert.Equal(Now.AddHours(3), Assert.Single(carried.Children, x => x.Id == delayedGrandchild.Id).ExecutionTime);
        Assert.Null(Assert.Single(carried.Children, x => x.Id == grandchild.Id).ExecutionTime);
    }

    #region The fallback and a child without a time of its own (#84)

    // A parent with a child without a time that was never started.
    private async Task<(FakeTimeTicker Parent, FakeTimeTicker Child)> SeedLeftWaiting(TickerStatus parentStatus, DateTime? parentEndedAt,
        RunCondition? condition = RunCondition.OnSuccess, Action<FakeTimeTicker>? shapeChild = null)
    {
        var child = Row(null, condition: condition, function: "inline-child");
        shapeChild?.Invoke(child);
        var parent = Row(Now.AddHours(1), parentStatus); // not due, so the fallback does not take the parent itself
        parent.ExecutedAt = parentEndedAt;
        parent.Children = [child];
        await _provider.AddTimeTickers([parent], CancellationToken.None);
        return (parent, child);
    }

    private async Task<List<TimeTickerEntity>> Fallback()
    {
        var taken = new List<TimeTickerEntity>();
        await foreach (var row in _provider.QueueTimedOutTimeTickers(CancellationToken.None)) taken.Add(row);
        return taken;
    }

    // The parent's run starts such a child right after the parent ends. One that still waits 30 s later was never
    // started: the fallback claims it as a job of its own, with what it carries.
    [Fact]
    public async Task TheFallback_TakesAChildWithoutATime_ThatStillWaits30SecondsAfterItsParentEnded()
    {
        var grandchild = Row(null, condition: RunCondition.OnSuccess, function: "inline-grandchild");
        var (_, child) = await SeedLeftWaiting(TickerStatus.Done, Now.AddSeconds(-30), shapeChild: c => c.Children = [grandchild]);

        var taken = Assert.Single(await Fallback());

        Assert.Equal(child.Id, taken.Id);
        Assert.Equal([grandchild.Id], taken.Children.Select(x => x.Id));
        var row = await _provider.GetTimeTickerById(child.Id, CancellationToken.None);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("node-A", row.LockHolder);
        Assert.NotNull(row.LockedAt);
        Assert.Null(row.ExecutionTime);
        Assert.Equal(row.UpdatedAt, taken.UpdatedAt);
        Assert.Equal(TickerStatus.Idle, await StatusOf(grandchild.Id));
        Assert.Empty(await Fallback());
    }

    // A final parent without an end time counts as ended long ago: its child must not wait forever.
    [Fact]
    public async Task TheFallback_TakesAChildWithoutATime_WhoseEndedParentHasNoEndTime()
    {
        var (_, child) = await SeedLeftWaiting(TickerStatus.Done, null);

        Assert.Equal([child.Id], (await Fallback()).Select(x => x.Id));
    }

    // Within the grace time the parent's run may still be about to start the child; a parent that has not ended starts
    // it itself.
    [Theory]
    [InlineData(TickerStatus.Done, 29)]
    [InlineData(TickerStatus.Failed, 29)]
    [InlineData(TickerStatus.Idle, 3600)]
    [InlineData(TickerStatus.Queued, 3600)]
    [InlineData(TickerStatus.InProgress, 3600)]
    public async Task TheFallback_LeavesAChildWithoutATime_WhoseParentHasNotEndedLongEnoughAgo(TickerStatus parentStatus, int endedSecondsAgo)
    {
        var (_, child) = await SeedLeftWaiting(parentStatus, Now.AddSeconds(-endedSecondsAgo));

        Assert.Empty(await Fallback());

        Assert.Equal(TickerStatus.Idle, await StatusOf(child.Id));
    }

    // A row another start holds, or one that has run, is not a child left waiting.
    [Theory]
    [InlineData(TickerStatus.Idle, "node-B")]
    [InlineData(TickerStatus.InProgress, "node-B")]
    [InlineData(TickerStatus.Queued, null)]
    [InlineData(TickerStatus.Done, null)]
    public async Task TheFallback_LeavesAChildWithoutATime_ThatDoesNotWaitForItsStart(TickerStatus childStatus, string? lockHolder)
    {
        var (_, child) = await SeedLeftWaiting(TickerStatus.Done, Now.AddMinutes(-5), shapeChild: c =>
        {
            c.Status = childStatus;
            c.LockHolder = lockHolder;
        });

        Assert.Empty(await Fallback());

        Assert.Equal(childStatus, await StatusOf(child.Id));
    }

    // The outcome does not let the child run, or the child was to start with its parent (InProgress) and the parent is
    // over: it never runs. What it carries follows on a later pass, as for a delayed child.
    [Theory]
    [InlineData(TickerStatus.Failed, RunCondition.OnSuccess)]
    [InlineData(TickerStatus.Skipped, RunCondition.OnAnyCompletedStatus)]
    [InlineData(TickerStatus.Skipped, null)]
    [InlineData(TickerStatus.Done, RunCondition.InProgress)]
    public async Task TheFallback_SkipsAChildWithoutATime_ThatItsEndedParentDoesNotLetRun(TickerStatus parentStatus, RunCondition? condition)
    {
        var (_, child) = await SeedLeftWaiting(parentStatus, Now.AddSeconds(-30), condition);

        Assert.Empty(await Fallback());

        var row = await _provider.GetTimeTickerById(child.Id, CancellationToken.None);
        Assert.Equal(TickerStatus.Skipped, row.Status);
        Assert.Equal(ChainRunGate.NotMatchedReason, row.SkippedReason);
        Assert.NotNull(row.ExecutedAt);
    }

    [Fact]
    public async Task TheFallback_DoesNotSkipAChildWithoutATime_WithinTheGraceTime()
    {
        var (_, child) = await SeedLeftWaiting(TickerStatus.Failed, Now.AddSeconds(-29));

        Assert.Empty(await Fallback());

        Assert.Equal(TickerStatus.Idle, await StatusOf(child.Id));
    }

    // One start per child: the inline start of the parent's run and the fallback are claims on the same row.
    [Fact]
    public async Task AChildTheFallbackTook_IsRefusedToAnInlineStart_AndOneAnInlineStartTook_IsNotTakenByTheFallback()
    {
        var (_, takenByFallback) = await SeedLeftWaiting(TickerStatus.Done, Now.AddMinutes(-1));
        var (_, startedInline) = await SeedLeftWaiting(TickerStatus.Done, Now.AddMinutes(-1));
        Assert.Equal(1, await _provider.StartInlineChildAsync(startedInline.Id, Now.AddMilliseconds(500), CancellationToken.None));

        Assert.Equal([takenByFallback.Id], (await Fallback()).Select(x => x.Id));

        Assert.Equal(0, await _provider.StartInlineChildAsync(takenByFallback.Id, Now.AddMilliseconds(501), CancellationToken.None));
    }

    // A child that is started after the fallback read it is not claimed by the fallback's write.
    [Fact]
    public async Task TheFallbacksClaim_IsRefused_WhenTheChildWasStartedAfterItWasRead()
    {
        var (_, first) = await SeedLeftWaiting(TickerStatus.Done, Now.AddMinutes(-1));
        var (_, second) = await SeedLeftWaiting(TickerStatus.Done, Now.AddMinutes(-1));
        var taken = new List<Guid>();

        // The pass has read both children when it hands out the first; the other is started before its claim is written.
        await foreach (var row in _provider.QueueTimedOutTimeTickers(CancellationToken.None))
        {
            taken.Add(row.Id);
            var other = row.Id == first.Id ? second.Id : first.Id;
            Assert.Equal(1, await _provider.StartInlineChildAsync(other, Now.AddHours(-2), CancellationToken.None));
        }

        Assert.Single(taken);
        var started = await _provider.GetTimeTickerById(taken[0] == first.Id ? second.Id : first.Id, CancellationToken.None);
        Assert.Equal(Now.AddHours(-2), started.UpdatedAt);
    }

    #endregion
}
