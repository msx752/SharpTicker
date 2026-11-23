using Microsoft.Extensions.Logging;
using NSubstitute;
using SharpTicker.EntityFrameworkCore.DependencyInjection;
using SharpTicker.EntityFrameworkCore.Infrastructure;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Interfaces;

namespace SharpTicker.EntityFrameworkCore.Tests.DependencyInjection;

/// <summary>
/// The lease service while the host stops (#24): a pass that is cut off by the stop fails with whatever the store throws
/// then (a disposed context pool, a cancelled command). That must not end the service as failed, nor be logged above
/// Debug.
/// </summary>
public class InProgressLeaseStopTests
{
    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public List<(LogLevel Level, string Text)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(Entries);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class Logger(List<(LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (entries)
                    entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    public static TheoryData<string, Exception> Failures => new()
    {
        { "renew", new ObjectDisposedException("DbContextPool") },
        { "renew", new InvalidOperationException("the command was cancelled") },
        { "cancel", new ObjectDisposedException("DbContextPool") },
        { "cancel", new InvalidOperationException("the command was cancelled") }
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task AFailureOfAPassWhileTheHostStops_EndsTheServiceQuietly(string step, Exception failure)
    {
        var store = Substitute.For<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>, IInProgressLease>();
        var lease = (IInProgressLease)store;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The pass reaches the step, the host begins to stop, then the store fails.
        async Task<T> FailOnceTheStopBegan<T>()
        {
            reached.TrySetResult();
            await stopping.Task;
            throw failure;
        }

        if (step == "renew")
            lease.RenewInProgressLeaseAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(_ => FailOnceTheStopBegan<int>());
        else
        {
            lease.RenewInProgressLeaseAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(1);
            lease.CancelExpiredInProgressOfOtherNodesAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                .Returns(_ => FailOnceTheStopBegan<(int, IReadOnlyList<(string, int)>)>());
        }

        var options = new SharpTickerEfCoreOptionBuilder<TimeTickerEntity, CronTickerEntity>().SetInProgressLease(TimeSpan.FromSeconds(1));
        var context = new TickerExecutionContext { RunStartedAt = DateTime.UtcNow };
        var log = new RecordingLoggerFactory();
        var service = new InProgressLeaseBackgroundService<TimeTickerEntity, CronTickerEntity>(store, options, context, log);

        await service.StartAsync(CancellationToken.None);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stop = service.StopAsync(CancellationToken.None);
        stopping.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully, $"The service ended {service.ExecuteTask.Status}: {service.ExecuteTask.Exception}");
        lock (log.Entries)
            Assert.Empty(log.Entries.Where(e => e.Level > LogLevel.Debug).Select(e => $"{e.Level}: {e.Text}"));
    }

    // #255: the renewal ends after the host began to stop; the second step of the pass is not started with the cancelled
    // token (the EF Core store would open a connection with it, which EF Core 8 and 9 log at Error).
    [Fact]
    public async Task AStopAfterTheRenewal_DoesNotStartTheSecondStepOfThePass()
    {
        var store = Substitute.For<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>, IInProgressLease>();
        var lease = (IInProgressLease)store;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lease.RenewInProgressLeaseAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            reached.TrySetResult();
            await stopping.Task;
            return 1;
        });

        var options = new SharpTickerEfCoreOptionBuilder<TimeTickerEntity, CronTickerEntity>().SetInProgressLease(TimeSpan.FromSeconds(1));
        var context = new TickerExecutionContext { RunStartedAt = DateTime.UtcNow };
        var log = new RecordingLoggerFactory();
        var service = new InProgressLeaseBackgroundService<TimeTickerEntity, CronTickerEntity>(store, options, context, log);

        await service.StartAsync(CancellationToken.None);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stop = service.StopAsync(CancellationToken.None);
        stopping.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));

        await lease.DidNotReceive().CancelExpiredInProgressOfOtherNodesAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully, $"The service ended {service.ExecuteTask.Status}: {service.ExecuteTask.Exception}");
        lock (log.Entries)
            Assert.Empty(log.Entries.Where(e => e.Level > LogLevel.Debug).Select(e => $"{e.Level}: {e.Text}"));
    }
}
