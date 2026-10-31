using Microsoft.Extensions.Logging;
using NSubstitute;
using SharpTicker.EntityFrameworkCore.DependencyInjection;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;

namespace SharpTicker.EntityFrameworkCore.Tests.DependencyInjection;

/// <summary>
/// What the EF Core store does when the application has started: it releases the jobs that a dead node, or an earlier run of
/// this node, left locked, and restarts the host scheduler so it sees what was seeded in the meantime. It runs on its own
/// task, so a failure has to be logged, because nobody awaits it.
/// </summary>
public class StartupReleaseTests
{
    private sealed class CapturedLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    [Fact]
    public async Task AFailureOfTheRelease_IsLoggedWithItsException_AndDoesNotFaultTheTask()
    {
        var manager = Substitute.For<IInternalTickerManager>();
        manager.ReleaseDeadNodeResources("node-a", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("the store refused")));
        var scheduler = Substitute.For<ISharpTickerHostScheduler>();
        scheduler.IsRunning.Returns(true);
        var logger = new CapturedLogger();

        await ServiceExtension.ReleaseDeadNodeResourcesOnStartAsync(manager, "node-a", scheduler, logger);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("the store refused", entry.Exception?.Message);
        Assert.Contains("node-a", entry.Message);
        scheduler.DidNotReceive().Restart();
    }

    [Fact]
    public async Task AReleaseThatSucceeds_RestartsTheHostScheduler_WhenItIsRunning()
    {
        var manager = Substitute.For<IInternalTickerManager>();
        manager.ReleaseDeadNodeResources("node-a", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var scheduler = Substitute.For<ISharpTickerHostScheduler>();
        scheduler.IsRunning.Returns(true);
        var logger = new CapturedLogger();

        await ServiceExtension.ReleaseDeadNodeResourcesOnStartAsync(manager, "node-a", scheduler, logger);

        scheduler.Received(1).Restart();
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task AReleaseThatSucceeds_LeavesAHostSchedulerThatIsNotRunning_Alone()
    {
        var manager = Substitute.For<IInternalTickerManager>();
        manager.ReleaseDeadNodeResources("node-a", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var scheduler = Substitute.For<ISharpTickerHostScheduler>();
        scheduler.IsRunning.Returns(false);

        await ServiceExtension.ReleaseDeadNodeResourcesOnStartAsync(manager, "node-a", scheduler, new CapturedLogger());

        scheduler.DidNotReceive().Restart();
    }
}
