using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SharpTicker.BackgroundServices;
using SharpTicker.SharpTickerThreadPool;
using SharpTicker.Provider;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Models;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// The scheduler loop hands a job to a worker only after this node won the claim on its row. A loop that wakes late finds the
/// row taken by the fallback or cancelled, and must not run it. The first group drives the loop with a substitute manager, the
/// second with the real manager and the in-memory store.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class SchedulerClaimHandoverTests : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FakeClock(DateTime start) : ITickerClock
    {
        public DateTime UtcNow { get; set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    private sealed class RecordingLogger : ILogger<SharpTickerSchedulerBackgroundService>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((logLevel, formatter(state, exception)));
    }

    /// <summary>Runs the queued work on the thread pool and remembers it, so a test can wait for what the loop handed over.</summary>
    private sealed class RecordingTaskScheduler : ISharpTickerTaskScheduler
    {
        private readonly ConcurrentQueue<Task> _running = new();
        public ConcurrentQueue<TickerTaskPriority> Priorities { get; } = new();
        private volatile bool _frozen;
        public bool IsFrozen => _frozen;
        public bool IsDisposed => false;
        /// <summary>Called after a job was accepted, with the number of jobs accepted so far.</summary>
        public Action<int>? OnQueued { get; set; }
        public int QueuedCount => Priorities.Count;
        public int ActiveWorkers => 0;
        public int TotalQueuedTasks => 0;

        public ValueTask QueueAsync(Func<CancellationToken, Task> work, TickerTaskPriority priority, CancellationToken cancellationToken = default)
        {
            // Like the real scheduler, a refusal is in the returned task and not a throw.
            if (_frozen)
                return new ValueTask(Task.FromException(new InvalidOperationException("Scheduler is frozen")));

            Priorities.Enqueue(priority);
            _running.Enqueue(Task.Run(() => work(CancellationToken.None)));
            OnQueued?.Invoke(Priorities.Count);
            return ValueTask.CompletedTask;
        }

        public Task WhenAllIdleAsync() => Task.WhenAll(_running.ToArray());

        public void Freeze() => _frozen = true;
        public void Resume() => _frozen = false;
        public string GetDiagnostics() => string.Empty;
        public Task<bool> WaitForRunningTasksAsync(TimeSpan? timeout = null) => Task.FromResult(true);
    }

    /// <summary>
    /// Starts the queued work on the calling thread with the job's token, so the work has reached its first wait when
    /// <see cref="QueueAsync"/> returns.
    /// </summary>
    private sealed class InlineTaskScheduler : ISharpTickerTaskScheduler
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

    private readonly List<Guid> _timeTickerIds = [];
    private readonly List<Guid> _occurrenceIds = [];
    private readonly List<Guid> _cronTickerIds = [];
    private readonly FakeClock _clock = new(T0);

    public Task InitializeAsync()
    {
        TickerFunctionProvider.Build();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        var node = CreateProvider("cleanup");
        await node.RemoveTimeTickers(_timeTickerIds.ToArray(), CancellationToken.None);
        await node.RemoveCronTickerOccurrences(_occurrenceIds.ToArray(), CancellationToken.None);
        await node.RemoveCronTickers(_cronTickerIds.ToArray(), CancellationToken.None);
    }

    private static InternalFunctionContext Function(string name, TickerTaskPriority priority = TickerTaskPriority.Normal, params InternalFunctionContext[] children)
        => new()
        {
            TickerId = Guid.NewGuid(),
            FunctionName = name,
            Type = TickerType.TimeTicker,
            CachedPriority = priority,
            TimeTickerChildren = children.ToList()
        };

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

    private static Task InvokeRunSchedulerAsync(SharpTickerSchedulerBackgroundService service, CancellationToken cancellationToken)
    {
        var method = typeof(SharpTickerSchedulerBackgroundService)
            .GetMethod("RunSharpTickerSchedulerAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)method.Invoke(service, [CancellationToken.None, cancellationToken])!;
    }

    // The loop is run until it has handed the batch over and polled again, which is where a batch ends.
    private static async Task RunOneBatchAsync(TickerExecutionContext context, IInternalTickerManager manager,
        ISharpTickerTaskScheduler scheduler, ITickerExecutionTaskHandler handler, InternalFunctionContext[] batch,
        ILogger<SharpTickerSchedulerBackgroundService>? logger = null)
    {
        manager.GetNextTickers(Arg.Any<CancellationToken>()).Returns((Timeout.InfiniteTimeSpan, Array.Empty<InternalFunctionContext>()));
        context.SetFunctions(batch);
        var service = new SharpTickerSchedulerBackgroundService(context, handler, scheduler, manager,
            new SchedulerOptionsBuilder { MinPollingInterval = TimeSpan.FromMilliseconds(10) }, new TickerFunctionConcurrencyGate(), logger);

        using var cts = new CancellationTokenSource();
        var run = InvokeRunSchedulerAsync(service, cts.Token);
        Assert.True(await WaitUntilAsync(() => manager.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IInternalTickerManager.GetNextTickers))),
            "the loop never polled after the batch");
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }
    }

    private static int Calls(IInternalTickerManager manager, string method)
        => manager.ReceivedCalls().Count(c => c.GetMethodInfo().Name == method);

    [Fact]
    public async Task Handover_LostClaim_JobIsNotQueued()
    {
        var manager = Substitute.For<IInternalTickerManager>();
        var scheduler = Substitute.For<ISharpTickerTaskScheduler>();
        var handler = Substitute.For<ITickerExecutionTaskHandler>();
        var logger = new RecordingLogger();
        manager.TryClaimDueTicker(Arg.Any<InternalFunctionContext>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(false);
        var job = Function("Handover.Lost");

        await RunOneBatchAsync(new TickerExecutionContext(), manager, scheduler, handler, [job], logger);

        await manager.Received(1).TryClaimDueTicker(job, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await scheduler.DidNotReceiveWithAnyArgs().QueueAsync(default!, default, default);
        await manager.DidNotReceiveWithAnyArgs().NotifyInProgress(default!);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains(job.TickerId.ToString()) && e.Message.Contains("Handover.Lost"));
    }

    [Fact]
    public async Task Handover_ChainParentLost_ChildrenAreNotRun()
    {
        var manager = Substitute.For<IInternalTickerManager>();
        var scheduler = new RecordingTaskScheduler();
        var handler = Substitute.For<ITickerExecutionTaskHandler>();
        handler.ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        manager.TryClaimDueTicker(Arg.Any<InternalFunctionContext>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(false);
        var child = Function("Handover.Child");
        var parent = Function("Handover.Parent", TickerTaskPriority.Normal, child);

        await RunOneBatchAsync(new TickerExecutionContext(), manager, scheduler, handler, [parent]);
        await scheduler.WhenAllIdleAsync();

        // Only the parent is a row of the batch; its children are run by the handler once the parent runs, so a parent that
        // is not handed over takes its children with it.
        Assert.Equal(0, scheduler.QueuedCount);
        await handler.DidNotReceiveWithAnyArgs().ExecuteTaskAsync(default!, default, default);
    }

    [Fact]
    public async Task Handover_NotificationThrows_JobStillRuns()
    {
        var manager = Substitute.For<IInternalTickerManager>();
        var scheduler = new RecordingTaskScheduler();
        var handler = Substitute.For<ITickerExecutionTaskHandler>();
        var logger = new RecordingLogger();
        handler.ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        manager.TryClaimDueTicker(Arg.Any<InternalFunctionContext>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);
        manager.NotifyInProgress(Arg.Any<InternalFunctionContext>()).Returns<Task>(_ => throw new InvalidOperationException("hub is down"));
        var job = Function("Handover.Notify");

        await RunOneBatchAsync(new TickerExecutionContext(), manager, scheduler, handler, [job], logger);
        await scheduler.WhenAllIdleAsync();

        await handler.Received(1).ExecuteTaskAsync(job, false, Arg.Any<CancellationToken>());
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Handover_KeepsPriorityOrder()
    {
        var manager = Substitute.For<IInternalTickerManager>();
        var scheduler = new RecordingTaskScheduler();
        var handler = Substitute.For<ITickerExecutionTaskHandler>();
        handler.ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var claimed = new ConcurrentQueue<Guid>();
        manager.TryClaimDueTicker(Arg.Any<InternalFunctionContext>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            claimed.Enqueue(call.Arg<InternalFunctionContext>().TickerId);
            return true;
        });
        var normal = Function("Handover.Normal", TickerTaskPriority.Normal);
        var high = Function("Handover.High", TickerTaskPriority.High);
        var longRunning = Function("Handover.LongRunning", TickerTaskPriority.LongRunning);
        var batch = new[] { normal, high, longRunning };

        await RunOneBatchAsync(new TickerExecutionContext(), manager, scheduler, handler, batch);
        await scheduler.WhenAllIdleAsync();

        var expected = batch.OrderBy(x => x.CachedPriority).ToArray();
        Assert.NotEqual(batch.Select(x => x.TickerId), expected.Select(x => x.TickerId));
        Assert.Equal(expected.Select(x => x.TickerId), claimed);
        Assert.Equal(expected.Select(x => x.CachedPriority), scheduler.Priorities);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Real manager over the in-memory store
    // ---------------------------------------------------------------------------------------------------------------

    private TickerInMemoryPersistenceProvider<TimeTickerEntity, CronTickerEntity> CreateProvider(string nodeIdentifier)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITickerClock>(_clock);
        services.AddSingleton(new SchedulerOptionsBuilder { NodeIdentifier = nodeIdentifier });
        return new TickerInMemoryPersistenceProvider<TimeTickerEntity, CronTickerEntity>(services.BuildServiceProvider());
    }

    private IInternalTickerManager CreateRealManager(TickerInMemoryPersistenceProvider<TimeTickerEntity, CronTickerEntity> provider)
    {
        var hub = Substitute.For<ISharpTickerNotificationHubSender>();
        hub.UpdateTimeTickerNotifyAsync(Arg.Any<object>()).Returns(Task.CompletedTask);
        hub.UpdateTimeTickerFromInternalFunctionContext<TimeTickerEntity>(Arg.Any<InternalFunctionContext>()).Returns(Task.CompletedTask);
        hub.UpdateCronOccurrenceFromInternalFunctionContext<CronTickerEntity>(Arg.Any<InternalFunctionContext>()).Returns(Task.CompletedTask);

        var type = typeof(IInternalTickerManager).Assembly
            .GetType("SharpTicker.Utilities.Managers.InternalTickerManager`2")!
            .MakeGenericType(typeof(TimeTickerEntity), typeof(CronTickerEntity));
        return (IInternalTickerManager)Activator.CreateInstance(type,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, [provider, _clock, hub], null)!;
    }

    /// <summary>
    /// The real manager with two hooks: a poll that has queued its rows can be held before it returns, so the loop wakes
    /// late by the test's choice and not by a sleep, and a claim can be made to fail or to restart the loop on a given call.
    /// </summary>
    private sealed class HookedManager(IInternalTickerManager inner) : IInternalTickerManager
    {
        private int _polls;
        private int _claims;
        public TaskCompletionSource HoldFirstPoll { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstPollQueued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Polls => Volatile.Read(ref _polls);
        public int ThrowOnClaim { get; set; }
        public Func<CancellationToken, Task>? OnClaim { get; set; }
        /// <summary>Runs before the n-th claim is forwarded, which then goes on.</summary>
        public int BeforeClaimNumber { get; set; }
        public Func<Task>? BeforeClaim { get; set; }
        /// <summary>Like <see cref="BeforeClaim"/>, but waits on the token the claim was given, as a store call does.</summary>
        public Func<CancellationToken, Task>? BeforeClaimWithToken { get; set; }
        public bool UseHold { get; set; }
        /// <summary>When set, the held first poll returns this instead of what the store has.</summary>
        public (TimeSpan, InternalFunctionContext[])? CannedFirstPoll { get; set; }
        public ConcurrentQueue<bool> ClaimResults { get; } = new();

        public async Task<(TimeSpan TimeRemaining, InternalFunctionContext[] Functions)> GetNextTickers(CancellationToken cancellationToken = default)
        {
            var poll = Interlocked.Increment(ref _polls);
            if (!UseHold)
                return (Timeout.InfiniteTimeSpan, []);

            if (poll > 1)
                return (Timeout.InfiniteTimeSpan, []);

            var result = CannedFirstPoll ?? await inner.GetNextTickers(cancellationToken);
            FirstPollQueued.SetResult();
            await HoldFirstPoll.Task;
            return result;
        }

        /// <summary>The claim of this row throws before it reaches the store.</summary>
        public Guid? ThrowBeforeClaimOf { get; set; }
        /// <summary>The claim of this row reaches the store and its write lands, then the call throws (a lost reply).</summary>
        public Guid? ThrowAfterClaimOf { get; set; }
        /// <summary>The first this-many put-backs throw, as when the store cannot be reached.</summary>
        public int FailPutBacks { get; set; }
        /// <summary>The first this-many put-backs reach the store and land, then throw (a lost reply).</summary>
        public int LandThenFailPutBacks { get; set; }
        /// <summary>When set, replaces what the store says about concurrent claims.</summary>
        public int? FanOut { get; set; }
        /// <summary>How long each claim takes, so concurrent claims overlap.</summary>
        public TimeSpan ClaimDelay { get; set; }
        private int _inFlight;
        private int _mostInFlight;
        public int MostClaimsAtOnce => Volatile.Read(ref _mostInFlight);
        public ConcurrentQueue<Guid> PutBacks { get; } = new();

        public async Task<bool> TryClaimDueTicker(InternalFunctionContext function, DateTime claimMarker, CancellationToken cancellationToken = default)
        {
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _mostInFlight)) && Interlocked.CompareExchange(ref _mostInFlight, now, seen) != seen) { }
            try
            {
                var claim = Interlocked.Increment(ref _claims);
                if (claim == ThrowOnClaim || function.TickerId == ThrowBeforeClaimOf)
                {
                    if (OnClaim != null)
                        await OnClaim(cancellationToken);
                    throw new InvalidOperationException("the store is unreachable");
                }
                if (claim == BeforeClaimNumber && BeforeClaim != null)
                    await BeforeClaim();
                if (claim == BeforeClaimNumber && BeforeClaimWithToken != null)
                    await BeforeClaimWithToken(cancellationToken);
                if (ClaimDelay > TimeSpan.Zero)
                    await Task.Delay(ClaimDelay, CancellationToken.None);

                var won = await inner.TryClaimDueTicker(function, claimMarker, cancellationToken);
                ClaimResults.Enqueue(won);
                if (function.TickerId == ThrowAfterClaimOf)
                    throw new TimeoutException("the reply of the claim was lost");
                return won;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        /// <summary>Every put-back waits until its token is cancelled, as with a store that does not answer.</summary>
        public bool HangPutBacks { get; set; }
        private int _putBackAttempts;
        public int PutBackAttempts => Volatile.Read(ref _putBackAttempts);

        public async Task<bool> TryReleaseClaim(InternalFunctionContext function, DateTime claimMarker, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _putBackAttempts);
            if (HangPutBacks)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            if (FailPutBacks > 0)
            {
                FailPutBacks--;
                throw new InvalidOperationException("the store is unreachable");
            }

            var putBack = await inner.TryReleaseClaim(function, claimMarker, cancellationToken);
            if (putBack)
                PutBacks.Enqueue(function.TickerId);
            if (LandThenFailPutBacks > 0)
            {
                LandThenFailPutBacks--;
                throw new TimeoutException("the reply of the put-back was lost");
            }
            return putBack;
        }

        public async Task<int> GetMaxConcurrentClaims(CancellationToken cancellationToken = default)
            => FanOut ?? await inner.GetMaxConcurrentClaims(cancellationToken);

        public DateTime NextClaimMarker() => inner.NextClaimMarker();

        public Task NotifyStatusChanged(InternalFunctionContext function) => inner.NotifyStatusChanged(function);

        public Task NotifyInProgress(InternalFunctionContext function) => inner.NotifyInProgress(function);

        public async Task ReleaseAcquiredResources(InternalFunctionContext[] context, CancellationToken cancellationToken = default)
        {
            await inner.ReleaseAcquiredResources(context, cancellationToken);
            Released.TrySetResult();
        }

        public Task<bool> UpdateTickerAsync(InternalFunctionContext context, CancellationToken cancellationToken = default) => inner.UpdateTickerAsync(context, cancellationToken);
        public Task RecordChildStartedAsync(InternalFunctionContext child, CancellationToken cancellationToken = default) => inner.RecordChildStartedAsync(child, cancellationToken);
        public Task<T> GetRequestAsync<T>(Guid tickerId, TickerType type, CancellationToken cancellationToken = default) => inner.GetRequestAsync<T>(tickerId, type, cancellationToken);
        public Task<T> GetRequestAsync<T>(Guid tickerId, TickerType type, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default) => inner.GetRequestAsync(tickerId, type, typeInfo, cancellationToken);
        public Task<InternalFunctionContext[]> RunTimedOutTickers(CancellationToken cancellationToken = default) => inner.RunTimedOutTickers(cancellationToken);
        public Task MigrateDefinedCronTickers((string, string)[] cronExpressions, CancellationToken cancellationToken = default) => inner.MigrateDefinedCronTickers(cronExpressions, cancellationToken);
        public Task DeleteTicker(Guid tickerId, TickerType type, CancellationToken cancellationToken = default) => inner.DeleteTicker(tickerId, type, cancellationToken);
        public Task ReleaseDeadNodeResources(string instanceIdentifier, CancellationToken cancellationToken = default) => inner.ReleaseDeadNodeResources(instanceIdentifier, cancellationToken);
        public Task ReleaseEarlierRunResources(string nodeIdentifier, DateTime runStartedAt, CancellationToken cancellationToken = default) => inner.ReleaseEarlierRunResources(nodeIdentifier, runStartedAt, cancellationToken);
        public Task UpdateSkipTimeTickersWithUnifiedContextAsync(InternalFunctionContext[] context, CancellationToken cancellationToken = default) => inner.UpdateSkipTimeTickersWithUnifiedContextAsync(context, cancellationToken);
        public Task<InternalFunctionContext[]> AcquireDelayedChildrenAsync(Guid[] ids, CancellationToken cancellationToken = default) => inner.AcquireDelayedChildrenAsync(ids, cancellationToken);
        public Task<int> SkipStaleCronOccurrencesAsync(TimeSpan staleThreshold, CancellationToken cancellationToken = default) => inner.SkipStaleCronOccurrencesAsync(staleThreshold, cancellationToken);
    }

    private async Task<Guid> SeedTimeTicker(string function, DateTime? executionTime = null)
    {
        var ticker = new TimeTickerEntity
        {
            Id = Guid.NewGuid(),
            Function = function,
            ExecutionTime = executionTime ?? T0,
            Status = TickerStatus.Idle,
            CreatedAt = T0.AddHours(-2),
            UpdatedAt = T0.AddHours(-1),
            Request = []
        };
        _timeTickerIds.Add(ticker.Id);
        await CreateProvider("seed").AddTimeTickers([ticker], CancellationToken.None);
        return ticker.Id;
    }

    private (SharpTickerSchedulerBackgroundService Service, TickerExecutionContext Context) CreateService(
        IInternalTickerManager manager, ISharpTickerTaskScheduler scheduler, ITickerExecutionTaskHandler handler,
        ILogger<SharpTickerSchedulerBackgroundService>? logger = null)
    {
        var context = new TickerExecutionContext();
        var service = new SharpTickerSchedulerBackgroundService(context, handler, scheduler, manager,
            new SchedulerOptionsBuilder { MinPollingInterval = TimeSpan.FromMilliseconds(10) }, new TickerFunctionConcurrencyGate(),
            logger, _clock);
        return (service, context);
    }

    /// <summary>Queues the ids as this node's rows and returns the batch the loop would hold, in the given order.</summary>
    private async Task<InternalFunctionContext[]> QueueBatch(TickerInMemoryPersistenceProvider<TimeTickerEntity, CronTickerEntity> node, params Guid[] ids)
    {
        var window = await node.GetEarliestTimeTickers(CancellationToken.None);
        var queued = new List<TimeTickerEntity>();
        await foreach (var row in node.QueueTimeTickers(window.Where(x => ids.Contains(x.Id)).ToArray(), CancellationToken.None))
            queued.Add(row);
        Assert.Equal(ids.Length, queued.Count);
        return ids.Select(id => new InternalFunctionContext
        {
            TickerId = id,
            FunctionName = queued.Single(x => x.Id == id).Function,
            Type = TickerType.TimeTicker,
            TimeTickerChildren = []
        }).ToArray();
    }

    private async Task<TickerStatus> StatusOf(TickerInMemoryPersistenceProvider<TimeTickerEntity, CronTickerEntity> node, Guid id)
        => (await node.GetTimeTickerById(id, CancellationToken.None)).Status;

    // The rows of a batch are claimed independently: one claim that fails does not keep the others from running. The row whose
    // claim failed before reaching the store is still Queued and goes back to Idle with the loop's error handling.
    [Fact]
    public async Task Handover_ClaimOfOneRowThrows_TheOthersRun_ThatRowIsReleased()
    {
        var node = CreateProvider("node-A");
        var ids = new[] { await SeedTimeTicker("Handover.Batch"), await SeedTimeTicker("Handover.Batch"), await SeedTimeTicker("Handover.Batch"), await SeedTimeTicker("Handover.Batch") };
        var batch = await QueueBatch(node, ids);
        var manager = new HookedManager(CreateRealManager(node)) { ThrowBeforeClaimOf = ids[2] };
        var scheduler = new RecordingTaskScheduler();
        var handler = Substitute.For<ITickerExecutionTaskHandler>();
        handler.ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var (service, context) = CreateService(manager, scheduler, handler);
        context.SetFunctions(batch);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await manager.Released.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
        await scheduler.WhenAllIdleAsync();

        foreach (var id in new[] { ids[0], ids[1], ids[3] })
        {
            Assert.Equal(TickerStatus.InProgress, await StatusOf(node, id));
            await handler.Received(1).ExecuteTaskAsync(Arg.Is<InternalFunctionContext>(f => f.TickerId == id), false, Arg.Any<CancellationToken>());
        }
        Assert.Equal(TickerStatus.Idle, await StatusOf(node, ids[2]));
        Assert.Equal(3, scheduler.QueuedCount);
        Assert.Equal(0, service.Claims.Count);
    }

    // The claim's write landed but its reply was lost: the row is InProgress for this node with no worker. Its marker was
    // recorded before the call, so it is put back and the fallback runs the job once.
    [Fact]
    public async Task Handover_ClaimReplyLost_RowIsPutBack()
    {
        var node = CreateProvider("node-A");
        var ids = new[] { await SeedTimeTicker("Handover.Batch"), await SeedTimeTicker("Handover.Batch") };
        var batch = await QueueBatch(node, ids);
        var manager = new HookedManager(CreateRealManager(node)) { ThrowAfterClaimOf = ids[1] };
        var scheduler = new RecordingTaskScheduler();
        var handler = Substitute.For<ITickerExecutionTaskHandler>();
        handler.ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var logger = new RecordingLogger();
        var (service, context) = CreateService(manager, scheduler, handler, logger);
        context.SetFunctions(batch);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await WaitUntilAsync(() => manager.PutBacks.Contains(ids[1])), "the claim whose reply was lost was not put back");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
        await scheduler.WhenAllIdleAsync();

        Assert.Equal(TickerStatus.InProgress, await StatusOf(node, ids[0]));
        Assert.Equal(TickerStatus.Idle, await StatusOf(node, ids[1]));
        await handler.DidNotReceive().ExecuteTaskAsync(Arg.Is<InternalFunctionContext>(f => f.TickerId == ids[1]), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(ids[1].ToString()) && e.Message.Contains("put back"));
    }

    // The application stops while a claimed job waits for its function's concurrency slot, on the real task scheduler. The job
    // never started, so its row is put back for the fallback instead of staying InProgress with nothing to run it.
    [Fact]
    public async Task Stop_WhileAClaimedJobWaitsForItsConcurrencySlot_PutsTheRowBack()
    {
        var node = CreateProvider("node-A");
        var id = await SeedTimeTicker("Handover.Gate");
        var batch = await QueueBatch(node, id);
        batch[0].CachedMaxConcurrency = 1;
        var manager = new HookedManager(CreateRealManager(node));
        var gate = new TickerFunctionConcurrencyGate();
        var slot = gate.GetSemaphoreOrNull("Handover.Gate", 1)!;
        await slot.WaitAsync(); // another run of the same function holds the only slot
        var handler = Substitute.For<ITickerExecutionTaskHandler>();
        handler.ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await using var scheduler = new SharpTickerTaskScheduler(4);
        var logger = new RecordingLogger();
        var context = new TickerExecutionContext();
        var service = new SharpTickerSchedulerBackgroundService(context, handler, scheduler, manager,
            new SchedulerOptionsBuilder { MinPollingInterval = TimeSpan.FromMilliseconds(10) }, gate, logger, _clock)
        { ShutdownWaitLimit = TimeSpan.FromMilliseconds(200) };
        context.SetFunctions(batch);

        await service.StartAsync(CancellationToken.None);
        Assert.True(await WaitUntilAsync(() => manager.ClaimResults.Contains(true)), "the job was never claimed");
        await Task.Delay(100);
        await service.StopAsync(CancellationToken.None);
        slot.Release();
        await Task.Delay(100);

        Assert.Equal(TickerStatus.Idle, await StatusOf(node, id));
        Assert.Contains(id, manager.PutBacks);
        await handler.DidNotReceiveWithAnyArgs().ExecuteTaskAsync(default!, default, default);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(id.ToString()) && e.Message.Contains("put back"));
        Assert.Equal(0, service.Claims.Count);
    }

    // A loop job waits for its slot while another stopping service sweeps the claims of this node. The put-back lands but its
    // reply is lost, so the row is Idle and the fallback may run the job. When the slot frees up, the waiting worker must not
    // run it as well.
    [Fact]
    public async Task APutBackWhoseReplyWasLost_TheWaitingJobNeverStarts()
    {
        var node = CreateProvider("node-A");
        var id = await SeedTimeTicker("Handover.LostReply");
        var batch = await QueueBatch(node, id);
        batch[0].CachedMaxConcurrency = 1;
        var manager = new HookedManager(CreateRealManager(node)) { LandThenFailPutBacks = 1 };
        var gate = new TickerFunctionConcurrencyGate();
        var slot = gate.GetSemaphoreOrNull("Handover.LostReply", 1)!;
        await slot.WaitAsync(); // another run of the same function holds the only slot
        var handler = Substitute.For<ITickerExecutionTaskHandler>();
        handler.ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await using var scheduler = new SharpTickerTaskScheduler(4);
        var work = new ClaimedWorkQueue(manager, scheduler, handler, gate);
        var context = new TickerExecutionContext();
        var service = new SharpTickerSchedulerBackgroundService(context, handler, scheduler, manager,
            new SchedulerOptionsBuilder { MinPollingInterval = TimeSpan.FromMilliseconds(10) }, gate, null, _clock, null, work)
        { ShutdownWaitLimit = TimeSpan.FromMilliseconds(200) };
        context.SetFunctions(batch);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await WaitUntilAsync(() => manager.ClaimResults.Contains(true)), "the job was never claimed");
            await Task.Delay(100);

            await work.PutBackUnstartedAsync();
            Assert.Equal(TickerStatus.Idle, await StatusOf(node, id));
            slot.Release();
            await Task.Delay(300);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        await handler.DidNotReceiveWithAnyArgs().ExecuteTaskAsync(default!, default, default);
        Assert.Equal(TickerStatus.Idle, await StatusOf(node, id));
    }

    // A claimed job waits for its function's only slot, and the application stops. The job that holds the slot ends on that
    // stop and frees the slot before the stop reaches the waiting job, so its wait succeeds although the stop has begun. It
    // must not start: it would run with a cancelled token and end Cancelled. Its claim stays pending and the stop puts it back.
    [Fact]
    public async Task ASlotFreedByTheStop_DoesNotStartTheWaitingJob_TheStopPutsItBack()
    {
        var manager = new HookedManager(CreateRealManager(CreateProvider("node-A")));
        var gate = new TickerFunctionConcurrencyGate();
        var function = Function("Handover.SlotFreedByTheStop");
        function.CachedMaxConcurrency = 1;
        var slot = gate.GetSemaphoreOrNull(function.FunctionName, 1)!;
        await slot.WaitAsync(); // another run of the same function holds the only slot
        var handler = Substitute.For<ITickerExecutionTaskHandler>();
        handler.ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var scheduler = new InlineTaskScheduler();
        var work = new ClaimedWorkQueue(manager, scheduler, handler, gate);
        using var stopping = new CancellationTokenSource();
        var entry = work.Record(function, manager.NextClaimMarker());

        await work.QueueWork(entry, isDue: false, stopping.Token);
        // Registered after the waiting job's wait, so it runs first: the holder ends on the stop and frees the slot. This relies
        // on cancellation callbacks running in reverse order of registration; if that changed, the wait would be cancelled, the
        // job's task would end Canceled and WhenAllIdleAsync would throw, so the test fails instead of passing for no reason.
        stopping.Token.Register(() => slot.Release());
        stopping.Cancel();
        await scheduler.WhenAllIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await handler.DidNotReceiveWithAnyArgs().ExecuteTaskAsync(default!, default, default);
        Assert.True(entry.IsPending);
        Assert.Equal(1, slot.CurrentCount);
        await work.PutBackUnstartedAsync();
        Assert.Equal(1, manager.PutBackAttempts);
    }

    // The fallback stops first (the host stops services in reverse order) and sweeps the shared ledger while the loop's claim
    // call is still in flight. The claim has no outcome yet, so the sweep must leave it to the loop: once it is won, the job
    // runs instead of the row staying InProgress with neither a worker nor a put-back.
    [Fact]
    public async Task ASweepWhileAClaimIsInFlight_LeavesItToTheLoop_TheJobRuns()
    {
        var node = CreateProvider("node-A");
        var id = await SeedTimeTicker("Handover.Sweep");
        var batch = await QueueBatch(node, id);
        var manager = new HookedManager(CreateRealManager(node));
        var scheduler = new RecordingTaskScheduler();
        var handler = Substitute.For<ITickerExecutionTaskHandler>();
        handler.ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var gate = new TickerFunctionConcurrencyGate();
        var work = new ClaimedWorkQueue(manager, scheduler, handler, gate);
        manager.BeforeClaimNumber = 1;
        manager.BeforeClaim = () => work.PutBackUnstartedAsync();
        var context = new TickerExecutionContext();
        var service = new SharpTickerSchedulerBackgroundService(context, handler, scheduler, manager,
            new SchedulerOptionsBuilder { MinPollingInterval = TimeSpan.FromMilliseconds(10) }, gate, null, _clock, null, work);
        context.SetFunctions(batch);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await WaitUntilAsync(() => manager.ClaimResults.Contains(true)), "the job was never claimed");
            Assert.True(await WaitUntilAsync(() => handler.ReceivedCalls().Any()), "the won claim was never started");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
        await scheduler.WhenAllIdleAsync();

        await handler.Received(1).ExecuteTaskAsync(Arg.Is<InternalFunctionContext>(f => f.TickerId == id), false, Arg.Any<CancellationToken>());
        Assert.Equal(0, work.Claims.Count);
    }

    // The store cannot be reached when the claim is put back: the row stays InProgress, the failure is an error, and the loop
    // tries again on its next pass until it works.
    [Fact]
    public async Task Handover_PutBackFails_IsTriedAgainUntilItWorks()
    {
        var node = CreateProvider("node-A");
        var id = await SeedTimeTicker("Handover.Batch");
        var batch = await QueueBatch(node, id);
        var manager = new HookedManager(CreateRealManager(node)) { ThrowAfterClaimOf = id, FailPutBacks = 2 };
        var scheduler = new RecordingTaskScheduler();
        var handler = Substitute.For<ITickerExecutionTaskHandler>();
        var logger = new RecordingLogger();
        var (service, context) = CreateService(manager, scheduler, handler, logger);
        // The loop has nothing else to do and would sleep a day; a pending put-back keeps its sleep short.
        service.PutBackRetryInterval = TimeSpan.FromMilliseconds(50);
        context.SetFunctions(batch);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await WaitUntilAsync(() => manager.PutBacks.Contains(id)), "the put-back was not tried again");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal(TickerStatus.Idle, await StatusOf(node, id));
        // One error for the claim, not one per attempt; the later attempts are at Debug and the put-back that works is a warning.
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("could not be put back"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("could not be put back"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("put back"));
        Assert.Equal(0, service.Claims.Count);
        await handler.DidNotReceiveWithAnyArgs().ExecuteTaskAsync(default!, default, default);
    }

    // A claim call (the fallback, run-now) failed after it had claimed a row, and the store's own put-back of it failed too:
    // the row is InProgress for this node with no worker, and it reached the retry list. The loop puts it back once the store
    // answers, with the claim's marker, and the fallback then runs it. Without that the row stays InProgress for as long as
    // the node lives (candidate row 50, scenario AFallbackPassThatFailsPartway_RunsEachOverdueRowOnce).
    [Fact]
    public async Task ARowWhosePutBackFailedInsideAClaimCall_IsPutBackByTheLoop_OnceTheStoreAnswers()
    {
        var (node, id, retry, manager, service, handler, _) = await RowWhosePutBackFailed(markerMatches: true);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await WaitUntilAsync(() => manager.PutBacks.Contains(id)), "the row was never put back");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal(TickerStatus.Idle, await StatusOf(node, id));
        Assert.Null((await node.GetTimeTickerById(id, CancellationToken.None)).LockHolder);
        Assert.True(manager.PutBackAttempts >= 2, "the first put-back was to fail like the store's own");
        Assert.True(retry.IsEmpty);
        Assert.Equal(0, service.Claims.Count);
        await handler.DidNotReceiveWithAnyArgs().ExecuteTaskAsync(default!, default, default);
    }

    // The usual case: the loop has nothing to do and sleeps (up to a day) when the fallback's claim call fails and its row
    // reaches the retry list. Adding it wakes the loop, so the row is put back at once and not at the next planned wake-up.
    [Fact]
    public async Task ARowAddedToTheRetryListWhileTheLoopSleeps_WakesItAndIsPutBack()
    {
        var (node, id, retry, manager, service, _, add) = await RowWhosePutBackFailed(markerMatches: true, addNow: false);
        service.PutBackRetryInterval = TimeSpan.FromMinutes(10); // only a wake-up can put the row back within the wait below
        manager.FailPutBacks = 0; // the store answers again by the time the row is added

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await WaitUntilAsync(() => manager.Polls >= 1), "the loop never polled");
            await Task.Delay(200); // the loop is asleep now: nothing is due
            add();
            Assert.True(await WaitUntilAsync(() => manager.PutBacks.Contains(id), seconds: 5), "adding the row did not wake the loop");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal(TickerStatus.Idle, await StatusOf(node, id));
        Assert.True(retry.IsEmpty);
    }

    // At the stop: the fallback's sweep (with its owner) leaves such a row alone, since the scheduler may still try it; the
    // scheduler's sweep, the last one, takes it from the retry list and puts it back.
    [Fact]
    public async Task AtTheStop_OnlyTheSchedulersSweep_PutsBackARowFromTheRetryList()
    {
        var (node, id, retry, manager, _, _, _) = await RowWhosePutBackFailed(markerMatches: true);
        manager.FailPutBacks = 0;
        var work = new ClaimedWorkQueue(manager, new RecordingTaskScheduler(), Substitute.For<ITickerExecutionTaskHandler>(),
            new TickerFunctionConcurrencyGate(), null, retry);

        await work.PutBackUnstartedAsync(owner: new object());
        Assert.False(retry.IsEmpty);
        Assert.Equal(TickerStatus.InProgress, await StatusOf(node, id));

        await work.PutBackUnstartedAsync();
        Assert.True(retry.IsEmpty);
        Assert.Equal(TickerStatus.Idle, await StatusOf(node, id));
        Assert.Equal(0, work.Claims.Count);
    }

    // The row moved on before the retry (this node's fallback took it again, under a new marker): the fenced put-back leaves
    // it alone, so a row that is running is never put back.
    [Fact]
    public async Task ARowWhosePutBackFailed_ThatMovedOnSince_IsLeftAlone()
    {
        var (node, id, _, manager, service, _, _) = await RowWhosePutBackFailed(markerMatches: false);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await WaitUntilAsync(() => manager.PutBackAttempts >= 2), "the put-back was not tried again");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal(TickerStatus.InProgress, await StatusOf(node, id));
        Assert.Empty(manager.PutBacks);
        Assert.Equal(0, service.Claims.Count);
    }

    // The fallback of node-A claims a row; the store's put-back of it failed, so it is in the retry list with that claim's
    // marker (or another one: the row moved on since). The first put-back by the loop fails as well, the next ones reach the store.
    // With addNow false the row is added only when the test calls Add.
    private async Task<(TickerInMemoryPersistenceProvider<TimeTickerEntity, CronTickerEntity> Node, Guid Id, ClaimPutBackRetryList Retry,
        HookedManager Manager, SharpTickerSchedulerBackgroundService Service, ITickerExecutionTaskHandler Handler, Action Add)> RowWhosePutBackFailed(
        bool markerMatches, bool addNow = true)
    {
        var node = CreateProvider("node-A");
        var id = await SeedTimeTicker("PutBack.InsideAClaimCall");
        _clock.Advance(TimeSpan.FromSeconds(2)); // past the fallback's threshold
        var claimed = new List<TimeTickerEntity>();
        await foreach (var row in node.QueueTimedOutTimeTickers(CancellationToken.None))
            claimed.Add(row);
        var marker = Assert.Single(claimed, x => x.Id == id).UpdatedAt;
        Assert.Equal(TickerStatus.InProgress, await StatusOf(node, id));

        var retry = new ClaimPutBackRetryList();
        Action add = () => retry.AddFrom(new ClaimPutBackFailedException(1, [new InvalidOperationException("store failed"), new TimeoutException("release failed")],
            [new NotPutBackClaim(id, markerMatches ? marker : marker.AddTicks(-1), TickerType.TimeTicker)]));
        if (addNow)
            add();

        var manager = new HookedManager(CreateRealManager(node)) { FailPutBacks = 1 };
        var scheduler = new RecordingTaskScheduler();
        var handler = Substitute.For<ITickerExecutionTaskHandler>();
        var gate = new TickerFunctionConcurrencyGate();
        var work = new ClaimedWorkQueue(manager, scheduler, handler, gate, null, retry);
        var service = new SharpTickerSchedulerBackgroundService(new TickerExecutionContext(), handler, scheduler, manager,
            new SchedulerOptionsBuilder { MinPollingInterval = TimeSpan.FromMilliseconds(10) }, gate, null, _clock, null, work)
        {
            // Nothing else to do, so the loop would sleep a day; a row to put back keeps its sleep short.
            PutBackRetryInterval = TimeSpan.FromMilliseconds(50)
        };
        return (node, id, retry, manager, service, handler, add);
    }

    // The batch is claimed as many rows at once as the store takes, and the won rows are still handed over in priority order.
    [Fact]
    public async Task Handover_ClaimsConcurrently_UpToTheStoresFanOut_AndKeepsPriorityOrder()
    {
        var node = CreateProvider("node-A");
        var ids = new List<Guid>();
        for (var i = 0; i < 12; i++)
            ids.Add(await SeedTimeTicker("Handover.Batch"));
        var batch = await QueueBatch(node, ids.ToArray());
        for (var i = 0; i < batch.Length; i++)
            batch[i].CachedPriority = (TickerTaskPriority)(i % 3);
        var manager = new HookedManager(CreateRealManager(node)) { FanOut = 4, ClaimDelay = TimeSpan.FromMilliseconds(50) };
        var scheduler = new RecordingTaskScheduler();
        var handler = Substitute.For<ITickerExecutionTaskHandler>();
        handler.ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var (service, context) = CreateService(manager, scheduler, handler);
        context.SetFunctions(batch);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await WaitUntilAsync(() => scheduler.QueuedCount == 12), "not every row was handed over");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.InRange(manager.MostClaimsAtOnce, 2, 4);
        Assert.Equal(batch.Select(x => x.CachedPriority).OrderBy(x => x), scheduler.Priorities);
    }

    // A store that takes one claim at a time (SQLite, a custom provider) is never sent two at once.
    [Fact]
    public async Task Handover_FanOutOfOne_ClaimsOneRowAtATime()
    {
        var node = CreateProvider("node-A");
        var ids = new[] { await SeedTimeTicker("Handover.Batch"), await SeedTimeTicker("Handover.Batch"), await SeedTimeTicker("Handover.Batch") };
        var batch = await QueueBatch(node, ids);
        var manager = new HookedManager(CreateRealManager(node)) { FanOut = 1, ClaimDelay = TimeSpan.FromMilliseconds(30) };
        var scheduler = new RecordingTaskScheduler();
        var handler = Substitute.For<ITickerExecutionTaskHandler>();
        handler.ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var (service, context) = CreateService(manager, scheduler, handler);
        context.SetFunctions(batch);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await WaitUntilAsync(() => scheduler.QueuedCount == 3), "not every row was handed over");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal(1, manager.MostClaimsAtOnce);
    }

    [Fact]
    public async Task Handover_RestartMidBatch_RunningRowsUntouched()
    {
        var node = CreateProvider("node-A");
        var ids = new[] { await SeedTimeTicker("Handover.Batch"), await SeedTimeTicker("Handover.Batch"), await SeedTimeTicker("Handover.Batch") };
        var batch = await QueueBatch(node, ids);
        var scheduler = new RecordingTaskScheduler();
        var handler = Substitute.For<ITickerExecutionTaskHandler>();
        handler.ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var manager = new HookedManager(CreateRealManager(node));
        var (service, context) = CreateService(manager, scheduler, handler);
        // A restart is requested while the third claim is under way; the debounce has fired when the claim goes on.
        manager.BeforeClaimNumber = 3;
        manager.BeforeClaim = async () =>
        {
            service.Restart();
            await Task.Delay(300);
        };
        context.SetFunctions(batch);

        await service.StartAsync(CancellationToken.None);
        try
        {
            // The batch is finished first; the restart takes effect at the loop's next wait, and releases what Functions holds.
            await manager.Released.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
        await scheduler.WhenAllIdleAsync();

        foreach (var id in ids)
            Assert.Equal(TickerStatus.InProgress, await StatusOf(node, id));
        Assert.Equal(3, scheduler.QueuedCount);
        foreach (var id in ids)
            await handler.Received(1).ExecuteTaskAsync(Arg.Is<InternalFunctionContext>(f => f.TickerId == id), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handover_RestartMidBatch_DoesNotInterruptClaim()
    {
        var node = CreateProvider("node-A");
        var ids = new[] { await SeedTimeTicker("Handover.Batch"), await SeedTimeTicker("Handover.Batch"), await SeedTimeTicker("Handover.Batch") };
        var batch = await QueueBatch(node, ids);
        var scheduler = new RecordingTaskScheduler();
        var handler = Substitute.For<ITickerExecutionTaskHandler>();
        handler.ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var manager = new HookedManager(CreateRealManager(node));
        var (service, context) = CreateService(manager, scheduler, handler);
        // The claim waits on its own token while the restart fires. A claim bound to the loop's token would be cancelled here.
        manager.BeforeClaimNumber = 3;
        manager.BeforeClaimWithToken = async ct =>
        {
            var loopSource = (SafeCancellationTokenSource)typeof(SharpTickerSchedulerBackgroundService)
                .GetField("_schedulerLoopCancellationTokenSource", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            service.Restart();
            // Wait until the restart really cancelled the loop's token, not for a fixed time: a starved debounce timer
            // would otherwise let a claim bound to the loop token pass.
            Assert.True(await WaitUntilAsync(() => loopSource.IsCancellationRequested), "the restart never cancelled the loop token");
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
        };
        context.SetFunctions(batch);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await manager.Released.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
        await scheduler.WhenAllIdleAsync();

        Assert.Equal([true, true, true], manager.ClaimResults.ToArray());
        foreach (var id in ids)
            Assert.Equal(TickerStatus.InProgress, await StatusOf(node, id));
        Assert.Equal(3, scheduler.QueuedCount);
    }

    // The whole batch is claimed before the hand-over; the stop arrives after the first job was accepted, so the rows not
    // handed over are claimed with no worker and are put back.
    [Fact]
    public async Task Handover_StopMidBatch_RowsNotHandedOverArePutBack()
    {
        var node = CreateProvider("node-A");
        var ids = new[] { await SeedTimeTicker("Handover.Batch"), await SeedTimeTicker("Handover.Batch"), await SeedTimeTicker("Handover.Batch"), await SeedTimeTicker("Handover.Batch") };
        var batch = await QueueBatch(node, ids);
        var manager = new HookedManager(CreateRealManager(node));
        var scheduler = new RecordingTaskScheduler();
        var handler = Substitute.For<ITickerExecutionTaskHandler>();
        handler.ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var logger = new RecordingLogger();
        var (service, context) = CreateService(manager, scheduler, handler, logger);
        Task? stop = null;
        // The stop begins once the first job is accepted: StopAsync freezes the scheduler and cancels the loop's token before
        // it first waits, and the loop is then in the middle of the batch.
        scheduler.OnQueued = n => { if (n == 1) stop = service.StopAsync(CancellationToken.None); };
        context.SetFunctions(batch);

        await service.StartAsync(CancellationToken.None);
        Assert.True(await WaitUntilAsync(() => manager.PutBacks.Count == 3), "the rows not handed over were not put back");
        await stop!.WaitAsync(TimeSpan.FromSeconds(10));
        await scheduler.WhenAllIdleAsync();

        Assert.Equal(TickerStatus.InProgress, await StatusOf(node, ids[0]));
        foreach (var id in ids.Skip(1))
            Assert.Equal(TickerStatus.Idle, await StatusOf(node, id));
        Assert.Equal(ids.Skip(1).OrderBy(x => x), manager.PutBacks.OrderBy(x => x));
        Assert.Equal(1, scheduler.QueuedCount);
        Assert.Equal(0, service.Claims.Count);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Handover_SchedulerFrozenMidBatch_RowsNotHandedOverArePutBack()
    {
        var node = CreateProvider("node-A");
        var ids = new[] { await SeedTimeTicker("Handover.Batch"), await SeedTimeTicker("Handover.Batch"), await SeedTimeTicker("Handover.Batch") };
        var batch = await QueueBatch(node, ids);
        var manager = new HookedManager(CreateRealManager(node));
        var scheduler = new RecordingTaskScheduler();
        var handler = Substitute.For<ITickerExecutionTaskHandler>();
        handler.ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var logger = new RecordingLogger();
        var (service, context) = CreateService(manager, scheduler, handler, logger);
        // A manual freeze, with no stop: the loop goes on, but the rows it did not hand over must not be left claimed.
        scheduler.OnQueued = n => { if (n == 1) scheduler.Freeze(); };
        context.SetFunctions(batch);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await WaitUntilAsync(() => manager.PutBacks.Count == 2), "the rows not handed over were not put back");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
        await scheduler.WhenAllIdleAsync();

        Assert.Equal(TickerStatus.InProgress, await StatusOf(node, ids[0]));
        Assert.Equal(TickerStatus.Idle, await StatusOf(node, ids[1]));
        Assert.Equal(TickerStatus.Idle, await StatusOf(node, ids[2]));
        Assert.Equal(0, service.Claims.Count);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Error);
    }

    // The scheduler refused a job it had claimed and the put-back cannot reach the store either: the row stays InProgress,
    // which is an error naming the job.
    [Fact]
    public async Task Handover_QueueFaults_AndPutBackFails_IsLoggedAsAnError()
    {
        var manager = Substitute.For<IInternalTickerManager>();
        manager.TryClaimDueTicker(Arg.Any<InternalFunctionContext>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);
        manager.TryReleaseClaim(Arg.Any<InternalFunctionContext>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("the store is unreachable"));
        var scheduler = Substitute.For<ISharpTickerTaskScheduler>();
        scheduler.QueueAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<TickerTaskPriority>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(Task.FromException(new InvalidOperationException("Scheduler is frozen"))));
        var logger = new RecordingLogger();
        var job = Function("Handover.Faulted");

        await RunOneBatchAsync(new TickerExecutionContext(), manager, scheduler, Substitute.For<ITickerExecutionTaskHandler>(), [job], logger);

        Assert.True(await WaitUntilAsync(() => logger.Entries.Any(e => e.Level == LogLevel.Error)), "the refusal was not logged");
        var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(job.TickerId.ToString(), entry.Message);
        Assert.Contains("Handover.Faulted", entry.Message);
        Assert.Contains("InProgress", entry.Message);
    }

    private async Task<(HookedManager Manager, RecordingTaskScheduler Scheduler, SharpTickerSchedulerBackgroundService Loop, Guid Id)> StartLateLoop(
        string function, ConcurrentDictionary<Guid, int> runs, Guid? cronOccurrenceId = null)
    {
        var id = cronOccurrenceId ?? await SeedTimeTicker(function);
        var nodeA = CreateProvider("node-A");
        var manager = new HookedManager(CreateRealManager(nodeA)) { UseHold = true };
        if (cronOccurrenceId != null)
            manager.CannedFirstPoll = (TimeSpan.Zero, [new InternalFunctionContext
            {
                TickerId = id, FunctionName = function, Type = TickerType.CronTickerOccurrence, TimeTickerChildren = []
            }]);
        var scheduler = new RecordingTaskScheduler();
        var handler = Substitute.For<ITickerExecutionTaskHandler>();
        handler.ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            runs.AddOrUpdate(call.Arg<InternalFunctionContext>().TickerId, 1, (_, n) => n + 1);
            return Task.CompletedTask;
        });
        var (loop, _) = CreateService(manager, scheduler, handler);
        await loop.StartAsync(CancellationToken.None);
        try
        {
            // The loop has polled and queued the row as node A, and is held before it returns.
            await manager.FirstPollQueued.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            manager.HoldFirstPoll.TrySetResult();
            await loop.StopAsync(CancellationToken.None);
            throw;
        }
        return (manager, scheduler, loop, id);
    }

    private SharpTickerFallbackBackgroundService CreateFallback(ConcurrentDictionary<Guid, int> runs, out RecordingTaskScheduler fallbackScheduler)
    {
        var nodeB = CreateProvider("node-B");
        fallbackScheduler = new RecordingTaskScheduler();
        var fallbackHandler = Substitute.For<ITickerExecutionTaskHandler>();
        fallbackHandler.ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            runs.AddOrUpdate(call.Arg<InternalFunctionContext>().TickerId, 1, (_, n) => n + 1);
            return Task.CompletedTask;
        });
        return new SharpTickerFallbackBackgroundService(CreateRealManager(nodeB),
            new SchedulerOptionsBuilder { FallbackIntervalChecker = TimeSpan.FromMilliseconds(20) },
            fallbackHandler, fallbackScheduler, new TickerFunctionConcurrencyGate());
    }

    private async Task RunLateLoopAgainstFallback(ConcurrentDictionary<Guid, int> runs, HookedManager manager,
        RecordingTaskScheduler scheduler, SharpTickerSchedulerBackgroundService loop, Guid id, Func<Task<(TickerStatus, string?)>> rowState)
    {
        // The row is Queued and belongs to node A before the fallback may take it; otherwise the test proves nothing.
        var (status, holder) = await rowState();
        Assert.Equal(TickerStatus.Queued, status);
        Assert.Equal("node-A", holder);

        using var fallback = CreateFallback(runs, out var fallbackScheduler);
        try
        {
            await fallback.StartAsync(CancellationToken.None);
            // The row has been Queued for 2 s on the fake clock, so the fallback takes it while the loop is still held.
            _clock.Advance(TimeSpan.FromSeconds(2));
            Assert.True(await WaitUntilAsync(() => runs.TryGetValue(id, out var n) && n >= 1), "the fallback never ran the job");

            manager.HoldFirstPoll.TrySetResult();
            // The loop returns from the held poll, hands its batch over, and polls again: the second poll ends the batch.
            Assert.True(await WaitUntilAsync(() => manager.Polls >= 2), "the loop never finished its batch");
            await scheduler.WhenAllIdleAsync();
            await fallbackScheduler.WhenAllIdleAsync();
        }
        finally
        {
            manager.HoldFirstPoll.TrySetResult();
            await fallback.StopAsync(CancellationToken.None);
            await loop.StopAsync(CancellationToken.None);
        }

        Assert.Equal(1, runs[id]);
        // The loop did try, and lost.
        Assert.Equal([false], manager.ClaimResults);
        // A lost claim leaves the ledger: nothing to start, nothing to put back, and nothing for every later pass to scan.
        Assert.Equal(0, loop.Claims.Count);
    }

    [Fact]
    public async Task LateLoopAndFallback_RealServices_HandlerRunsOnce()
    {
        var runs = new ConcurrentDictionary<Guid, int>();
        var (manager, scheduler, loop, id) = await StartLateLoop("LateLoop.Fn", runs);
        var nodeA = CreateProvider("node-A");

        await RunLateLoopAgainstFallback(runs, manager, scheduler, loop, id, async () =>
        {
            var row = await nodeA.GetTimeTickerById(id, CancellationToken.None);
            return (row.Status, row.LockHolder);
        });
    }

    [Fact]
    public async Task LateLoopAndFallback_CronOccurrence_HandlerRunsOnce()
    {
        var nodeA = CreateProvider("node-A");
        var cronTicker = new CronTickerEntity
        {
            Id = Guid.NewGuid(), Function = "LateLoop.Cron", Expression = "* * * * *", Request = [],
            CreatedAt = T0.AddHours(-2), UpdatedAt = T0.AddHours(-1)
        };
        Assert.Equal(1, await nodeA.InsertCronTickers([cronTicker], CancellationToken.None));
        var slot = (T0, new[] { new InternalManagerContext(cronTicker.Id) { FunctionName = cronTicker.Function, Expression = cronTicker.Expression } });
        var occurrenceId = Assert.Single(await Drain(nodeA.QueueCronTickerOccurrences(slot, CancellationToken.None))).Id;
        _occurrenceIds.Add(occurrenceId);
        _cronTickerIds.Add(cronTicker.Id);

        var runs = new ConcurrentDictionary<Guid, int>();
        var (manager, scheduler, loop, id) = await StartLateLoop("LateLoop.Cron", runs, occurrenceId);

        await RunLateLoopAgainstFallback(runs, manager, scheduler, loop, id, async () =>
        {
            var row = (await nodeA.GetAllCronTickerOccurrences(x => x.Id == id, CancellationToken.None)).Single();
            return (row.Status, row.LockHolder);
        });
    }

    private static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The fallback's claims
    // ---------------------------------------------------------------------------------------------------------------

    private static readonly TickerFunctionDelegate NoOp = (_, _, _) => Task.CompletedTask;

    private static void RegisterFunction(string name, int maxConcurrency)
    {
        TickerFunctionProvider.RegisterFunctions(new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>
        {
            [name] = ("", TickerTaskPriority.Normal, NoOp, maxConcurrency)
        });
        TickerFunctionProvider.Build();
    }

    private static ITickerExecutionTaskHandler CountingHandler(ConcurrentDictionary<Guid, int> runs)
    {
        var handler = Substitute.For<ITickerExecutionTaskHandler>();
        handler.ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            runs.AddOrUpdate(call.Arg<InternalFunctionContext>().TickerId, 1, (_, n) => n + 1);
            return Task.CompletedTask;
        });
        return handler;
    }

    // The fallback took a row whose function's only slot is held by another run, and the application stops while the job waits
    // for it. The job never started, so its row is put back instead of staying InProgress with nothing to run it.
    [Fact]
    public async Task Fallback_StopWhileAClaimedJobWaitsForItsConcurrencySlot_PutsTheRowBack()
    {
        RegisterFunction("Fallback.Gate", 1);
        var id = await SeedTimeTicker("Fallback.Gate");
        var node = CreateProvider("node-B");
        var gate = new TickerFunctionConcurrencyGate();
        var slot = gate.GetSemaphoreOrNull("Fallback.Gate", 1)!;
        await slot.WaitAsync(); // another run of the same function holds the only slot
        var runs = new ConcurrentDictionary<Guid, int>();
        await using var scheduler = new SharpTickerTaskScheduler(4);
        var fallback = new SharpTickerFallbackBackgroundService(CreateRealManager(node),
            new SchedulerOptionsBuilder { FallbackIntervalChecker = TimeSpan.FromMilliseconds(20) }, CountingHandler(runs), scheduler, gate);
        _clock.Advance(TimeSpan.FromSeconds(2)); // the row is past the fallback's threshold

        await fallback.StartAsync(CancellationToken.None);
        Assert.True(await WaitUntilAsync(() => StatusOf(node, id).Result == TickerStatus.InProgress), "the fallback never took the row");
        await Task.Delay(100);
        scheduler.Freeze();
        await fallback.StopAsync(CancellationToken.None);
        slot.Release();
        await Task.Delay(100);

        Assert.Equal(TickerStatus.Idle, await StatusOf(node, id));
        Assert.Null((await node.GetTimeTickerById(id, CancellationToken.None)).LockHolder);
        Assert.False(runs.ContainsKey(id));
    }

    // The host stops the fallback before the scheduler. A claim of the loop or of run-now that waits for its worker is not the
    // fallback's: the scheduler, still running, may yet start it, and its own stop puts it back if not. The fallback's stop
    // must leave it alone.
    [Fact]
    public async Task Fallback_Stop_LeavesClaimsItDidNotMake()
    {
        var node = CreateProvider("node-B");
        var manager = new HookedManager(CreateRealManager(node));
        var scheduler = new RecordingTaskScheduler();
        var handler = Substitute.For<ITickerExecutionTaskHandler>();
        var gate = new TickerFunctionConcurrencyGate();
        var work = new ClaimedWorkQueue(manager, scheduler, handler, gate);
        var notTheFallbacks = work.Record(Function("Fallback.NotMine"), manager.NextClaimMarker());
        var fallback = new SharpTickerFallbackBackgroundService(manager,
            new SchedulerOptionsBuilder { FallbackIntervalChecker = TimeSpan.FromMilliseconds(20) }, handler, scheduler, gate, null, work);

        await fallback.StartAsync(CancellationToken.None);
        await fallback.StopAsync(CancellationToken.None);

        Assert.True(notTheFallbacks.IsPending);
        Assert.Equal(1, work.Claims.Count);
        Assert.Empty(manager.PutBacks);
        await work.PutBackUnstartedAsync();
        Assert.Equal(0, work.Claims.Count);
    }

    // A put-back failed during the run (an Error, then the loop retries at Debug). At the stop it fails once more: nothing
    // tries again after that and the row stays InProgress until its store releases it, so that last failure is an Error too.
    [Fact]
    public async Task APutBackThatFailsAtTheStop_IsLoggedAsAnError()
    {
        var node = CreateProvider("node-A");
        var manager = new HookedManager(CreateRealManager(node)) { FailPutBacks = 2 };
        var logger = new RecordingLogger();
        var work = new ClaimedWorkQueue(manager, new RecordingTaskScheduler(), Substitute.For<ITickerExecutionTaskHandler>(),
            new TickerFunctionConcurrencyGate(), logger);
        var entry = work.Record(Function("PutBack.Final"), manager.NextClaimMarker());

        await work.PutBackAsync(entry, "the scheduler refused it");
        await work.PutBackUnstartedAsync();

        Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Error && e.Message.Contains(entry.Function.TickerId.ToString())));
        Assert.True(entry.NeedsPutBack);
    }

    private ClaimedWorkQueue WorkWith(HookedManager manager, RecordingLogger? logger = null)
        => new(manager, new RecordingTaskScheduler(), Substitute.For<ITickerExecutionTaskHandler>(), new TickerFunctionConcurrencyGate(), logger);

    // Three put-backs failed while the store was out of reach. The loop's retry stops at the first one that fails again, so a
    // pass waits for at most one store timeout instead of one per row; the others are tried on the next pass.
    [Fact]
    public async Task RetryOfFailedPutBacks_StopsAtTheFirstFailure()
    {
        var manager = new HookedManager(CreateRealManager(CreateProvider("node-A"))) { FailPutBacks = 10 };
        var work = WorkWith(manager);
        var entries = Enumerable.Range(0, 3).Select(_ => work.Record(Function("PutBack.Retry"), manager.NextClaimMarker())).ToArray();
        foreach (var entry in entries)
            await work.PutBackAsync(entry, "the scheduler refused it");
        var before = manager.PutBackAttempts;

        await work.RetryFailedPutBacksAsync();

        Assert.Equal(before + 1, manager.PutBackAttempts);
        Assert.All(entries, e => Assert.True(e.NeedsPutBack));
    }

    // A store that does not answer must not hold the loop: each retried put-back has its own time limit.
    [Fact]
    public async Task RetryOfFailedPutBacks_DoesNotWaitLongerThanItsLimit()
    {
        var manager = new HookedManager(CreateRealManager(CreateProvider("node-A"))) { FailPutBacks = 1 };
        var work = WorkWith(manager);
        work.PutBackAttemptLimit = TimeSpan.FromMilliseconds(100);
        var entry = work.Record(Function("PutBack.Hang"), manager.NextClaimMarker());
        await work.PutBackAsync(entry, "the scheduler refused it");
        manager.HangPutBacks = true;

        await work.RetryFailedPutBacksAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(entry.NeedsPutBack);
    }

    // The host's stop gives the services a limited time. When it is used up, the put-backs that have not answered give up:
    // each is logged as an Error, and the stop returns instead of waiting for every store timeout.
    [Fact]
    public async Task PutBackAtTheStop_GivesUpWhenTheStopTimeIsUsedUp()
    {
        var manager = new HookedManager(CreateRealManager(CreateProvider("node-A"))) { HangPutBacks = true };
        var logger = new RecordingLogger();
        var work = WorkWith(manager, logger);
        var entries = Enumerable.Range(0, 3).Select(_ => work.Record(Function("PutBack.Stop"), manager.NextClaimMarker())).ToArray();
        using var stopTime = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await work.PutBackUnstartedAsync(cancellationToken: stopTime.Token).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, logger.Entries.Count(e => e.Level == LogLevel.Error));
        Assert.All(entries, e => Assert.True(e.NeedsPutBack));
    }

    // Both services hand the host's stop time to their put-backs: with that time used up, a store that does not answer
    // does not hold the stop.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAsync_WhenTheStopTimeIsUsedUp_DoesNotWaitForPutBacks(bool fallback)
    {
        var manager = new HookedManager(CreateRealManager(CreateProvider("node-A"))) { HangPutBacks = true };
        var scheduler = new RecordingTaskScheduler();
        var handler = Substitute.For<ITickerExecutionTaskHandler>();
        var gate = new TickerFunctionConcurrencyGate();
        var work = new ClaimedWorkQueue(manager, scheduler, handler, gate);
        IHostedService service = fallback
            ? new SharpTickerFallbackBackgroundService(manager,
                new SchedulerOptionsBuilder { FallbackIntervalChecker = TimeSpan.FromMilliseconds(20) }, handler, scheduler, gate, null, work)
            : new SharpTickerSchedulerBackgroundService(new TickerExecutionContext(), handler, scheduler, manager,
                new SchedulerOptionsBuilder { MinPollingInterval = TimeSpan.FromMilliseconds(10) }, gate, null, _clock, null, work);
        work.Record(Function("PutBack.StopToken"), manager.NextClaimMarker(), owner: fallback ? service : null);

        await service.StopAsync(new CancellationToken(canceled: true)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, manager.PutBackAttempts);
    }

    // The scheduler is frozen while the fallback hands over the rows it took. The rows it could not hand over were already
    // InProgress for this node; they are put back instead of being left with no worker.
    [Fact]
    public async Task Fallback_SchedulerFrozenMidPass_RowsNotHandedOverArePutBack()
    {
        var ids = new[] { await SeedTimeTicker("Fallback.Batch"), await SeedTimeTicker("Fallback.Batch"), await SeedTimeTicker("Fallback.Batch") };
        var node = CreateProvider("node-B");
        var runs = new ConcurrentDictionary<Guid, int>();
        var scheduler = new RecordingTaskScheduler();
        scheduler.OnQueued = n => { if (n == 1) scheduler.Freeze(); };
        using var fallback = new SharpTickerFallbackBackgroundService(CreateRealManager(node),
            new SchedulerOptionsBuilder { FallbackIntervalChecker = TimeSpan.FromMilliseconds(20) }, CountingHandler(runs), scheduler,
            new TickerFunctionConcurrencyGate());
        _clock.Advance(TimeSpan.FromSeconds(2));

        await fallback.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await WaitUntilAsync(() => ids.Count(id => StatusOf(node, id).Result == TickerStatus.Idle) == 2),
                "the rows not handed over were not put back");
        }
        finally
        {
            await fallback.StopAsync(CancellationToken.None);
        }
        await scheduler.WhenAllIdleAsync();

        var handedOver = Assert.Single(runs.Keys);
        Assert.Equal(TickerStatus.InProgress, await StatusOf(node, handedOver));
        Assert.Equal(1, scheduler.QueuedCount);
    }

    [Fact]
    public async Task LateLoop_RowCancelledMeanwhile_JobDoesNotRun()
    {
        var runs = new ConcurrentDictionary<Guid, int>();
        var (manager, scheduler, loop, id) = await StartLateLoop("LateLoop.Cancelled", runs);

        try
        {
            var cancelled = new InternalFunctionContext { TickerId = id }.SetProperty(x => x.Status, TickerStatus.Cancelled);
            Assert.Equal(1, await CreateProvider("node-A").UpdateTimeTicker(cancelled, CancellationToken.None));

            manager.HoldFirstPoll.TrySetResult();
            Assert.True(await WaitUntilAsync(() => manager.Polls >= 2), "the loop never finished its batch");
            await scheduler.WhenAllIdleAsync();
        }
        finally
        {
            manager.HoldFirstPoll.TrySetResult();
            await loop.StopAsync(CancellationToken.None);
        }

        Assert.False(runs.ContainsKey(id));
        Assert.Equal([false], manager.ClaimResults);
        Assert.Equal(TickerStatus.Cancelled, await StatusOf(CreateProvider("node-A"), id));
    }
}
