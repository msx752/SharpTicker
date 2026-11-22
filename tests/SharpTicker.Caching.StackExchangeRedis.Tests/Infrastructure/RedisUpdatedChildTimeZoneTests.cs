using NSubstitute;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Managers;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>
/// #257 on Redis: <see cref="ITimeTickerManager{TTimeTicker}.UpdateAsync"/> of a chain's root converts a child time
/// given as Local or Unspecified to UTC, as on add, and a child read back from the store (which the Redis store returns as UTC)
/// keeps its time. The manager over the real Redis store; the scheduler time zone is Tokyo (UTC+9).
/// The scheduler time zone is process-wide and other collections set it when they build a host, so this class runs in a
/// collection of its own that does not run in parallel with the others.
/// </summary>
[Collection(RedisTimeZoneCollection.Name)]
public class RedisUpdatedChildTimeZoneTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private const string Function = "UpdatedChildTimeZone.Redis.Job";
    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
    private static readonly DateTime RootUtc = new(2026, 6, 15, 6, 0, 0, DateTimeKind.Utc);

    private TimeZoneInfo _zoneBefore = null!;
    private ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> _store = null!;
    private ITimeTickerManager<TimeTickerEntity> _tickers = null!;

    public Task InitializeAsync()
    {
        TickerFunctionProvider.RegisterFunctions(new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>
        {
            [Function] = ("", TickerTaskPriority.Normal, (_, _, _) => Task.CompletedTask, 0)
        });
        TickerFunctionProvider.Build();

        var clock = new FakeTickerClock(RootUtc.AddDays(-1));
        _store = fixture.CreateProvider("updated-child-time-zone", clock);
        var dispatcher = Substitute.For<ISharpTickerDispatcher>();
        dispatcher.IsEnabled.Returns(false);
        _tickers = new TickerManager<TimeTickerEntity, CronTickerEntity>(
            _store, Substitute.For<ISharpTickerHostScheduler>(), clock, Substitute.For<ISharpTickerNotificationHubSender>(),
            new TickerExecutionContext(), dispatcher);

        _zoneBefore = CronScheduleCache.TimeZoneInfo;
        CronScheduleCache.TimeZoneInfo = Tokyo;
        return fixture.ResetAsync();
    }

    public Task DisposeAsync()
    {
        CronScheduleCache.TimeZoneInfo = _zoneBefore;
        return fixture.ResetAsync();
    }

    private async Task<(Guid Root, Guid Child)> AddChainAsync()
    {
        var chain = new TimeTickerEntity
        {
            Function = Function, ExecutionTime = RootUtc,
            Children = [new TimeTickerEntity { Function = Function, RunCondition = RunCondition.OnSuccess, ExecutionTime = RootUtc.AddHours(1) }]
        };
        var added = await _tickers.AddAsync(chain);
        Assert.True(added.IsSucceeded, added.Exception?.ToString());
        return (chain.Id, chain.Children.Single().Id);
    }

    private async Task<DateTime> StoredTimeAsync(Guid id) => (await _store.GetTimeTickerById(id)).ExecutionTime!.Value;

    [Theory]
    [InlineData(DateTimeKind.Local, false)]
    [InlineData(DateTimeKind.Local, true)]
    [InlineData(DateTimeKind.Unspecified, false)]
    [InlineData(DateTimeKind.Unspecified, true)]
    public async Task AChildTimeGivenAsLocalOrUnspecifiedOnUpdate_IsStoredAsTheUtcInstant(DateTimeKind kind, bool batch)
    {
        var (rootId, childId) = await AddChainAsync();
        var given = DateTime.SpecifyKind(RootUtc.Date.AddDays(1).AddHours(15), kind);
        var expected = kind == DateTimeKind.Local ? given.ToUniversalTime() : TimeZoneInfo.ConvertTimeToUtc(given, Tokyo);
        var root = new TimeTickerEntity
        {
            Id = rootId, Function = Function, ExecutionTime = RootUtc,
            Children = [new TimeTickerEntity { Id = childId, ParentId = rootId, Function = Function, RunCondition = RunCondition.OnSuccess, ExecutionTime = given }]
        };

        var succeeded = batch ? (await _tickers.UpdateBatchAsync([root])).IsSucceeded : (await _tickers.UpdateAsync(root)).IsSucceeded;

        Assert.True(succeeded);
        Assert.Same(Tokyo, CronScheduleCache.TimeZoneInfo);
        var stored = await StoredTimeAsync(childId);
        Assert.True(stored.Ticks == expected.Ticks, $"{kind}, batch {batch}: given {given:O}, stored {stored:O}, the UTC instant is {expected:O}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AChainReadBackFromTheStoreAndUpdated_KeepsItsTimes(bool batch)
    {
        var (rootId, childId) = await AddChainAsync();
        var root = await _store.GetTimeTickerById(rootId);
        var child = await _store.GetTimeTickerById(childId);
        Assert.Equal(DateTimeKind.Utc, root.ExecutionTime!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, child.ExecutionTime!.Value.Kind);
        root.Children = [child];
        root.Description = "edited";

        var succeeded = batch ? (await _tickers.UpdateBatchAsync([root])).IsSucceeded : (await _tickers.UpdateAsync(root)).IsSucceeded;

        Assert.True(succeeded);
        Assert.Equal(RootUtc.Ticks, (await StoredTimeAsync(rootId)).Ticks);
        Assert.Equal(RootUtc.AddHours(1).Ticks, (await StoredTimeAsync(childId)).Ticks);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RedisTimeZoneCollection : ICollectionFixture<RedisTestFixture>
{
    public const string Name = "Redis, scheduler time zone";
}
