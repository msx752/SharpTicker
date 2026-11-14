using System.Reflection;
using StackExchange.Redis;
using SharpTicker.Caching.StackExchangeRedis.Helpers;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>
/// A pending entry whose row can no longer be claimed (#57). The row key and the index keys live in different hash slots,
/// so they are written by separate commands, and a write that lands in between can leave such an entry behind.
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class RedisStalePendingEntryTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeTickerClock _clock = new(T0);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> CreateProvider(string nodeIdentifier, IDatabase? database = null)
        => fixture.CreateProvider(nodeIdentifier, _clock, database);

    private static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    private static TimeTickerEntity NewTimeTicker(DateTime executionTime) => new()
    {
        Id = Guid.NewGuid(),
        Function = "stale-fn",
        ExecutionTime = executionTime,
        Status = TickerStatus.Idle,
        CreatedAt = T0.AddHours(-2),
        UpdatedAt = T0.AddHours(-1).AddTicks(1234567),
        Request = []
    };

    private static InternalFunctionContext StatusContext(Guid id, TickerStatus status)
        => new InternalFunctionContext { TickerId = id }.SetProperty(x => x.Status, status);

    private static (DateTime, InternalManagerContext[]) CronSlot()
        => (T0, [new InternalManagerContext(Guid.NewGuid()) { FunctionName = "stale-cron-fn", Expression = "* * * * *" }]);

    private Task<double?> PendingScore(RedisKey key, Guid id) => fixture.Database.SortedSetScoreAsync(key, id.ToString());

    // The put-back writes the pending entry of the row first, then the row. When the row ends in between, the put-back
    // changes nothing, and the entry it wrote must not stay on a row that never runs again.
    [Fact]
    public async Task APutBack_WhoseRowEndsBeforeItIsWritten_LeavesNoPendingEntry_TimeTicker()
    {
        var node = CreateProvider("node-A");
        var ticker = NewTimeTicker(T0);
        await node.AddTimeTickers([ticker], CancellationToken.None);
        var window = await node.GetEarliestTimeTickers(CancellationToken.None);
        Assert.Single(await Drain(node.QueueTimeTickers(window.Where(x => x.Id == ticker.Id).ToArray(), CancellationToken.None)));
        var marker = _clock.NextClaimMarker();
        Assert.True(await node.TryMarkTimeTickerInProgress(ticker.Id, marker));
        Assert.Null(await PendingScore(RedisKeyBuilder.TimeTickerPendingKey, ticker.Id));

        var hook = HookAfterRowRead(() => node.UpdateTimeTicker(StatusContext(ticker.Id, TickerStatus.Done), CancellationToken.None), out var hooked);

        Assert.False(await CreateProvider("node-A", hooked).TryReleaseTimeTickerClaim(ticker.Id, marker));

        Assert.True(hook.Ran);
        Assert.Equal(TickerStatus.Done, (await node.GetTimeTickerById(ticker.Id, CancellationToken.None))!.Status);
        Assert.Null(await PendingScore(RedisKeyBuilder.TimeTickerPendingKey, ticker.Id));
    }

    [Fact]
    public async Task APutBack_WhoseRowEndsBeforeItIsWritten_LeavesNoPendingEntry_CronOccurrence()
    {
        var node = CreateProvider("node-A");
        var id = Assert.Single(await Drain(node.QueueCronTickerOccurrences(CronSlot(), CancellationToken.None))).Id;
        var marker = _clock.NextClaimMarker();
        Assert.True(await node.TryMarkCronOccurrenceInProgress(id, marker));
        Assert.Null(await PendingScore(RedisKeyBuilder.CronOccurrencePendingKey, id));

        var hook = HookAfterRowRead(() => node.UpdateCronTickerOccurrence(StatusContext(id, TickerStatus.Done), CancellationToken.None), out var hooked);

        Assert.False(await CreateProvider("node-A", hooked).TryReleaseCronOccurrenceClaim(id, marker));

        Assert.True(hook.Ran);
        Assert.Equal(TickerStatus.Done, (await node.GetAllCronTickerOccurrences(x => x.Id == id, CancellationToken.None)).Single().Status);
        Assert.Null(await PendingScore(RedisKeyBuilder.CronOccurrencePendingKey, id));
    }

    // A put-back whose row was released meanwhile (its node was taken for dead) and is now queued by another node: the
    // entry is that node's, and the job would be found by nothing without it. A put-back that finds the row not ended
    // leaves its index to the row's own writes.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APutBack_WhoseRowIsNowAnotherNodes_LeavesItsPendingEntry(bool queuedByTheOtherNode)
    {
        var nodeA = CreateProvider("node-A");
        var nodeB = CreateProvider("node-B");
        var ticker = NewTimeTicker(T0);
        await nodeA.AddTimeTickers([ticker], CancellationToken.None);
        var window = await nodeA.GetEarliestTimeTickers(CancellationToken.None);
        Assert.Single(await Drain(nodeA.QueueTimeTickers(window.Where(x => x.Id == ticker.Id).ToArray(), CancellationToken.None)));
        var marker = _clock.NextClaimMarker();
        Assert.True(await nodeA.TryMarkTimeTickerInProgress(ticker.Id, marker));

        var hook = HookAfterRowRead(async () =>
        {
            await nodeB.ReleaseDeadNodeTimeTickerResources("node-A", CancellationToken.None);
            if (queuedByTheOtherNode)
            {
                var windowOfB = await nodeB.GetEarliestTimeTickers(CancellationToken.None);
                Assert.Single(await Drain(nodeB.QueueTimeTickers(windowOfB.Where(x => x.Id == ticker.Id).ToArray(), CancellationToken.None)));
            }
        }, out var hooked);

        Assert.False(await CreateProvider("node-A", hooked).TryReleaseTimeTickerClaim(ticker.Id, marker));

        Assert.True(hook.Ran);
        var row = (await nodeA.GetTimeTickerById(ticker.Id, CancellationToken.None))!;
        Assert.Equal(queuedByTheOtherNode ? (TickerStatus.Queued, "node-B") : (TickerStatus.Idle, (string?)null), (row.Status, row.LockHolder));
        Assert.NotNull(await PendingScore(RedisKeyBuilder.TimeTickerPendingKey, ticker.Id));
    }

    // The control: a put-back that lands puts the row back with its pending entry, so it is found again.
    [Fact]
    public async Task APutBackThatLands_KeepsThePendingEntry()
    {
        var node = CreateProvider("node-A");
        var id = Assert.Single(await Drain(node.QueueCronTickerOccurrences(CronSlot(), CancellationToken.None))).Id;
        var marker = _clock.NextClaimMarker();
        Assert.True(await node.TryMarkCronOccurrenceInProgress(id, marker));

        Assert.True(await node.TryReleaseCronOccurrenceClaim(id, marker));

        Assert.NotNull(await PendingScore(RedisKeyBuilder.CronOccurrencePendingKey, id));
    }

    // A pending entry left on an ended row (an index write that never landed) is dropped by the fallback, for cron
    // occurrences as it already was for time tickers; otherwise the fallback tries to claim it on every pass.
    [Fact]
    public async Task AStalePendingEntryOfAnEndedRow_IsDroppedByTheFallback()
    {
        var node = CreateProvider("node-A");
        var ticker = NewTimeTicker(T0);
        await node.AddTimeTickers([ticker], CancellationToken.None);
        await node.UpdateTimeTicker(StatusContext(ticker.Id, TickerStatus.Done), CancellationToken.None);
        await fixture.Database.SortedSetAddAsync(RedisKeyBuilder.TimeTickerPendingKey, ticker.Id.ToString(), T0.Ticks);

        var occurrenceId = Assert.Single(await Drain(node.QueueCronTickerOccurrences(CronSlot(), CancellationToken.None))).Id;
        await node.UpdateCronTickerOccurrence(StatusContext(occurrenceId, TickerStatus.Done), CancellationToken.None);
        await fixture.Database.SortedSetAddAsync(RedisKeyBuilder.CronOccurrencePendingKey, occurrenceId.ToString(), T0.Ticks);

        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(await Drain(node.QueueTimedOutTimeTickers(CancellationToken.None)));
        Assert.Empty(await Drain(node.QueueTimedOutCronTickerOccurrences(CancellationToken.None)));

        Assert.Null(await PendingScore(RedisKeyBuilder.TimeTickerPendingKey, ticker.Id));
        Assert.Null(await PendingScore(RedisKeyBuilder.CronOccurrencePendingKey, occurrenceId));
        Assert.Equal(TickerStatus.Done, (await node.GetAllCronTickerOccurrences(x => x.Id == occurrenceId, CancellationToken.None)).Single().Status);
    }

    // The fallback leaves the entry of an occurrence another node holds as it is: that node writes its index.
    [Fact]
    public async Task ThePendingEntryOfAnOccurrenceAnotherNodeRuns_IsLeftAsItIs()
    {
        var nodeA = CreateProvider("node-A");
        var occurrenceId = Assert.Single(await Drain(nodeA.QueueCronTickerOccurrences(CronSlot(), CancellationToken.None))).Id;
        Assert.True(await nodeA.TryMarkCronOccurrenceInProgress(occurrenceId, _clock.NextClaimMarker()));
        await fixture.Database.SortedSetAddAsync(RedisKeyBuilder.CronOccurrencePendingKey, occurrenceId.ToString(), T0.Ticks);

        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(await Drain(CreateProvider("node-B").QueueTimedOutCronTickerOccurrences(CancellationToken.None)));

        Assert.NotNull(await PendingScore(RedisKeyBuilder.CronOccurrencePendingKey, occurrenceId));
    }

    // A pending entry ahead of now whose row this node cannot claim (here: run now and still InProgress, its index write
    // lost) does not define the scheduler loop's window: the rows due after it are still found.
    [Fact]
    public async Task AFutureEntryOfARowThatCannotBeClaimed_DoesNotHideTheRowsDueAfterIt()
    {
        var node = CreateProvider("node-A");
        var running = NewTimeTicker(T0.AddMinutes(1));
        var next = NewTimeTicker(T0.AddMinutes(2));
        await node.AddTimeTickers([running, next], CancellationToken.None);
        Assert.Single(await node.AcquireImmediateTimeTickersAsync([running.Id], CancellationToken.None));
        await fixture.Database.SortedSetAddAsync(RedisKeyBuilder.TimeTickerPendingKey, running.Id.ToString(), RedisKeyBuilder.ToScore(running.ExecutionTime!.Value));

        var window = await node.GetEarliestTimeTickers(CancellationToken.None);

        Assert.Equal([next.Id], window.Select(x => x.Id));
    }

    private sealed class HookState
    {
        public bool Ran;
    }

    // The hook runs once, right after the put-back has read the row and before it writes the index and the row.
    private HookState HookAfterRowRead(Func<Task> between, out IDatabase hooked)
    {
        var state = new HookState();
        hooked = DispatchProxy.Create<IDatabase, RedisClaimMultiNodeTests.HookedDatabase>();
        var hook = (RedisClaimMultiNodeTests.HookedDatabase)hooked;
        hook.Inner = fixture.Database;
        hook.HookOn = nameof(IDatabase.StringGetAsync);
        hook.AfterNextCall = async () =>
        {
            await between();
            state.Ran = true;
        };
        return state;
    }
}
