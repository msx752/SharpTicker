using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SharpTicker.BackgroundServices;
using SharpTicker.Provider;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Instrumentation;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Managers;
using SharpTicker.Utilities.Models;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// The scheduler writes again the outcomes whose final write threw (#74 PR-C). The first group drives the drain with a
/// mocked manager; the second runs real jobs on the in-memory store with the real manager, whose final write is made to fail.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class LostOutcomeDrainTests : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FakeClock(DateTime start) : ITickerClock
    {
        public DateTime UtcNow { get; set; } = start;
    }

    private readonly FakeClock _clock = new(T0);
    private readonly CapturedLogs _logs = new();
    private readonly List<Guid> _timeTickerIds = [];
    private readonly List<Guid> _occurrenceIds = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        TickerCancellationTokenManager.CleanUpTickerCancellationTokens();
        var store = CreateStore("cleanup");
        await store.RemoveTimeTickers(_timeTickerIds.ToArray(), CancellationToken.None);
        await store.RemoveCronTickerOccurrences(_occurrenceIds.ToArray(), CancellationToken.None);
    }

    private LostOutcomeDrain Drain(LostOutcomeRetryList list, IInternalTickerManager manager, ILateOutcomeFollowUp? followUp = null)
        => new(list, manager, followUp, new LoggerFactory([_logs]).CreateLogger<LostOutcomeDrain>(), _clock);

    private static InternalFunctionContext Outcome(TickerStatus status = TickerStatus.Done)
    {
        var context = new InternalFunctionContext
        {
            TickerId = Guid.NewGuid(),
            FunctionName = "lost-fn",
            Type = TickerType.TimeTicker,
            RunMarker = T0
        };
        return context.SetProperty(x => x.Status, status);
    }

    private LostOutcome List(LostOutcomeRetryList list, InternalFunctionContext outcome)
    {
        Assert.True(list.TryAdd(outcome, isChild: false, _clock.UtcNow));
        return list.Snapshot().Single(e => e.Context.TickerId == outcome.TickerId);
    }

    private sealed class RecordingFollowUp : ILateOutcomeFollowUp
    {
        public System.Collections.Concurrent.ConcurrentQueue<(Guid TickerId, bool ClaimChildren)> Calls { get; } = new();
        public Exception? Failure { get; set; }

        public Task AfterLateStoreAsync(InternalFunctionContext outcome, bool claimChildren, CancellationToken cancellationToken)
        {
            Calls.Enqueue((outcome.TickerId, claimChildren));
            return Failure == null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    #region The drain, with a mocked manager

    [Fact]
    public async Task AStoredOutcome_IsRemoved_Reported_AndFollowedUpOnce_WithClaims()
    {
        var list = new LostOutcomeRetryList();
        var manager = Substitute.For<IInternalTickerManager>();
        manager.StoreLostOutcomeAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>()).Returns(true);
        var followUp = new RecordingFollowUp();
        var outcome = Outcome();
        List(list, outcome);

        await Drain(list, manager, followUp).RetryAsync();

        Assert.True(list.IsEmpty);
        Assert.Equal([(outcome.TickerId, true)], followUp.Calls);
        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("was stored late"));
    }

    [Fact]
    public async Task AnOutcomeWhoseRowIsNoLongerThisRuns_IsDroppedWithAWarning_AndNotFollowedUp()
    {
        var list = new LostOutcomeRetryList();
        var manager = Substitute.For<IInternalTickerManager>();
        manager.StoreLostOutcomeAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>()).Returns(false);
        var followUp = new RecordingFollowUp();
        List(list, Outcome());

        await Drain(list, manager, followUp).RetryAsync();

        Assert.True(list.IsEmpty);
        Assert.Empty(followUp.Calls);
        var warning = Assert.Single(_logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("was not stored", warning.Message);
    }

    // The first write that fails again ends the pass: the store is still out of reach. The rest wait for the next pass.
    [Fact]
    public async Task AWriteThatFailsAgain_KeepsTheOutcome_AndEndsThePass()
    {
        var list = new LostOutcomeRetryList();
        var manager = Substitute.For<IInternalTickerManager>();
        var first = Outcome();
        var second = Outcome();
        manager.StoreLostOutcomeAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new InvalidOperationException("the store is down")), Task.FromResult(true));
        List(list, first);
        List(list, second);
        var drain = Drain(list, manager);

        await drain.RetryAsync();

        Assert.Equal(2, list.Snapshot().Count);
        Assert.Equal(1, list.Snapshot()[0].FailedAttempts);
        await manager.Received(1).StoreLostOutcomeAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>());
        Assert.DoesNotContain(_logs.Entries, e => e.Level >= LogLevel.Warning);

        await drain.RetryAsync();

        Assert.True(list.IsEmpty);
        Assert.Contains(_logs.Entries, e => e.Message.Contains("after 1 failed attempt"));
    }

    // A write that hangs ends at the attempt limit instead of holding the loop.
    [Fact]
    public async Task AWriteThatHangs_EndsAtTheAttemptLimit()
    {
        var list = new LostOutcomeRetryList();
        var manager = Substitute.For<IInternalTickerManager>();
        manager.StoreLostOutcomeAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>()).ContinueWith(t => { t.Wait(); return true; }));
        List(list, Outcome());
        var drain = Drain(list, manager);
        drain.OutcomeAttemptLimit = TimeSpan.FromMilliseconds(200);

        var pass = drain.RetryAsync();

        Assert.Same(pass, await Task.WhenAny(pass, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.Single(list.Snapshot());
    }

    // While outcomes stay unstored, a reminder names them once the oldest has waited the interval: at Error, then at Critical
    // every interval after that.
    [Fact]
    public async Task OutcomesThatStayUnstored_AreRemindedOfAtErrorThenAtCritical()
    {
        var list = new LostOutcomeRetryList();
        var manager = Substitute.For<IInternalTickerManager>();
        manager.StoreLostOutcomeAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the store is down"));
        var outcome = Outcome();
        List(list, outcome);
        var drain = Drain(list, manager);

        _clock.UtcNow = T0 + LostOutcomeDrain.ReminderInterval - TimeSpan.FromSeconds(1);
        await drain.RetryAsync();
        Assert.DoesNotContain(_logs.Entries, e => e.Level >= LogLevel.Error);

        _clock.UtcNow = T0 + LostOutcomeDrain.ReminderInterval;
        await drain.RetryAsync();
        var error = Assert.Single(_logs.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(outcome.TickerId.ToString(), error.Message);

        _clock.UtcNow += TimeSpan.FromMinutes(1);
        await drain.RetryAsync();
        Assert.Single(_logs.Entries, e => e.Level >= LogLevel.Error);

        _clock.UtcNow = T0 + LostOutcomeDrain.ReminderInterval * 2;
        await drain.RetryAsync();
        Assert.Single(_logs.Entries, e => e.Level == LogLevel.Critical);
    }

    // The loop and the stop can write the same outcome at once, and both writes report it stored. Only one reports it and
    // follows it up.
    [Fact]
    public async Task TheLoopAndTheStopWritingTheSameOutcome_FollowItUpOnce()
    {
        var list = new LostOutcomeRetryList();
        var manager = Substitute.For<IInternalTickerManager>();
        var loopWriteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoopWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        manager.StoreLostOutcomeAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref calls) > 1)
                    return Task.FromResult(true);
                loopWriteEntered.SetResult();
                return releaseLoopWrite.Task;
            });
        var followUp = new RecordingFollowUp();
        List(list, Outcome());
        var drain = Drain(list, manager, followUp);

        var loopPass = drain.RetryAsync();
        await loopWriteEntered.Task;
        await drain.StoreAtStopAsync();
        releaseLoopWrite.SetResult(true);
        await loopPass;

        Assert.Equal(2, calls);
        Assert.Single(followUp.Calls);
        Assert.Single(_logs.Entries, e => e.Message.Contains("was stored late"));
    }

    // The stop writes each listed outcome once more: a stored one is followed up without claims (the stopped scheduler would
    // refuse a claimed child); after the first that fails no more are tried, and each one not stored is an error and stays
    // listed for a start of the scheduler in this process. The list takes nothing more.
    [Fact]
    public async Task AtTheStop_StoredOutcomesClaimNoChild_AndTheOnesNotStoredAreErrors()
    {
        var list = new LostOutcomeRetryList();
        var manager = Substitute.For<IInternalTickerManager>();
        var stored = Outcome();
        var failing = Outcome();
        var notTried = Outcome();
        manager.StoreLostOutcomeAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true), Task.FromException<bool>(new InvalidOperationException("the store is down")));
        var followUp = new RecordingFollowUp();
        List(list, stored);
        List(list, failing);
        List(list, notTried);

        await Drain(list, manager, followUp).StoreAtStopAsync();

        Assert.Equal([(stored.TickerId, false)], followUp.Calls);
        await manager.Received(2).StoreLostOutcomeAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>());
        var errors = _logs.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Message.Contains(failing.TickerId.ToString()));
        Assert.Contains(errors, e => e.Message.Contains(notTried.TickerId.ToString()));
        Assert.Equal(new[] { failing.TickerId, notTried.TickerId }, list.Snapshot().Select(e => e.Context.TickerId));
        Assert.False(list.TryAdd(Outcome(), isChild: false, T0));
    }

    [Fact]
    public async Task AFollowUpThatFails_IsAWarning_AndTheOutcomeStaysStored()
    {
        var list = new LostOutcomeRetryList();
        var manager = Substitute.For<IInternalTickerManager>();
        manager.StoreLostOutcomeAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>()).Returns(true);
        var followUp = new RecordingFollowUp();
        followUp.Failure = new InvalidOperationException("skip write failed");
        List(list, Outcome());

        await Drain(list, manager, followUp).RetryAsync();

        Assert.True(list.IsEmpty);
        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("could not be followed up"));
    }

    #endregion

    #region Real jobs on the in-memory store

    private TickerInMemoryPersistenceProvider<TimeTickerEntity, CronTickerEntity> CreateStore(string nodeIdentifier)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITickerClock>(_clock);
        services.AddSingleton(new SchedulerOptionsBuilder { NodeIdentifier = nodeIdentifier });
        return new TickerInMemoryPersistenceProvider<TimeTickerEntity, CronTickerEntity>(services.BuildServiceProvider());
    }

    private IInternalTickerManager RealManager(string nodeIdentifier)
        => new InternalTickerManager<TimeTickerEntity, CronTickerEntity>(CreateStore(nodeIdentifier), _clock, Substitute.For<ISharpTickerNotificationHubSender>());

    /// <summary>
    /// The real manager, except that a job's final write fails: it throws before it reaches the store, or with
    /// <see cref="LandThenThrow"/> it lands and then throws (a lost reply). Only the first <see cref="FinalWritesToFail"/> fail.
    /// The loop finds nothing to run.
    /// </summary>
    private sealed class FlakyFinalWrite
    {
        public IInternalTickerManager Manager { get; }
        public int FinalWritesToFail { get; set; } = 1;
        public bool LandThenThrow { get; set; }
        /// <summary>The first this-many writes again of a lost outcome throw too, as while the store stays down.</summary>
        public int LostOutcomeWritesToFail { get; set; }

        public FlakyFinalWrite(IInternalTickerManager real)
        {
            var manager = Substitute.For<IInternalTickerManager>();
            manager.GetNextTickers(Arg.Any<CancellationToken>()).Returns((Timeout.InfiniteTimeSpan, Array.Empty<InternalFunctionContext>()));
            manager.FencesRuns.Returns(real.FencesRuns);
            manager.NextClaimMarker().Returns(_ => real.NextClaimMarker());
            manager.StoreLostOutcomeAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>())
                .Returns(call => LostOutcomeWritesToFail-- > 0
                    ? Task.FromException<bool>(new InvalidOperationException("the store is still down"))
                    : real.StoreLostOutcomeAsync(call.Arg<InternalFunctionContext>(), call.Arg<CancellationToken>()));
            manager.UpdateTickerAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    var context = call.Arg<InternalFunctionContext>();
                    if (context.Status == TickerStatus.InProgress || FinalWritesToFail <= 0)
                        return await real.UpdateTickerAsync(context, call.Arg<CancellationToken>());
                    FinalWritesToFail--;
                    if (LandThenThrow)
                        await real.UpdateTickerAsync(context, call.Arg<CancellationToken>());
                    throw new InvalidOperationException("the store is down");
                });
            Manager = manager;
        }
    }

    private async Task<Guid> SeedTimeTicker()
    {
        var ticker = new TimeTickerEntity
        {
            Id = Guid.NewGuid(),
            Function = "lost-fn",
            ExecutionTime = T0,
            Status = TickerStatus.Idle,
            CreatedAt = T0.AddHours(-2),
            UpdatedAt = T0.AddHours(-1),
            Request = []
        };
        _timeTickerIds.Add(ticker.Id);
        await CreateStore("seed").AddTimeTickers([ticker], CancellationToken.None);
        return ticker.Id;
    }

    // Queues and claims the row the way the scheduler does, on the node of the manager. Returns the context the run gets.
    private async Task<InternalFunctionContext> Claim(IInternalTickerManager manager, string nodeIdentifier, Guid id, TickerFunctionDelegate run)
    {
        var store = CreateStore(nodeIdentifier);
        var window = await store.GetEarliestTimeTickers(CancellationToken.None);
        await foreach (var _ in store.QueueTimeTickers(window.Where(x => x.Id == id).ToArray(), CancellationToken.None)) { }
        var context = new InternalFunctionContext
        {
            TickerId = id, FunctionName = "lost-fn", Type = TickerType.TimeTicker, ExecutionTime = T0,
            RetryIntervals = [], CachedDelegate = run, TimeTickerChildren = []
        };
        Assert.True(await manager.TryClaimDueTicker(context, manager.NextClaimMarker(), CancellationToken.None));
        return context;
    }

    private Task<TimeTickerEntity> Row(Guid id) => CreateStore("reader").GetTimeTickerById(id, CancellationToken.None);

    private (TickerExecutionTaskHandler Handler, LostOutcomeRetryList List) Handler(IInternalTickerManager manager)
    {
        var list = new LostOutcomeRetryList();
        var services = new ServiceCollection();
        services.AddSingleton(list);
        services.AddLogging(b => b.AddProvider(_logs));
        var handler = new TickerExecutionTaskHandler(services.BuildServiceProvider(), _clock, Substitute.For<ISharpTickerInstrumentation>(), manager);
        return (handler, list);
    }

    // T1: the final write throws once. The listed outcome wakes the loop, which sleeps for a day, and the loop stores it: the
    // row ends Done, and the job ran once. With the sleep cap long, only the wake can do it. When the store is still down at
    // the wake, the loop keeps its sleep short (the cap) while the outcome waits, and stores it on a later pass.
    [Theory]
    [InlineData(0, 3600_000)]
    [InlineData(1, 300)]
    public async Task ALostOutcome_WakesTheLoop_WhichStoresIt(int writesAgainThatFail, int sleepCapMs)
    {
        var real = RealManager("node-A");
        var flaky = new FlakyFinalWrite(real) { LostOutcomeWritesToFail = writesAgainThatFail };
        var (handler, list) = Handler(flaky.Manager);
        var drain = Drain(list, flaky.Manager, handler);
        // The loop reports its next planned occurrence right before it goes to sleep.
        var asleep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionContext = new TickerExecutionContext
        {
            NotifyCoreAction = (_, type) => { if (type == CoreNotifyActionType.NotifyNextOccurence) asleep.TrySetResult(); }
        };
        var service = new SharpTickerSchedulerBackgroundService(
            executionContext, handler, Substitute.For<ISharpTickerTaskScheduler>(), flaky.Manager,
            new SchedulerOptionsBuilder(), new TickerFunctionConcurrencyGate(), lostOutcomes: drain)
        {
            PutBackRetryInterval = TimeSpan.FromMilliseconds(sleepCapMs)
        };
        var runs = 0;
        var id = await SeedTimeTicker();
        var run = await Claim(real, "node-A", id, (_, _, _) => { Interlocked.Increment(ref runs); return Task.CompletedTask; });
        await service.StartAsync(CancellationToken.None);
        try
        {
            // The job ends while the loop sleeps for a day, so only the wake can make it write the outcome soon.
            await asleep.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            await handler.ExecuteTaskAsync(run, isDue: false);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while ((await Row(id)).Status != TickerStatus.Done && DateTime.UtcNow < deadline)
                await Task.Delay(25);

            Assert.Equal(TickerStatus.Done, (await Row(id)).Status);
            Assert.Equal(1, runs);
            Assert.True(list.IsEmpty);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    // T2: with a retry, the write of the failed attempt lands and the final write throws; the outcome written again lands on
    // the same marker, with the run's retry count.
    [Fact]
    public async Task ALostOutcomeAfterARetry_IsStoredOnTheSameMarker()
    {
        var real = RealManager("node-A");
        var flaky = new FlakyFinalWrite(real);
        var (handler, list) = Handler(flaky.Manager);
        var calls = 0;
        var id = await SeedTimeTicker();
        var run = await Claim(real, "node-A", id, (_, _, _) => ++calls == 1 ? throw new InvalidOperationException("first attempt fails") : Task.CompletedTask);
        run.Retries = 1;
        run.RetryIntervals = [0];

        await handler.ExecuteTaskAsync(run, isDue: false);
        Assert.Equal(1, (await Row(id)).RetryCount);
        await Drain(list, flaky.Manager, handler).RetryAsync();

        var row = await Row(id);
        Assert.Equal(TickerStatus.Done, row.Status);
        Assert.Equal(1, row.RetryCount);
        Assert.Equal(run.ClaimMarker, row.UpdatedAt);
        Assert.Equal(2, calls);
    }

    // T3: the row was released and another node claimed it before the write again: the write changes nothing, and the
    // outcome is dropped with a warning.
    [Fact]
    public async Task ALostOutcomeOfARowAnotherNodeTook_ChangesNothing_AndIsDropped()
    {
        var real = RealManager("node-A");
        var flaky = new FlakyFinalWrite(real);
        var (handler, list) = Handler(flaky.Manager);
        var id = await SeedTimeTicker();
        var run = await Claim(real, "node-A", id, (_, _, _) => Task.CompletedTask);
        await handler.ExecuteTaskAsync(run, isDue: false);
        Assert.Single(list.Snapshot());

        Assert.True(await CreateStore("node-A").TryReleaseTimeTickerClaim(id, run.ClaimMarker!.Value, CancellationToken.None));
        var b = RealManager("node-B");
        var runB = await Claim(b, "node-B", id, (_, _, _) => Task.CompletedTask);
        await Drain(list, flaky.Manager, handler).RetryAsync();

        Assert.True(list.IsEmpty);
        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("was not stored"));
        var row = await Row(id);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("node-B", row.LockHolder);
        Assert.Equal(runB.ClaimMarker, row.UpdatedAt);
    }

    // T4: the final write landed and then threw (a lost reply). Written again on the same marker it lands again: stored, and
    // followed up once.
    [Fact]
    public async Task ALostOutcomeWhoseWriteHadLanded_IsStored_AndFollowedUpOnce()
    {
        var real = RealManager("node-A");
        var flaky = new FlakyFinalWrite(real) { LandThenThrow = true };
        var (handler, list) = Handler(flaky.Manager);
        var followUp = new RecordingFollowUp();
        var id = await SeedTimeTicker();
        var run = await Claim(real, "node-A", id, (_, _, _) => Task.CompletedTask);
        await handler.ExecuteTaskAsync(run, isDue: false);
        Assert.Equal(TickerStatus.Done, (await Row(id)).Status);

        await Drain(list, flaky.Manager, followUp).RetryAsync();

        Assert.True(list.IsEmpty);
        Assert.Single(followUp.Calls);
        Assert.Equal(TickerStatus.Done, (await Row(id)).Status);
    }

    #endregion
}
