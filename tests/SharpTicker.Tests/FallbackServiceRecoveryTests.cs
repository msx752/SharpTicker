using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SharpTicker.BackgroundServices;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Models;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// The safety net that runs the jobs the main scheduler missed: it looks them up in the function registry so each job,
/// and each of its children, gets its delegate, priority and concurrency limit, hands them to the scheduler as due jobs,
/// and keeps going when the store fails or the scheduler is frozen or disposed.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class FallbackServiceRecoveryTests : IAsyncLifetime
{
    private readonly IInternalTickerManager _manager = Substitute.For<IInternalTickerManager>();
    private readonly ITickerExecutionTaskHandler _handler = Substitute.For<ITickerExecutionTaskHandler>();
    private readonly ISharpTickerTaskScheduler _scheduler = Substitute.For<ISharpTickerTaskScheduler>();
    private readonly ConcurrentQueue<(Func<CancellationToken, Task> Work, TickerTaskPriority Priority)> _queued = new();
    private SharpTickerFallbackBackgroundService _service = null!;

    private static readonly TickerFunctionDelegate ParentDelegate = (_, _, _) => Task.CompletedTask;
    private static readonly TickerFunctionDelegate ChildDelegate = (_, _, _) => Task.CompletedTask;
    private static readonly TickerFunctionDelegate GrandChildDelegate = (_, _, _) => Task.CompletedTask;

    public Task InitializeAsync()
    {
        TickerFunctionProvider.RegisterFunctions(new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>
        {
            ["Fallback.Parent"] = ("", TickerTaskPriority.High, ParentDelegate, 2),
            ["Fallback.Child"] = ("", TickerTaskPriority.Low, ChildDelegate, 3),
            ["Fallback.GrandChild"] = ("", TickerTaskPriority.Normal, GrandChildDelegate, 0),
        });
        TickerFunctionProvider.Build();

        _scheduler.IsFrozen.Returns(false);
        _scheduler.IsDisposed.Returns(false);
        _scheduler.QueueAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<TickerTaskPriority>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _queued.Enqueue((call.ArgAt<Func<CancellationToken, Task>>(0), call.ArgAt<TickerTaskPriority>(1)));
                return ValueTask.CompletedTask;
            });
        _handler.ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        _service = new SharpTickerFallbackBackgroundService(
            _manager, new SchedulerOptionsBuilder { FallbackIntervalChecker = TimeSpan.FromMilliseconds(40) },
            _handler, _scheduler, new TickerFunctionConcurrencyGate());
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _service.StopAsync(CancellationToken.None);
        _service.Dispose();
    }

    private static InternalFunctionContext Job(string function, params InternalFunctionContext[] children)
        => new() { TickerId = Guid.NewGuid(), FunctionName = function, TimeTickerChildren = children.ToList() };

    private int Polls() => _manager.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IInternalTickerManager.RunTimedOutTickers));

    /// <summary>The jobs are returned once, then nothing is left.</summary>
    private void ReturnOnce(params InternalFunctionContext[] jobs)
    {
        var first = true;
        _manager.RunTimedOutTickers(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (!first) return Array.Empty<InternalFunctionContext>();
            first = false;
            return jobs;
        });
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int seconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    // The task scheduler is full and has not accepted the job yet. The fallback waits for it before it claims more, so it
    // does not take every overdue row of the cluster onto this node while it cannot run them.
    [Fact]
    public async Task WhileTheSchedulerHasNotAcceptedTheJob_TheFallbackClaimsNoMore()
    {
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _scheduler.QueueAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<TickerTaskPriority>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(accepted.Task));
        _manager.RunTimedOutTickers(Arg.Any<CancellationToken>())
            .Returns(_ => [new InternalFunctionContext { TickerId = Guid.NewGuid(), FunctionName = "Fallback.Parent", ClaimMarker = DateTime.UtcNow }]);

        await _service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await WaitUntilAsync(() => Polls() >= 1));
            await Task.Delay(300);

            Assert.Equal(1, Polls());
        }
        finally
        {
            accepted.TrySetResult();
        }
    }

    [Fact]
    public async Task AJobAndItsChildren_GetTheirDelegateAndPriorityFromTheRegistry()
    {
        var grandChild = Job("Fallback.GrandChild");
        var child = Job("Fallback.Child", grandChild);
        var parent = Job("Fallback.Parent", child);
        ReturnOnce(parent);

        await _service.StartAsync(CancellationToken.None);

        Assert.True(await WaitUntilAsync(() => !_queued.IsEmpty));
        Assert.Same(ParentDelegate, parent.CachedDelegate);
        Assert.Equal(TickerTaskPriority.High, parent.CachedPriority);
        Assert.Equal(2, parent.CachedMaxConcurrency);
        Assert.Same(ChildDelegate, child.CachedDelegate);
        Assert.Equal(TickerTaskPriority.Low, child.CachedPriority);
        Assert.Equal(3, child.CachedMaxConcurrency);
        Assert.Same(GrandChildDelegate, grandChild.CachedDelegate);
        Assert.Equal(TickerTaskPriority.Normal, grandChild.CachedPriority);
    }

    [Fact]
    public async Task TheJobIsQueuedWithItsPriority_AndRunsAsADueJob()
    {
        var parent = Job("Fallback.Parent");
        ReturnOnce(parent);

        await _service.StartAsync(CancellationToken.None);
        Assert.True(await WaitUntilAsync(() => !_queued.IsEmpty));
        _queued.TryPeek(out var queued);

        Assert.Equal(TickerTaskPriority.High, queued.Priority);
        await queued.Work(CancellationToken.None);
        await _handler.Received(1).ExecuteTaskAsync(parent, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFunctionThatIsNotRegistered_IsStillQueued_SoTheHandlerCanReportIt()
    {
        var unknown = Job("Fallback.NobodyRegisteredThis");
        ReturnOnce(unknown);

        await _service.StartAsync(CancellationToken.None);

        Assert.True(await WaitUntilAsync(() => !_queued.IsEmpty));
        Assert.Null(unknown.CachedDelegate);
    }

    [Fact]
    public async Task EveryTimedOutJob_IsQueued()
    {
        ReturnOnce(Job("Fallback.Parent"), Job("Fallback.Parent"), Job("Fallback.Child"));

        await _service.StartAsync(CancellationToken.None);

        Assert.True(await WaitUntilAsync(() => _queued.Count == 3));
    }

    [Fact]
    public async Task TheConcurrencyLimit_IsReleased_EvenWhenTheJobFails()
    {
        // Three jobs of a function with a limit of 2. Each claim starts its job once, so each job is its own queued work.
        ReturnOnce(Job("Fallback.Parent"), Job("Fallback.Parent"), Job("Fallback.Parent"));
        _handler.ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("the job failed")));

        await _service.StartAsync(CancellationToken.None);
        Assert.True(await WaitUntilAsync(() => _queued.Count == 3));

        // More failing runs than the limit allows, one after the other: if a slot leaked, the third would wait forever.
        foreach (var queued in _queued)
            await Assert.ThrowsAsync<InvalidOperationException>(() => queued.Work(CancellationToken.None))
                .WaitAsync(TimeSpan.FromSeconds(5));
        await _handler.Received(3).ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenTheSchedulerIsFrozenWhileQueueing_TheLoopKeepsRunning()
    {
        ReturnOnce(Job("Fallback.Parent"), Job("Fallback.Parent"));
        _scheduler.QueueAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<TickerTaskPriority>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _scheduler.IsFrozen.Returns(true);
                throw new InvalidOperationException("Scheduler is frozen");
            });

        await _service.StartAsync(CancellationToken.None);

        Assert.True(await WaitUntilAsync(() => Polls() >= 1));
        await Task.Delay(300);
        // Frozen now, so it only waits; it did not crash and does not poll the store.
        var pollsWhileFrozen = Polls();
        await Task.Delay(300);
        Assert.Equal(pollsWhileFrozen, Polls());
    }

    [Fact]
    public async Task ADisposedScheduler_IsNotPolledFor()
    {
        _scheduler.IsDisposed.Returns(true);

        await _service.StartAsync(CancellationToken.None);
        await Task.Delay(300);

        Assert.Equal(0, Polls());
        Assert.True(_queued.IsEmpty);
    }

    [Fact]
    public async Task AFailureOfTheStore_IsSwallowed_AndTheLoopTriesAgain()
    {
        _manager.RunTimedOutTickers(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<InternalFunctionContext[]>(new InvalidOperationException("the store is down")));

        await _service.StartAsync(CancellationToken.None);

        Assert.True(await WaitUntilAsync(() => Polls() >= 3), "the loop gave up after the store failed");
    }

    // An outage of the safety net must not be silent: the first failure is an error, the attempts while it goes on are at
    // Debug, and the recovery is written once.
    [Fact]
    public async Task AFailureOfTheStore_IsLoggedOnceAsAnError_AndTheRecoveryIsLogged()
    {
        var logs = new CapturedLogs();
        using var loggerFactory = new LoggerFactory(new[] { logs });
        var failures = 3;
        _manager.RunTimedOutTickers(Arg.Any<CancellationToken>()).Returns(_ => failures-- > 0
            ? Task.FromException<InternalFunctionContext[]>(new InvalidOperationException("the store is down"))
            : Task.FromResult(Array.Empty<InternalFunctionContext>()));
        using var service = new SharpTickerFallbackBackgroundService(
            _manager, new SchedulerOptionsBuilder { FallbackIntervalChecker = TimeSpan.FromMilliseconds(20) },
            _handler, _scheduler, new TickerFunctionConcurrencyGate(), loggerFactory.CreateLogger<SharpTickerFallbackBackgroundService>());

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await WaitUntilAsync(() => logs.Entries.Any(e => e.Level == LogLevel.Information)), "the recovery was not logged");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        var error = Assert.Single(logs.Entries, e => e.Level >= LogLevel.Error);
        Assert.Equal("the store is down", error.Exception?.Message);
        Assert.Equal(2, logs.Entries.Count(e => e.Level == LogLevel.Debug));
        Assert.Contains("recovered", Assert.Single(logs.Entries, e => e.Level == LogLevel.Information).Message);
    }

    // The store failed after the fallback had claimed a row: the row comes back with the failure. It is run, not left
    // InProgress, and the failure is still reported.
    [Fact]
    public async Task TheStoreFailsAfterARowWasClaimed_TheRowIsStillRun_AndTheFailureIsLogged()
    {
        var logs = new CapturedLogs();
        using var loggerFactory = new LoggerFactory(new[] { logs });
        var claimed = Job("Fallback.Parent");
        claimed.ClaimMarker = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var failure = new InvalidOperationException("the connection dropped");
        var first = true;
        _manager.RunTimedOutTickers(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (!first) return Task.FromResult(Array.Empty<InternalFunctionContext>());
            first = false;
            return Task.FromException<InternalFunctionContext[]>(new SharpTicker.Utilities.Exceptions.ClaimsInterruptedException([claimed], failure));
        });
        using var service = new SharpTickerFallbackBackgroundService(
            _manager, new SchedulerOptionsBuilder { FallbackIntervalChecker = TimeSpan.FromMilliseconds(20) },
            _handler, _scheduler, new TickerFunctionConcurrencyGate(), loggerFactory.CreateLogger<SharpTickerFallbackBackgroundService>());

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await WaitUntilAsync(() => _queued.Count == 1), "the claimed row was not handed over");
            await _queued.Single().Work(CancellationToken.None);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        await _handler.Received(1).ExecuteTaskAsync(claimed, true, Arg.Any<CancellationToken>());
        Assert.Same(failure, Assert.Single(logs.Entries, e => e.Level >= LogLevel.Error).Exception);
    }

    // The stop cancels the claim call after it claimed a row: the row is put back, not handed to a scheduler that stops.
    [Fact]
    public async Task TheStopInterruptsTheClaimCall_TheClaimedRowIsPutBack()
    {
        var claimed = Job("Fallback.Parent");
        claimed.ClaimMarker = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var calling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager.RunTimedOutTickers(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var token = call.Arg<CancellationToken>();
            calling.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException ex)
            {
                throw new SharpTicker.Utilities.Exceptions.ClaimsInterruptedException([claimed], ex);
            }
            return Array.Empty<InternalFunctionContext>();
        });
        _manager.TryReleaseClaim(Arg.Any<InternalFunctionContext>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);

        await _service.StartAsync(CancellationToken.None);
        await calling.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await _service.StopAsync(CancellationToken.None);

        await _manager.Received(1).TryReleaseClaim(claimed, claimed.ClaimMarker.Value, Arg.Any<CancellationToken>());
        Assert.True(_queued.IsEmpty);
        await _handler.DidNotReceiveWithAnyArgs().ExecuteTaskAsync(default!, default, default);
    }

    [Fact]
    public async Task AfterStopping_TheServiceCanBeStartedAgain()
    {
        _manager.RunTimedOutTickers(Arg.Any<CancellationToken>()).Returns(Array.Empty<InternalFunctionContext>());
        await _service.StartAsync(CancellationToken.None);
        Assert.True(await WaitUntilAsync(() => Polls() >= 1));
        await _service.StopAsync(CancellationToken.None);
        var pollsAtStop = Polls();

        await _service.StartAsync(CancellationToken.None);

        Assert.True(await WaitUntilAsync(() => Polls() > pollsAtStop));
    }
}
