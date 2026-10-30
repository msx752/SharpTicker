using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
