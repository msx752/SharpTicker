using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Models;
using Xunit.Abstractions;

namespace SharpTicker.EntityFrameworkCore.Tests.Infrastructure;

/// <summary>
/// Provider instances (separate NodeIdentifier) over one shared store and a fake clock, driving the fenced
/// Queued to InProgress claim and the fallback claim the way several nodes would. The scenarios run on SQLite and on
/// a real PostgreSQL through the same bodies. Every call goes through the provider interface, so a custom default
/// body would be caught.
/// </summary>
public abstract class EfCoreClaimMultiNodeTests : IAsyncLifetime
{
    protected static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    protected readonly FakeTickerClock Clock = new(T0);

    protected abstract Task InitializeStoreAsync();

    protected abstract Task DisposeStoreAsync();

    protected abstract TestSharpTickerDbContext CreateContext();

    protected abstract ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> CreateProvider(string nodeIdentifier, ITickerClock clock);

    public Task InitializeAsync() => InitializeStoreAsync();

    public Task DisposeAsync() => DisposeStoreAsync();

    protected abstract ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> CreateProvider(string nodeIdentifier, ITickerClock clock, IInterceptor interceptor);

    protected ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> CreateProvider(string nodeIdentifier)
        => CreateProvider(nodeIdentifier, Clock);

    protected static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    protected async Task<Guid> SeedTimeTicker(
        TickerStatus status = TickerStatus.Idle,
        string? lockHolder = null,
        DateTime? updatedAt = null,
        DateTime? executionTime = null)
    {
        var ticker = NewTimeTicker(status, lockHolder, updatedAt, executionTime);
        await using var context = CreateContext();
        context.Set<TimeTickerEntity>().Add(ticker);
        await context.SaveChangesAsync();
        return ticker.Id;
    }

    protected static TimeTickerEntity NewTimeTicker(
        TickerStatus status = TickerStatus.Idle,
        string? lockHolder = null,
        DateTime? updatedAt = null,
        DateTime? executionTime = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Function = "claim-fn",
            ExecutionTime = executionTime ?? T0,
            Status = status,
            LockHolder = lockHolder,
            LockedAt = lockHolder is null ? null : T0.AddHours(-1),
            CreatedAt = T0.AddHours(-2),
            UpdatedAt = updatedAt ?? T0.AddHours(-1),
            Request = []
        };

    protected async Task<Guid> SeedCronTicker()
    {
        var cron = new CronTickerEntity
        {
            Id = Guid.NewGuid(),
            Function = "claim-cron-fn",
            Expression = "* * * * *",
            CreatedAt = T0.AddHours(-2),
            UpdatedAt = T0.AddHours(-2),
            Request = []
        };
        await using var context = CreateContext();
        context.Set<CronTickerEntity>().Add(cron);
        await context.SaveChangesAsync();
        return cron.Id;
    }

    protected static (DateTime Key, InternalManagerContext[] Items) Slot(Guid cronId)
        => (T0, [new InternalManagerContext(cronId) { FunctionName = "claim-cron-fn", Expression = "* * * * *" }]);

    // Seeds a cron and queues its occurrence for the T0 slot on the given node.
    protected async Task<Guid> QueueOccurrenceOn(ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> node)
    {
        var cronId = await SeedCronTicker();
        return Assert.Single(await Drain(node.QueueCronTickerOccurrences(Slot(cronId), CancellationToken.None))).Id;
    }

    protected async Task<TimeTickerEntity> ReadTimeTicker(Guid id)
    {
        await using var context = CreateContext();
        return await context.Set<TimeTickerEntity>().AsNoTracking().SingleAsync(x => x.Id == id);
    }

    protected async Task<CronTickerOccurrenceEntity<CronTickerEntity>> ReadOccurrence(Guid id)
    {
        await using var context = CreateContext();
        return await context.Set<CronTickerOccurrenceEntity<CronTickerEntity>>().AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private async Task<Guid> QueueTimeTickerOn(ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> node)
    {
        var id = await SeedTimeTicker();
        var window = await node.GetEarliestTimeTickers(CancellationToken.None);
        Assert.Single(await Drain(node.QueueTimeTickers(window.Where(x => x.Id == id).ToArray(), CancellationToken.None)));
        return id;
    }

    public static TheoryData<TickerStatus> NonQueuedStatuses => new()
    {
        TickerStatus.Idle, TickerStatus.InProgress, TickerStatus.Done, TickerStatus.DueDone,
        TickerStatus.Failed, TickerStatus.Cancelled, TickerStatus.Skipped
    };

    #region TryMark

    [Fact]
    public async Task TryMark_OwnQueuedRow_ReturnsTrueAndWritesInProgress_TimeTicker()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueTimeTickerOn(nodeA);
        Clock.Advance(TimeSpan.FromMilliseconds(300));

        Assert.True(await nodeA.TryMarkTimeTickerInProgress(id, Clock.NextClaimMarker()));

        var row = await ReadTimeTicker(id);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("node-A", row.LockHolder);
        Assert.Equal(T0, row.LockedAt);
        Assert.Equal(Clock.UtcNow, row.UpdatedAt);
    }

    [Fact]
    public async Task TryMark_OwnQueuedRow_ReturnsTrueAndWritesInProgress_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueOccurrenceOn(nodeA);
        Clock.Advance(TimeSpan.FromMilliseconds(300));

        Assert.True(await nodeA.TryMarkCronOccurrenceInProgress(id, Clock.NextClaimMarker()));

