using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MongoDB.Driver.Core.Configuration;
using MongoDB.Driver.Core.Events;
using SharpTicker.MongoDB.Infrastructure;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Models;

namespace SharpTicker.MongoDB.Tests;

/// <summary>
/// Provider instances (separate NodeIdentifier) over one shared MongoDB and fake clocks, driving the fenced Queued to
/// InProgress claim and the fallback claim the way several nodes would. Every call goes through the provider
/// interface, so a custom default body would be caught. BSON dates have millisecond precision, so clocks only move
/// in whole milliseconds.
/// </summary>
[Collection("Mongo")]
public class MongoClaimMultiNodeTests : IClassFixture<MongoTestFixture>, IAsyncLifetime
{
    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly MongoTestFixture _f;
    private readonly FakeClock _clock = new(T0);

    public MongoClaimMultiNodeTests(MongoTestFixture fixture) => _f = fixture;

    public Task InitializeAsync() => _f.DropAllAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A clock the test moves by hand.</summary>
    private sealed class FakeClock(DateTime start) : ITickerClock
    {
        public DateTime UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    private ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> CreateProvider(string nodeIdentifier, ITickerClock? clock = null)
        => new TickerMongoPersistenceProvider<TimeTickerEntity, CronTickerEntity>(
            new TickerMongoContext<TimeTickerEntity, CronTickerEntity>(_f.Database, "ticker_"),
            clock ?? _clock,
            new SchedulerOptionsBuilder { NodeIdentifier = nodeIdentifier });

    private static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    private static TimeTickerEntity NewTimeTicker(
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

    private async Task<Guid> SeedTimeTicker(
        TickerStatus status = TickerStatus.Idle,
        string? lockHolder = null,
        DateTime? updatedAt = null,
        DateTime? executionTime = null)
    {
        var ticker = NewTimeTicker(status, lockHolder, updatedAt, executionTime);
        await _f.TimeTickers.InsertOneAsync(ticker);
        return ticker.Id;
    }

    private async Task<Guid> SeedCronTicker()
    {
        var cron = new CronTickerEntity
        {
            Id = Guid.NewGuid(),
            Function = "claim-cron-fn",
            Expression = "* * * * *",
            CreatedAt = T0.AddHours(-2),
            UpdatedAt = T0.AddHours(-2),
            IsEnabled = true,
            Request = []
        };
        await _f.CronTickers.InsertOneAsync(cron);
        return cron.Id;
    }

    private static (DateTime Key, InternalManagerContext[] Items) Slot(Guid cronId)
        => (T0, [new InternalManagerContext(cronId) { FunctionName = "claim-cron-fn", Expression = "* * * * *" }]);

    // Seeds a cron and queues its occurrence for the T0 slot on the given node.
    private async Task<Guid> QueueOccurrenceOn(ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> node)
    {
        var cronId = await SeedCronTicker();
        return Assert.Single(await Drain(node.QueueCronTickerOccurrences(Slot(cronId), CancellationToken.None))).Id;
    }

    private Task<TimeTickerEntity> ReadTimeTicker(Guid id)
        => _f.TimeTickers.Find(x => x.Id == id).SingleAsync();

    private Task<CronTickerOccurrenceEntity<CronTickerEntity>> ReadOccurrence(Guid id)
        => _f.CronTickerOccurrences.Find(x => x.Id == id).SingleAsync();

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
        _clock.Advance(TimeSpan.FromMilliseconds(300));

        Assert.True(await nodeA.TryMarkTimeTickerInProgress(id, _clock.NextClaimMarker()));

        var row = await ReadTimeTicker(id);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("node-A", row.LockHolder);
        Assert.Equal(T0, row.LockedAt);
        Assert.Equal(_clock.UtcNow, row.UpdatedAt);
    }

    [Fact]
    public async Task TryMark_OwnQueuedRow_ReturnsTrueAndWritesInProgress_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueOccurrenceOn(nodeA);
        _clock.Advance(TimeSpan.FromMilliseconds(300));

        Assert.True(await nodeA.TryMarkCronOccurrenceInProgress(id, _clock.NextClaimMarker()));

        var row = await ReadOccurrence(id);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("node-A", row.LockHolder);
        Assert.Equal(T0, row.LockedAt);
        Assert.Equal(_clock.UtcNow, row.UpdatedAt);
    }

