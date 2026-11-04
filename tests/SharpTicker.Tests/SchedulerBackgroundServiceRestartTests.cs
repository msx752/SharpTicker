using System;
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
/// How the scheduler loop starts, restarts and recovers: the manual start mode, when a new ticker makes the loop restart
/// to pick up an earlier time, and what the loop does after an error. A running loop sleeps for an hour between
/// polls, so a restart shows up as one more call to read the next tickers.
/// </summary>
public class SchedulerBackgroundServiceRestartTests : IAsyncLifetime
{
    private readonly TickerExecutionContext _executionContext = new();
    private readonly IInternalTickerManager _manager = Substitute.For<IInternalTickerManager>();
    private readonly ISharpTickerTaskScheduler _taskScheduler = Substitute.For<ISharpTickerTaskScheduler>();
    private SharpTickerSchedulerBackgroundService _service = null!;

    public Task InitializeAsync()
    {
        _manager.GetNextTickers(Arg.Any<CancellationToken>()).Returns((TimeSpan.FromHours(1), Array.Empty<InternalFunctionContext>()));
        _service = Create();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _service.StopAsync(CancellationToken.None);
        _service.Dispose();
    }

    private SharpTickerSchedulerBackgroundService Create(SchedulerOptionsBuilder? options = null)
        => new(_executionContext, Substitute.For<ITickerExecutionTaskHandler>(), _taskScheduler, _manager,
            options ?? new SchedulerOptionsBuilder(), new TickerFunctionConcurrencyGate());

