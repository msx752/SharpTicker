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

        Assert.True(await nodeA.TryMarkTimeTickerInProgress(id));

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

        Assert.True(await nodeA.TryMarkCronOccurrenceInProgress(id));

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

        Assert.False(await nodeA.TryMarkTimeTickerInProgress(id));

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

        Assert.False(await nodeA.TryMarkCronOccurrenceInProgress(id));

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

        Assert.False(await nodeA.TryMarkTimeTickerInProgress(id));

        var untouched = await ReadTimeTicker(id);
        Assert.Equal(TickerStatus.Queued, untouched.Status);
        Assert.Equal("node-B", untouched.LockHolder);
        Assert.Equal(before.UpdatedAt, untouched.UpdatedAt);
        Assert.True(await nodeB.TryMarkTimeTickerInProgress(id));
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

        Assert.False(await nodeA.TryMarkCronOccurrenceInProgress(id));

        var untouched = await ReadOccurrence(id);
        Assert.Equal(TickerStatus.Queued, untouched.Status);
        Assert.Equal("node-B", untouched.LockHolder);
        Assert.Equal(before.UpdatedAt, untouched.UpdatedAt);
        Assert.True(await nodeB.TryMarkCronOccurrenceInProgress(id));
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

        Assert.False(await nodeA.TryMarkTimeTickerInProgress(id));

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

        Assert.False(await nodeA.TryMarkCronOccurrenceInProgress(id));

        var after = await ReadOccurrence(id);
        Assert.Equal(status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task TryMark_MissingRow_ReturnsFalse()
    {
        var nodeA = CreateProvider("node-A");

        Assert.False(await nodeA.TryMarkTimeTickerInProgress(Guid.NewGuid()));
        Assert.False(await nodeA.TryMarkCronOccurrenceInProgress(Guid.NewGuid()));
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

    private sealed class CancelAfterFirstUpdate(CancellationTokenSource cts) : DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            cts.Cancel();
            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
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
        var lateLoopWon = await nodeA.TryMarkTimeTickerInProgress(id);

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
        var lateLoopWon = await nodeA.TryMarkCronOccurrenceInProgress(id);

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
        var lateOwnerWon = await owner.TryMarkTimeTickerInProgress(id);

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
        var tryMarkWon = await first.TryMarkTimeTickerInProgress(markedFirst);
        var twinTryMarkWon = await twin.TryMarkTimeTickerInProgress(markedFirst);
        // Row 2: the twin's fallback wins, the other's TryMark then loses.
        var claimedByTwin = await Drain(twin.QueueTimedOutTimeTickers(CancellationToken.None));
        var lateTryMarkWon = await first.TryMarkTimeTickerInProgress(fallbackFirst);

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
}

public class EfCoreClaimMultiNodeSqliteTests : EfCoreClaimMultiNodeTests
{
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
                if (await node.TryMarkTimeTickerInProgress(id))
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
            if (await node.TryMarkTimeTickerInProgress(row.Id))
                claimed++;
        watch.Stop();
        output.WriteLine($"{rowCount} rows in one second: window read, queue and claim took {watch.ElapsedMilliseconds} ms");

        var again = 0;
        foreach (var row in queued)
            if (await node.TryMarkTimeTickerInProgress(row.Id))
                again++;

        Assert.Equal(rowCount, window.Length);
        Assert.Equal(rowCount, queued.Count);
        Assert.Equal(rowCount, claimed);
        Assert.Equal(0, again);
        await using var verify = CreateContext();
        Assert.Equal(rowCount, await verify.Set<TimeTickerEntity>().CountAsync(x => x.Status == TickerStatus.InProgress));
    }
}