    [Fact]
    public async Task TryMark_RowClaimedByFallbackOfAnotherNode_ReturnsFalseAndLeavesRow_TimeTicker()
    {
        var nodeA = CreateProvider("node-A");
        var nodeB = CreateProvider("node-B");
        var id = await QueueTimeTickerOn(nodeA);
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Contains(await Drain(nodeB.QueueTimedOutTimeTickers(CancellationToken.None)), x => x.Id == id);
        var before = await ReadTimeTicker(id);

        Assert.False(await nodeA.TryMarkTimeTickerInProgress(id, _clock.NextClaimMarker()));

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
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Contains(await Drain(nodeB.QueueTimedOutCronTickerOccurrences(CancellationToken.None)), x => x.Id == id);
        var before = await ReadOccurrence(id);

        Assert.False(await nodeA.TryMarkCronOccurrenceInProgress(id, _clock.NextClaimMarker()));

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

        Assert.False(await nodeA.TryMarkTimeTickerInProgress(id, _clock.NextClaimMarker()));

        var untouched = await ReadTimeTicker(id);
        Assert.Equal(TickerStatus.Queued, untouched.Status);
        Assert.Equal("node-B", untouched.LockHolder);
        Assert.Equal(before.UpdatedAt, untouched.UpdatedAt);
        Assert.True(await nodeB.TryMarkTimeTickerInProgress(id, _clock.NextClaimMarker()));
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

        Assert.False(await nodeA.TryMarkCronOccurrenceInProgress(id, _clock.NextClaimMarker()));

        var untouched = await ReadOccurrence(id);
        Assert.Equal(TickerStatus.Queued, untouched.Status);
        Assert.Equal("node-B", untouched.LockHolder);
        Assert.Equal(before.UpdatedAt, untouched.UpdatedAt);
        Assert.True(await nodeB.TryMarkCronOccurrenceInProgress(id, _clock.NextClaimMarker()));
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

        Assert.False(await nodeA.TryMarkTimeTickerInProgress(id, _clock.NextClaimMarker()));

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
        await _f.CronTickerOccurrences.UpdateOneAsync(
            Builders<CronTickerOccurrenceEntity<CronTickerEntity>>.Filter.Eq(x => x.Id, id),
            Builders<CronTickerOccurrenceEntity<CronTickerEntity>>.Update.Set(x => x.Status, status));
        var before = await ReadOccurrence(id);

        Assert.False(await nodeA.TryMarkCronOccurrenceInProgress(id, _clock.NextClaimMarker()));

        var after = await ReadOccurrence(id);
        Assert.Equal(status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task TryMark_MissingRow_ReturnsFalse()
    {
        var nodeA = CreateProvider("node-A");

        Assert.False(await nodeA.TryMarkTimeTickerInProgress(Guid.NewGuid(), _clock.NextClaimMarker()));
        Assert.False(await nodeA.TryMarkCronOccurrenceInProgress(Guid.NewGuid(), _clock.NextClaimMarker()));
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
    // dispatched; the rest must stay Idle. A command event subscriber cancels right after the first update.
    [Fact]
    public async Task AcquireImmediate_CancelledMidBatch_ReturnsWonRowsAndLeavesTheRestIdle_CronOccurrence()
    {
        var seed = CreateProvider("node-A");
        var ids = new[] { await QueueOccurrenceOn(seed), await QueueOccurrenceOn(seed), await QueueOccurrenceOn(seed) };
        await seed.ReleaseAcquiredCronTickerOccurrences(ids, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var settings = MongoClientSettings.FromConnectionString(_f.Container.GetConnectionString());
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandSucceededEvent>(e =>
        {
            if (e.CommandName == "update")
                cts.Cancel();
        });
        var database = new MongoClient(settings).GetDatabase(_f.Database.DatabaseNamespace.DatabaseName);
        var node = new TickerMongoPersistenceProvider<TimeTickerEntity, CronTickerEntity>(
            new TickerMongoContext<TimeTickerEntity, CronTickerEntity>(database, "ticker_"),
            _clock,
            new SchedulerOptionsBuilder { NodeIdentifier = "node-A" });

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

    [Fact]
    public async Task AcquireImmediate_CancelledMidBatch_ReturnsWonRowsAndLeavesTheRestIdle()
    {
        using var cts = new CancellationTokenSource();
        var settings = MongoClientSettings.FromConnectionString(_f.Container.GetConnectionString());
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandSucceededEvent>(e =>
        {
            if (e.CommandName == "update")
                cts.Cancel();
        });
        var database = new MongoClient(settings).GetDatabase(_f.Database.DatabaseNamespace.DatabaseName);
        var node = new TickerMongoPersistenceProvider<TimeTickerEntity, CronTickerEntity>(
            new TickerMongoContext<TimeTickerEntity, CronTickerEntity>(database, "ticker_"),
            _clock,
            new SchedulerOptionsBuilder { NodeIdentifier = "node-A" });
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

    // The store fails once after the call has won rows: either the write of a later row or the read of the won rows.
    // Nobody gets the won rows to run, so they must be put back (Idle, no holder) for the fallback, and the call must throw.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquireImmediate_StoreFailsAfterAWin_PutsTheWonRowsBackAndThrows(bool readFails)
    {
        var node = CreateFailingNode(readFails ? "find" : "update", readFails ? 1 : 2);
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
        var node = CreateFailingNode(readFails ? "find" : "update", readFails ? 1 : 2);

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.AcquireImmediateCronOccurrencesAsync(ids, CancellationToken.None));

        foreach (var id in ids)
        {
            var row = await ReadOccurrence(id);
            Assert.Equal(TickerStatus.Idle, row.Status);
            Assert.Null(row.LockHolder);
        }
    }

    // The read of the crons (the second find, after the read of the won occurrences) fails: nobody runs the won rows.
    [Fact]
    public async Task AcquireImmediate_CronReadFailsAfterAWin_PutsTheWonRowsBackAndThrows_CronOccurrence()
    {
        var seed = CreateProvider("node-A");
        var ids = new[] { await QueueOccurrenceOn(seed), await QueueOccurrenceOn(seed) };
        await seed.ReleaseAcquiredCronTickerOccurrences(ids, CancellationToken.None);
        var node = CreateFailingNode("find", 2);

        await Assert.ThrowsAsync<InvalidOperationException>(() => node.AcquireImmediateCronOccurrencesAsync(ids, CancellationToken.None));

        foreach (var id in ids)
        {
            var row = await ReadOccurrence(id);
            Assert.Equal(TickerStatus.Idle, row.Status);
            Assert.Null(row.LockHolder);
        }
    }

    // A claim's write lands, then its reply is lost (the call throws). The row is InProgress for this node with nothing to
    // run it, so it must be put back like a won row. Covers run-now and the fallback, on time tickers and cron occurrences.
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
        _clock.Advance(TimeSpan.FromSeconds(2));

        var failed = 0;
        var settings = MongoClientSettings.FromConnectionString(_f.Container.GetConnectionString());
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandSucceededEvent>(e =>
        {
            if (e.CommandName == "update" && Interlocked.Exchange(ref failed, 1) == 0)
                throw new TimeoutException("the reply of the claim was lost");
        });
        var database = new MongoClient(settings).GetDatabase(_f.Database.DatabaseNamespace.DatabaseName);
        var node = new TickerMongoPersistenceProvider<TimeTickerEntity, CronTickerEntity>(
            new TickerMongoContext<TimeTickerEntity, CronTickerEntity>(database, "ticker_"),
            _clock,
            new SchedulerOptionsBuilder { NodeIdentifier = "node-A" });

        Func<Task> claim = path switch
        {
            "immediate-time" => () => node.AcquireImmediateTimeTickersAsync([id], CancellationToken.None),
            "immediate-cron" => () => node.AcquireImmediateCronOccurrencesAsync([id], CancellationToken.None),
            "fallback-time" => () => Drain(node.QueueTimedOutTimeTickers(CancellationToken.None)),
            _ => () => Drain(node.QueueTimedOutCronTickerOccurrences(CancellationToken.None))
        };
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

    // As above, and the store's own put-back (the next update) fails too, before it is sent. The row stays InProgress for
    // this node; the exception names it with the marker of the claim and its type, and a put-back with that marker frees it
    // once the store answers, which is what the scheduler loop does with it (candidate row 50).
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
        _clock.Advance(TimeSpan.FromSeconds(2));

        var claimLanded = 0;
        var putBackFailed = 0;
        var settings = MongoClientSettings.FromConnectionString(_f.Container.GetConnectionString());
        settings.ClusterConfigurator = builder =>
        {
            builder.Subscribe<CommandSucceededEvent>(e =>
            {
                if (e.CommandName == "update" && Interlocked.Exchange(ref claimLanded, 1) == 0)
                    throw new TimeoutException("the reply of the claim was lost");
            });
            builder.Subscribe<CommandStartedEvent>(e =>
            {
                if (e.CommandName == "update" && Volatile.Read(ref claimLanded) == 1 && Interlocked.Exchange(ref putBackFailed, 1) == 0)
                    throw new InvalidOperationException("the store failed again, before the put-back reached it");
            });
        };
        var database = new MongoClient(settings).GetDatabase(_f.Database.DatabaseNamespace.DatabaseName);
        var node = new TickerMongoPersistenceProvider<TimeTickerEntity, CronTickerEntity>(
            new TickerMongoContext<TimeTickerEntity, CronTickerEntity>(database, "ticker_"),
            _clock,
            new SchedulerOptionsBuilder { NodeIdentifier = "node-A" });

        Func<Task> claim = path switch
        {
            "immediate-time" => () => node.AcquireImmediateTimeTickersAsync([id], CancellationToken.None),
            "immediate-cron" => () => node.AcquireImmediateCronOccurrencesAsync([id], CancellationToken.None),
            "fallback-time" => () => Drain(node.QueueTimedOutTimeTickers(CancellationToken.None)),
            _ => () => Drain(node.QueueTimedOutCronTickerOccurrences(CancellationToken.None))
        };
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

    // A node whose n-th command of the given name throws before it is sent, once; every later command runs normally.
    private TickerMongoPersistenceProvider<TimeTickerEntity, CronTickerEntity> CreateFailingNode(string commandName, int n)
        => CreateNodeWithHook(commandName, n, () => throw new InvalidOperationException("store failed"));

    // A node that runs the hook just before its n-th command of the given name is sent, once.
    private TickerMongoPersistenceProvider<TimeTickerEntity, CronTickerEntity> CreateNodeWithHook(string commandName, int n, Action hook, ILogger? logger = null)
    {
        var seen = 0;
        var settings = MongoClientSettings.FromConnectionString(_f.Container.GetConnectionString());
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(e =>
        {
            if (e.CommandName == commandName && ++seen == n)
                hook();
        });
        var database = new MongoClient(settings).GetDatabase(_f.Database.DatabaseNamespace.DatabaseName);
        return new TickerMongoPersistenceProvider<TimeTickerEntity, CronTickerEntity>(
            new TickerMongoContext<TimeTickerEntity, CronTickerEntity>(database, "ticker_"),
            _clock,
            new SchedulerOptionsBuilder { NodeIdentifier = "node-A" },
            logger);
    }

    // Keeps the level and exception of each entry written.
    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, exception));
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

        _clock.Advance(TimeSpan.FromSeconds(2));
        var claimedByFallback = await Drain(fallbackNode.QueueTimedOutTimeTickers(CancellationToken.None));
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        var lateLoopWon = await nodeA.TryMarkTimeTickerInProgress(id, _clock.NextClaimMarker());

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

        _clock.Advance(TimeSpan.FromSeconds(2));
        var claimedByFallback = await Drain(nodeB.QueueTimedOutCronTickerOccurrences(CancellationToken.None));
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        var lateLoopWon = await nodeA.TryMarkCronOccurrenceInProgress(id, _clock.NextClaimMarker());

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
        var nodeBehind = CreateProvider("node-behind", new FakeClock(T0.AddHours(-2)));
        var first = await SeedTimeTicker();
        var second = await SeedTimeTicker();
        _clock.Advance(TimeSpan.FromSeconds(2));

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
        var nodeBehind = CreateProvider("node-behind", new FakeClock(T0.AddHours(-2)));
        var first = await QueueOccurrenceOn(nodeBehind);
        var second = await QueueOccurrenceOn(nodeBehind);
        _clock.Advance(TimeSpan.FromSeconds(2));

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
        await _f.TimeTickers.InsertOneAsync(seeded);
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
        var slowClock = new FakeClock(T0);
        var fastClock = new FakeClock(T0.AddSeconds(2));
        var owner = CreateProvider("node-slow", slowClock);
        var faster = CreateProvider("node-fast", fastClock);
        var id = await QueueTimeTickerOn(owner);

        var claimedByFaster = await Drain(faster.QueueTimedOutTimeTickers(CancellationToken.None));
        slowClock.Advance(TimeSpan.FromMilliseconds(300));
        var lateOwnerWon = await owner.TryMarkTimeTickerInProgress(id, _clock.NextClaimMarker());

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
        _clock.Advance(TimeSpan.FromSeconds(2));

        // Row 1: one TryMark wins, the twin's fallback then finds nothing to claim for it.
        var tryMarkWon = await first.TryMarkTimeTickerInProgress(markedFirst, _clock.NextClaimMarker());
        var twinTryMarkWon = await twin.TryMarkTimeTickerInProgress(markedFirst, _clock.NextClaimMarker());
        // Row 2: the twin's fallback wins, the other's TryMark then loses.
        var claimedByTwin = await Drain(twin.QueueTimedOutTimeTickers(CancellationToken.None));
        var lateTryMarkWon = await first.TryMarkTimeTickerInProgress(fallbackFirst, _clock.NextClaimMarker());

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
        var rows = await _f.CronTickerOccurrences.Find(x => x.CronTickerId == cronId).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("node-A", row.LockHolder);
        Assert.Equal(TickerStatus.Queued, row.Status);
    }

    #endregion

    [Fact]
    public async Task ClaimFanOut_TakesConcurrentClaims()
    {
        var fanOut = (IClaimFanOut)CreateProvider("node-A");

        Assert.Equal(16, await fanOut.GetMaxConcurrentClaimsAsync());
    }

    #region Putting back a claim whose job never started

    [Fact]
    public async Task ReleaseClaim_OwnClaim_PutsTheRowBackForTheFallback_TimeTicker()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueTimeTickerOn(nodeA);
        var marker = _clock.NextClaimMarker();
        Assert.True(await nodeA.TryMarkTimeTickerInProgress(id, marker));
        _clock.Advance(TimeSpan.FromMilliseconds(40));

        Assert.True(await nodeA.TryReleaseTimeTickerClaim(id, marker));

        var row = await ReadTimeTicker(id);
        Assert.Equal(TickerStatus.Idle, row.Status);
        Assert.Null(row.LockHolder);
        Assert.Null(row.LockedAt);
        Assert.Equal(_clock.UtcNow, row.UpdatedAt);
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Contains(await Drain(CreateProvider("node-B").QueueTimedOutTimeTickers(CancellationToken.None)), x => x.Id == id);
    }

    [Fact]
    public async Task ReleaseClaim_OwnClaim_PutsTheRowBack_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueOccurrenceOn(nodeA);
        var marker = _clock.NextClaimMarker();
        Assert.True(await nodeA.TryMarkCronOccurrenceInProgress(id, marker));

        Assert.True(await nodeA.TryReleaseCronOccurrenceClaim(id, marker));

        var row = await ReadOccurrence(id);
        Assert.Equal(TickerStatus.Idle, row.Status);
        Assert.Null(row.LockHolder);
    }