    private int PollCount() => _manager.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IInternalTickerManager.GetNextTickers));

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

    /// <summary>Starts the loop and waits for its first poll, so the next planned occurrence is set.</summary>
    private async Task StartRunningAsync()
    {
        await _service.StartAsync(CancellationToken.None);
        Assert.True(await WaitUntilAsync(() => PollCount() >= 1 && _executionContext.GetNextPlannedOccurrence() != null),
            "the loop never polled");
    }

    #region Starting

    [Fact]
    public async Task StartingInManualMode_FreezesTheTaskScheduler_AndDoesNotRunTheLoop()
    {
        _service.SkipFirstRun = true;

        await _service.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        _taskScheduler.Received(1).Freeze();
        _taskScheduler.DidNotReceive().Resume();
        Assert.False(_service.IsRunning);
        Assert.Equal(0, PollCount());
        Assert.False(_service.SkipFirstRun);
    }

    [Fact]
    public async Task StartingAgainAfterManualMode_ResumesAndRunsTheLoop()
    {
        _service.SkipFirstRun = true;
        await _service.StartAsync(CancellationToken.None);

        await StartRunningAsync();

        _taskScheduler.Received().Resume();
        Assert.True(_service.IsRunning);
    }

    [Fact]
    public async Task StartingTwice_RunsTheLoopOnce()
    {
        await StartRunningAsync();

        await _service.StartAsync(CancellationToken.None);
        await Task.Delay(300);

        Assert.Equal(1, PollCount());
    }

    [Fact]
    public async Task StoppingTheService_StopsTheLoopFromRunning()
    {
        await StartRunningAsync();

        await _service.StopAsync(CancellationToken.None);

        Assert.False(_service.IsRunning);
        _taskScheduler.Received().Freeze();
    }

    #endregion

    #region Restarting

    [Fact]
    public async Task ARestart_ReleasesWhatTheLoopHeld_AndPollsAgain()
    {
        await StartRunningAsync();

        _service.Restart();

        Assert.True(await WaitUntilAsync(() => PollCount() >= 2), "the loop did not poll again after a restart");
        await _manager.Received().ReleaseAcquiredResources(Arg.Any<InternalFunctionContext[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeveralRestartsInARow_AreCombinedIntoOne()
    {
        await StartRunningAsync();

        for (var i = 0; i < 10; i++)
            _service.Restart();

        Assert.True(await WaitUntilAsync(() => PollCount() >= 2));
        await Task.Delay(500);
        Assert.Equal(2, PollCount());
    }

    [Fact]
    public async Task ATickerEarlierThanThePlannedOne_RestartsTheLoop()
    {
        await StartRunningAsync();          // planned in about an hour

        _service.RestartIfNeeded(DateTime.UtcNow.AddMinutes(30));

        Assert.True(await WaitUntilAsync(() => PollCount() >= 2));
    }

    [Fact]
    public async Task ATickerThatIsAlreadyDue_RestartsTheLoop()
    {
        await StartRunningAsync();

        _service.RestartIfNeeded(DateTime.UtcNow.AddMinutes(-1));

        Assert.True(await WaitUntilAsync(() => PollCount() >= 2));
    }

    [Fact]
    public async Task ATickerLaterThanThePlannedOne_DoesNotRestartTheLoop()
    {
        await StartRunningAsync();

        _service.RestartIfNeeded(DateTime.UtcNow.AddHours(2));
        await Task.Delay(500);

        Assert.Equal(1, PollCount());
    }

    [Fact]
    public async Task ATickerOnlyJustBeforeThePlannedOne_DoesNotRestartTheLoop()
    {
        await StartRunningAsync();
        var planned = _executionContext.GetNextPlannedOccurrence()!.Value;

        _service.RestartIfNeeded(planned.AddMilliseconds(-200));      // inside the 500 ms the loop tolerates
        await Task.Delay(500);

        Assert.Equal(1, PollCount());
    }

    [Fact]
    public async Task WhenNothingIsPlanned_AnyNewTicker_RestartsTheLoop()
    {
        await StartRunningAsync();
        _executionContext.SetNextPlannedOccurrence(null);

        _service.RestartIfNeeded(DateTime.UtcNow.AddHours(10));

        Assert.True(await WaitUntilAsync(() => PollCount() >= 2));
    }

    [Fact]
    public async Task ARestartRequestWithoutATime_IsIgnored()
    {
        await StartRunningAsync();

        _service.RestartIfNeeded(null);
        await Task.Delay(500);

        Assert.Equal(1, PollCount());
    }

    #endregion

    #region Errors and the polling interval

    [Fact]
    public async Task AnErrorInTheLoop_IsReportedToTheHost_ReleasesEverything_AndTheLoopKeepsRunning()
    {
        var reported = new System.Collections.Concurrent.ConcurrentBag<(string Message, CoreNotifyActionType Type)>();
        _executionContext.NotifyCoreAction = (value, type) => reported.Add(((string)value, type));
        _manager.GetNextTickers(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<(TimeSpan, InternalFunctionContext[])>(new InvalidOperationException("the store is down")));

        await _service.StartAsync(CancellationToken.None);
        Assert.True(await WaitUntilAsync(() => PollCount() >= 1));
        // It recovers: the next poll succeeds, a second after the error.
        _manager.GetNextTickers(Arg.Any<CancellationToken>()).Returns((TimeSpan.FromHours(1), Array.Empty<InternalFunctionContext>()));

        Assert.True(await WaitUntilAsync(() => PollCount() >= 2, seconds: 8), "the loop stopped after the error");
        Assert.Contains(reported, r => r.Type == CoreNotifyActionType.NotifyHostExceptionMessage && r.Message.Contains("the store is down"));
        await _manager.Received().ReleaseAcquiredResources(null, Arg.Any<CancellationToken>());
    }

    // When the store is unreachable, releasing what the loop held fails for the same reason as the poll did. That second error
    // must not end the loop: a background service that throws stops the whole host under the default StopHost behavior.
    [Fact]
    public async Task WhenTheStoreIsDown_ReleasingAfterTheError_FailsToo_AndTheLoopStillKeepsRunning()
    {
        var storeDown = new InvalidOperationException("the store is down");
        _manager.GetNextTickers(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<(TimeSpan, InternalFunctionContext[])>(storeDown));
        _manager.ReleaseAcquiredResources(Arg.Any<InternalFunctionContext[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(storeDown));

        await _service.StartAsync(CancellationToken.None);
        Assert.True(await WaitUntilAsync(() => PollCount() >= 1));
        Assert.True(await WaitUntilAsync(() => _manager.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(IInternalTickerManager.ReleaseAcquiredResources))));

        // The store comes back.
        _manager.GetNextTickers(Arg.Any<CancellationToken>()).Returns((TimeSpan.FromHours(1), Array.Empty<InternalFunctionContext>()));
        _manager.ReleaseAcquiredResources(Arg.Any<InternalFunctionContext[]>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        Assert.True(await WaitUntilAsync(() => PollCount() >= 2, seconds: 8), "the loop stopped when releasing failed after the error");
        Assert.False(_service.ExecuteTask!.IsFaulted, _service.ExecuteTask.Exception?.ToString());
    }

    [Fact]
    public async Task WhenTheStoreIsDown_ReleasingOnARestart_Fails_AndTheLoopStillKeepsRunning()
    {
        await StartRunningAsync();
        var storeDown = new InvalidOperationException("the store is down");
        _manager.ReleaseAcquiredResources(Arg.Any<InternalFunctionContext[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(storeDown));

        _service.Restart();
        Assert.True(await WaitUntilAsync(() => _manager.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(IInternalTickerManager.ReleaseAcquiredResources))));

        _manager.ReleaseAcquiredResources(Arg.Any<InternalFunctionContext[]>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        Assert.True(await WaitUntilAsync(() => PollCount() >= 2, seconds: 8), "the loop stopped when releasing failed on a restart");
        Assert.False(_service.ExecuteTask!.IsFaulted, _service.ExecuteTask.Exception?.ToString());
    }

    [Fact]
    public async Task WhenReportingTheErrorToTheDashboardFails_TheLoopStillKeepsRunning()
    {
        _executionContext.NotifyCoreAction = (_, type) =>
        {
            if (type == CoreNotifyActionType.NotifyHostExceptionMessage)
                throw new InvalidOperationException("the dashboard is gone");
        };
        _manager.GetNextTickers(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<(TimeSpan, InternalFunctionContext[])>(new InvalidOperationException("the store is down")));

        await _service.StartAsync(CancellationToken.None);
        Assert.True(await WaitUntilAsync(() => PollCount() >= 1));
        _manager.GetNextTickers(Arg.Any<CancellationToken>()).Returns((TimeSpan.FromHours(1), Array.Empty<InternalFunctionContext>()));

        Assert.True(await WaitUntilAsync(() => PollCount() >= 2, seconds: 8), "the loop stopped when reporting the error failed");
        Assert.False(_service.ExecuteTask!.IsFaulted, _service.ExecuteTask.Exception?.ToString());
        await _manager.Received().ReleaseAcquiredResources(null, Arg.Any<CancellationToken>());
    }

    // An outage is logged by its changes, not on every attempt of the loop: one error when it starts, then one line when it
    // recovers with the attempts and how long it lasted.
    [Fact]
    public async Task AnOutage_IsLoggedWhenItStartsAndWhenItEnds_NotOnEveryAttempt()
    {
        var storeDown = new InvalidOperationException("the store is down");
        _manager.GetNextTickers(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<(TimeSpan, InternalFunctionContext[])>(storeDown));
        _manager.ReleaseAcquiredResources(Arg.Any<InternalFunctionContext[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(storeDown));
        var logs = new CapturedLogs();
        var service = CreateWithLogs(logs);
        try
        {
            await service.StartAsync(CancellationToken.None);
            Assert.True(await WaitUntilAsync(() => PollCount() >= 3, seconds: 8));

            _manager.GetNextTickers(Arg.Any<CancellationToken>()).Returns((TimeSpan.FromHours(1), Array.Empty<InternalFunctionContext>()));
            _manager.ReleaseAcquiredResources(Arg.Any<InternalFunctionContext[]>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            Assert.True(await WaitUntilAsync(() => logs.Entries.Any(e => e.Level == LogLevel.Information), seconds: 8));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }

        var errors = logs.Entries.Where(e => e.Level >= LogLevel.Error).ToArray();
        Assert.Equal(2, errors.Length);
        Assert.Contains(errors, e => e.Message.StartsWith("The scheduler loop failed.") && e.Exception == storeDown);
        Assert.Contains(errors, e => e.Message.StartsWith("The scheduler could not release") && e.Exception == storeDown);
        Assert.True(logs.Entries.Count(e => e.Level == LogLevel.Debug && e.Message.StartsWith("The scheduler loop failed again")) >= 2);

        var recovered = Assert.Single(logs.Entries, e => e.Level == LogLevel.Information);
        Assert.Matches(@"^The scheduler loop recovered after ([3-9]|\d\d+) failed attempts in \d\d:\d\d:\d\d\.$", recovered.Message);
    }

    // While the outage goes on it is reported again once a minute with the attempts and the time so far. A few minutes can be
    // a restart of the database; once it lasts CriticalFailureDuration the store stays out of reach, and the reminder is critical.
    [Fact]
    public async Task AnOutageThatGoesOn_IsReportedOnceAMinute_AndBecomesCriticalAfterFiveMinutes()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var clock = Substitute.For<ITickerClock>();
        clock.UtcNow.Returns(_ => now);
        _manager.GetNextTickers(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<(TimeSpan, InternalFunctionContext[])>(new InvalidOperationException("the store is down")));
        var logs = new CapturedLogs();
        var service = CreateWithLogs(logs, clock);
        int Reports() => logs.Entries.Count(e => e.Level >= LogLevel.Error);
        try
        {
            await service.StartAsync(CancellationToken.None);
            Assert.True(await WaitUntilAsync(() => PollCount() >= 2, seconds: 8));
            Assert.Equal(1, Reports());

            now = now.AddSeconds(61);
            Assert.True(await WaitUntilAsync(() => Reports() >= 2, seconds: 8));

            now = now.AddMinutes(4);
            Assert.True(await WaitUntilAsync(() => Reports() >= 3, seconds: 8));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }

        var reports = logs.Entries.Where(e => e.Level >= LogLevel.Error).ToArray();
        Assert.Equal(LogLevel.Error, reports[0].Level);
        Assert.Equal(LogLevel.Error, reports[1].Level);
        Assert.Matches(@"^The scheduler loop is still failing: \d+ attempts in 00:01:01\. No job is picked up until it recovers\.$", reports[1].Message);
        Assert.Equal(LogLevel.Critical, reports[2].Level);
        Assert.Matches(@"^The scheduler loop is still failing: \d+ attempts in 00:05:01\. No job is picked up until it recovers\.$", reports[2].Message);
    }

    // A normal stop is not an error. A store call that the stop cuts off can end in an exception other than
    // OperationCanceledException, and a release that fails while stopping only leaves this node's own locks behind.
    [Fact]
    public async Task StoppingWhileAPollIsCutOff_LogsNoError()
    {
        _manager.GetNextTickers(Arg.Any<CancellationToken>()).Returns(ci => FailWhenCancelled(ci.Arg<CancellationToken>(),
            () => new InvalidOperationException("the connection was closed")));

        var logs = await RunAndStopAsync();

        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error);
        await _manager.Received().ReleaseAcquiredResources(Arg.Any<InternalFunctionContext[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StoppingWhenTheStoreIsDown_AFailedRelease_IsAWarning_NotAnError()
    {
        _manager.GetNextTickers(Arg.Any<CancellationToken>()).Returns(ci => FailWhenCancelled(ci.Arg<CancellationToken>(),
            () => new OperationCanceledException()));
        _manager.ReleaseAcquiredResources(Arg.Any<InternalFunctionContext[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("the store is down")));

        var logs = await RunAndStopAsync();

        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Exception?.Message == "the store is down");
    }

    private static Task<(TimeSpan, InternalFunctionContext[])> FailWhenCancelled(CancellationToken token, Func<Exception> failure)
    {
        var pending = new TaskCompletionSource<(TimeSpan, InternalFunctionContext[])>(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(() => pending.TrySetException(failure()));
        return pending.Task;
    }

    private SharpTickerSchedulerBackgroundService CreateWithLogs(CapturedLogs logs, ITickerClock? clock = null)
        => new(_executionContext, Substitute.For<ITickerExecutionTaskHandler>(), _taskScheduler, _manager,
            new SchedulerOptionsBuilder(), new TickerFunctionConcurrencyGate(),
            new LoggerFactory(new[] { logs }).CreateLogger<SharpTickerSchedulerBackgroundService>(), clock);

    private async Task<CapturedLogs> RunAndStopAsync()
    {
        var logs = new CapturedLogs();
        var service = CreateWithLogs(logs);
        try
        {
            await service.StartAsync(CancellationToken.None);
            Assert.True(await WaitUntilAsync(() => PollCount() >= 1));
            await service.StopAsync(CancellationToken.None);
            Assert.False(service.ExecuteTask!.IsFaulted, service.ExecuteTask.Exception?.ToString());
        }
        finally
        {
            service.Dispose();
        }
        return logs;
    }

    [Fact]
    public async Task WithoutAConfiguredInterval_TheLoopWaitsAtLeastASecondBetweenPollsThatAreDue()
    {
        _manager.GetNextTickers(Arg.Any<CancellationToken>()).Returns((TimeSpan.Zero, Array.Empty<InternalFunctionContext>()));
        var before = DateTime.UtcNow;
        var service = new SharpTickerSchedulerBackgroundService(
            _executionContext, Substitute.For<ITickerExecutionTaskHandler>(), _taskScheduler, _manager, null!, new TickerFunctionConcurrencyGate());

        try
        {
            await service.StartAsync(CancellationToken.None);
            Assert.True(await WaitUntilAsync(() => _executionContext.GetNextPlannedOccurrence() != null));

            var ahead = _executionContext.GetNextPlannedOccurrence()!.Value - before;
            Assert.True(ahead >= TimeSpan.FromMilliseconds(900), $"planned {ahead.TotalMilliseconds:F0} ms ahead");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    #endregion
}