        var row = await ReadOccurrence(id);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("node-A", row.LockHolder);
        Assert.Equal(T0, row.LockedAt);
        Assert.Equal(Clock.UtcNow, row.UpdatedAt);
    }

    [Fact]
    public async Task TryMark_RowClaimedByFallbackOfAnotherNode_ReturnsFalseAndLeavesRow_TimeTicker()
    {
        var nodeA = CreateProvider("node-A");
        var nodeB = CreateProvider("node-B");
        var id = await QueueTimeTickerOn(nodeA);
        Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Contains(await Drain(nodeB.QueueTimedOutTimeTickers(CancellationToken.None)), x => x.Id == id);
        var before = await ReadTimeTicker(id);

        Assert.False(await nodeA.TryMarkTimeTickerInProgress(id, Clock.NextClaimMarker()));

        var after = await ReadTimeTicker(id);
        Assert.Equal("node-B", after.LockHolder);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task TryMark_RowClaimedByFallbackOfAnotherNode_ReturnsFalseAndLeavesRow_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var nodeB = CreateProvider("node-B");
        var id = await QueueOccurrenceOn(nodeA);
        Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Contains(await Drain(nodeB.QueueTimedOutCronTickerOccurrences(CancellationToken.None)), x => x.Id == id);
        var before = await ReadOccurrence(id);

        Assert.False(await nodeA.TryMarkCronOccurrenceInProgress(id, Clock.NextClaimMarker()));

        var after = await ReadOccurrence(id);
        Assert.Equal("node-B", after.LockHolder);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    // The status clause alone cannot tell these apart: the row is Queued again, but for another node. Only the
    // holder clause stops the first node's late write, and the new holder's own write must still go through.
    [Fact]
    public async Task TryMark_RowRequeuedByAnotherNode_ReturnsFalseUntilItsHolderMarks_TimeTicker()
    {
        var nodeA = CreateProvider("node-A");
        var nodeB = CreateProvider("node-B");
        var id = await QueueTimeTickerOn(nodeA);
        await nodeA.ReleaseAcquiredTimeTickers([id], CancellationToken.None);
        var window = await nodeB.GetEarliestTimeTickers(CancellationToken.None);
        Assert.Single(await Drain(nodeB.QueueTimeTickers(window.Where(x => x.Id == id).ToArray(), CancellationToken.None)));
        var before = await ReadTimeTicker(id);

        Assert.False(await nodeA.TryMarkTimeTickerInProgress(id, Clock.NextClaimMarker()));

        var untouched = await ReadTimeTicker(id);
        Assert.Equal(TickerStatus.Queued, untouched.Status);
        Assert.Equal("node-B", untouched.LockHolder);
        Assert.Equal(before.UpdatedAt, untouched.UpdatedAt);
        Assert.True(await nodeB.TryMarkTimeTickerInProgress(id, Clock.NextClaimMarker()));
        Assert.Equal(TickerStatus.InProgress, (await ReadTimeTicker(id)).Status);
    }

    [Fact]
    public async Task TryMark_RowRequeuedByAnotherNode_ReturnsFalseUntilItsHolderMarks_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var nodeB = CreateProvider("node-B");
        var cronId = await SeedCronTicker();
        var id = Assert.Single(await Drain(nodeA.QueueCronTickerOccurrences(Slot(cronId), CancellationToken.None))).Id;
        await nodeA.ReleaseAcquiredCronTickerOccurrences([id], CancellationToken.None);
        var requeue = Slot(cronId);
        requeue.Items[0].NextCronOccurrence = new NextCronOccurrence(id, T0);
        Assert.Single(await Drain(nodeB.QueueCronTickerOccurrences(requeue, CancellationToken.None)));
        var before = await ReadOccurrence(id);

        Assert.False(await nodeA.TryMarkCronOccurrenceInProgress(id, Clock.NextClaimMarker()));

        var untouched = await ReadOccurrence(id);
        Assert.Equal(TickerStatus.Queued, untouched.Status);
        Assert.Equal("node-B", untouched.LockHolder);
        Assert.Equal(before.UpdatedAt, untouched.UpdatedAt);
        Assert.True(await nodeB.TryMarkCronOccurrenceInProgress(id, Clock.NextClaimMarker()));
        Assert.Equal(TickerStatus.InProgress, (await ReadOccurrence(id)).Status);
    }

    // Finished rows keep the lock holder, so even the owning node must not move them back to InProgress.
    [Theory]
    [MemberData(nameof(NonQueuedStatuses))]
    public async Task TryMark_RowInStatus_ReturnsFalse_TimeTicker(TickerStatus status)
    {
        var nodeA = CreateProvider("node-A");
        var id = await SeedTimeTicker(status, lockHolder: "node-A");
        var before = await ReadTimeTicker(id);

        Assert.False(await nodeA.TryMarkTimeTickerInProgress(id, Clock.NextClaimMarker()));

        var after = await ReadTimeTicker(id);
        Assert.Equal(status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Theory]
    [MemberData(nameof(NonQueuedStatuses))]
    public async Task TryMark_RowInStatus_ReturnsFalse_CronOccurrence(TickerStatus status)
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueOccurrenceOn(nodeA);
        await using (var context = CreateContext())
        {
            await context.Set<CronTickerOccurrenceEntity<CronTickerEntity>>()
                .Where(x => x.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status));
        }
        var before = await ReadOccurrence(id);

        Assert.False(await nodeA.TryMarkCronOccurrenceInProgress(id, Clock.NextClaimMarker()));

        var after = await ReadOccurrence(id);
        Assert.Equal(status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task TryMark_MissingRow_ReturnsFalse()
    {
        var nodeA = CreateProvider("node-A");

        Assert.False(await nodeA.TryMarkTimeTickerInProgress(Guid.NewGuid(), Clock.NextClaimMarker()));
        Assert.False(await nodeA.TryMarkCronOccurrenceInProgress(Guid.NewGuid(), Clock.NextClaimMarker()));
    }

    #endregion

    #region Immediate acquire

    // R6: a row this node's own fallback took earlier, or one another node holds, is not won by the call and must not
    // come back, or the row is handed to a worker twice.
    [Fact]
    public async Task AcquireImmediate_Batch_ReturnsOnlyRowsThisCallAcquired()
    {
        var node = CreateProvider("node-A");
        var heldByOther = await QueueTimeTickerOn(CreateProvider("node-B"));
        var takenByFallback = await SeedTimeTicker(executionTime: T0.AddSeconds(-5));
        var fresh = await SeedTimeTicker(executionTime: T0.AddHours(1));
        var freshToo = await SeedTimeTicker(executionTime: T0.AddHours(1));
        Assert.Equal(takenByFallback, Assert.Single(await Drain(node.QueueTimedOutTimeTickers(CancellationToken.None))).Id);
        var fallbackRow = await ReadTimeTicker(takenByFallback);
        var otherRow = await ReadTimeTicker(heldByOther);

        var acquired = await node.AcquireImmediateTimeTickersAsync([takenByFallback, heldByOther, fresh, freshToo], CancellationToken.None);

        Assert.Equal(new[] { fresh, freshToo }.Order(), acquired.Select(x => x.Id).Order());
        Assert.Equal(fallbackRow.UpdatedAt, (await ReadTimeTicker(takenByFallback)).UpdatedAt);
        Assert.Equal(otherRow.UpdatedAt, (await ReadTimeTicker(heldByOther)).UpdatedAt);
        Assert.Equal("node-B", (await ReadTimeTicker(heldByOther)).LockHolder);
        foreach (var id in new[] { fresh, freshToo })
        {
            var row = await ReadTimeTicker(id);
            Assert.Equal(TickerStatus.InProgress, row.Status);
            Assert.Equal("node-A", row.LockHolder);
        }
    }

    [Fact]
    public async Task AcquireImmediate_NothingWon_ReturnsEmpty()
    {
        var node = CreateProvider("node-A");
        var heldByOther = await QueueTimeTickerOn(CreateProvider("node-B"));

        Assert.Empty(await node.AcquireImmediateTimeTickersAsync([heldByOther, Guid.NewGuid()], CancellationToken.None));
        Assert.Equal(TickerStatus.Queued, (await ReadTimeTicker(heldByOther)).Status);
    }

    [Fact]
    public async Task AcquireImmediate_SingleId_ReturnsTheRow()
    {
        var node = CreateProvider("node-A");
        var id = await SeedTimeTicker();

        var acquired = await node.AcquireImmediateTimeTickersAsync([id], CancellationToken.None);

        Assert.Equal(id, Assert.Single(acquired).Id);
        Assert.Equal(TickerStatus.InProgress, (await ReadTimeTicker(id)).Status);
    }

    // The caller's token fires between two row writes. The rows won so far are InProgress and must come back to be
    // dispatched; the rest must stay Idle. The interceptor cancels right after the first write.
    [Fact]
    public async Task AcquireImmediate_CancelledMidBatch_ReturnsWonRowsAndLeavesTheRestIdle()
    {
        using var cts = new CancellationTokenSource();
        var node = CreateProvider("node-A", Clock, new CancelAfterFirstUpdate(cts));
        var ids = new[] { await SeedTimeTicker(), await SeedTimeTicker(), await SeedTimeTicker() };

        var acquired = await node.AcquireImmediateTimeTickersAsync(ids, cts.Token);

        Assert.Equal(ids[0], Assert.Single(acquired).Id);
        Assert.Equal(TickerStatus.InProgress, (await ReadTimeTicker(ids[0])).Status);
        foreach (var id in ids.Skip(1))
        {
            var row = await ReadTimeTicker(id);
            Assert.Equal(TickerStatus.Idle, row.Status);
            Assert.Null(row.LockHolder);
        }
    }

    [Fact]
    public async Task AcquireImmediate_CancelledMidBatch_ReturnsWonRowsAndLeavesTheRestIdle_CronOccurrence()
    {
        var seed = CreateProvider("node-A");
        var ids = new[] { await QueueOccurrenceOn(seed), await QueueOccurrenceOn(seed), await QueueOccurrenceOn(seed) };
        await seed.ReleaseAcquiredCronTickerOccurrences(ids, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var node = CreateProvider("node-A", Clock, new CancelAfterFirstUpdate(cts));

        var acquired = await node.AcquireImmediateCronOccurrencesAsync(ids, cts.Token);

        Assert.Equal(ids[0], Assert.Single(acquired).Id);
        Assert.Equal(TickerStatus.InProgress, (await ReadOccurrence(ids[0])).Status);
        foreach (var id in ids.Skip(1))
        {
            var row = await ReadOccurrence(id);
            Assert.Equal(TickerStatus.Idle, row.Status);
            Assert.Null(row.LockHolder);
        }
    }

    private sealed class CancelAfterFirstUpdate(CancellationTokenSource cts) : DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            cts.Cancel();
            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    // The store fails once after the call has won rows: either the write of a later row or the final read. Nobody
    // gets the won rows to run, so they must be put back (Idle, no holder) for the fallback, and the call must throw.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquireImmediate_StoreFailsAfterAWin_PutsTheWonRowsBackAndThrows(bool readFails)
    {
        var node = CreateProvider("node-A", Clock, readFails ? FailOnce.FirstRead() : FailOnce.NonQuery(2));
        var ids = new[] { await SeedTimeTicker(), await SeedTimeTicker(), await SeedTimeTicker() };

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.AcquireImmediateTimeTickersAsync(ids, CancellationToken.None));

        foreach (var id in ids)
        {
            var row = await ReadTimeTicker(id);
            Assert.Equal(TickerStatus.Idle, row.Status);
            Assert.Null(row.LockHolder);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquireImmediate_StoreFailsAfterAWin_PutsTheWonRowsBackAndThrows_CronOccurrence(bool readFails)
    {
        var seed = CreateProvider("node-A");
        var ids = new[] { await QueueOccurrenceOn(seed), await QueueOccurrenceOn(seed), await QueueOccurrenceOn(seed) };
        await seed.ReleaseAcquiredCronTickerOccurrences(ids, CancellationToken.None);
        var node = CreateProvider("node-A", Clock, readFails ? FailOnce.FirstRead() : FailOnce.NonQuery(2));

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.AcquireImmediateCronOccurrencesAsync(ids, CancellationToken.None));

        foreach (var id in ids)
        {
            var row = await ReadOccurrence(id);
            Assert.Equal(TickerStatus.Idle, row.Status);
            Assert.Null(row.LockHolder);
        }
    }

    // A claim's write reaches the store and lands, then its reply is lost (the call throws). The row is InProgress for this
    // node, but the caller has nothing to run, so the row must be put back like a row that was won. Covers run-now on time
    // tickers and cron occurrences, and the fallback on both.
    [Theory]
    [InlineData("immediate-time")]
    [InlineData("immediate-cron")]
    [InlineData("fallback-time")]
    [InlineData("fallback-cron")]
    public async Task AClaimWhoseReplyIsLost_IsPutBack(string path)
    {
        var cron = path.EndsWith("cron");
        var seed = CreateProvider("node-A");
        Guid id;
        if (cron)
        {
            id = await QueueOccurrenceOn(seed);
            if (path.StartsWith("immediate"))
                await seed.ReleaseAcquiredCronTickerOccurrences([id], CancellationToken.None);
        }
        else
        {
            id = await SeedTimeTicker();
        }
        Clock.Advance(TimeSpan.FromSeconds(2));
        var node = CreateProvider("node-A", Clock, new LandThenFailOnce());

        Func<Task> claim = path switch
        {
            "immediate-time" => () => node.AcquireImmediateTimeTickersAsync([id], CancellationToken.None),
            "immediate-cron" => () => node.AcquireImmediateCronOccurrencesAsync([id], CancellationToken.None),
            "fallback-time" => () => Drain(node.QueueTimedOutTimeTickers(CancellationToken.None)),
            _ => () => Drain(node.QueueTimedOutCronTickerOccurrences(CancellationToken.None))
        };
        // PostgreSQL runs with a retrying execution strategy, which wraps the failure.
        await Assert.ThrowsAnyAsync<Exception>(claim);

        if (cron)
        {
            var row = await ReadOccurrence(id);
            Assert.Equal(TickerStatus.Idle, row.Status);
            Assert.Null(row.LockHolder);
        }
        else
        {
            var row = await ReadTimeTicker(id);
            Assert.Equal(TickerStatus.Idle, row.Status);
            Assert.Null(row.LockHolder);
        }
    }

    // As above, and the store's own put-back fails too (the store is still failing). The row stays InProgress for this node;
    // the exception names it with the marker of the claim and its type, and a put-back with that marker frees it once the
    // store answers. That is what the scheduler loop does with the row (candidate row 50).
    [Theory]
    [InlineData("immediate-time")]
    [InlineData("immediate-cron")]
    [InlineData("fallback-time")]
    [InlineData("fallback-cron")]
    public async Task AClaimWhoseReplyIsLost_AndItsPutBackFails_NamesTheRowSoItCanBePutBackLater(string path)
    {
        var cron = path.EndsWith("cron");
        var seed = CreateProvider("node-A");
        Guid id;
        if (cron)
        {
            id = await QueueOccurrenceOn(seed);
            if (path.StartsWith("immediate"))
                await seed.ReleaseAcquiredCronTickerOccurrences([id], CancellationToken.None);
        }
        else
        {
            id = await SeedTimeTicker();
        }
        Clock.Advance(TimeSpan.FromSeconds(2));
        var node = CreateProvider("node-A", Clock, new LandThenFailOnce(failPutBackToo: true));

        Func<Task> claim = path switch
        {
            "immediate-time" => () => node.AcquireImmediateTimeTickersAsync([id], CancellationToken.None),
            "immediate-cron" => () => node.AcquireImmediateCronOccurrencesAsync([id], CancellationToken.None),
            "fallback-time" => () => Drain(node.QueueTimedOutTimeTickers(CancellationToken.None)),
            _ => () => Drain(node.QueueTimedOutCronTickerOccurrences(CancellationToken.None))
        };
        // PostgreSQL runs with a retrying execution strategy, which may wrap the failure.
        var thrown = await Assert.ThrowsAnyAsync<Exception>(claim);

        var failed = Assert.Single(ClaimPutBackFailedException.FindIn(thrown));
        var row = Assert.Single(failed.NotPutBack);
        Assert.Equal(id, row.Id);
        Assert.Equal(cron ? TickerType.CronTickerOccurrence : TickerType.TimeTicker, row.Type);

        var healthy = CreateProvider("node-A");
        if (cron)
        {
            Assert.Equal(TickerStatus.InProgress, (await ReadOccurrence(id)).Status);
            Assert.True(await healthy.TryReleaseCronOccurrenceClaim(id, row.ClaimMarker, CancellationToken.None));
            Assert.Equal(TickerStatus.Idle, (await ReadOccurrence(id)).Status);
        }
        else
        {
            Assert.Equal(TickerStatus.InProgress, (await ReadTimeTicker(id)).Status);
            Assert.True(await healthy.TryReleaseTimeTickerClaim(id, row.ClaimMarker, CancellationToken.None));
            Assert.Equal(TickerStatus.Idle, (await ReadTimeTicker(id)).Status);
        }
    }

    // The first write runs and lands, then the call throws as if its reply was lost; every later command runs normally.
    // With failPutBackToo, the next write (the put-back) fails before it reaches the store.
    private sealed class LandThenFailOnce(bool failPutBackToo = false) : DbCommandInterceptor
    {
        private bool _failed;
        private bool _putBackFailed;

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (!_failed)
            {
                _failed = true;
                throw new TimeoutException("the reply of the claim was lost");
            }
            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (failPutBackToo && _failed && !_putBackFailed)
            {
                _putBackFailed = true;
                throw new InvalidOperationException("the store failed again, before the put-back reached it");
            }
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    // Throws once, on the n-th write or on the first read; every later command runs normally.
    private sealed class FailOnce : DbCommandInterceptor
    {
        private readonly int _failAtNonQuery;
        private readonly bool _failFirstRead;
        private int _nonQueries;
        private bool _failed;

        private FailOnce(int failAtNonQuery, bool failFirstRead)
        {
            _failAtNonQuery = failAtNonQuery;
            _failFirstRead = failFirstRead;
        }

        public static FailOnce NonQuery(int n) => new(n, false);

        public static FailOnce FirstRead() => new(0, true);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_failed && ++_nonQueries == _failAtNonQuery)
                Fail();
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!_failed && _failFirstRead)
                Fail();
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Fail()
        {
            _failed = true;
            throw new InvalidOperationException("store failed");
        }
    }

    [Fact]
    public async Task AcquireImmediate_DuplicateIds_ReturnsTheRowOnce()
    {
        var node = CreateProvider("node-A");
        var id = await SeedTimeTicker();

        var acquired = await node.AcquireImmediateTimeTickersAsync([id, id], CancellationToken.None);

        Assert.Equal(id, Assert.Single(acquired).Id);
    }

    #endregion

    #region Late loop, clock skew, shared identifier

    // R1: the loop that queued a row wakes late; the fallback (this node's own or another's) has already claimed it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateLoop_LosesToFallback_JobClaimedOnce(bool fallbackOnSameNode)
    {
        var nodeA = CreateProvider("node-A");
        var fallbackNode = fallbackOnSameNode ? nodeA : CreateProvider("node-B");
        var id = await QueueTimeTickerOn(nodeA);

        Clock.Advance(TimeSpan.FromSeconds(2));
        var claimedByFallback = await Drain(fallbackNode.QueueTimedOutTimeTickers(CancellationToken.None));
        Clock.Advance(TimeSpan.FromMilliseconds(500));
        var lateLoopWon = await nodeA.TryMarkTimeTickerInProgress(id, Clock.NextClaimMarker());

        Assert.Equal(id, Assert.Single(claimedByFallback).Id);
        Assert.False(lateLoopWon);
        var row = await ReadTimeTicker(id);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal(fallbackOnSameNode ? "node-A" : "node-B", row.LockHolder);
        Assert.Equal(T0.AddSeconds(2), row.UpdatedAt);
    }

    [Fact]
    public async Task LateLoop_LosesToFallback_JobClaimedOnce_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var nodeB = CreateProvider("node-B");
        var id = await QueueOccurrenceOn(nodeA);

        Clock.Advance(TimeSpan.FromSeconds(2));
        var claimedByFallback = await Drain(nodeB.QueueTimedOutCronTickerOccurrences(CancellationToken.None));
        Clock.Advance(TimeSpan.FromMilliseconds(500));
        var lateLoopWon = await nodeA.TryMarkCronOccurrenceInProgress(id, Clock.NextClaimMarker());

        Assert.Equal(id, Assert.Single(claimedByFallback).Id);
        Assert.False(lateLoopWon);
        var row = await ReadOccurrence(id);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("node-B", row.LockHolder);
    }

    // The fallback scans a snapshot, then claims each row. A row another node moved to InProgress between the two, on
    // a clock that is behind (so its UpdatedAt is earlier), still passed the old UpdatedAt comparison.
    [Fact]
    public async Task Fallback_DoesNotClaimInProgressRow_WhenItsUpdatedAtIsEarlier_TimeTicker()
    {
        var nodeFallback = CreateProvider("node-fallback");
        var nodeBehind = CreateProvider("node-behind", new FakeTickerClock(T0.AddHours(-2)));
        var first = await SeedTimeTicker();
        var second = await SeedTimeTicker();
        Clock.Advance(TimeSpan.FromSeconds(2));

        var claimed = new List<Guid>();
        await foreach (var row in nodeFallback.QueueTimedOutTimeTickers(CancellationToken.None))
        {
            claimed.Add(row.Id);
            if (claimed.Count != 1)
                continue;

            var other = claimed[0] == first ? second : first;
            var inProgress = new InternalFunctionContext().SetProperty(x => x.Status, TickerStatus.InProgress);
            await nodeBehind.UpdateTimeTickersWithUnifiedContext([other], inProgress, CancellationToken.None);
        }

        var untouched = await ReadTimeTicker(claimed[0] == first ? second : first);
        Assert.Single(claimed);
        Assert.Equal(TickerStatus.InProgress, untouched.Status);
        Assert.NotEqual("node-fallback", untouched.LockHolder);
    }

    // The cron compare-and-set is "UpdatedAt ==", so the row only passes it when the behind clock writes the same
    // UpdatedAt the fallback read (both nodes queued at that clock value).
    [Fact]
    public async Task Fallback_DoesNotClaimInProgressRow_WhenItsUpdatedAtIsEarlier_CronOccurrence()
    {
        var nodeFallback = CreateProvider("node-fallback");
        var nodeBehind = CreateProvider("node-behind", new FakeTickerClock(T0.AddHours(-2)));
        var first = await QueueOccurrenceOn(nodeBehind);
        var second = await QueueOccurrenceOn(nodeBehind);
        Clock.Advance(TimeSpan.FromSeconds(2));

        var claimed = new List<Guid>();
        await foreach (var row in nodeFallback.QueueTimedOutCronTickerOccurrences(CancellationToken.None))
        {
            claimed.Add(row.Id);
            if (claimed.Count != 1)
                continue;

            var other = claimed[0] == first ? second : first;
            var inProgress = new InternalFunctionContext().SetProperty(x => x.Status, TickerStatus.InProgress);
            await nodeBehind.UpdateCronTickerOccurrencesWithUnifiedContext([other], inProgress, CancellationToken.None);
        }

        var untouched = await ReadOccurrence(claimed[0] == first ? second : first);
        Assert.Single(claimed);
        Assert.Equal(TickerStatus.InProgress, untouched.Status);
        Assert.NotEqual("node-fallback", untouched.LockHolder);
    }

    // R7: the main loop passes the UpdatedAt it read. A row another node holds (or already runs) can carry the very
    // same UpdatedAt, and the old compare-and-set took it over.
    [Theory]
    [InlineData(TickerStatus.Queued)]
    [InlineData(TickerStatus.InProgress)]
    public async Task QueueTimeTickers_DoesNotTakeRowHeldByAnotherNode_WhenUpdatedAtMatches(TickerStatus heldStatus)
    {
        var nodeA = CreateProvider("node-A");
        var seeded = NewTimeTicker(heldStatus, lockHolder: "node-B");
        await using (var context = CreateContext())
        {
            context.Set<TimeTickerEntity>().Add(seeded);
            await context.SaveChangesAsync();
        }
        var staleRead = new TimeTickerEntity { Id = seeded.Id, UpdatedAt = seeded.UpdatedAt, Function = seeded.Function };

        var queued = await Drain(nodeA.QueueTimeTickers([staleRead], CancellationToken.None));

        Assert.Empty(queued);
        var row = await ReadTimeTicker(seeded.Id);
        Assert.Equal("node-B", row.LockHolder);
        Assert.Equal(heldStatus, row.Status);
        Assert.Equal(seeded.UpdatedAt, row.UpdatedAt);
    }

    // R3: the pod with the faster clock sees the row as overdue and runs it; the owner, a second late, must lose.
    [Fact]
    public async Task ClockSkew_FallbackOfFasterNodeWins_LateOwnerLoses()
    {
        var slowClock = new FakeTickerClock(T0);
        var fastClock = new FakeTickerClock(T0.AddSeconds(2));
        var owner = CreateProvider("node-slow", slowClock);
        var faster = CreateProvider("node-fast", fastClock);
        var id = await QueueTimeTickerOn(owner);

        var claimedByFaster = await Drain(faster.QueueTimedOutTimeTickers(CancellationToken.None));
        slowClock.Advance(TimeSpan.FromMilliseconds(300));
        var lateOwnerWon = await owner.TryMarkTimeTickerInProgress(id, Clock.NextClaimMarker());

        Assert.Equal(id, Assert.Single(claimedByFaster).Id);
        Assert.False(lateOwnerWon);
        var row = await ReadTimeTicker(id);
        Assert.Equal("node-fast", row.LockHolder);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal(T0.AddSeconds(2), row.UpdatedAt);
    }

    // Two pods that start with the same identifier: Status = Queued is the token, so only one of them gets a row.
    [Fact]
    public async Task TwoNodesWithSameIdentifier_OneWins()
    {
        var first = CreateProvider("node-X");
        var twin = CreateProvider("node-X");
        var markedFirst = await QueueTimeTickerOn(first);
        var fallbackFirst = await QueueTimeTickerOn(first);
        Clock.Advance(TimeSpan.FromSeconds(2));

        // Row 1: one TryMark wins, the twin's fallback then finds nothing to claim for it.
        var tryMarkWon = await first.TryMarkTimeTickerInProgress(markedFirst, Clock.NextClaimMarker());
        var twinTryMarkWon = await twin.TryMarkTimeTickerInProgress(markedFirst, Clock.NextClaimMarker());
        // Row 2: the twin's fallback wins, the other's TryMark then loses.
        var claimedByTwin = await Drain(twin.QueueTimedOutTimeTickers(CancellationToken.None));
        var lateTryMarkWon = await first.TryMarkTimeTickerInProgress(fallbackFirst, Clock.NextClaimMarker());

        Assert.True(tryMarkWon);
        Assert.False(twinTryMarkWon);
        Assert.DoesNotContain(claimedByTwin, x => x.Id == markedFirst);
        Assert.Equal(fallbackFirst, Assert.Single(claimedByTwin).Id);
        Assert.False(lateTryMarkWon);
        Assert.Equal(TickerStatus.InProgress, (await ReadTimeTicker(markedFirst)).Status);
        Assert.Equal(TickerStatus.InProgress, (await ReadTimeTicker(fallbackFirst)).Status);
    }

    // The slot is claimed by the unique (ExecutionTime, CronTickerId) insert: one occurrence, one node.
    [Fact]
    public async Task CronSlot_TwoNodes_OneOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var nodeB = CreateProvider("node-B");
        var cronId = await SeedCronTicker();

        var byA = await Drain(nodeA.QueueCronTickerOccurrences(Slot(cronId), CancellationToken.None));
        var byB = await Drain(nodeB.QueueCronTickerOccurrences(Slot(cronId), CancellationToken.None));

        Assert.Single(byA);
        Assert.Empty(byB);
        await using var context = CreateContext();
        var rows = await context.Set<CronTickerOccurrenceEntity<CronTickerEntity>>().AsNoTracking()
            .Where(x => x.CronTickerId == cronId).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("node-A", row.LockHolder);
        Assert.Equal(TickerStatus.Queued, row.Status);
    }

    #endregion

    #region Claim fan-out

    protected abstract int ExpectedClaimFanOut { get; }

    // SQLite allows one writer and a single-connection setup runs one command at a time, so its rows are claimed one by
    // one; server databases take the scheduler's concurrent claims.
    [Fact]
    public async Task ClaimFanOut_FollowsTheDatabase()
    {
        var fanOut = (IClaimFanOut)CreateProvider("node-A");

        Assert.Equal(ExpectedClaimFanOut, await fanOut.GetMaxConcurrentClaimsAsync());
    }

    #endregion

    #region Putting back a claim whose job never started

    // A claim the node won but never handed to a worker (stop, freeze, a refused queue, a claim call that failed after its
    // write landed) is put back so the job still runs, once, through the fallback.
    [Fact]
    public async Task ReleaseClaim_OwnClaim_PutsTheRowBackForTheFallback_TimeTicker()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueTimeTickerOn(nodeA);
        var marker = Clock.NextClaimMarker();
        Assert.True(await nodeA.TryMarkTimeTickerInProgress(id, marker));
        Clock.Advance(TimeSpan.FromMilliseconds(40));

        Assert.True(await nodeA.TryReleaseTimeTickerClaim(id, marker));

        var row = await ReadTimeTicker(id);
        Assert.Equal(TickerStatus.Idle, row.Status);
        Assert.Null(row.LockHolder);
        Assert.Null(row.LockedAt);
        Assert.Equal(Clock.UtcNow, row.UpdatedAt);
        Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Contains(await Drain(CreateProvider("node-B").QueueTimedOutTimeTickers(CancellationToken.None)), x => x.Id == id);
    }

    [Fact]
    public async Task ReleaseClaim_OwnClaim_PutsTheRowBackForTheFallback_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueOccurrenceOn(nodeA);
        var marker = Clock.NextClaimMarker();
        Assert.True(await nodeA.TryMarkCronOccurrenceInProgress(id, marker));

        Assert.True(await nodeA.TryReleaseCronOccurrenceClaim(id, marker));

        var row = await ReadOccurrence(id);
        Assert.Equal(TickerStatus.Idle, row.Status);
        Assert.Null(row.LockHolder);
        Assert.Null(row.LockedAt);
    }

    // The loop lost the row to this node's own fallback, which runs it now. The loop's put-back carries the loop's marker,
    // which the fallback's write does not have, so the running job is left alone even within the same millisecond.
    [Fact]
    public async Task ReleaseClaim_RowTakenByTheSameNodesFallback_IsLeftRunning_TimeTicker()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueTimeTickerOn(nodeA);
        Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Contains(await Drain(nodeA.QueueTimedOutTimeTickers(CancellationToken.None)), x => x.Id == id);
        var loopMarker = Clock.NextClaimMarker();
        Assert.False(await nodeA.TryMarkTimeTickerInProgress(id, loopMarker));
        var before = await ReadTimeTicker(id);

        Assert.False(await nodeA.TryReleaseTimeTickerClaim(id, loopMarker));

        var after = await ReadTimeTicker(id);
        Assert.Equal(TickerStatus.InProgress, after.Status);
        Assert.Equal("node-A", after.LockHolder);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task ReleaseClaim_RowTakenByTheSameNodesFallback_IsLeftRunning_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueOccurrenceOn(nodeA);
        Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Contains(await Drain(nodeA.QueueTimedOutCronTickerOccurrences(CancellationToken.None)), x => x.Id == id);
        var loopMarker = Clock.NextClaimMarker();
        Assert.False(await nodeA.TryMarkCronOccurrenceInProgress(id, loopMarker));

        Assert.False(await nodeA.TryReleaseCronOccurrenceClaim(id, loopMarker));

        var after = await ReadOccurrence(id);
        Assert.Equal(TickerStatus.InProgress, after.Status);
        Assert.Equal("node-A", after.LockHolder);
    }

    // The fallback and run-now hand back each row they claimed with the marker their write gave it, so a job that never
    // starts can be put back with that marker.
    [Fact]
    public async Task FallbackClaim_ReturnsItsMarker_SoItCanBePutBack_TimeTicker()
    {
        var nodeB = CreateProvider("node-B");
        var id = await SeedTimeTicker();
        Clock.Advance(TimeSpan.FromSeconds(2));

        var taken = Assert.Single(await Drain(nodeB.QueueTimedOutTimeTickers(CancellationToken.None)), x => x.Id == id);

        Assert.Equal((await ReadTimeTicker(id)).UpdatedAt, taken.UpdatedAt);
        Assert.True(await nodeB.TryReleaseTimeTickerClaim(id, taken.UpdatedAt));
        Assert.Equal(TickerStatus.Idle, (await ReadTimeTicker(id)).Status);
    }

    [Fact]
    public async Task FallbackClaim_ReturnsItsMarker_SoItCanBePutBack_CronOccurrence()
    {
        var nodeB = CreateProvider("node-B");
        var id = await QueueOccurrenceOn(CreateProvider("node-A"));
        Clock.Advance(TimeSpan.FromSeconds(2));

        var taken = Assert.Single(await Drain(nodeB.QueueTimedOutCronTickerOccurrences(CancellationToken.None)), x => x.Id == id);

        Assert.Equal((await ReadOccurrence(id)).UpdatedAt, taken.UpdatedAt);
        Assert.True(await nodeB.TryReleaseCronOccurrenceClaim(id, taken.UpdatedAt));
        Assert.Equal(TickerStatus.Idle, (await ReadOccurrence(id)).Status);
    }

    [Fact]
    public async Task RunNowClaim_ReturnsItsMarker_SoItCanBePutBack_TimeTicker()
    {
        var nodeA = CreateProvider("node-A");
        var id = await SeedTimeTicker();

        var taken = Assert.Single(await nodeA.AcquireImmediateTimeTickersAsync([id], CancellationToken.None));

        Assert.Equal((await ReadTimeTicker(id)).UpdatedAt, taken.UpdatedAt);
        Assert.True(await nodeA.TryReleaseTimeTickerClaim(id, taken.UpdatedAt));
        Assert.Equal(TickerStatus.Idle, (await ReadTimeTicker(id)).Status);
    }

    [Fact]
    public async Task RunNowClaim_ReturnsItsMarker_SoItCanBePutBack_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueOccurrenceOn(nodeA);
        await nodeA.ReleaseAcquiredCronTickerOccurrences([id], CancellationToken.None);

        var taken = Assert.Single(await nodeA.AcquireImmediateCronOccurrencesAsync([id], CancellationToken.None));

        Assert.Equal((await ReadOccurrence(id)).UpdatedAt, taken.UpdatedAt);
        Assert.True(await nodeA.TryReleaseCronOccurrenceClaim(id, taken.UpdatedAt));
        Assert.Equal(TickerStatus.Idle, (await ReadOccurrence(id)).Status);
    }

    // Run-now returns only the rows its own call claimed. A row this node's fallback holds is already running; handing it
    // out again would run it twice, and its put-back would undo the fallback's claim.
    [Fact]
    public async Task RunNowClaim_DoesNotReturnARowThisNodesFallbackHolds_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var heldByFallback = await QueueOccurrenceOn(nodeA);
        Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Contains(await Drain(nodeA.QueueTimedOutCronTickerOccurrences(CancellationToken.None)), x => x.Id == heldByFallback);
        var idle = await QueueOccurrenceOn(nodeA);
        await nodeA.ReleaseAcquiredCronTickerOccurrences([idle], CancellationToken.None);

        var taken = await nodeA.AcquireImmediateCronOccurrencesAsync([heldByFallback, idle], CancellationToken.None);

        Assert.Equal([idle], taken.Select(x => x.Id));
    }

    [Fact]
    public async Task ReleaseClaim_WithAnotherMarker_LeavesTheRow()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueTimeTickerOn(nodeA);
        var marker = Clock.NextClaimMarker();
        Assert.True(await nodeA.TryMarkTimeTickerInProgress(id, marker));

        Assert.False(await nodeA.TryReleaseTimeTickerClaim(id, marker.AddMilliseconds(1)));

        Assert.Equal(TickerStatus.InProgress, (await ReadTimeTicker(id)).Status);
    }

    [Fact]
    public async Task ReleaseClaim_OfAnotherNode_LeavesTheRow()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueTimeTickerOn(nodeA);
        var marker = Clock.NextClaimMarker();
        Assert.True(await nodeA.TryMarkTimeTickerInProgress(id, marker));

        Assert.False(await CreateProvider("node-B").TryReleaseTimeTickerClaim(id, marker));

        var row = await ReadTimeTicker(id);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("node-A", row.LockHolder);
    }

    // A job that started and finished (or was cancelled) is past InProgress; putting it back would run it again.
    [Theory]
    [InlineData(TickerStatus.Done)]
    [InlineData(TickerStatus.Failed)]
    [InlineData(TickerStatus.Cancelled)]
    [InlineData(TickerStatus.Skipped)]
    public async Task ReleaseClaim_RowNoLongerInProgress_LeavesTheRow(TickerStatus status)
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueTimeTickerOn(nodeA);
        var marker = Clock.NextClaimMarker();
        Assert.True(await nodeA.TryMarkTimeTickerInProgress(id, marker));
        await using (var context = CreateContext())
            await context.Set<TimeTickerEntity>().Where(x => x.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status));

        Assert.False(await nodeA.TryReleaseTimeTickerClaim(id, marker));

        Assert.Equal(status, (await ReadTimeTicker(id)).Status);
    }

    [Fact]
    public async Task ReleaseClaim_MissingRow_ReturnsFalse()
    {
        var nodeA = CreateProvider("node-A");

        Assert.False(await nodeA.TryReleaseTimeTickerClaim(Guid.NewGuid(), Clock.NextClaimMarker()));
        Assert.False(await nodeA.TryReleaseCronOccurrenceClaim(Guid.NewGuid(), Clock.NextClaimMarker()));
    }

    #endregion

    #region Startup release of an earlier run

    // The startup release frees what an earlier run of this node left locked. It runs after the fallback and the loop have
    // started, so a row this run has claimed since must be left alone, or the fallback claims it again and runs it twice.
    [Fact]
    public async Task ReleaseEarlierRun_ReleasesRowsLockedBeforeStart_KeepsRowsClaimedSince_TimeTicker()
    {
        var nodeA = CreateProvider("node-A");
        var leftByEarlierRun = await SeedTimeTicker(TickerStatus.InProgress, "node-A");
        var queuedByEarlierRun = await SeedTimeTicker(TickerStatus.Queued, "node-A", executionTime: T0.AddHours(1));
        var unlockedOfAnyNode = await SeedTimeTicker(executionTime: T0.AddHours(1));
        var start = Clock.UtcNow;
        var claimedByFallback = await SeedTimeTicker();
        Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Contains(await Drain(nodeA.QueueTimedOutTimeTickers(CancellationToken.None)), x => x.Id == claimedByFallback);
        var unlockedBefore = await ReadTimeTicker(unlockedOfAnyNode);

        await nodeA.ReleaseDeadNodeTimeTickerResources("node-A", start, CancellationToken.None);

        foreach (var released in new[] { leftByEarlierRun, queuedByEarlierRun })
        {
            var row = await ReadTimeTicker(released);
            Assert.Equal(TickerStatus.Idle, row.Status);
            Assert.Null(row.LockHolder);
            Assert.Null(row.LockedAt);
        }
        var kept = await ReadTimeTicker(claimedByFallback);
        Assert.Equal(TickerStatus.InProgress, kept.Status);
        Assert.Equal("node-A", kept.LockHolder);
        Assert.DoesNotContain(await Drain(nodeA.QueueTimedOutTimeTickers(CancellationToken.None)), x => x.Id == claimedByFallback);
        Assert.Equal(unlockedBefore.UpdatedAt, (await ReadTimeTicker(unlockedOfAnyNode)).UpdatedAt);
    }

    [Fact]
    public async Task ReleaseEarlierRun_ReleasesRowsLockedBeforeStart_KeepsRowsClaimedSince_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var leftByEarlierRun = await QueueOccurrenceOn(nodeA);
        await nodeA.TryMarkCronOccurrenceInProgress(leftByEarlierRun, Clock.NextClaimMarker());
        Clock.Advance(TimeSpan.FromMinutes(1));
        var start = Clock.UtcNow;
        Clock.Advance(TimeSpan.FromMilliseconds(5));
        var queuedSince = await QueueOccurrenceOn(nodeA);
        var claimedSince = await QueueOccurrenceOn(nodeA);
        Assert.True(await nodeA.TryMarkCronOccurrenceInProgress(claimedSince, Clock.NextClaimMarker()));

        await nodeA.ReleaseDeadNodeOccurrenceResources("node-A", start, CancellationToken.None);

        var released = await ReadOccurrence(leftByEarlierRun);
        Assert.Equal(TickerStatus.Idle, released.Status);
        Assert.Null(released.LockHolder);
        Assert.Equal(TickerStatus.Queued, (await ReadOccurrence(queuedSince)).Status);
        var kept = await ReadOccurrence(claimedSince);
        Assert.Equal(TickerStatus.InProgress, kept.Status);
        Assert.Equal("node-A", kept.LockHolder);
    }

    // The overload without a start keeps today's behaviour: every row of that node is released. The Redis heartbeat uses it
    // for nodes that are dead, where nothing of theirs can be running here.
    [Fact]
    public async Task ReleaseDeadNode_WithoutStart_StillReleasesEveryRowOfThatNode()
    {
        var nodeB = CreateProvider("node-B");
        var held = await SeedTimeTicker(TickerStatus.InProgress, "node-A", executionTime: T0);
        Clock.Advance(TimeSpan.FromHours(2));

        await nodeB.ReleaseDeadNodeTimeTickerResources("node-A", CancellationToken.None);

        Assert.Equal(TickerStatus.Idle, (await ReadTimeTicker(held)).Status);
    }

    #endregion

    #region Children

    protected async Task<(Guid ParentId, Guid ChildId)> SeedParentWithChild()
    {
        var child = NewTimeTicker();
        child.ExecutionTime = null;
        child.Function = "claim-child-fn";
        child.RunCondition = RunCondition.OnSuccess;
        var parent = NewTimeTicker();
        parent.Children = [child];
        await using var context = CreateContext();
        context.Set<TimeTickerEntity>().Add(parent);
        await context.SaveChangesAsync();
        return (parent.Id, child.Id);
    }

    // A child without an execution time of its own runs only inside its parent. No claim path hands it out as a row
    // of its own, and only the claim that won the parent carries it, so a late loop cannot run it a second time.
    [Fact]
    public async Task Child_IsCarriedOnlyByTheClaimThatWonItsParent()
    {
        var nodeA = CreateProvider("node-A");
        var nodeB = CreateProvider("node-B");
        var (parentId, childId) = await SeedParentWithChild();

        var window = await nodeA.GetEarliestTimeTickers(CancellationToken.None);
        Assert.DoesNotContain(window, x => x.Id == childId);
        var parent = Assert.Single(window, x => x.Id == parentId);
        Assert.Equal([childId], parent.Children.Select(x => x.Id));
        Assert.Single(await Drain(nodeA.QueueTimeTickers([parent], CancellationToken.None)));

        Clock.Advance(TimeSpan.FromSeconds(2));
        var fallback = await Drain(nodeB.QueueTimedOutTimeTickers(CancellationToken.None));

        Assert.DoesNotContain(fallback, x => x.Id == childId);
        var taken = Assert.Single(fallback, x => x.Id == parentId);
        Assert.Equal([childId], taken.Children.Select(x => x.Id));
        Assert.False(await nodeA.TryMarkTimeTickerInProgress(parentId, Clock.NextClaimMarker()));
        Assert.False(await nodeA.TryMarkTimeTickerInProgress(childId, Clock.NextClaimMarker()));
        Assert.Empty(await Drain(nodeA.QueueTimedOutTimeTickers(CancellationToken.None)));
        Assert.Empty(await Drain(nodeB.QueueTimedOutTimeTickers(CancellationToken.None)));
    }

    // A delayed child (a child with an execution time of its own) is a row of its own. Every claim path carries it in its
    // parent's payload with its time, so the handler never runs it inline as well (it used to run twice: the filtered
    // Include was ignored under the projection, so the payload carried it without its time).
    [Fact]
    public async Task DelayedChildAndGrandchild_AreCarriedWithTheirTimes_ByEveryClaimPath()
    {
        var delayedGrandchild = NewTimeTicker(executionTime: T0.AddHours(3));
        delayedGrandchild.Function = "delayed-grandchild-fn";
        var child = NewTimeTicker();
        child.ExecutionTime = null;
        child.Function = "claim-child-fn";
        child.Children = [delayedGrandchild];
        var delayedChild = NewTimeTicker(executionTime: T0.AddHours(2));
        delayedChild.Function = "delayed-child-fn";
        var parent = NewTimeTicker();
        parent.Children = [child, delayedChild];
        await using (var context = CreateContext())
        {
            context.Set<TimeTickerEntity>().Add(parent);
            await context.SaveChangesAsync();
        }

        void AssertCarried(TimeTickerEntity payload)
        {
            Assert.Equal(T0.AddHours(2), Assert.Single(payload.Children, x => x.Id == delayedChild.Id).ExecutionTime);
            var carried = Assert.Single(payload.Children, x => x.Id == child.Id);
            Assert.Null(carried.ExecutionTime);
            Assert.Equal(T0.AddHours(3), Assert.Single(carried.Children, x => x.Id == delayedGrandchild.Id).ExecutionTime);
        }

        AssertCarried(Assert.Single(await CreateProvider("node-A").GetEarliestTimeTickers(CancellationToken.None), x => x.Id == parent.Id));
        AssertCarried(Assert.Single(await CreateProvider("node-B").AcquireImmediateTimeTickersAsync([parent.Id], CancellationToken.None)));

        await using (var context = CreateContext())
            await context.Set<TimeTickerEntity>().Where(x => x.Id == parent.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, TickerStatus.Idle).SetProperty(x => x.LockHolder, (string?)null).SetProperty(x => x.LockedAt, (DateTime?)null));
        Clock.Advance(TimeSpan.FromSeconds(2));
        AssertCarried(Assert.Single(await Drain(CreateProvider("node-C").QueueTimedOutTimeTickers(CancellationToken.None)), x => x.Id == parent.Id));
    }

    #endregion

    #region Delayed children

    // A parent row (not due, or already run) with a delayed child: a child with an execution time of its own.
    private async Task<(Guid ParentId, Guid ChildId)> SeedDelayedChild(TickerStatus parentStatus, RunCondition? condition, DateTime childTime, TimeTickerEntity? grandchild = null)
    {
        var child = NewTimeTicker(executionTime: childTime);
        child.Function = "delayed-child-fn";
        child.RunCondition = condition;
        if (grandchild != null) child.Children = [grandchild];
        var parent = NewTimeTicker(parentStatus, executionTime: T0.AddHours(-1));
        parent.Children = [child];
        await using var context = CreateContext();
        context.Set<TimeTickerEntity>().Add(parent);
        await context.SaveChangesAsync();
        return (parent.Id, child.Id);
    }

    private async Task<TimeTickerEntity> ReadRow(Guid id)
    {
        await using var context = CreateContext();
        return await context.Set<TimeTickerEntity>().AsNoTracking().SingleAsync(x => x.Id == id);
    }

    public static IEnumerable<object?[]> EveryConditionAndParentStatus()
        => from condition in Enum.GetValues<RunCondition>().Select(c => (RunCondition?)c).Append(null)
           from status in Enum.GetValues<TickerStatus>()
           select new object?[] { condition, status };

    // The gate is written out for EF Core to translate; it must agree with ChainRunGate everywhere.
    [Theory]
    [MemberData(nameof(EveryConditionAndParentStatus))]
    public async Task DelayedChild_IsHandedOutExactlyWhenItsParentsStatusLetsItRun(RunCondition? condition, TickerStatus parentStatus)
    {
        var (_, childId) = await SeedDelayedChild(parentStatus, condition, T0.AddSeconds(30));

        var claimed = await CreateProvider("node-A").AcquireImmediateTimeTickersAsync([childId], CancellationToken.None);

        Assert.Equal(ChainRunGate.Allows(condition, parentStatus), claimed.Any(x => x.Id == childId));
    }

    // The fallback's skip query is the negation of the gate; both must agree with ChainRunGate everywhere: an overdue
    // delayed child is claimed when its parent's status lets it run, skipped when its parent ended without a match, and
    // left waiting otherwise.
    [Theory]
    [MemberData(nameof(EveryConditionAndParentStatus))]
    public async Task OverdueDelayedChild_IsClaimedSkippedOrLeftByTheFallback_ExactlyAsChainRunGateSays(RunCondition? condition, TickerStatus parentStatus)
    {
        var (_, childId) = await SeedDelayedChild(parentStatus, condition, T0.AddSeconds(-30));

        var fallback = await Drain(CreateProvider("node-A").QueueTimedOutTimeTickers(CancellationToken.None));

        var claimed = fallback.Any(x => x.Id == childId);
        var status = (await ReadRow(childId)).Status;
        if (ChainRunGate.Allows(condition, parentStatus))
            Assert.True(claimed, "a child its parent's status lets run was not claimed");
        else if (ChainRunGate.IsFinal(parentStatus))
            Assert.True(!claimed && status == TickerStatus.Skipped, $"claimed={claimed}, status={status}; expected Skipped");
        else
            Assert.True(!claimed && status == TickerStatus.Idle, $"claimed={claimed}, status={status}; expected it to wait");
    }

    // A delayed child is a row of its own; removing its parent removes it, and its children, with it.
    [Fact]
    public async Task RemovingAParent_RemovesItsDelayedDescendants()
    {
        var grandchild = NewTimeTicker(executionTime: T0.AddHours(3));
        var (parentId, childId) = await SeedDelayedChild(TickerStatus.Done, RunCondition.OnSuccess, T0.AddHours(2), grandchild);

        await CreateProvider("node-A").RemoveTimeTickers([parentId], CancellationToken.None);

        await using var context = CreateContext();
        Assert.False(await context.Set<TimeTickerEntity>().AnyAsync(x => x.Id == parentId || x.Id == childId || x.Id == grandchild.Id));
    }

    [Theory]
    [InlineData(TickerStatus.Idle, 30)]
    [InlineData(TickerStatus.Queued, 30)]
    [InlineData(TickerStatus.InProgress, 30)]
    [InlineData(TickerStatus.InProgress, -30)]
    public async Task DelayedChild_IsNotHandedOutByTheWindowOrTheFallback_WhileItsParentHasNotEnded(TickerStatus parentStatus, int childDueInSeconds)
    {
        var (_, childId) = await SeedDelayedChild(parentStatus, RunCondition.OnSuccess, T0.AddSeconds(childDueInSeconds));
        var node = CreateProvider("node-A");

        Assert.DoesNotContain(await node.GetEarliestTimeTickers(CancellationToken.None), x => x.Id == childId);
        Assert.DoesNotContain(await Drain(node.QueueTimedOutTimeTickers(CancellationToken.None)), x => x.Id == childId);
        Assert.Equal(TickerStatus.Idle, (await ReadRow(childId)).Status);
    }

    [Fact]
    public async Task DelayedChild_IsHandedOutByTheWindowAtItsTime_OnceItsParentSucceeded()
    {
        var (_, childId) = await SeedDelayedChild(TickerStatus.Done, RunCondition.OnSuccess, T0.AddSeconds(30));

        var window = await CreateProvider("node-A").GetEarliestTimeTickers(CancellationToken.None);

        Assert.Equal([childId], window.Select(x => x.Id));
    }

    [Fact]
    public async Task DelayedChild_IsHandedOutByTheFallback_WhenItsTimePassedBeforeItsParentSucceeded()
    {
        var (_, childId) = await SeedDelayedChild(TickerStatus.Done, RunCondition.OnSuccess, T0.AddSeconds(-30));

        var fallback = await Drain(CreateProvider("node-A").QueueTimedOutTimeTickers(CancellationToken.None));

        Assert.Contains(fallback, x => x.Id == childId);
    }

    [Fact]
    public async Task AWaitingDelayedChildDueFirst_DoesNotHideTheJobsDueAfterIt()
    {
        await SeedDelayedChild(TickerStatus.InProgress, RunCondition.OnSuccess, T0.AddSeconds(10));
        var laterId = await SeedTimeTicker(executionTime: T0.AddSeconds(20));

        var window = await CreateProvider("node-A").GetEarliestTimeTickers(CancellationToken.None);

        Assert.Equal([laterId], window.Select(x => x.Id));
    }

    // The parent's run skips a child its outcome does not let run; the fallback does it for a node that stopped first,
    // and the skip reaches a delayed grandchild on a later pass.
    [Fact]
    public async Task TheFallback_SkipsADelayedChildWhoseParentsOutcomeDoesNotMatch_AndThenItsDelayedGrandchild()
    {
        var grandchild = NewTimeTicker(executionTime: T0.AddSeconds(-20));
        grandchild.Function = "delayed-grandchild-fn";
        var (_, childId) = await SeedDelayedChild(TickerStatus.Failed, RunCondition.OnSuccess, T0.AddSeconds(-30), grandchild);
        var node = CreateProvider("node-A");

        Assert.Empty(await Drain(node.QueueTimedOutTimeTickers(CancellationToken.None)));
        Assert.Empty(await Drain(node.QueueTimedOutTimeTickers(CancellationToken.None)));

        var skipped = await ReadRow(childId);
        Assert.Equal(TickerStatus.Skipped, skipped.Status);
        Assert.Equal(ChainRunGate.NotMatchedReason, skipped.SkippedReason);
        Assert.Equal(TickerStatus.Skipped, (await ReadRow(grandchild.Id)).Status);
    }

    // The skip write only touches a row that has not started: a delayed child the store already ran keeps its status.
    [Theory]
    [InlineData(TickerStatus.InProgress)]
    [InlineData(TickerStatus.Done)]
    [InlineData(TickerStatus.Failed)]
    public async Task SkippingChildren_LeavesARowThatAlreadyStarted(TickerStatus status)
    {
        var started = await SeedTimeTicker(status, executionTime: T0.AddSeconds(-30));
        var waiting = await SeedTimeTicker(executionTime: T0.AddSeconds(-30));
        var skip = new InternalFunctionContext()
            .SetProperty(x => x.Status, TickerStatus.Skipped)
            .SetProperty(x => x.ExceptionDetails, ChainRunGate.NotMatchedReason);

        await ((IChildSkipWriter)CreateProvider("node-A")).SkipNotStartedTimeTickersAsync([started, waiting], skip, CancellationToken.None);

        Assert.Equal(status, (await ReadRow(started)).Status);
        Assert.Equal(TickerStatus.Skipped, (await ReadRow(waiting)).Status);
    }

    #endregion

    #region Fenced run writes (#74)

    // A node: its store and the real internal manager over it, as a running node has them.
    private (ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> Store, Utilities.Interfaces.Managers.IInternalTickerManager Manager) Node(string nodeIdentifier)
    {
        var store = CreateProvider(nodeIdentifier);
        return (store, new Utilities.Managers.InternalTickerManager<TimeTickerEntity, CronTickerEntity>(store, Clock, Substitute.For<ISharpTickerNotificationHubSender>()));
    }

    // Queues the row on the node and claims it the way the scheduler does. Returns the context the run gets.
    private static async Task<InternalFunctionContext> ClaimRun(
        (ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> Store, Utilities.Interfaces.Managers.IInternalTickerManager Manager) node, Guid id)
    {
        var window = await node.Store.GetEarliestTimeTickers(CancellationToken.None);
        Assert.Single(await Drain(node.Store.QueueTimeTickers(window.Where(x => x.Id == id).ToArray(), CancellationToken.None)));
        var run = new InternalFunctionContext { TickerId = id, FunctionName = "claim-fn", Type = TickerType.TimeTicker };
        Assert.True(await node.Manager.TryClaimDueTicker(run, node.Manager.NextClaimMarker(), CancellationToken.None));
        run.RunMarker = run.ClaimMarker; // as the handler does when the run starts
        return run;
    }

    private static async Task<InternalFunctionContext> ClaimOccurrenceRun(
        (ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> Store, Utilities.Interfaces.Managers.IInternalTickerManager Manager) node, Guid id)
    {
        var run = new InternalFunctionContext { TickerId = id, FunctionName = "claim-cron-fn", Type = TickerType.CronTickerOccurrence };
        Assert.True(await node.Manager.TryClaimDueTicker(run, node.Manager.NextClaimMarker(), CancellationToken.None));
        run.RunMarker = run.ClaimMarker;
        return run;
    }

    private static InternalFunctionContext Ended(InternalFunctionContext run, TickerStatus status, DateTime executedAt)
    {
        run.ResetUpdateProps().SetProperty(x => x.Status, status).SetProperty(x => x.ExecutedAt, executedAt);
        return run;
    }

    private async Task<Guid> SeedChain(Guid childId)
    {
        var parent = NewTimeTicker(executionTime: T0.AddHours(1));
        var child = NewTimeTicker(executionTime: null);
        child.Id = childId;
        child.Function = "claim-child";
        child.ExecutionTime = null;
        child.RunCondition = RunCondition.OnSuccess;
        parent.Children = [child];
        await using var context = CreateContext();
        context.Set<TimeTickerEntity>().Add(parent);
        await context.SaveChangesAsync();
        return parent.Id;
    }

    // T6: the scheduler's due claim hands the run the marker it wrote, as the other claim paths do.
    [Fact]
    public async Task Fence_TheDueClaim_GivesTheRunTheMarkerItWrote()
    {
        var a = Node("node-A");
        var run = await ClaimRun(a, await SeedTimeTicker());
        var occurrence = await ClaimOccurrenceRun(a, await QueueOccurrenceOn(a.Store));

        Assert.Equal((await ReadTimeTicker(run.TickerId)).UpdatedAt, run.ClaimMarker);
        Assert.Equal((await ReadOccurrence(occurrence.TickerId)).UpdatedAt, occurrence.ClaimMarker);
    }

    // T1: node A's run lost its row (the release of an earlier run's rows freed it) and node B claimed it and finished. A's
    // late write must not overwrite B's outcome.
    [Fact]
    public async Task Fence_ALateWriteOfARunWhoseRowAnotherNodeTook_ChangesNothing()
    {
        var a = Node("node-A");
        var b = Node("node-B");
        var id = await SeedTimeTicker();
        var runA = await ClaimRun(a, id);
        await a.Store.ReleaseDeadNodeTimeTickerResources("node-A", CancellationToken.None);
        var runB = await ClaimRun(b, id);
        Assert.True(await b.Manager.UpdateTickerAsync(Ended(runB, TickerStatus.Done, T0.AddMinutes(1))));

        var stored = await a.Manager.UpdateTickerAsync(Ended(runA, TickerStatus.Failed, T0.AddMinutes(2)));

        Assert.False(stored);
        var row = await ReadTimeTicker(id);
        Assert.Equal(TickerStatus.Done, row.Status);
        Assert.Equal("node-B", row.LockHolder);
        Assert.Equal(T0.AddMinutes(1), row.ExecutedAt);
    }

    // The same for a cron occurrence.
    [Fact]
    public async Task Fence_ALateWriteOfACronRunWhoseRowAnotherNodeTook_ChangesNothing()
    {
        var a = Node("node-A");
        var b = Node("node-B");
        var id = await QueueOccurrenceOn(a.Store);
        var runA = await ClaimOccurrenceRun(a, id);
        await a.Store.ReleaseDeadNodeOccurrenceResources("node-A", CancellationToken.None);
        var runB = new InternalFunctionContext { TickerId = id, Type = TickerType.CronTickerOccurrence };
        runB.RunMarker = Assert.Single(await b.Store.AcquireImmediateCronOccurrencesAsync([id], CancellationToken.None)).UpdatedAt;
        Assert.True(await b.Manager.UpdateTickerAsync(Ended(runB, TickerStatus.Done, T0.AddMinutes(1))));

        var stored = await a.Manager.UpdateTickerAsync(Ended(runA, TickerStatus.Failed, T0.AddMinutes(2)));

        Assert.False(stored);
        var row = await ReadOccurrence(id);
        Assert.Equal(TickerStatus.Done, row.Status);
        Assert.Equal("node-B", row.LockHolder);
    }

    // T2: the same node claimed the row again after it was released: the first run's late write changes nothing.
    [Fact]
    public async Task Fence_ALateWriteOfAnEarlierRunOfTheSameNode_ChangesNothing()
    {
        var a = Node("node-A");
        var id = await SeedTimeTicker();
        var first = await ClaimRun(a, id);
        await a.Store.ReleaseDeadNodeTimeTickerResources("node-A", CancellationToken.None);
        var second = await ClaimRun(a, id);

        Assert.False(await a.Manager.UpdateTickerAsync(Ended(first, TickerStatus.Done, T0.AddMinutes(1))));

        var row = await ReadTimeTicker(id);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal(second.ClaimMarker, row.UpdatedAt);
    }

    // T4 (guard) and the marker kept: every write of a run keeps the claim marker, and a final write sent again lands again.
    [Fact]
    public async Task Fence_TheWritesOfARunKeepItsMarker_AndAFinalWriteSentAgainIsStoredAgain()
    {
        var a = Node("node-A");
        var id = await SeedTimeTicker();
        var run = await ClaimRun(a, id);
        Clock.UtcNow = T0.AddMinutes(5);

        Assert.True(await a.Manager.UpdateTickerAsync(run.ResetUpdateProps().SetProperty(x => x.RetryCount, 1)));
        Assert.True(await a.Manager.UpdateTickerAsync(Ended(run, TickerStatus.Done, T0.AddMinutes(6))));
        Assert.True(await a.Manager.UpdateTickerAsync(Ended(run, TickerStatus.Done, T0.AddMinutes(6))));

        var row = await ReadTimeTicker(id);
        Assert.Equal(TickerStatus.Done, row.Status);
        Assert.Equal(1, row.RetryCount);
        Assert.Equal(run.ClaimMarker, row.UpdatedAt);
    }

    // T3 and T9: an inline child carries the lock of the node that started it and a marker of its own. Started again by
    // another run of its parent, the first run's late write changes nothing; left by a node, the release frees it, and with
    // no time of its own no claim path hands it out again.
    [Fact]
    public async Task Fence_AnInlineChild_IsHeldByTheRunThatStartedItLast_AndFreedWithItsNodesRows()
    {
        var a = Node("node-A");
        var b = Node("node-B");
        var childId = Guid.NewGuid();
        await SeedChain(childId);
        var childOfA = new InternalFunctionContext { TickerId = childId, Type = TickerType.TimeTicker };
        var childOfB = new InternalFunctionContext { TickerId = childId, Type = TickerType.TimeTicker };
        await a.Manager.RecordChildStartedAsync(childOfA);
        await b.Manager.RecordChildStartedAsync(childOfB);

        Assert.False(await a.Manager.UpdateTickerAsync(Ended(childOfA, TickerStatus.Done, T0.AddMinutes(1))));
        Assert.Equal("node-B", (await ReadTimeTicker(childId)).LockHolder);

        await b.Store.ReleaseDeadNodeTimeTickerResources("node-B", CancellationToken.None);
        var freed = await ReadTimeTicker(childId);
        Assert.Equal(TickerStatus.Idle, freed.Status);
        Assert.Null(freed.LockHolder);
        Assert.DoesNotContain(await a.Store.GetEarliestTimeTickers(CancellationToken.None), x => x.Id == childId);
        Assert.DoesNotContain(await Drain(a.Store.QueueTimedOutTimeTickers(CancellationToken.None)), x => x.Id == childId);
    }

    // T10: an edit of a running job (a full-entity update with no run state, as UpdateAsync and the dashboard send it) writes
    // its fields and keeps the run, so the run's outcome is still stored and the job does not run again.
    [Fact]
    public async Task Fence_EditingARunningJob_KeepsItsRun_AndItsOutcomeIsStillStored()
    {
        var a = Node("node-A");
        var id = await SeedTimeTicker();
        var run = await ClaimRun(a, id);
        var edit = NewTimeTicker(executionTime: T0.AddHours(3));
        edit.Id = id;
        edit.Description = "edited";

        await a.Store.UpdateTimeTickers([edit], CancellationToken.None);

        var row = await ReadTimeTicker(id);
        Assert.Equal("edited", row.Description);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("node-A", row.LockHolder);
        Assert.True(await a.Manager.UpdateTickerAsync(Ended(run, TickerStatus.Done, T0.AddMinutes(1))));
        Assert.Equal(TickerStatus.Done, (await ReadTimeTicker(id)).Status);
    }

    // The same for an inline child that runs while its chain is edited.
    [Fact]
    public async Task Fence_EditingAChainWhileItsInlineChildRuns_KeepsTheChildsRun()
    {
        var a = Node("node-A");
        var childId = Guid.NewGuid();
        var parentId = await SeedChain(childId);
        var childRun = new InternalFunctionContext { TickerId = childId, Type = TickerType.TimeTicker };
        await a.Manager.RecordChildStartedAsync(childRun);
        var parentEdit = NewTimeTicker(executionTime: T0.AddHours(2));
        parentEdit.Id = parentId;
        var childEdit = NewTimeTicker(executionTime: null);
        childEdit.Id = childId;
        childEdit.ExecutionTime = null;
        childEdit.Function = "claim-child";
        childEdit.Description = "child edited";
        childEdit.RunCondition = RunCondition.OnSuccess;
        parentEdit.Children = [childEdit];

        await a.Store.UpdateTimeTickers([parentEdit], CancellationToken.None);

        var row = await ReadTimeTicker(childId);
        Assert.Equal("child edited", row.Description);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.True(await a.Manager.UpdateTickerAsync(Ended(childRun, TickerStatus.Done, T0.AddMinutes(1))));
    }

    // A body read before the job ran (a dialog opened before, saved after) must not write the ended job back as not started:
    // the row stays Done and is not handed out again.
    [Fact]
    public async Task Fence_AnEditWithABodyReadBeforeTheJobRan_DoesNotRunItAgain()
    {
        var a = Node("node-A");
        var id = await SeedTimeTicker();
        var readBefore = await ReadTimeTicker(id);
        var run = await ClaimRun(a, id);
        Assert.True(await a.Manager.UpdateTickerAsync(Ended(run, TickerStatus.Done, T0.AddMinutes(1))));
        readBefore.Description = "edited";

        await a.Store.UpdateTimeTickers([readBefore], CancellationToken.None);

        var row = await ReadTimeTicker(id);
        Assert.Equal("edited", row.Description);
        Assert.Equal(TickerStatus.Done, row.Status);
        Assert.DoesNotContain(await a.Store.GetEarliestTimeTickers(CancellationToken.None), x => x.Id == id);
    }

    // T16: a body read while the job ran and sent after it ended must not bring the run back: the row stays Done.
    [Fact]
    public async Task Fence_AnEditWithABodyReadWhileTheJobRan_DoesNotBringTheRunBack()
    {
        var a = Node("node-A");
        var id = await SeedTimeTicker();
        var run = await ClaimRun(a, id);
        var readWhileRunning = await ReadTimeTicker(id);
        Assert.True(await a.Manager.UpdateTickerAsync(Ended(run, TickerStatus.Done, T0.AddMinutes(1))));
        readWhileRunning.Description = "edited";

        await a.Store.UpdateTimeTickers([readWhileRunning], CancellationToken.None);

        var row = await ReadTimeTicker(id);
        Assert.Equal("edited", row.Description);
        Assert.Equal(TickerStatus.Done, row.Status);
    }

    // T8 (store side): the write a cron edit makes to move the planned occurrence leaves a running one alone, and its
    // outcome is still stored.
    [Fact]
    public async Task Fence_MovingAPlannedOccurrence_LeavesARunningOneAlone()
    {
        var a = Node("node-A");
        var run = await ClaimOccurrenceRun(a, await QueueOccurrenceOn(a.Store));
        var move = new InternalFunctionContext { TickerId = run.TickerId, Type = TickerType.CronTickerOccurrence }
            .SetProperty(x => x.ExecutionTime, T0.AddHours(1));

        Assert.Equal(0, await ((IFencedRunWriter)a.Store).UpdateNotStartedCronOccurrenceAsync(move));

        Assert.Equal(run.ClaimMarker, (await ReadOccurrence(run.TickerId)).UpdatedAt);
        Assert.True(await a.Manager.UpdateTickerAsync(Ended(run, TickerStatus.Done, T0.AddMinutes(1))));
    }

    #endregion
}

