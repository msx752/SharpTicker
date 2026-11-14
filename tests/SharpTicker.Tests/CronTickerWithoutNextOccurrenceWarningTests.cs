using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Managers;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// #79: the startup check for cron tickers whose expression has no next occurrence is only a check. A store that cannot
/// be read for it is logged and does not stop the startup.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class CronTickerWithoutNextOccurrenceWarningTests
{
    public class FakeTimeTicker : TimeTickerEntity<FakeTimeTicker> { }
    public class FakeCronTicker : CronTickerEntity { }

    private sealed class RecordingLogger : ILogger<InternalTickerManager<FakeTimeTicker, FakeCronTicker>>
    {
        public ConcurrentQueue<(LogLevel Level, string Text, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((logLevel, formatter(state, exception), exception));
    }

    private readonly ITickerPersistenceProvider<FakeTimeTicker, FakeCronTicker> _store =
        Substitute.For<ITickerPersistenceProvider<FakeTimeTicker, FakeCronTicker>>();
    private readonly RecordingLogger _log = new();

    private InternalTickerManager<FakeTimeTicker, FakeCronTicker> Manager()
    {
        var clock = Substitute.For<ITickerClock>();
        clock.UtcNow.Returns(new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc));
        return new InternalTickerManager<FakeTimeTicker, FakeCronTicker>(_store, clock, Substitute.For<ISharpTickerNotificationHubSender>(), _log);
    }

    [Fact]
    public async Task AStoreThatCannotBeRead_IsLoggedAsAWarning_AndTheCheckDoesNotThrow()
    {
        var failure = new InvalidOperationException("the store is not reachable");
        _store.GetAllCronTickerExpressions(Arg.Any<CancellationToken>()).ThrowsAsync(failure);

        await Manager().WarnAboutCronTickersWithoutANextOccurrenceAsync(CancellationToken.None);

        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Same(failure, entry.Exception);
    }

    [Fact]
    public async Task ACancelledStartup_IsNotTurnedIntoAWarning()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        _store.GetAllCronTickerExpressions(Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException(cancelled.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Manager().WarnAboutCronTickersWithoutANextOccurrenceAsync(cancelled.Token));
        Assert.Empty(_log.Entries);
    }

    // The cron library throws for an expression that matches no date; since #194 that is read as no next occurrence.
    [Fact]
    public async Task AnExpressionThatMatchesNoDate_IsNamed()
    {
        var noDate = new CronTickerEntity { Id = Guid.NewGuid(), Function = "NoDate", Expression = "0 0 0 30 2 *" };
        _store.GetAllCronTickerExpressions(Arg.Any<CancellationToken>()).Returns([noDate]);

        await Manager().WarnAboutCronTickersWithoutANextOccurrenceAsync(CancellationToken.None);

        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(noDate.Id.ToString(), entry.Text);
    }
}
