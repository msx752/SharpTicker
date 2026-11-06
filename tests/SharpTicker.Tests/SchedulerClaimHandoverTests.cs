using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SharpTicker.BackgroundServices;
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
        manager.TryClaimDueTicker(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>()).Returns(false);
        var job = Function("Handover.Lost");

        await RunOneBatchAsync(new TickerExecutionContext(), manager, scheduler, handler, [job], logger);

        await manager.Received(1).TryClaimDueTicker(job, Arg.Any<CancellationToken>());
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
        manager.TryClaimDueTicker(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>()).Returns(false);
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
        manager.TryClaimDueTicker(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>()).Returns(true);
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
        manager.TryClaimDueTicker(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>()).Returns(call =>
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

        public async Task<bool> TryClaimDueTicker(InternalFunctionContext function, CancellationToken cancellationToken = default)
        {
            var claim = Interlocked.Increment(ref _claims);
            if (claim == ThrowOnClaim)
            {
                if (OnClaim != null)
                    await OnClaim(cancellationToken);
                throw new InvalidOperationException("the store is unreachable");
            }
            if (claim == BeforeClaimNumber && BeforeClaim != null)
                await BeforeClaim();
            if (claim == BeforeClaimNumber && BeforeClaimWithToken != null)
                await BeforeClaimWithToken(cancellationToken);

            var won = await inner.TryClaimDueTicker(function, cancellationToken);
            ClaimResults.Enqueue(won);
            return won;
        }

        public Task NotifyInProgress(InternalFunctionContext function) => inner.NotifyInProgress(function);

        public async Task ReleaseAcquiredResources(InternalFunctionContext[] context, CancellationToken cancellationToken = default)
        {
            await inner.ReleaseAcquiredResources(context, cancellationToken);
            Released.TrySetResult();
        }

        public Task UpdateTickerAsync(InternalFunctionContext context, CancellationToken cancellationToken = default) => inner.UpdateTickerAsync(context, cancellationToken);
        public Task<T> GetRequestAsync<T>(Guid tickerId, TickerType type, CancellationToken cancellationToken = default) => inner.GetRequestAsync<T>(tickerId, type, cancellationToken);
        public Task<T> GetRequestAsync<T>(Guid tickerId, TickerType type, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default) => inner.GetRequestAsync(tickerId, type, typeInfo, cancellationToken);
        public Task<InternalFunctionContext[]> RunTimedOutTickers(CancellationToken cancellationToken = default) => inner.RunTimedOutTickers(cancellationToken);
        public Task MigrateDefinedCronTickers((string, string)[] cronExpressions, CancellationToken cancellationToken = default) => inner.MigrateDefinedCronTickers(cronExpressions, cancellationToken);
        public Task DeleteTicker(Guid tickerId, TickerType type, CancellationToken cancellationToken = default) => inner.DeleteTicker(tickerId, type, cancellationToken);
        public Task ReleaseDeadNodeResources(string instanceIdentifier, CancellationToken cancellationToken = default) => inner.ReleaseDeadNodeResources(instanceIdentifier, cancellationToken);
        public Task UpdateSkipTimeTickersWithUnifiedContextAsync(InternalFunctionContext[] context, CancellationToken cancellationToken = default) => inner.UpdateSkipTimeTickersWithUnifiedContextAsync(context, cancellationToken);
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

    [Fact]
    public async Task Handover_ClaimThrowsOnThirdRow_FirstTwoRun_RestReleased()
    {
        var node = CreateProvider("node-A");
        var ids = new[] { await SeedTimeTicker("Handover.Batch"), await SeedTimeTicker("Handover.Batch"), await SeedTimeTicker("Handover.Batch"), await SeedTimeTicker("Handover.Batch") };
        var batch = await QueueBatch(node, ids);
        var manager = new HookedManager(CreateRealManager(node)) { ThrowOnClaim = 3 };
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

        // The two rows that were handed over are running work and stay InProgress; the one whose claim failed and the one not
        // reached go back to Idle for the next poll.
        Assert.Equal(TickerStatus.InProgress, await StatusOf(node, ids[0]));
        Assert.Equal(TickerStatus.InProgress, await StatusOf(node, ids[1]));
        Assert.Equal(TickerStatus.Idle, await StatusOf(node, ids[2]));
        Assert.Equal(TickerStatus.Idle, await StatusOf(node, ids[3]));
        Assert.Equal(2, scheduler.QueuedCount);
        await handler.Received(1).ExecuteTaskAsync(Arg.Is<InternalFunctionContext>(f => f.TickerId == ids[0]), false, Arg.Any<CancellationToken>());
        await handler.Received(1).ExecuteTaskAsync(Arg.Is<InternalFunctionContext>(f => f.TickerId == ids[1]), false, Arg.Any<CancellationToken>());
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
            service.Restart();
            await Task.Delay(300, ct);
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

    [Fact]
    public async Task Handover_StopMidBatch_UnreachedRowsAreReleased()
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
        await manager.Released.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stop!.WaitAsync(TimeSpan.FromSeconds(10));
        await scheduler.WhenAllIdleAsync();

        Assert.Equal(TickerStatus.InProgress, await StatusOf(node, ids[0]));
        foreach (var id in ids.Skip(1))
            Assert.Equal(TickerStatus.Idle, await StatusOf(node, id));
        Assert.Single(manager.ClaimResults);
        Assert.Equal(1, scheduler.QueuedCount);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Handover_SchedulerFrozenMidBatch_RowsAreNotClaimed()
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
            await manager.Released.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
        await scheduler.WhenAllIdleAsync();

        Assert.Equal(TickerStatus.InProgress, await StatusOf(node, ids[0]));
        Assert.Equal(TickerStatus.Idle, await StatusOf(node, ids[1]));
        Assert.Equal(TickerStatus.Idle, await StatusOf(node, ids[2]));
        Assert.Single(manager.ClaimResults);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Handover_QueueFaults_IsLogged()
    {
        var manager = Substitute.For<IInternalTickerManager>();
        manager.TryClaimDueTicker(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>()).Returns(true);
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
