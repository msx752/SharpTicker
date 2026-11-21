using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using SharpTicker.Caching.StackExchangeRedis.Helpers;
using SharpTicker.Caching.StackExchangeRedis.Infrastructure;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using static SharpTicker.Caching.StackExchangeRedis.DependencyInjection.ServiceExtension;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>
/// #246: a stored row of an application's own type that reads but cannot be serialized again (its label is in the older
/// form, see <see cref="LabelledTimeTicker"/>), next to rows that can, over a real Redis. Each claim, release and skip
/// that goes over many rows leaves that row as it is, with a warning that names its key, and goes on with the others.
/// A failure of Redis itself still fails the call.
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class RedisRowThatDoesNotWriteBackTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private const string Node = "node-A";
    private const string DeadNode = "node-dead";

    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    // The lowest id there is, so the row comes first wherever rows are read in the order of their ids.
    private static readonly Guid FirstId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly FakeTickerClock _clock = new(T0);
    private readonly ListLogger<TickerRedisPersistenceProvider<LabelledTimeTicker, CronTickerEntity>> _log = new();

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TickerRedisPersistenceProvider<LabelledTimeTicker, CronTickerEntity> Provider()
        => new(fixture.Database, _clock, new SchedulerOptionsBuilder { NodeIdentifier = Node }, new SharpTickerRedisOptionBuilder(), _log);

    private static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    private async Task<Guid> AddAsync(DateTime executionTime, Guid? id = null, TickerStatus status = TickerStatus.Idle, string? holder = null)
    {
        var ticker = new LabelledTimeTicker
        {
            Id = id ?? Guid.NewGuid(), Function = "labelled-fn", ExecutionTime = executionTime, Status = status,
            LockHolder = holder, LockedAt = holder == null ? null : T0.AddMinutes(-30),
            CreatedAt = T0.AddHours(-2), UpdatedAt = T0.AddHours(-1), Label = "current"
        };
        await Provider().AddTimeTickers([ticker], CancellationToken.None);
        return ticker.Id;
    }

    // The row as an earlier version of the application wrote it: its label in the older form. Returns the text stored.
    private async Task<string> WriteLabelInTheOlderFormAsync(Guid id)
    {
        var key = RedisKeyBuilder.TimeTickerKey(id);
        var row = JsonNode.Parse((string)(await fixture.Database.StringGetAsync(key))!)!.AsObject();
        var name = row.Select(p => p.Key).Single(k => string.Equals(k, nameof(LabelledTimeTicker.Label), StringComparison.OrdinalIgnoreCase));
        row[name] = 17;
        var text = row.ToJsonString();
        Assert.True(await fixture.Database.StringSetAsync(key, text));
        return text;
    }

    private async Task<string> RawAsync(Guid id) => (string)(await fixture.Database.StringGetAsync(RedisKeyBuilder.TimeTickerKey(id)))!;

    // The row is left exactly as it was, and every entry at Warning or above is the warning that names its key.
    private async Task AssertLeftAsItIsAndReported(Guid id, string stored)
    {
        Assert.Equal(stored, await RawAsync(id));

        var problems = _log.Entries.Where(e => e.Level >= LogLevel.Warning).ToList();
        Assert.NotEmpty(problems);
        Assert.All(problems, p =>
        {
            Assert.Equal(LogLevel.Warning, p.Level);
            Assert.Contains($"Redis key '{RedisKeyBuilder.TimeTickerKey(id)}' cannot be written as {nameof(LabelledTimeTicker)}", p.Message);
            Assert.IsType<NotSupportedException>(p.Exception);
        });
    }

    // What the tests below rest on: the row reads, and serializing what was read throws.
    [Fact]
    public async Task ARowInTheOlderForm_Reads_ButIsNotWrittenAgain()
    {
        var id = await AddAsync(T0.AddMinutes(-1));
        await WriteLabelInTheOlderFormAsync(id);

        var read = await Provider().GetTimeTickerById(id, CancellationToken.None);

        Assert.Equal("older:17", read.Label);
        Assert.Throws<NotSupportedException>(() => System.Text.Json.JsonSerializer.Serialize(read));
    }

    [Fact]
    public async Task TheLoopsClaim_LeavesTheRow_AndClaimsTheOthersOfItsSecond()
    {
        var bad = await AddAsync(T0, FirstId);
        var stored = await WriteLabelInTheOlderFormAsync(bad);
        Guid[] others = [await AddAsync(T0), await AddAsync(T0)];
        var provider = Provider();

        var window = await provider.GetEarliestTimeTickers(CancellationToken.None);
        Assert.Contains(window, t => t.Id == bad);
        var claimed = await Drain(provider.QueueTimeTickers(window, CancellationToken.None));

        Assert.Equal(others.Order(), claimed.Select(t => t.Id).Order());
        await AssertLeftAsItIsAndReported(bad, stored);
    }

    [Fact]
    public async Task TheFallback_LeavesTheRow_AndClaimsTheOverdueRowsBehindIt()
    {
        var bad = await AddAsync(T0.AddMinutes(-5), FirstId);
        var stored = await WriteLabelInTheOlderFormAsync(bad);
        Guid[] others = [await AddAsync(T0.AddMinutes(-5)), await AddAsync(T0.AddMinutes(-4)), await AddAsync(T0.AddMinutes(-3))];

        var claimed = await Drain(Provider().QueueTimedOutTimeTickers(CancellationToken.None));

        Assert.Equal(others.Order(), claimed.Select(t => t.Id).Order());
        await AssertLeftAsItIsAndReported(bad, stored);
    }

    [Fact]
    public async Task TheRunNowClaim_LeavesTheRow_AndClaimsTheOthers()
    {
        var bad = await AddAsync(T0.AddMinutes(5), FirstId);
        var stored = await WriteLabelInTheOlderFormAsync(bad);
        Guid[] others = [await AddAsync(T0.AddMinutes(5)), await AddAsync(T0.AddMinutes(6))];

        var claimed = await Provider().AcquireImmediateTimeTickersAsync([bad, .. others], CancellationToken.None);

        Assert.Equal(others.Order(), claimed.Select(t => t.Id).Order());
        await AssertLeftAsItIsAndReported(bad, stored);
    }

    // The release of everything this node holds (the loop's error handling) goes over every row: it does not stop at the
    // row, and the rows this node holds are released.
    [Fact]
    public async Task TheReleaseOfEveryRow_LeavesTheRow_AndReleasesTheOthers()
    {
        var bad = await AddAsync(T0.AddMinutes(5), FirstId);
        var stored = await WriteLabelInTheOlderFormAsync(bad);
        var others = new List<Guid>();
        for (var i = 0; i < 5; i++)
            others.Add(await AddAsync(T0.AddMinutes(5), status: TickerStatus.Queued, holder: Node));
        var provider = Provider();

        await provider.ReleaseAcquiredTimeTickers([], CancellationToken.None);

        foreach (var id in others)
        {
            var row = await provider.GetTimeTickerById(id, CancellationToken.None);
            Assert.Equal(TickerStatus.Idle, row.Status);
            Assert.Null(row.LockHolder);
        }
        await AssertLeftAsItIsAndReported(bad, stored);
    }

    [Fact]
    public async Task TheReleaseOfADeadNodesRows_LeavesTheRow_AndReleasesTheOthers()
    {
        var bad = await AddAsync(T0.AddMinutes(5), FirstId, TickerStatus.Queued, DeadNode);
        var stored = await WriteLabelInTheOlderFormAsync(bad);
        var others = new List<Guid>();
        for (var i = 0; i < 5; i++)
            others.Add(await AddAsync(T0.AddMinutes(5), status: TickerStatus.Queued, holder: DeadNode));
        var provider = Provider();

        await provider.ReleaseDeadNodeTimeTickerResources(DeadNode, CancellationToken.None);

        foreach (var id in others)
        {
            var row = await provider.GetTimeTickerById(id, CancellationToken.None);
            Assert.Equal(TickerStatus.Idle, row.Status);
            Assert.Null(row.LockHolder);
        }
        await AssertLeftAsItIsAndReported(bad, stored);
    }

    // Only the serializer's failure is taken: when Redis fails during the claim's write, the fallback pass fails as
    // before, and nothing is claimed.
    [Fact]
    public async Task AFailureOfRedisDuringTheFallbacksClaim_StillFailsThePass()
    {
        var id = await AddAsync(T0.AddMinutes(-5));
        var before = await RawAsync(id);
        var provider = Provider();
        provider.BeforeCompareAndSetHook = _ => throw new RedisTimeoutException("simulated timeout of the claim's write", CommandStatus.Unknown);

        await Assert.ThrowsAsync<RedisTimeoutException>(() => Drain(provider.QueueTimedOutTimeTickers(CancellationToken.None)));

        Assert.Equal(before, await RawAsync(id));
        Assert.DoesNotContain(_log.Entries, e => e.Message.Contains("cannot be written"));
    }

    // The same for the release of every row: a failure of Redis is not taken for a row that cannot be written.
    [Fact]
    public async Task AFailureOfRedisDuringTheReleaseOfEveryRow_StillFailsTheRelease()
    {
        await AddAsync(T0.AddMinutes(5), status: TickerStatus.Queued, holder: Node);
        var provider = Provider();
        provider.BeforeCompareAndSetHook = _ => throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "simulated lost connection");

        await Assert.ThrowsAsync<RedisConnectionException>(() => provider.ReleaseAcquiredTimeTickers([], CancellationToken.None));

        Assert.DoesNotContain(_log.Entries, e => e.Message.Contains("cannot be written"));
    }
}
