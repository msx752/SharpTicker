using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using SharpTicker.Caching.StackExchangeRedis.Helpers;
using SharpTicker.Caching.StackExchangeRedis.Infrastructure;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using static SharpTicker.Caching.StackExchangeRedis.DependencyInjection.ServiceExtension;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>An application's own time ticker type with numbers of its own.</summary>
public sealed class NumberedTimeTicker : TimeTickerEntity<NumberedTimeTicker>
{
    public long Ticks { get; set; }

    public double Ratio { get; set; }
}

public sealed class NumberedCronTicker : CronTickerEntity;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(NumberedTimeTicker))]
[JsonSerializable(typeof(NumberedCronTicker))]
[JsonSerializable(typeof(CronTickerOccurrenceEntity<NumberedCronTicker>))]
internal partial class NumberedJsonSerializerContext : JsonSerializerContext;

/// <summary>
/// #151: a number of an application's own ticker type comes back as it was stored after every write the store makes to
/// claim, release or skip the row. Up to 1.5.1 those writes were Lua scripts that decoded and encoded the row with the
/// JSON library of Redis, which keeps 14 significant digits: a <c>long</c> of 15 digits or more came back in exponent
/// form and the row could no longer be read.
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class RedisOwnNumberTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private const string Node = "node-A";
    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    private readonly FakeTickerClock _clock = new(T0);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TickerRedisPersistenceProvider<NumberedTimeTicker, NumberedCronTicker> Provider(string node = Node)
        => new(fixture.Database, _clock, new SchedulerOptionsBuilder { NodeIdentifier = node },
            new SharpTickerRedisOptionBuilder { JsonSerializerContext = NumberedJsonSerializerContext.Default },
            NullLogger<TickerRedisPersistenceProvider<NumberedTimeTicker, NumberedCronTicker>>.Instance);

    private static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    // 123 and 14 digits came back as stored before the fix too: the controls.
    public static TheoryData<long, double> Numbers => new()
    {
        { 123L, 0.5 },
        { 99_999_999_999_999L, 0.25 },
        { 100_000_000_000_000L, 0.12345678901234559 },
        { long.MaxValue, 1234567890.1234567 },
    };

    private async Task<NumberedTimeTicker> AddAsync(long ticks, double ratio, Guid? parentId = null, RunCondition? condition = null,
        DateTime? executionTime = null)
    {
        var ticker = new NumberedTimeTicker
        {
            Id = Guid.NewGuid(), Function = "numbered-fn", ExecutionTime = executionTime ?? T0.AddMinutes(-1),
            CreatedAt = T0.AddHours(-2), UpdatedAt = T0.AddHours(-1), Ticks = ticks, Ratio = ratio, ParentId = parentId,
            RunCondition = condition
        };
        await Provider().AddTimeTickers([ticker], CancellationToken.None);
        return ticker;
    }

    private async Task AssertNumbersKeptAsync(Guid id, long ticks, double ratio, TickerStatus status)
    {
        var raw = (string)(await fixture.Database.StringGetAsync(RedisKeyBuilder.TimeTickerKey(id)))!;
        var row = await Provider("reader").GetTimeTickerById(id, CancellationToken.None);
        Assert.True(row != null, $"the row could not be read back: {raw}");
        Assert.Equal(status, row!.Status);
        Assert.Equal(ticks, row.Ticks);
        Assert.Equal(ratio, row.Ratio);
    }

    [Theory]
    [MemberData(nameof(Numbers))]
    public async Task TheFallbacksClaim_KeepsTheNumbers(long ticks, double ratio)
    {
        var added = await AddAsync(ticks, ratio);

        Assert.Equal([added.Id], (await Drain(Provider().QueueTimedOutTimeTickers(CancellationToken.None))).Select(x => x.Id));

        await AssertNumbersKeptAsync(added.Id, ticks, ratio, TickerStatus.InProgress);
    }

    [Theory]
    [MemberData(nameof(Numbers))]
    public async Task TheLoopsClaim_QueuedThenInProgress_KeepsTheNumbers(long ticks, double ratio)
    {
        var added = await AddAsync(ticks, ratio);
        var provider = Provider();
        var stored = await provider.GetTimeTickerById(added.Id, CancellationToken.None);

        Assert.Single(await Drain(provider.QueueTimeTickers([new TimeTickerEntity { Id = added.Id, UpdatedAt = stored.UpdatedAt }], CancellationToken.None)));
        await AssertNumbersKeptAsync(added.Id, ticks, ratio, TickerStatus.Queued);
        Assert.True(await provider.TryMarkTimeTickerInProgress(added.Id, _clock.NextClaimMarker(), CancellationToken.None));

        await AssertNumbersKeptAsync(added.Id, ticks, ratio, TickerStatus.InProgress);
    }

    [Theory]
    [MemberData(nameof(Numbers))]
    public async Task RunNow_KeepsTheNumbers(long ticks, double ratio)
    {
        var added = await AddAsync(ticks, ratio, executionTime: T0.AddHours(1));

        Assert.Single(await Provider().AcquireImmediateTimeTickersAsync([added.Id], CancellationToken.None));

        await AssertNumbersKeptAsync(added.Id, ticks, ratio, TickerStatus.InProgress);
    }

    [Theory]
    [MemberData(nameof(Numbers))]
    public async Task TheReleaseOfAQueuedRow_KeepsTheNumbers(long ticks, double ratio)
    {
        var added = await AddAsync(ticks, ratio);
        var provider = Provider();
        var stored = await provider.GetTimeTickerById(added.Id, CancellationToken.None);
        Assert.Single(await Drain(provider.QueueTimeTickers([new TimeTickerEntity { Id = added.Id, UpdatedAt = stored.UpdatedAt }], CancellationToken.None)));

        await provider.ReleaseAcquiredTimeTickers([added.Id], CancellationToken.None);

        await AssertNumbersKeptAsync(added.Id, ticks, ratio, TickerStatus.Idle);
    }

    [Theory]
    [MemberData(nameof(Numbers))]
    public async Task TheReleaseOfAStoppedNode_KeepsTheNumbers(long ticks, double ratio)
    {
        var added = await AddAsync(ticks, ratio);
        Assert.Single(await Drain(Provider("node-X").QueueTimedOutTimeTickers(CancellationToken.None)));

        await Provider("node-B").ReleaseDeadNodeTimeTickerResources("node-X", CancellationToken.None);

        await AssertNumbersKeptAsync(added.Id, ticks, ratio, TickerStatus.Idle);
    }

    [Theory]
    [MemberData(nameof(Numbers))]
    public async Task TheSkipOfADelayedChild_KeepsTheNumbers(long ticks, double ratio)
    {
        var parent = new NumberedTimeTicker
        {
            Id = Guid.NewGuid(), Function = "numbered-fn", ExecutionTime = T0.AddHours(-1), Status = TickerStatus.Failed,
            ExecutedAt = T0.AddMinutes(-30), CreatedAt = T0.AddHours(-2), UpdatedAt = T0.AddMinutes(-30)
        };
        await Provider().AddTimeTickers([parent], CancellationToken.None);
        var child = await AddAsync(ticks, ratio, parent.Id, RunCondition.OnSuccess);

        Assert.Empty(await Drain(Provider().QueueTimedOutTimeTickers(CancellationToken.None)));

        await AssertNumbersKeptAsync(child.Id, ticks, ratio, TickerStatus.Skipped);
    }
}
