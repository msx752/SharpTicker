using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Managers;
using SharpTicker.Utilities.Models;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// #255: once the token of a pass is cancelled (a stop, or a restart of the scheduler loop), the manager starts no further
/// store call of that pass; the EF Core store would open a connection with the cancelled token, and EF Core 8 and 9 log that
/// open at Error. The pass ends in an OperationCanceledException, as a cancelled store call ends it.
/// </summary>
public class InternalTickerManagerCancelledTokenTests
{
    private static readonly DateTime Now = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> _store =
        Substitute.For<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>, IClaimFanOut>();
    private readonly CancellationTokenSource _cancel = new();
    private readonly InternalTickerManager<TimeTickerEntity, CronTickerEntity> _manager;

    public InternalTickerManagerCancelledTokenTests()
    {
        var clock = Substitute.For<ITickerClock>();
        clock.UtcNow.Returns(Now);
        _manager = new InternalTickerManager<TimeTickerEntity, CronTickerEntity>(_store, clock, Substitute.For<ISharpTickerNotificationHubSender>());

        _store.GetAllCronTickerExpressions(Arg.Any<CancellationToken>()).Returns(Array.Empty<CronTickerEntity>());
        _store.GetEarliestAvailableCronOccurrence(Arg.Any<Guid[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CronTickerOccurrenceEntity<CronTickerEntity>>(null!));
        _store.GetEarliestTimeTickers(Arg.Any<CancellationToken>()).Returns(Array.Empty<TimeTickerEntity>());
    }

    private static CronTickerEntity EverySecond() => new() { Id = Guid.NewGuid(), Function = "Cron", Expression = "* * * * * *" };

    private static async IAsyncEnumerable<T> NothingThenCancel<T>(CancellationTokenSource cancel, [EnumeratorCancellation] CancellationToken _ = default)
    {
        await Task.Yield();
        cancel.Cancel();
        yield break;
    }

    [Fact]
    public async Task APassWhoseTokenIsCancelledAlready_StartsNoStoreCall()
    {
        _cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _manager.GetNextTickers(_cancel.Token));

        await _store.DidNotReceive().GetAllCronTickerExpressions(Arg.Any<CancellationToken>());
        await _store.DidNotReceive().GetEarliestTimeTickers(Arg.Any<CancellationToken>());
    }

    // The cron read and the time read start together; the cancel comes during the first store call of the cron read. Neither
    // its second call nor the time read, which starts after it, is made.
    [Fact]
    public async Task ACancelDuringTheCronRead_StartsNeitherItsNextCallNorTheTimeRead()
    {
        _store.GetAllCronTickerExpressions(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _cancel.Cancel();
            return Task.FromResult(new[] { EverySecond() });
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _manager.GetNextTickers(_cancel.Token));

        await _store.DidNotReceive().GetEarliestAvailableCronOccurrence(Arg.Any<Guid[]>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().GetEarliestTimeTickers(Arg.Any<CancellationToken>());
    }

    // The reads found a cron due; the cancel comes during the last read: the occurrence is not queued.
    [Fact]
    public async Task ACancelAfterTheReads_QueuesNoCronOccurrence()
    {
        _store.GetAllCronTickerExpressions(Arg.Any<CancellationToken>()).Returns(new[] { EverySecond() });
        _store.GetEarliestTimeTickers(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _cancel.Cancel();
            return Task.FromResult(Array.Empty<TimeTickerEntity>());
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _manager.GetNextTickers(_cancel.Token));

        _store.DidNotReceive().QueueCronTickerOccurrences(Arg.Any<(DateTime, InternalManagerContext[])>(), Arg.Any<CancellationToken>());
    }

    // The reads found a time job due; the cancel comes during the read: the job is not queued.
    [Fact]
    public async Task ACancelAfterTheReads_QueuesNoTimeJob()
    {
        _store.GetEarliestTimeTickers(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _cancel.Cancel();
            return Task.FromResult(new[] { new TimeTickerEntity { Id = Guid.NewGuid(), Function = "Time", ExecutionTime = Now.AddSeconds(1) } });
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _manager.GetNextTickers(_cancel.Token));

        _store.DidNotReceive().QueueTimeTickers(Arg.Any<TimeTickerEntity[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AClaimWhoseTokenIsCancelled_IsNotSentToTheStore()
    {
        _cancel.Cancel();
        var job = new InternalFunctionContext { TickerId = Guid.NewGuid(), Type = TickerType.TimeTicker, FunctionName = "Time" };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _manager.TryClaimDueTicker(job, Now, _cancel.Token));

        await _store.DidNotReceive().TryMarkTimeTickerInProgress(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheClaimFanOutWithACancelledToken_IsNotAskedOfTheStore()
    {
        _cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _manager.GetMaxConcurrentClaims(_cancel.Token));

        await ((IClaimFanOut)_store).DidNotReceive().GetMaxConcurrentClaimsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFallbackPassWhoseTokenIsCancelledAlready_StartsNoStoreCall()
    {
        _cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _manager.RunTimedOutTickers(_cancel.Token));

        _store.DidNotReceive().QueueTimedOutTimeTickers(Arg.Any<CancellationToken>());
        _store.DidNotReceive().QueueTimedOutCronTickerOccurrences(Arg.Any<CancellationToken>());
    }

    // The cancel comes while the fallback takes the overdue time jobs: it does not go on to the cron occurrences.
    [Fact]
    public async Task ACancelDuringTheFallbacksTimeJobs_DoesNotReadTheCronOccurrences()
    {
        _store.QueueTimedOutTimeTickers(Arg.Any<CancellationToken>()).Returns(_ => NothingThenCancel<TimeTickerEntity>(_cancel));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _manager.RunTimedOutTickers(_cancel.Token));

        _store.DidNotReceive().QueueTimedOutCronTickerOccurrences(Arg.Any<CancellationToken>());
    }
}