public class EfCoreClaimMultiNodeSqliteTests : EfCoreClaimMultiNodeTests
{
    protected override int ExpectedClaimFanOut => 1;

    private SqliteConnection _connection = null!;
    private DbContextOptions<TestSharpTickerDbContext> _options = null!;

    protected override async Task InitializeStoreAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<TestSharpTickerDbContext>().UseSqlite(_connection).Options;
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
    }

    protected override async Task DisposeStoreAsync() => await _connection.DisposeAsync();

    protected override TestSharpTickerDbContext CreateContext() => new(_options);

    protected override ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> CreateProvider(string nodeIdentifier, ITickerClock clock)
        => CreateProvider(nodeIdentifier, clock, _options);

    protected override ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> CreateProvider(string nodeIdentifier, ITickerClock clock, IInterceptor interceptor)
        => CreateProvider(nodeIdentifier, clock, new DbContextOptionsBuilder<TestSharpTickerDbContext>().UseSqlite(_connection).AddInterceptors(interceptor).Options);

    private static ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> CreateProvider(
        string nodeIdentifier, ITickerClock clock, DbContextOptions<TestSharpTickerDbContext> options)
    {
        var redisContext = Substitute.For<ISharpTickerRedisContext>();
        redisContext.HasRedisConnection.Returns(false);

        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<TestSharpTickerDbContext>>(new PooledDbContextFactory<TestSharpTickerDbContext>(options));

        return new TestableProvider(services.BuildServiceProvider(), clock, new SchedulerOptionsBuilder { NodeIdentifier = nodeIdentifier }, redisContext);
    }
}

