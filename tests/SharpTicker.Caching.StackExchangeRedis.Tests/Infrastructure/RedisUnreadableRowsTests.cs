using System.Text.Json;
using Microsoft.Extensions.Logging;
using SharpTicker.Caching.StackExchangeRedis.Helpers;
using SharpTicker.Utilities;
using static SharpTicker.Caching.StackExchangeRedis.DependencyInjection.ServiceExtension;
using SharpTicker.Caching.StackExchangeRedis.Infrastructure;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>
/// Rows a version before 1.3.0 left in Redis with an empty collection stored as an object, written here as the raw text
/// such a version stored, over a real Redis.
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class RedisUnreadableRowsTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private const string Node = "node-A";

    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeTickerClock _clock = new(T0);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TickerRedisPersistenceProvider<TimeTickerEntity, CronTickerEntity> Provider(string node = Node)
        => fixture.CreateProvider(node, _clock);

    private static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    private static InternalFunctionContext StatusContext(Guid id, TickerStatus status)
        => new InternalFunctionContext { TickerId = id }.SetProperty(x => x.Status, status);

    private async Task<string> RawAsync(string key) => (string)(await fixture.Database.StringGetAsync(key))!;

    // The row as the key holds it, read without the provider.
    private async Task<(int Status, string? LockHolder)> StoredAsync(string key)
    {
        using var doc = JsonDocument.Parse(await RawAsync(key));
        var holder = doc.RootElement.GetProperty("LockHolder");
        return (doc.RootElement.GetProperty("Status").GetInt32(), holder.ValueKind == JsonValueKind.Null ? null : holder.GetString());
    }

    private static readonly (int, string?) Released = ((int)TickerStatus.Idle, null);

    #region A time job a version before 1.3.0 left InProgress

    // The text of a due time job as such a version left it after its claim script ran: InProgress, held by the node, the
    // key order of cjson, and the empty Children collection written as {} (or as [] for the control).
    private async Task<Guid> SeedRowLeftInProgressAsync(string children, string holder = Node, DateTime? lockedAt = null)
    {
        var id = Guid.NewGuid();
        var locked = (lockedAt ?? T0.AddMinutes(-5).AddTicks(1234567)).ToString("O");
        var json = "{\"Description\":null,\"Retries\":0,\"ExecutedAt\":null,\"ExceptionMessage\":null,"
                   + $"\"CreatedAt\":\"{T0.AddHours(-1):O}\",\"Status\":2,\"UpdatedAt\":\"{locked}\",\"Children\":{children},"
                   + $"\"RetryCount\":0,\"SkippedReason\":null,\"ElapsedTime\":0,\"Id\":\"{id}\",\"ParentId\":null,\"RunCondition\":null,"
                   + $"\"Request\":\"\",\"ExecutionTime\":\"{T0.AddMinutes(-6):O}\",\"LockHolder\":\"{holder}\",\"LockedAt\":\"{locked}\","
                   + "\"Function\":\"older-fn\",\"RetryIntervals\":null,\"InitIdentifier\":null}";

        var db = fixture.Database;
        await db.StringSetAsync(RedisKeyBuilder.TimeTickerKey(id), json);
        await db.SetAddAsync(RedisKeyBuilder.TimeTickerIdsKey, id.ToString());
        await db.SortedSetAddAsync(RedisKeyBuilder.TimeTickerPendingKey, id.ToString(), RedisKeyBuilder.ToScore(T0.AddMinutes(-6)));
        return id;
    }

    private Task<(int Status, string? LockHolder)> StoredTimeTickerAsync(Guid id) => StoredAsync(RedisKeyBuilder.TimeTickerKey(id));

    // The start of a node releases what its earlier run left locked. Control: the same row with Children as an array.
    [Fact]
    public async Task ARowLeftRunning_WithChildrenAsAnArray_IsReleasedByTheStartOfItsNode()
    {
        var id = await SeedRowLeftInProgressAsync("[]");

        await Provider().ReleaseDeadNodeTimeTickerResources(Node, T0, CancellationToken.None);

        Assert.Equal(Released, await StoredTimeTickerAsync(id));
    }

    [Fact]
    public async Task ARowLeftRunning_WithChildrenAsAnObject_IsReleasedByTheStartOfItsNode()
    {
        var id = await SeedRowLeftInProgressAsync("{}");

        await Provider().ReleaseDeadNodeTimeTickerResources(Node, T0, CancellationToken.None);

        Assert.Equal(Released, await StoredTimeTickerAsync(id));
    }

    // Once released, the row is stored with an array, and the fallback hands it out one time.
    [Fact]
    public async Task ARowLeftRunning_WithChildrenAsAnObject_IsStoredWithAnArrayOnceReleased_AndHandedOutOnce()
    {
        var id = await SeedRowLeftInProgressAsync("{}");
        await Provider().ReleaseDeadNodeTimeTickerResources(Node, T0, CancellationToken.None);

        var stored = await RawAsync(RedisKeyBuilder.TimeTickerKey(id));
        Assert.Contains("\"Children\":[]", stored);
        Assert.DoesNotContain("\"Children\":{}", stored);

        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal([id], (await Drain(Provider().QueueTimedOutTimeTickers(CancellationToken.None))).Select(x => x.Id));
        Assert.Empty(await Drain(Provider().QueueTimedOutTimeTickers(CancellationToken.None)));
    }

    // The release rule still decides: a row this run locked, or a row of another node, keeps its lock.
    [Fact]
    public async Task ARowWithChildrenAsAnObject_LockedByThisRunOrByAnotherNode_KeepsItsLock()
    {
        var ofThisRun = await SeedRowLeftInProgressAsync("{}", lockedAt: T0.AddSeconds(1));
        var ofAnotherNode = await SeedRowLeftInProgressAsync("{}", holder: "node-B");

        await Provider().ReleaseDeadNodeTimeTickerResources(Node, T0, CancellationToken.None);

        Assert.Equal(((int)TickerStatus.InProgress, Node), await StoredTimeTickerAsync(ofThisRun));
        Assert.Equal(((int)TickerStatus.InProgress, "node-B"), await StoredTimeTickerAsync(ofAnotherNode));
    }

    // The release writes through the compare-and-set on the text the key holds: it lands in one round, and a write another
    // node made in between is kept.
    [Fact]
    public async Task TheReleaseOfARowWithChildrenAsAnObject_LandsInOneRound()
    {
        var id = await SeedRowLeftInProgressAsync("{}");
        var provider = Provider();
        var rounds = 0;
        provider.BeforeCompareAndSetHook = _ => { rounds++; return Task.CompletedTask; };

        await provider.ReleaseDeadNodeTimeTickerResources(Node, T0, CancellationToken.None);

        Assert.Equal(1, rounds);
        Assert.Equal(Released, await StoredTimeTickerAsync(id));
    }

    [Fact]
    public async Task TheReleaseOfARowWithChildrenAsAnObject_DoesNotOverwriteAWriteMadeInBetween()
    {
        var id = await SeedRowLeftInProgressAsync("{}");
        var key = RedisKeyBuilder.TimeTickerKey(id);
        var provider = Provider();
        var rounds = 0;
        provider.BeforeCompareAndSetHook = async _ =>
        {
            // Another writer ends the row between the read and the set of the first round.
            if (rounds++ == 0)
                await fixture.Database.StringSetAsync(key, (await RawAsync(key)).Replace("\"Status\":2", $"\"Status\":{(int)TickerStatus.Done}"));
        };

        await provider.ReleaseDeadNodeTimeTickerResources(Node, T0, CancellationToken.None);

        Assert.Equal(((int)TickerStatus.Done, Node), await StoredTimeTickerAsync(id));
    }

    // What the dashboard and GetTimeTickerById read. Reading changes nothing in the store.
    [Fact]
    public async Task ARowLeftRunning_WithChildrenAsAnObject_IsRead_AndReadingLeavesTheStoredTextAsItWas()
    {
        var id = await SeedRowLeftInProgressAsync("{}");
        var before = await RawAsync(RedisKeyBuilder.TimeTickerKey(id));
        var provider = Provider();

        var row = await provider.GetTimeTickerById(id, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Contains(await provider.GetTimeTickers(null!, CancellationToken.None), x => x.Id == id);

        Assert.Equal(before, await RawAsync(RedisKeyBuilder.TimeTickerKey(id)));
    }

    [Fact]
    public async Task AnUpdateOfARowWithChildrenAsAnObject_IsStored()
    {
        var id = await SeedRowLeftInProgressAsync("{}");

        Assert.Equal(1, await Provider().UpdateTimeTicker(StatusContext(id, TickerStatus.Done), CancellationToken.None));

        Assert.Equal((int)TickerStatus.Done, (await StoredTimeTickerAsync(id)).Status);
    }

    // A row that is not readable for another reason is still reported, and the warning names that row's key, in the reads
    // that load many rows at once.
    [Fact]
    public async Task ARowBrokenForAnotherReason_IsReportedWithItsOwnKey_WhenManyRowsAreLoaded()
    {
        var readable = await SeedRowLeftInProgressAsync("[]");
        var repaired = await SeedRowLeftInProgressAsync("{}");
        var broken = await SeedRowLeftInProgressAsync("{\"a\":1}");
        var log = new ListLogger<TickerRedisPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();
        var provider = new TickerRedisPersistenceProvider<TimeTickerEntity, CronTickerEntity>(fixture.Database, _clock,
            new SchedulerOptionsBuilder { NodeIdentifier = Node }, new SharpTickerRedisOptionBuilder { JsonSerializerContext = TestJsonSerializerContext.Default }, log);

        var listed = await provider.GetTimeTickers(null!, CancellationToken.None);
        await provider.ReleaseDeadNodeTimeTickerResources(Node, T0, CancellationToken.None);

        Assert.Equal(new[] { readable, repaired }.Order(), listed.Select(x => x.Id).Order());
        var warnings = log.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.Contains(RedisKeyBuilder.TimeTickerKey(broken), w.Message));
        Assert.Equal(((int)TickerStatus.InProgress, Node), await StoredTimeTickerAsync(broken));
    }

    // Another node that finds the holder dead releases the same row through the script, which reads any shape.
    [Fact]
    public async Task ARowLeftRunning_WithChildrenAsAnObject_IsReleasedWhenAnotherNodeFindsItsHolderDead()
    {
        var id = await SeedRowLeftInProgressAsync("{}");

        await Provider("node-B").ReleaseDeadNodeTimeTickerResources(Node, CancellationToken.None);

        Assert.Equal(Released, await StoredTimeTickerAsync(id));
    }

    #endregion

    #region A cron occurrence whose cron ticker has its empty RetryIntervals as an object

    private async Task<Guid> SeedOccurrenceLeftInProgressAsync(string retryIntervals)
    {
        var id = Guid.NewGuid();
        var cronId = Guid.NewGuid();
        var locked = T0.AddMinutes(-5).AddTicks(1234567).ToString("O");
        var json = $"{{\"Id\":\"{id}\",\"Status\":2,\"LockHolder\":\"{Node}\",\"ExecutionTime\":\"{T0.AddMinutes(-6):O}\","
                   + $"\"CronTickerId\":\"{cronId}\",\"LockedAt\":\"{locked}\",\"ExecutedAt\":null,"
                   + $"\"CronTicker\":{{\"Expression\":\"* * * * *\",\"Request\":\"\",\"Retries\":0,\"RetryIntervals\":{retryIntervals},"
                   + $"\"IsEnabled\":true,\"Id\":\"{cronId}\",\"Function\":\"older-cron-fn\",\"Description\":null,\"InitIdentifier\":null,"
                   + $"\"CreatedAt\":\"{T0.AddHours(-1):O}\",\"UpdatedAt\":\"{T0.AddHours(-1):O}\"}},"
                   + "\"ExceptionMessage\":null,\"SkippedReason\":null,\"ElapsedTime\":0,\"RetryCount\":0,"
                   + $"\"CreatedAt\":\"{T0.AddHours(-1):O}\",\"UpdatedAt\":\"{locked}\"}}";

        var db = fixture.Database;
        await db.StringSetAsync(RedisKeyBuilder.CronOccurrenceKey(id), json);
        await db.SetAddAsync(RedisKeyBuilder.CronOccurrenceIdsKey, id.ToString());
        return id;
    }

    [Fact]
    public async Task AnOccurrenceLeftRunning_WithIntervalsAsAnArray_IsReleasedByTheStartOfItsNode()
    {
        var id = await SeedOccurrenceLeftInProgressAsync("[]");

        await Provider().ReleaseDeadNodeOccurrenceResources(Node, T0, CancellationToken.None);

        Assert.Equal(Released, await StoredAsync(RedisKeyBuilder.CronOccurrenceKey(id)));
    }

    [Fact]
    public async Task AnOccurrenceLeftRunning_WithIntervalsAsAnObject_IsReleasedByTheStartOfItsNode()
    {
        var id = await SeedOccurrenceLeftInProgressAsync("{}");

        await Provider().ReleaseDeadNodeOccurrenceResources(Node, T0, CancellationToken.None);

        Assert.Equal(Released, await StoredAsync(RedisKeyBuilder.CronOccurrenceKey(id)));
    }

    #endregion
}
