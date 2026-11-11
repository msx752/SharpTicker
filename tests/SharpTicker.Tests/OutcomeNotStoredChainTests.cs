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
    private readonly ISharpTickerDispatcher _dispatcher = Substitute.For<ISharpTickerDispatcher>();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _runs = new();
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
        handlerServices.AddSingleton(_dispatcher); // not enabled unless a test says so
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

    // A parent whose outcome was stored late, with an OnSuccess inline child that carries an inline grandchild. The contexts
    // are the ones the parent's run carried.
    private async Task<(InternalFunctionContext Outcome, ChainTimeTicker Child, ChainTimeTicker Grandchild)> AParentStoredLate()
    {
        var grandchild = Row(null, TickerStatus.Idle, RunCondition.OnSuccess, "grandchild");
        var child = Row(null, TickerStatus.Idle, RunCondition.OnSuccess, "child");
        child.Children = [grandchild];
        var parent = Row(Now.AddMinutes(-1), TickerStatus.Done);
        parent.Children = [child];
        await _store.AddTimeTickers([parent], CancellationToken.None);

        var outcome = Context(parent.Id, (_, _, _) => Task.CompletedTask);
        outcome.Status = TickerStatus.Done;
        var childContext = Context(child.Id, (_, _, _) => Task.CompletedTask, RunCondition.OnSuccess);
        childContext.TimeTickerChildren.Add(Context(grandchild.Id, (_, _, _) => Task.CompletedTask, RunCondition.OnSuccess));
        outcome.TimeTickerChildren.Add(childContext);
        return (outcome, child, grandchild);
    }

    // The dispatcher runs what the handler claimed, as a claimed job: the functions are not registered in a test, so each
    // context gets one that counts its runs.
    private void TheDispatcherRunsWhatIsClaimed()
    {
        void Count(InternalFunctionContext context)
        {
            context.CachedDelegate = (_, _, _) => { _runs.AddOrUpdate(context.TickerId, 1, (_, n) => n + 1); return Task.CompletedTask; };
            foreach (var child in context.TimeTickerChildren) Count(child);
        }

        _dispatcher.IsEnabled.Returns(true);
        _dispatcher.DispatchAsync(Arg.Any<InternalFunctionContext[]>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            foreach (var context in call.Arg<InternalFunctionContext[]>())
            {
                Count(context);
                await _handler.ExecuteTaskAsync(context, isDue: false);
            }
        });
    }

    private async Task<TickerStatus> StatusOf(Guid id) => (await _store.GetTimeTickerById(id, CancellationToken.None)).Status;

    // After the late store the inline child is claimed from the store and runs as a claimed job, and its own inline child
    // runs after it: each once, also when the follow-up comes a second time.
    [Fact]
    public async Task AnInlineChildAndItsChild_RunOnce_AfterTheirParentsOutcomeWasStoredLate()
    {
        var (outcome, child, grandchild) = await AParentStoredLate();
        TheDispatcherRunsWhatIsClaimed();

        await _handler.AfterLateStoreAsync(outcome, claimChildren: true, CancellationToken.None);
        await _handler.AfterLateStoreAsync(outcome, claimChildren: true, CancellationToken.None);

        Assert.Equal(1, _runs.GetValueOrDefault(child.Id));
        Assert.Equal(1, _runs.GetValueOrDefault(grandchild.Id));
        Assert.Equal(TickerStatus.Done, await StatusOf(child.Id));
        Assert.Equal(TickerStatus.Done, await StatusOf(grandchild.Id));
    }

    // The store decides who starts the child: when another claim has taken its row meanwhile (it was given a time and the
    // fallback claimed it), the late follow-up starts nothing.
    [Fact]
    public async Task AnInlineChildAnotherClaimTookMeanwhile_IsNotStartedByTheLateFollowUp()
    {
        var (outcome, child, _) = await AParentStoredLate();
        TheDispatcherRunsWhatIsClaimed();
        Assert.Single(await _store.AcquireImmediateTimeTickersAsync([child.Id], CancellationToken.None));

        await _handler.AfterLateStoreAsync(outcome, claimChildren: true, CancellationToken.None);

        Assert.Equal(0, _runs.GetValueOrDefault(child.Id));
        Assert.Equal(TickerStatus.InProgress, await StatusOf(child.Id));
    }

    // At the stop nothing is claimed: the child keeps its row as it is.
    [Fact]
    public async Task AnInlineChild_IsNotStarted_WhenItsParentsOutcomeWasStoredAtTheStop()
    {
        var (outcome, child, grandchild) = await AParentStoredLate();
        TheDispatcherRunsWhatIsClaimed();

        await _handler.AfterLateStoreAsync(outcome, claimChildren: false, CancellationToken.None);

        Assert.Empty(_runs);
        Assert.Equal(TickerStatus.Idle, await StatusOf(child.Id));
        Assert.Equal(TickerStatus.Idle, await StatusOf(grandchild.Id));
    }
}