[Collection(PostgresTestFixture.CollectionName)]
public class EfCoreClaimMultiNodePostgresTests(PostgresTestFixture fixture, ITestOutputHelper output) : EfCoreClaimMultiNodeTests
{
    protected override int ExpectedClaimFanOut => 16;

    protected override Task InitializeStoreAsync() => fixture.ResetAsync();

    protected override Task DisposeStoreAsync() => Task.CompletedTask;

    protected override TestSharpTickerDbContext CreateContext() => fixture.CreateContext();

    protected override ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> CreateProvider(string nodeIdentifier, ITickerClock clock)
        => fixture.CreateProvider(nodeIdentifier, clock);

    protected override ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> CreateProvider(string nodeIdentifier, ITickerClock clock, IInterceptor interceptor)
        => fixture.CreateProvider(nodeIdentifier, clock, interceptor);

    // Eight nodes run both the fenced claim and the fallback against every row at once. Whatever the interleaving,
    // each row has exactly one winner: the outcome does not depend on timing, only the winner's identity does.
    [Fact]
    public async Task Claim_ManyNodesRacing_EachRowHasExactlyOneWinner()
    {
        const int nodeCount = 8;
        const int rowCount = 200;
        var providers = Enumerable.Range(0, nodeCount).Select(i => CreateProvider($"node-{i}")).ToArray();
        var rows = Enumerable.Range(0, rowCount)
            .Select(i => NewTimeTicker(TickerStatus.Queued, lockHolder: $"node-{i % nodeCount}", executionTime: T0.AddSeconds(-5)))
            .ToArray();
        await using (var context = CreateContext())
        {
            context.Set<TimeTickerEntity>().AddRange(rows);
            await context.SaveChangesAsync();
        }
        var ids = rows.Select(x => x.Id).ToArray();
        var winners = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        using var start = new ManualResetEventSlim(false);

        async Task Fallback(ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> node)
        {
            start.Wait();
            await foreach (var row in node.QueueTimedOutTimeTickers(CancellationToken.None))
                winners.Add(row.Id);
        }

        async Task TryMark(ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> node)
        {
            start.Wait();
            foreach (var id in ids)
                if (await node.TryMarkTimeTickerInProgress(id, Clock.NextClaimMarker()))
                    winners.Add(id);
        }

        var tasks = providers.SelectMany(node => new[] { Task.Run(() => Fallback(node)), Task.Run(() => TryMark(node)) }).ToArray();
        start.Set();
        await Task.WhenAll(tasks);

        var perRow = winners.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(rowCount, perRow.Count);
        Assert.All(perRow, pair => Assert.Equal(1, pair.Value));
        await using var verify = CreateContext();
        Assert.Equal(rowCount, await verify.Set<TimeTickerEntity>().CountAsync(x => x.Status == TickerStatus.InProgress));
    }

