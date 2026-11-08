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
    private readonly TickerExecutionTaskHandler _handler;

    public DelayedChildHandlerTests()
    {
        var clock = Substitute.For<ITickerClock>();
        clock.UtcNow.Returns(DateTime.UtcNow);
        var instrumentation = Substitute.For<ISharpTickerInstrumentation>();
        var services = new ServiceCollection();
        services.AddSingleton(_internalManager);
        services.AddSingleton(instrumentation);
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