    // MongoDB keeps milliseconds, so this is where two writes of one node in the same millisecond would look alike without
    // the per-node marker: the loop's put-back must leave the row this node's fallback runs.
    [Fact]
    public async Task ReleaseClaim_RowTakenByTheSameNodesFallback_IsLeftRunning_TimeTicker()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueTimeTickerOn(nodeA);
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Contains(await Drain(nodeA.QueueTimedOutTimeTickers(CancellationToken.None)), x => x.Id == id);
        var loopMarker = _clock.NextClaimMarker();
        Assert.False(await nodeA.TryMarkTimeTickerInProgress(id, loopMarker));

        Assert.False(await nodeA.TryReleaseTimeTickerClaim(id, loopMarker));

        var row = await ReadTimeTicker(id);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("node-A", row.LockHolder);
    }

    [Fact]
    public async Task ReleaseClaim_RowTakenByTheSameNodesFallback_IsLeftRunning_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueOccurrenceOn(nodeA);
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Contains(await Drain(nodeA.QueueTimedOutCronTickerOccurrences(CancellationToken.None)), x => x.Id == id);
        var loopMarker = _clock.NextClaimMarker();
        Assert.False(await nodeA.TryMarkCronOccurrenceInProgress(id, loopMarker));

        Assert.False(await nodeA.TryReleaseCronOccurrenceClaim(id, loopMarker));

        Assert.Equal(TickerStatus.InProgress, (await ReadOccurrence(id)).Status);
    }

    // The fallback and run-now hand back each row they claimed with the marker their write gave it, so a job that never
    // starts can be put back with that marker.
    [Fact]
    public async Task FallbackClaim_ReturnsItsMarker_SoItCanBePutBack_TimeTicker()
    {
        var nodeB = CreateProvider("node-B");
        var id = await SeedTimeTicker();
        _clock.Advance(TimeSpan.FromSeconds(2));

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
        _clock.Advance(TimeSpan.FromSeconds(2));

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

    // The dashboard's "Run now" on a cron inserts an occurrence with only CronTickerId set, claims it with run-now and
    // reads the function, retries and intervals from occurrence.CronTicker. Without the cron the claimed row was
    // InProgress and the read threw, so the job never ran and nothing put the row back.
    [Fact]
    public async Task RunNowClaim_ReturnsTheCronOfEachOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var cron = new CronTickerEntity
        {
            Id = Guid.NewGuid(),
            Function = "run-now-cron-fn",
            Expression = "* * * * *",
            Retries = 3,
            RetryIntervals = [5, 10],
            CreatedAt = T0.AddHours(-2),
            UpdatedAt = T0.AddHours(-2),
            IsEnabled = true,
            Request = []
        };
        await _f.CronTickers.InsertOneAsync(cron);
        var occurrence = new CronTickerOccurrenceEntity<CronTickerEntity>
        {
            Id = Guid.NewGuid(),
            Status = TickerStatus.Idle,
            ExecutionTime = T0,
            CronTickerId = cron.Id
        };
        await nodeA.InsertCronTickerOccurrences([occurrence], CancellationToken.None);

        var taken = Assert.Single(await nodeA.AcquireImmediateCronOccurrencesAsync([occurrence.Id], CancellationToken.None));

        Assert.NotNull(taken.CronTicker);
        Assert.Equal(cron.Id, taken.CronTicker.Id);
        Assert.Equal("run-now-cron-fn", taken.CronTicker.Function);
        Assert.Equal(3, taken.CronTicker.Retries);
        Assert.Equal([5, 10], taken.CronTicker.RetryIntervals);
    }

    // Run-now returns only the rows its own call claimed. A row this node's fallback holds is already running; handing it
    // out again would run it twice, and its put-back would undo the fallback's claim.
    [Fact]
    public async Task RunNowClaim_DoesNotReturnARowThisNodesFallbackHolds_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var heldByFallback = await QueueOccurrenceOn(nodeA);
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Contains(await Drain(nodeA.QueueTimedOutCronTickerOccurrences(CancellationToken.None)), x => x.Id == heldByFallback);
        var idle = await QueueOccurrenceOn(nodeA);
        await nodeA.ReleaseAcquiredCronTickerOccurrences([idle], CancellationToken.None);

        var taken = await nodeA.AcquireImmediateCronOccurrencesAsync([heldByFallback, idle], CancellationToken.None);

        Assert.Equal([idle], taken.Select(x => x.Id));
    }

    [Fact]
    public async Task ReleaseClaim_WithAnotherMarkerOrNode_LeavesTheRow()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueTimeTickerOn(nodeA);
        var marker = _clock.NextClaimMarker();
        Assert.True(await nodeA.TryMarkTimeTickerInProgress(id, marker));

        Assert.False(await nodeA.TryReleaseTimeTickerClaim(id, marker.AddMilliseconds(1)));
        Assert.False(await CreateProvider("node-B").TryReleaseTimeTickerClaim(id, marker));

        var row = await ReadTimeTicker(id);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("node-A", row.LockHolder);
    }

    // The status write leaves UpdatedAt equal to the marker, so only the status tells the row moved on.
    [Theory]
    [InlineData(TickerStatus.Done)]
    [InlineData(TickerStatus.Failed)]
    [InlineData(TickerStatus.Cancelled)]
    [InlineData(TickerStatus.Skipped)]
    public async Task ReleaseClaim_RowNoLongerInProgress_LeavesTheRow(TickerStatus status)
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueTimeTickerOn(nodeA);
        var marker = _clock.NextClaimMarker();
        Assert.True(await nodeA.TryMarkTimeTickerInProgress(id, marker));
        await _f.TimeTickers.UpdateOneAsync(Builders<TimeTickerEntity>.Filter.Eq(x => x.Id, id), Builders<TimeTickerEntity>.Update.Set(x => x.Status, status));

        Assert.False(await nodeA.TryReleaseTimeTickerClaim(id, marker));

        Assert.Equal(status, (await ReadTimeTicker(id)).Status);
    }

    [Fact]
    public async Task ReleaseClaim_MissingRow_ReturnsFalse()
    {
        var nodeA = CreateProvider("node-A");

        Assert.False(await nodeA.TryReleaseTimeTickerClaim(Guid.NewGuid(), _clock.NextClaimMarker()));
        Assert.False(await nodeA.TryReleaseCronOccurrenceClaim(Guid.NewGuid(), _clock.NextClaimMarker()));
    }

    #endregion

    #region Children

    // Mongo stores each child as a document of its own, linked by ParentId.
    private async Task<(Guid ParentId, Guid ChildId)> SeedParentWithChild()
    {
        var child = NewTimeTicker();
        child.ExecutionTime = null;
        child.Function = "claim-child-fn";
        child.RunCondition = RunCondition.OnSuccess;
        var parent = NewTimeTicker();
        parent.Children = [child];
        await CreateProvider("seed").AddTimeTickers([parent], CancellationToken.None);
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

        _clock.Advance(TimeSpan.FromSeconds(2));
        var fallback = await Drain(nodeB.QueueTimedOutTimeTickers(CancellationToken.None));

        Assert.DoesNotContain(fallback, x => x.Id == childId);
        var taken = Assert.Single(fallback, x => x.Id == parentId);
        Assert.Equal([childId], taken.Children.Select(x => x.Id));
        Assert.False(await nodeA.TryMarkTimeTickerInProgress(parentId, _clock.NextClaimMarker()));
        Assert.False(await nodeA.TryMarkTimeTickerInProgress(childId, _clock.NextClaimMarker()));
        Assert.Empty(await Drain(nodeA.QueueTimedOutTimeTickers(CancellationToken.None)));
        Assert.Empty(await Drain(nodeB.QueueTimedOutTimeTickers(CancellationToken.None)));
    }

    // Parent -> child -> grandchild, where neither descendant has an execution time of its own; plus a delayed grandchild
    // (one with an execution time of its own), which the payload carries with its time so the handler never runs it inline.
    private async Task<(Guid ParentId, Guid ChildId, Guid GrandchildId, Guid ScheduledGrandchildId)> SeedThreeLevelChain()
    {
        var grandchild = NewTimeTicker();
        grandchild.ExecutionTime = null;
        grandchild.Function = "claim-grandchild-fn";
        grandchild.RunCondition = RunCondition.OnSuccess;
        var scheduledGrandchild = NewTimeTicker(executionTime: T0.AddHours(1));
        scheduledGrandchild.Function = "claim-scheduled-grandchild-fn";
        var child = NewTimeTicker();
        child.ExecutionTime = null;
        child.Function = "claim-child-fn";
        child.RunCondition = RunCondition.OnSuccess;
        child.Children = [grandchild, scheduledGrandchild];
        var parent = NewTimeTicker();
        parent.Children = [child];
        await CreateProvider("seed").AddTimeTickers([parent], CancellationToken.None);
        return (parent.Id, child.Id, grandchild.Id, scheduledGrandchild.Id);
    }

    private static void AssertCarriesTheChain(TimeTickerEntity parent, Guid childId, Guid grandchildId, Guid delayedGrandchildId)
    {
        var child = Assert.Single(parent.Children);
        Assert.Equal(childId, child.Id);
        Assert.Null(child.ExecutionTime);
        Assert.Equal(2, child.Children.Count);
        var grandchild = Assert.Single(child.Children, x => x.Id == grandchildId);
        Assert.Equal("claim-grandchild-fn", grandchild.Function);
        Assert.Equal(RunCondition.OnSuccess, grandchild.RunCondition);
        Assert.Null(grandchild.ExecutionTime);
        var delayedGrandchild = Assert.Single(child.Children, x => x.Id == delayedGrandchildId);
        Assert.Equal("claim-scheduled-grandchild-fn", delayedGrandchild.Function);
        Assert.Equal(T0.AddHours(1), delayedGrandchild.ExecutionTime);
    }

    [Fact]
    public async Task Window_CarriesTheGrandchildrenOfAChain()
    {
        var nodeA = CreateProvider("node-A");
        var (parentId, childId, grandchildId, delayedGrandchildId) = await SeedThreeLevelChain();

        var window = await nodeA.GetEarliestTimeTickers(CancellationToken.None);

        AssertCarriesTheChain(Assert.Single(window, x => x.Id == parentId), childId, grandchildId, delayedGrandchildId);
    }

    [Fact]
    public async Task Fallback_CarriesTheGrandchildrenOfAChain()
    {
        var nodeA = CreateProvider("node-A");
        var (parentId, childId, grandchildId, delayedGrandchildId) = await SeedThreeLevelChain();
        _clock.Advance(TimeSpan.FromSeconds(2));

        var fallback = await Drain(nodeA.QueueTimedOutTimeTickers(CancellationToken.None));

        AssertCarriesTheChain(Assert.Single(fallback, x => x.Id == parentId), childId, grandchildId, delayedGrandchildId);
    }

    [Fact]
    public async Task RunNow_CarriesTheGrandchildrenOfAChain()
    {
        var nodeA = CreateProvider("node-A");
        var (parentId, childId, grandchildId, delayedGrandchildId) = await SeedThreeLevelChain();

        var acquired = await nodeA.AcquireImmediateTimeTickersAsync([parentId], CancellationToken.None);

        AssertCarriesTheChain(Assert.Single(acquired, x => x.Id == parentId), childId, grandchildId, delayedGrandchildId);
    }

    // A delayed child (a child with an execution time of its own) is a row of its own. Every claim path carries it in its
    // parent's payload with its time, so the handler knows of it and never runs it inline.
    [Fact]
    public async Task DelayedChild_IsCarriedWithItsTime_ByEveryClaimPath()
    {
        var delayedChild = NewTimeTicker(executionTime: T0.AddHours(2));
        delayedChild.Function = "delayed-child-fn";
        var parent = NewTimeTicker();
        parent.Children = [delayedChild];
        await CreateProvider("seed").AddTimeTickers([parent], CancellationToken.None);

        void AssertCarried(TimeTickerEntity payload)
            => Assert.Equal(T0.AddHours(2), Assert.Single(payload.Children, x => x.Id == delayedChild.Id).ExecutionTime);

        AssertCarried(Assert.Single(await CreateProvider("node-A").GetEarliestTimeTickers(CancellationToken.None), x => x.Id == parent.Id));
        AssertCarried(Assert.Single(await CreateProvider("node-B").AcquireImmediateTimeTickersAsync([parent.Id], CancellationToken.None)));

        await _f.TimeTickers.UpdateOneAsync(x => x.Id == parent.Id, Builders<TimeTickerEntity>.Update
            .Set(x => x.Status, TickerStatus.Idle).Set(x => x.LockHolder, (string?)null).Set(x => x.LockedAt, (DateTime?)null));
        _clock.Advance(TimeSpan.FromSeconds(2));
        AssertCarried(Assert.Single(await Drain(CreateProvider("node-C").QueueTimedOutTimeTickers(CancellationToken.None)), x => x.Id == parent.Id));
    }

    // The skip write only touches a row that has not started: a delayed child the store already ran keeps its status.
    [Theory]
    [InlineData(TickerStatus.InProgress)]
    [InlineData(TickerStatus.Done)]
    [InlineData(TickerStatus.Failed)]
    public async Task SkippingChildren_LeavesARowThatAlreadyStarted(TickerStatus status)
    {
        var started = NewTimeTicker(status);
        var waiting = NewTimeTicker();
        await CreateProvider("seed").AddTimeTickers([started, waiting], CancellationToken.None);
        var skip = new InternalFunctionContext()
            .SetProperty(x => x.Status, TickerStatus.Skipped)
            .SetProperty(x => x.ExceptionDetails, ChainRunGate.NotMatchedReason);

        await ((IChildSkipWriter)CreateProvider("node-A")).SkipNotStartedTimeTickersAsync([started.Id, waiting.Id], skip, CancellationToken.None);

        Assert.Equal(status, (await ReadTimeTicker(started.Id)).Status);
        Assert.Equal(TickerStatus.Skipped, (await ReadTimeTicker(waiting.Id)).Status);
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
        await CreateProvider("seed").AddTimeTickers([parent], CancellationToken.None);
        return (parent.Id, child.Id);
    }

    public static IEnumerable<object?[]> EveryConditionAndParentStatus()
        => from condition in Enum.GetValues<RunCondition>().Select(c => (RunCondition?)c).Append(null)
           from status in Enum.GetValues<TickerStatus>()
           select new object?[] { condition, status };

    // The gate is built from ChainRunGate; run-now must agree with it everywhere.
    [Theory]
    [MemberData(nameof(EveryConditionAndParentStatus))]
    public async Task DelayedChild_IsHandedOutExactlyWhenItsParentsStatusLetsItRun(RunCondition? condition, TickerStatus parentStatus)
    {
        var (_, childId) = await SeedDelayedChild(parentStatus, condition, T0.AddSeconds(30));

        var claimed = await CreateProvider("node-A").AcquireImmediateTimeTickersAsync([childId], CancellationToken.None);

        Assert.Equal(ChainRunGate.Allows(condition, parentStatus), claimed.Any(x => x.Id == childId));
        if (!claimed.Any(x => x.Id == childId))
            Assert.Equal(TickerStatus.Idle, (await ReadTimeTicker(childId)).Status);
    }

    // Run-now with several rows claims each row whose gate is open and leaves the others waiting.
    [Fact]
    public async Task RunNow_ClaimsTheRowsWhoseGateIsOpen_AndLeavesAWaitingDelayedChild()
    {
        var (_, waitingChildId) = await SeedDelayedChild(TickerStatus.InProgress, RunCondition.OnSuccess, T0.AddSeconds(30));
        var (_, readyChildId) = await SeedDelayedChild(TickerStatus.Done, RunCondition.OnSuccess, T0.AddSeconds(30));
        var plainId = await SeedTimeTicker(executionTime: T0.AddSeconds(30));

        var claimed = await CreateProvider("node-A").AcquireImmediateTimeTickersAsync([waitingChildId, readyChildId, plainId], CancellationToken.None);

        Assert.Equal(new[] { readyChildId, plainId }.OrderBy(x => x), claimed.Select(x => x.Id).OrderBy(x => x));
        Assert.Equal(TickerStatus.Idle, (await ReadTimeTicker(waitingChildId)).Status);
    }

    // The window's gate is part of the earliest-time query (an aggregation); it must agree with ChainRunGate everywhere.
    [Theory]
    [MemberData(nameof(EveryConditionAndParentStatus))]
    public async Task DelayedChild_IsInTheWindowExactlyWhenItsParentsStatusLetsItRun(RunCondition? condition, TickerStatus parentStatus)
    {
        var (_, childId) = await SeedDelayedChild(parentStatus, condition, T0.AddSeconds(30));

        var window = await CreateProvider("node-A").GetEarliestTimeTickers(CancellationToken.None);

        Assert.Equal(ChainRunGate.Allows(condition, parentStatus), window.Any(x => x.Id == childId));
    }

    // An overdue delayed child is claimed by the fallback when its parent's status lets it run, skipped when its parent
    // ended without a match, and left waiting otherwise.
    [Theory]
    [MemberData(nameof(EveryConditionAndParentStatus))]
    public async Task OverdueDelayedChild_IsClaimedSkippedOrLeftByTheFallback_ExactlyAsChainRunGateSays(RunCondition? condition, TickerStatus parentStatus)
    {
        var (_, childId) = await SeedDelayedChild(parentStatus, condition, T0.AddSeconds(-30));

        var fallback = await Drain(CreateProvider("node-A").QueueTimedOutTimeTickers(CancellationToken.None));

        var claimed = fallback.Any(x => x.Id == childId);
        var status = (await ReadTimeTicker(childId)).Status;
        if (ChainRunGate.Allows(condition, parentStatus))
            Assert.True(claimed, "a child its parent's status lets run was not claimed");
        else if (ChainRunGate.IsFinal(parentStatus))
            Assert.True(!claimed && status == TickerStatus.Skipped, $"claimed={claimed}, status={status}; expected Skipped");
        else
            Assert.True(!claimed && status == TickerStatus.Idle, $"claimed={claimed}, status={status}; expected it to wait");
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
        Assert.Equal(TickerStatus.Idle, (await ReadTimeTicker(childId)).Status);
    }

    [Fact]
    public async Task DelayedChild_IsHandedOutByTheWindowAtItsTime_OnceItsParentSucceeded()
    {
        var (_, childId) = await SeedDelayedChild(TickerStatus.Done, RunCondition.OnSuccess, T0.AddSeconds(30));
        var node = CreateProvider("node-A");

        var window = await node.GetEarliestTimeTickers(CancellationToken.None);

        Assert.Equal([childId], window.Select(x => x.Id));
        Assert.Single(await Drain(node.QueueTimeTickers(window, CancellationToken.None)));
    }

    [Fact]
    public async Task DelayedChild_IsHandedOutByTheFallback_WhenItsTimePassedBeforeItsParentSucceeded()
    {
        var (_, childId) = await SeedDelayedChild(TickerStatus.Done, RunCondition.OnSuccess, T0.AddSeconds(-30));

        var fallback = await Drain(CreateProvider("node-A").QueueTimedOutTimeTickers(CancellationToken.None));

        Assert.Contains(fallback, x => x.Id == childId);
    }

    // The gate is part of the earliest-time query: a waiting delayed child due first must not hide the rows due after it.
    [Fact]
    public async Task AWaitingDelayedChildDueFirst_DoesNotHideTheJobsDueAfterIt()
    {
        await SeedDelayedChild(TickerStatus.InProgress, RunCondition.OnSuccess, T0.AddSeconds(10));
        var laterId = await SeedTimeTicker(executionTime: T0.AddSeconds(20));

        var window = await CreateProvider("node-A").GetEarliestTimeTickers(CancellationToken.None);

        Assert.Equal([laterId], window.Select(x => x.Id));
    }

    // A waiting delayed child in the same second as a row that is due must not go out with it.
    [Fact]
    public async Task AWaitingDelayedChild_IsLeftOutOfTheWindowItsSecondOpens()
    {
        var (_, childId) = await SeedDelayedChild(TickerStatus.InProgress, RunCondition.OnSuccess, T0.AddSeconds(10).AddMilliseconds(100));
        var dueId = await SeedTimeTicker(executionTime: T0.AddSeconds(10).AddMilliseconds(200));

        var window = await CreateProvider("node-A").GetEarliestTimeTickers(CancellationToken.None);

        Assert.Equal([dueId], window.Select(x => x.Id));
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
        Assert.Equal(TickerStatus.Idle, (await ReadTimeTicker(grandchild.Id)).Status);
        Assert.Empty(await Drain(node.QueueTimedOutTimeTickers(CancellationToken.None)));

        var skipped = await ReadTimeTicker(childId);
        Assert.Equal(TickerStatus.Skipped, skipped.Status);
        Assert.Equal(ChainRunGate.NotMatchedReason, skipped.SkippedReason);
        Assert.Equal(T0, skipped.ExecutedAt);
        Assert.Null(skipped.LockHolder);
        Assert.Equal(TickerStatus.Skipped, (await ReadTimeTicker(grandchild.Id)).Status);
    }

    // A delayed child is a row of its own; removing its parent removes it, and its children, with it.
    [Fact]
    public async Task RemovingAParent_RemovesItsDelayedDescendants()
    {
        var grandchild = NewTimeTicker(executionTime: T0.AddHours(3));
        var (parentId, childId) = await SeedDelayedChild(TickerStatus.Done, RunCondition.OnSuccess, T0.AddHours(2), grandchild);

        await CreateProvider("node-A").RemoveTimeTickers([parentId], CancellationToken.None);

        Assert.Equal(0, await _f.TimeTickers.CountDocumentsAsync(x => x.Id == parentId || x.Id == childId || x.Id == grandchild.Id));
    }

    // A row whose parent is not found waits: it is not handed out, not skipped, and does not hide the rows due after it.
    [Fact]
    public async Task ADelayedChildWhoseParentIsMissing_Waits_AndDoesNotHideTheJobsDueAfterIt()
    {
        var overdueOrphan = NewTimeTicker(executionTime: T0.AddSeconds(-30));
        overdueOrphan.ParentId = Guid.NewGuid();
        var orphan = NewTimeTicker(executionTime: T0.AddSeconds(10));
        orphan.ParentId = Guid.NewGuid();
        await _f.TimeTickers.InsertManyAsync([overdueOrphan, orphan]);
        var laterId = await SeedTimeTicker(executionTime: T0.AddSeconds(20));
        var node = CreateProvider("node-A");

        Assert.Equal([laterId], (await node.GetEarliestTimeTickers(CancellationToken.None)).Select(x => x.Id));
        Assert.Empty(await Drain(node.QueueTimedOutTimeTickers(CancellationToken.None)));
        Assert.Empty(await node.AcquireImmediateTimeTickersAsync([overdueOrphan.Id, orphan.Id], CancellationToken.None));
        Assert.Equal(TickerStatus.Idle, (await ReadTimeTicker(overdueOrphan.Id)).Status);
        Assert.Equal(TickerStatus.Idle, (await ReadTimeTicker(orphan.Id)).Status);
    }

    // The fallback reads the parents' statuses before it claims: a parent it takes in the same pass has not ended, so its
    // overdue delayed child waits for the parent's run.
    [Fact]
    public async Task TheFallback_TakesAnOverdueParent_ButNotItsOverdueDelayedChild_InTheSamePass()
    {
        var child = NewTimeTicker(executionTime: T0.AddSeconds(-30));
        child.Function = "delayed-child-fn";
        child.RunCondition = RunCondition.OnSuccess;
        var parent = NewTimeTicker(executionTime: T0.AddSeconds(-60));
        parent.Children = [child];
        await CreateProvider("seed").AddTimeTickers([parent], CancellationToken.None);

        var fallback = await Drain(CreateProvider("node-A").QueueTimedOutTimeTickers(CancellationToken.None));

        Assert.Equal([parent.Id], fallback.Select(x => x.Id));
        Assert.Equal(TickerStatus.Idle, (await ReadTimeTicker(child.Id)).Status);
    }

    // A class map of the user's own that stores the statuses as strings: the gate writes its values with the members'
    // serializers, so it matches the stored rows as the typed filters do.
    public sealed class StringStatusTicker : TimeTickerEntity<StringStatusTicker>;

    private static readonly Lazy<bool> StringStatusClassMaps = new(() =>
    {
        BsonClassMap.RegisterClassMap<TimeTickerEntity<StringStatusTicker>>(cm =>
        {
            cm.AutoMap();
            cm.SetIgnoreExtraElements(true);
            cm.UnmapMember(x => x.Parent);
            cm.UnmapMember(x => x.Children);
            cm.MapMember(x => x.Status).SetSerializer(new EnumSerializer<TickerStatus>(BsonType.String));
            cm.MapMember(x => x.RunCondition).SetSerializer(new NullableSerializer<RunCondition>(new EnumSerializer<RunCondition>(BsonType.String)));
        });
        BsonClassMap.RegisterClassMap<StringStatusTicker>(cm => { cm.AutoMap(); cm.SetIgnoreExtraElements(true); });
        return true;
    });

    [Fact]
    public async Task TheGate_FollowsAClassMapThatStoresStatusesAsStrings()
    {
        _ = StringStatusClassMaps.Value;
        var context = new TickerMongoContext<StringStatusTicker, CronTickerEntity>(_f.Database, $"strings_{Guid.NewGuid():N}_");
        var node = new TickerMongoPersistenceProvider<StringStatusTicker, CronTickerEntity>(
            context, _clock, new SchedulerOptionsBuilder { NodeIdentifier = "node-A" });

        StringStatusTicker Row(TickerStatus status, DateTime time, RunCondition? condition = null) => new()
        {
            Id = Guid.NewGuid(), Function = "claim-fn", ExecutionTime = time, Status = status, RunCondition = condition,
            CreatedAt = T0.AddHours(-2), UpdatedAt = T0.AddHours(-1), Request = []
        };
        var readyChild = Row(TickerStatus.Idle, T0.AddSeconds(10), RunCondition.OnSuccess);
        var doneParent = Row(TickerStatus.Done, T0.AddHours(-1));
        doneParent.Children = [readyChild];
        var blockedChild = Row(TickerStatus.Idle, T0.AddSeconds(5), RunCondition.OnSuccess);
        var failedParent = Row(TickerStatus.Failed, T0.AddHours(-1));
        failedParent.Children = [blockedChild];
        var overdueChild = Row(TickerStatus.Idle, T0.AddSeconds(-30));
        var cancelledParent = Row(TickerStatus.Cancelled, T0.AddHours(-1));
        cancelledParent.Children = [overdueChild];
        await node.AddTimeTickers([doneParent, failedParent, cancelledParent], CancellationToken.None);

        var stored = await context.TimeTickers.Database.GetCollection<BsonDocument>(context.TimeTickers.CollectionNamespace.CollectionName)
            .Find(new BsonDocument("_id", new BsonBinaryData(doneParent.Id, GuidRepresentation.Standard))).SingleAsync();
        Assert.Equal("Done", stored["Status"].AsString);

        Assert.Equal([readyChild.Id], (await node.GetEarliestTimeTickers(CancellationToken.None)).Select(x => x.Id));
        Assert.Empty(await node.AcquireImmediateTimeTickersAsync([blockedChild.Id], CancellationToken.None));
        Assert.Equal([overdueChild.Id], (await Drain(node.QueueTimedOutTimeTickers(CancellationToken.None))).Select(x => x.Id));
    }

    // The fallback's skip is read first, then written by id: a row that started in between keeps its status.
    [Fact]
    public async Task TheFallbacksSkip_LeavesARowThatStartedAfterItWasRead()
    {
        var (_, childId) = await SeedDelayedChild(TickerStatus.Failed, RunCondition.OnSuccess, T0.AddSeconds(-30));
        var node = CreateNodeWithHook("update", 1, () => _f.TimeTickers.UpdateOne(
            x => x.Id == childId,
            Builders<TimeTickerEntity>.Update.Set(x => x.Status, TickerStatus.InProgress).Set(x => x.LockHolder, "node-B")));

        Assert.Empty(await Drain(node.QueueTimedOutTimeTickers(CancellationToken.None)));

        Assert.Equal(TickerStatus.InProgress, (await ReadTimeTicker(childId)).Status);
    }

    // A failed skip write must not stop the fallback from taking the other overdue jobs; the next pass tries again.
    [Fact]
    public async Task AFailedSkipWrite_DoesNotStopTheFallbackFromTakingTheOtherJobs()
    {
        var (_, childId) = await SeedDelayedChild(TickerStatus.Failed, RunCondition.OnSuccess, T0.AddSeconds(-30));
        var dueId = await SeedTimeTicker(executionTime: T0.AddSeconds(-30));
        var logger = new ListLogger();
        var node = CreateNodeWithHook("update", 1, () => throw new InvalidOperationException("store failed"), logger);

        var fallback = await Drain(node.QueueTimedOutTimeTickers(CancellationToken.None));

        Assert.Contains(fallback, x => x.Id == dueId);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("store failed", entry.Exception?.Message);
        Assert.Equal(TickerStatus.Idle, (await ReadTimeTicker(childId)).Status);
        Assert.Empty(await Drain(CreateProvider("node-A").QueueTimedOutTimeTickers(CancellationToken.None)));
        Assert.Equal(TickerStatus.Skipped, (await ReadTimeTicker(childId)).Status);
    }

    #endregion

    #region Racing

    // Eight nodes run both the fenced claim and the fallback against every row at once. Odd nodes have a clock ten
    // seconds behind: whatever they write is older than the UpdatedAt seeded on the rows, so only the fallback's
    // status guard (not its UpdatedAt comparison) stops a stale snapshot from taking a row that already runs.
    // Whatever the interleaving, each row has exactly one winner.
    [Fact]
    public async Task Claim_ManyNodesRacing_EachRowHasExactlyOneWinner()
    {
        const int nodeCount = 8;
        const int rowCount = 200;
        var providers = Enumerable.Range(0, nodeCount)
            .Select(i => CreateProvider($"node-{i}", new FakeClock(i % 2 == 0 ? T0 : T0.AddSeconds(-10))))
            .ToArray();
        var rows = Enumerable.Range(0, rowCount)
            .Select(i => NewTimeTicker(TickerStatus.Queued, lockHolder: $"node-{i % nodeCount}", updatedAt: T0, executionTime: T0.AddSeconds(-20)))
            .ToArray();
        await _f.TimeTickers.InsertManyAsync(rows);
        var ids = rows.Select(x => x.Id).ToArray();
        var winners = new ConcurrentBag<Guid>();
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
                if (await node.TryMarkTimeTickerInProgress(id, _clock.NextClaimMarker()))
                    winners.Add(id);
        }

        var tasks = providers.SelectMany(node => new[] { Task.Run(() => Fallback(node)), Task.Run(() => TryMark(node)) }).ToArray();
        start.Set();
        await Task.WhenAll(tasks);

        var perRow = winners.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(rowCount, perRow.Count);
        Assert.All(perRow, pair => Assert.Equal(1, pair.Value));
        Assert.Equal(rowCount, await _f.TimeTickers.CountDocumentsAsync(x => x.Status == TickerStatus.InProgress));
    }

    #endregion
}
