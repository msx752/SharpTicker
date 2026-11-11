using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
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

// A chain whose parent's outcome the store does not take, with the in-memory store, the real manager and the real handler.
// The entity types are private, so the store is separate from other tests.
[Collection(SharedStaticStateCollection.Name)]
public class OutcomeNotStoredChainTests : IAsyncLifetime
{
    private sealed class ChainTimeTicker : TimeTickerEntity<ChainTimeTicker> { }
    private sealed class ChainCronTicker : CronTickerEntity { }

    private static readonly DateTime Now = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    private readonly TickerInMemoryPersistenceProvider<ChainTimeTicker, ChainCronTicker> _store;
    private readonly TickerExecutionTaskHandler _handler;
    private readonly List<Guid> _rows = [];
    private long _marker;

    public OutcomeNotStoredChainTests()
    {
        var clock = Substitute.For<ITickerClock>();
        clock.UtcNow.Returns(Now);
        clock.NextClaimMarker().Returns(_ => Now.AddMilliseconds(Interlocked.Increment(ref _marker)));
        var services = new ServiceCollection();
        services.AddSingleton(clock);
        services.AddSingleton(new SchedulerOptionsBuilder { NodeIdentifier = "node-A" });
        _store = new TickerInMemoryPersistenceProvider<ChainTimeTicker, ChainCronTicker>(services.BuildServiceProvider());

        var hub = Substitute.For<ISharpTickerNotificationHubSender>();
        var internalManager = new InternalTickerManager<ChainTimeTicker, ChainCronTicker>(_store, clock, hub);
        var instrumentation = Substitute.For<ISharpTickerInstrumentation>();
        var handlerServices = new ServiceCollection();
        handlerServices.AddSingleton<IInternalTickerManager>(internalManager);
        handlerServices.AddSingleton(instrumentation);
        _handler = new TickerExecutionTaskHandler(handlerServices.BuildServiceProvider(), clock, instrumentation, internalManager);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        TickerCancellationTokenManager.CleanUpTickerCancellationTokens();
        await _store.RemoveTimeTickers(_rows.ToArray(), CancellationToken.None);
    }

    private ChainTimeTicker Row(DateTime? executionTime, TickerStatus status, RunCondition? condition = null, string function = "fn")
    {
        var row = new ChainTimeTicker
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
        _rows.Add(row.Id);
        return row;
    }

    private static InternalFunctionContext Context(Guid id, TickerFunctionDelegate run, RunCondition condition = RunCondition.OnAnyCompletedStatus)
        => new()
        {
            TickerId = id,
            FunctionName = "fn",
            Type = TickerType.TimeTicker,
            RetryIntervals = [],
            RunCondition = condition,
            Status = TickerStatus.InProgress,
            CachedDelegate = run,
            TimeTickerChildren = []
        };

    // The parent's final status write can save nothing: here its row is deleted while it runs. The store then reports that no
    // row was written, and the inline child, which follows the parent's outcome as the store has it, does not run.
    [Fact]
    public async Task AnInlineChild_DoesNotRun_WhenItsParentsOutcomeWasNotSaved()
    {
        var child = Row(null, TickerStatus.Idle, RunCondition.OnSuccess, "child");
        var parent = Row(Now.AddMinutes(-1), TickerStatus.InProgress);
        parent.LockHolder = "node-A";
        parent.LockedAt = Now;
        parent.Children = [child];
        await _store.AddTimeTickers([parent], CancellationToken.None);

        var childRan = false;
        var parentContext = Context(parent.Id, async (_, _, _) => await _store.RemoveTimeTickers([parent.Id], CancellationToken.None));
        parentContext.TimeTickerChildren.Add(Context(child.Id, (_, _, _) => { childRan = true; return Task.CompletedTask; }, RunCondition.OnSuccess));

        await _handler.ExecuteTaskAsync(parentContext, isDue: false);

        Assert.Null(await _store.GetTimeTickerById(parent.Id, CancellationToken.None)); // the outcome write saved nothing
        Assert.Equal(TickerStatus.Done, parentContext.Status);
        Assert.False(childRan, "the inline child ran although its parent's outcome was never stored");
    }
}
