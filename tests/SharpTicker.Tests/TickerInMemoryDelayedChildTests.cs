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
}