    // The scheduler path for one second with 1000 rows: window read, one queue write per row, one fenced claim per row.
    // The duration is only reported, never asserted.
    [Fact]
    public async Task Claim_ThousandRowsSameSecond_AllClaimedOnce()
    {
        const int rowCount = 1000;
        var node = CreateProvider("node-A");
        var rows = Enumerable.Range(0, rowCount)
            .Select(i => NewTimeTicker(executionTime: T0.AddMilliseconds(i % 900)))
            .ToArray();
        await using (var context = CreateContext())
        {
            context.Set<TimeTickerEntity>().AddRange(rows);
            await context.SaveChangesAsync();
        }

        var watch = Stopwatch.StartNew();
        var window = await node.GetEarliestTimeTickers(CancellationToken.None);
        var queued = await Drain(node.QueueTimeTickers(window, CancellationToken.None));
        var claimed = 0;
        foreach (var row in queued)
            if (await node.TryMarkTimeTickerInProgress(row.Id, Clock.NextClaimMarker()))
                claimed++;
        watch.Stop();
        output.WriteLine($"{rowCount} rows in one second: window read, queue and claim took {watch.ElapsedMilliseconds} ms");

        var again = 0;
        foreach (var row in queued)
            if (await node.TryMarkTimeTickerInProgress(row.Id, Clock.NextClaimMarker()))
                again++;

        Assert.Equal(rowCount, window.Length);
        Assert.Equal(rowCount, queued.Count);
        Assert.Equal(rowCount, claimed);
        Assert.Equal(0, again);
        await using var verify = CreateContext();
        Assert.Equal(rowCount, await verify.Set<TimeTickerEntity>().CountAsync(x => x.Status == TickerStatus.InProgress));
    }
}
