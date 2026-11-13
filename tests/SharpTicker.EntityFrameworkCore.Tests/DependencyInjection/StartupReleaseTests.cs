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

    private static readonly DateTime RunStartedAt = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    // The startup release frees what an earlier run of this node left behind; it must not free what this run has claimed
    // since it started, so it goes through the fenced release with the start of this run, never the unfenced one.
    [Fact]
    public async Task TheRelease_IsFencedByTheStartOfThisRun()
    {
        var manager = Substitute.For<IInternalTickerManager>();
        manager.ReleaseEarlierRunResources("node-a", RunStartedAt, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await ServiceExtension.ReleaseDeadNodeResourcesOnStartAsync(manager, "node-a", RunStartedAt, null, new CapturedLogger());

        await manager.Received(1).ReleaseEarlierRunResources("node-a", RunStartedAt, Arg.Any<CancellationToken>());
        await manager.DidNotReceive().ReleaseDeadNodeResources(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailureOfTheRelease_IsLoggedWithItsException_AndDoesNotFaultTheTask()
    {
        var manager = Substitute.For<IInternalTickerManager>();
        manager.ReleaseEarlierRunResources("node-a", RunStartedAt, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("the store refused")));
        var scheduler = Substitute.For<ISharpTickerHostScheduler>();
        scheduler.IsRunning.Returns(true);
        var logger = new CapturedLogger();

        await ServiceExtension.ReleaseDeadNodeResourcesOnStartAsync(manager, "node-a", RunStartedAt, scheduler, logger);

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
        manager.ReleaseEarlierRunResources("node-a", RunStartedAt, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var scheduler = Substitute.For<ISharpTickerHostScheduler>();
        scheduler.IsRunning.Returns(true);
        var logger = new CapturedLogger();

        await ServiceExtension.ReleaseDeadNodeResourcesOnStartAsync(manager, "node-a", RunStartedAt, scheduler, logger);

        scheduler.Received(1).Restart();
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task AReleaseThatSucceeds_LeavesAHostSchedulerThatIsNotRunning_Alone()
    {
        var manager = Substitute.For<IInternalTickerManager>();
        manager.ReleaseEarlierRunResources("node-a", RunStartedAt, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var scheduler = Substitute.For<ISharpTickerHostScheduler>();
        scheduler.IsRunning.Returns(false);

        await ServiceExtension.ReleaseDeadNodeResourcesOnStartAsync(manager, "node-a", RunStartedAt, scheduler, new CapturedLogger());

        scheduler.DidNotReceive().Restart();
    }

    private sealed class JobsOfOtherNodes(Func<IReadOnlyList<(string NodeIdentifier, int Jobs)>> read) : SharpTicker.EntityFrameworkCore.Infrastructure.IJobsOfOtherNodes
    {
        public string? AskedFor { get; private set; }

        public Task<IReadOnlyList<(string NodeIdentifier, int Jobs)>> CountInProgressOfOtherNodesAsync(string nodeIdentifier, CancellationToken cancellationToken = default)
        {
            AskedFor = nodeIdentifier;
            return Task.FromResult(read());
        }
    }

    private static IInternalTickerManager ManagerWhoseReleaseWorks()
    {
        var manager = Substitute.For<IInternalTickerManager>();
        manager.ReleaseEarlierRunResources("node-a", RunStartedAt, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return manager;
    }

    // #24: with the EF Core store alone a job that is InProgress under another node identifier is released only when a node
    // with that identifier starts. A starting node says which identifiers hold such jobs, and how many.
    [Fact]
    public async Task JobsInProgressUnderOtherIdentifiers_AreReportedOnce_AsInformation_WithTheirCounts()
    {
        var others = new JobsOfOtherNodes(() => new[] { ("pod-b", 1), ("pod-a", 3) });
        var logger = new CapturedLogger();

        await ServiceExtension.ReleaseDeadNodeResourcesOnStartAsync(ManagerWhoseReleaseWorks(), "node-a", RunStartedAt, null, logger, others);

        Assert.Equal("node-a", others.AskedFor);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.StartsWith("4 job(s) are InProgress under other node identifiers: pod-a (3), pod-b (1).", entry.Message);
        Assert.Contains("released when a node with the same identifier starts", entry.Message);
    }

    [Fact]
    public async Task NoJobsUnderOtherIdentifiers_NothingIsLogged()
    {
        var logger = new CapturedLogger();

        await ServiceExtension.ReleaseDeadNodeResourcesOnStartAsync(ManagerWhoseReleaseWorks(), "node-a", RunStartedAt, null, logger,
            new JobsOfOtherNodes(() => Array.Empty<(string, int)>()));

        Assert.Empty(logger.Entries);
    }

    // Every replaced container can have left an identifier behind: the ten with the most jobs are named, the rest counted.
    [Fact]
    public async Task ManyOtherIdentifiers_TheTenWithTheMostJobsAreNamed_AndTheRestCounted()
    {
        var many = Enumerable.Range(1, 13).Select(i => ($"pod-{i:00}", i)).ToArray();
        var logger = new CapturedLogger();

        await ServiceExtension.ReleaseDeadNodeResourcesOnStartAsync(ManagerWhoseReleaseWorks(), "node-a", RunStartedAt, null, logger,
            new JobsOfOtherNodes(() => many));

        var entry = Assert.Single(logger.Entries);
        Assert.StartsWith("91 job(s) are InProgress under other node identifiers: pod-13 (13), pod-12 (12), pod-11 (11), pod-10 (10), "
                          + "pod-09 (9), pod-08 (8), pod-07 (7), pod-06 (6), pod-05 (5), pod-04 (4) and 3 more.", entry.Message);
    }

    // The report is extra: when its query fails, the release has happened and the scheduler has been restarted.
    [Fact]
    public async Task TheCountFails_AWarningIsLogged_AndTheReleaseAndTheRestartHaveHappened()
    {
        var manager = ManagerWhoseReleaseWorks();
        var scheduler = Substitute.For<ISharpTickerHostScheduler>();
        scheduler.IsRunning.Returns(true);
        var logger = new CapturedLogger();
        var failure = new InvalidOperationException("the store refused");

        await ServiceExtension.ReleaseDeadNodeResourcesOnStartAsync(manager, "node-a", RunStartedAt, scheduler, logger,
            new JobsOfOtherNodes(() => throw failure));

        await manager.Received(1).ReleaseEarlierRunResources("node-a", RunStartedAt, Arg.Any<CancellationToken>());
        scheduler.Received(1).Restart();
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Same(failure, entry.Exception);
    }

    // The release failed: the jobs under other identifiers are reported all the same, after the error of the release.
    [Fact]
    public async Task TheReleaseFails_TheJobsOfOtherNodesAreStillReported()
    {
        var manager = Substitute.For<IInternalTickerManager>();
        manager.ReleaseEarlierRunResources("node-a", RunStartedAt, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("the store refused")));
        var logger = new CapturedLogger();

        await ServiceExtension.ReleaseDeadNodeResourcesOnStartAsync(manager, "node-a", RunStartedAt, null, logger,
            new JobsOfOtherNodes(() => new[] { ("pod-a", 1) }));

        Assert.Equal(new[] { LogLevel.Error, LogLevel.Information }, logger.Entries.Select(e => e.Level).ToArray());
    }
}
