using StackExchange.Redis;
using SharpTicker.Caching.StackExchangeRedis.Helpers;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Managers;
using SharpTicker.Utilities.Models;
using NSubstitute;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>
/// The Lua scripts decode a row with cjson and write it back. cjson has no empty-array type, so it writes an
/// empty array as {}, which System.Text.Json cannot read into a collection property: the row would stay stored but
/// become unreadable. These tests run every script on real Redis and read the rows back through the provider.
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class RedisLuaRoundTripTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    // A tick count that does not end in 0, so the stored JSON text equals DateTime.ToString("O") and the
    // compare-and-set in QueueTimeTickers can match.
    private static readonly DateTime SeedUpdatedAt = T0.AddHours(-1).AddTicks(1234567);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    public static TheoryData<string> Scripts => new() { "Acquire", "Release", "RecoverDeadNode" };

    private static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    // Empty Children (also on the nested child) and empty RetryIntervals, the shapes cjson rewrites.
    internal static TimeTickerEntity NewTimeTicker(
        string function = "roundtrip-fn",
        TickerStatus status = TickerStatus.Idle,
        string? lockHolder = null,
        DateTime? executionTime = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Function = function,
            ExecutionTime = executionTime ?? T0,
            Status = status,
            LockHolder = lockHolder,
            LockedAt = lockHolder is null ? null : T0.AddHours(-1),
            CreatedAt = T0.AddHours(-2),
            UpdatedAt = SeedUpdatedAt,
            Request = [],
            RetryIntervals = [],
            Children = [new TimeTickerEntity { Id = Guid.NewGuid(), Function = "roundtrip-child", RetryIntervals = [], Children = [] }]
        };

    private async Task<string> RawJson(Guid id)
        => (string)(await fixture.Database.StringGetAsync(RedisKeyBuilder.TimeTickerKey(id)))!;

    private async Task<string> RawOccurrenceJson(Guid id)
        => (string)(await fixture.Database.StringGetAsync(RedisKeyBuilder.CronOccurrenceKey(id)))!;

    private static void AssertEmptyCollectionsAreArrays(string json)
    {
        Assert.DoesNotContain("{}", json);
        Assert.Contains("\"RetryIntervals\":[]", json);
    }

    // Runs one script on a row that is stored in a form every script accepts, then reads the result back.
    [Theory]
    [MemberData(nameof(Scripts))]
    public async Task LuaScripts_KeepEmptyCollectionsAsArrays(string script)
    {
        var node = fixture.CreateProvider("node-A", new FakeTickerClock(T0));
        var ticker = NewTimeTicker(
            status: script == "RecoverDeadNode" ? TickerStatus.Queued : TickerStatus.Idle,
            lockHolder: script == "RecoverDeadNode" ? "node-dead" : null);
        await node.AddTimeTickers([ticker], CancellationToken.None);

        switch (script)
        {
            case "Acquire":
                Assert.Single(await node.AcquireImmediateTimeTickersAsync([ticker.Id], CancellationToken.None));
                break;
            case "Release":
                await node.ReleaseAcquiredTimeTickers([ticker.Id], CancellationToken.None);
                break;
            default:
                await node.ReleaseDeadNodeTimeTickerResources("node-dead", CancellationToken.None);
                break;
        }

        var json = await RawJson(ticker.Id);
        AssertEmptyCollectionsAreArrays(json);
        Assert.Contains("\"Children\":[{", json);
        var read = await node.GetTimeTickerById(ticker.Id, CancellationToken.None);
        Assert.NotNull(read);
        Assert.Empty(read!.Children.Single().Children);
        Assert.Empty(read.RetryIntervals!);
    }

    [Fact]
    public async Task LuaScripts_KeepEmptyChildrenAsArray()
    {
        var node = fixture.CreateProvider("node-A", new FakeTickerClock(T0));
        var ticker = NewTimeTicker();
        ticker.Children = [];
        await node.AddTimeTickers([ticker], CancellationToken.None);

        Assert.Single(await node.AcquireImmediateTimeTickersAsync([ticker.Id], CancellationToken.None));

        Assert.Contains("\"Children\":[]", await RawJson(ticker.Id));
        Assert.Empty((await node.GetTimeTickerById(ticker.Id, CancellationToken.None))!.Children);
    }

    // The occurrence carries the cron ticker nested inside, with its own RetryIntervals.
    [Fact]
    public async Task LuaScripts_KeepEmptyCollectionsAsArrays_CronOccurrence()
    {
        var node = fixture.CreateProvider("node-A", new FakeTickerClock(T0));
        var cronId = Guid.NewGuid();
        (DateTime Key, InternalManagerContext[] Items) slot =
            (T0, [new InternalManagerContext(cronId) { FunctionName = "roundtrip-cron", Expression = "* * * * *", RetryIntervals = [] }]);
        var occurrenceId = Assert.Single(await Drain(node.QueueCronTickerOccurrences(slot, CancellationToken.None))).Id;

        await node.ReleaseAcquiredCronTickerOccurrences([occurrenceId], CancellationToken.None);
        Assert.Contains("\"RetryIntervals\":[]", await RawOccurrenceJson(occurrenceId));
        Assert.DoesNotContain("{}", await RawOccurrenceJson(occurrenceId));

        slot.Items[0].NextCronOccurrence = new NextCronOccurrence(occurrenceId, T0);
        Assert.Single(await Drain(node.QueueCronTickerOccurrences(slot, CancellationToken.None)));
        Assert.Contains("\"RetryIntervals\":[]", await RawOccurrenceJson(occurrenceId));
        Assert.DoesNotContain("{}", await RawOccurrenceJson(occurrenceId));

        var read = (await node.GetAllCronTickerOccurrences(x => x.Id == occurrenceId, CancellationToken.None)).Single();
        Assert.Equal(TickerStatus.Queued, read.Status);
        Assert.Empty(read.CronTicker.RetryIntervals!);
    }

    // The path of a normal job: AddTimeTickers, the main loop reads the window, QueueTimeTickers acquires it.
    [Fact]
    public async Task AddedTimeTicker_ThroughProvider_IsReadableAfterAcquire()
    {
        var node = fixture.CreateProvider("node-A", new FakeTickerClock(T0));
        var ticker = new TimeTickerEntity
        {
            Id = Guid.NewGuid(), Function = "roundtrip-fn", ExecutionTime = T0, Status = TickerStatus.Idle,
            CreatedAt = T0.AddHours(-2), UpdatedAt = SeedUpdatedAt, Request = []
        };
        await node.AddTimeTickers([ticker], CancellationToken.None);

        var window = await node.GetEarliestTimeTickers(CancellationToken.None);
        var queued = await Drain(node.QueueTimeTickers(window, CancellationToken.None));

        Assert.Equal(ticker.Id, Assert.Single(queued).Id);
        var read = await node.GetTimeTickerById(ticker.Id, CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(TickerStatus.Queued, read!.Status);
        Assert.Equal("node-A", read.LockHolder);
    }

    // A row written by the old scripts has "Children":{} and is unreadable; the next script run writes it back as an
    // array, so rows already stored in that state heal.
    [Fact]
    public async Task StoredRowWithBrokenChildren_HealsOnNextAcquire()
    {
        var node = fixture.CreateProvider("node-A", new FakeTickerClock(T0));
        var ticker = NewTimeTicker();
        ticker.Children = [];
        await node.AddTimeTickers([ticker], CancellationToken.None);
        var key = RedisKeyBuilder.TimeTickerKey(ticker.Id);
        var broken = (await RawJson(ticker.Id)).Replace("\"Children\":[]", "\"Children\":{}").Replace("\"RetryIntervals\":[]", "\"RetryIntervals\":{}");
        await fixture.Database.StringSetAsync(key, broken);
        Assert.Null(await node.GetTimeTickerById(ticker.Id, CancellationToken.None));

        var acquired = await node.AcquireImmediateTimeTickersAsync([ticker.Id], CancellationToken.None);

        Assert.Single(acquired);
        var read = await node.GetTimeTickerById(ticker.Id, CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(TickerStatus.InProgress, read!.Status);
    }

    // The rewrite is keyed on the property name. Inside a string value the quotes are escaped, so the same text in a
    // function name or an exception message is left alone.
    [Fact]
    public async Task FunctionNameContainingThePattern_IsNotChanged()
    {
        var node = fixture.CreateProvider("node-A", new FakeTickerClock(T0));
        const string function = "fn \"Children\":{} and \"RetryIntervals\":{} \\\"Children\":{}";
        var ticker = NewTimeTicker(function);
        await node.AddTimeTickers([ticker], CancellationToken.None);

        Assert.Single(await node.AcquireImmediateTimeTickersAsync([ticker.Id], CancellationToken.None));

        var read = await node.GetTimeTickerById(ticker.Id, CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(function, read!.Function);
    }
}

/// <summary>
/// The same path through the real manager: it adds the row, then (dispatcher enabled) acquires it and hands it to
/// the dispatcher. Needs the static function registry, so it runs in the collection that serialises that state.
/// </summary>
[Collection("TickerFunctionProviderState")]
public class RedisManagerLuaPathTests : IClassFixture<RedisTestFixture>, IAsyncLifetime, IDisposable
{
    private const string ValidFunction = "ManagerLuaPathFunction";
    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly RedisTestFixture _fixture;

    public RedisManagerLuaPathTests(RedisTestFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => TickerFunctionProvider.Build();

    // A time ticker added through the manager reaches the dispatcher: the acquire script must hand the row back in
    // a form the provider can read. Children stays at the entity default, as the manager never sets it.
    [Fact]
    public async Task AddedTimeTicker_ThroughManager_RunsThroughTheLoopPath()
    {
        var clock = new FakeTickerClock(T0);
        var provider = _fixture.CreateProvider("node-A", clock);
        TickerFunctionProvider.RegisterFunctions(
            new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>
            {
                [ValidFunction] = ("", TickerTaskPriority.Normal, (_, _, _) => Task.CompletedTask, 0)
            });
        TickerFunctionProvider.Build();
        var dispatcher = Substitute.For<ISharpTickerDispatcher>();
        dispatcher.IsEnabled.Returns(true);
        dispatcher.DispatchAsync(Arg.Any<InternalFunctionContext[]>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var notificationHub = Substitute.For<ISharpTickerNotificationHubSender>();
        notificationHub.AddTimeTickerNotifyAsync(Arg.Any<Guid>()).Returns(Task.CompletedTask);
        var manager = new TickerManager<TimeTickerEntity, CronTickerEntity>(
            provider, Substitute.For<ISharpTickerHostScheduler>(), clock, notificationHub, new TickerExecutionContext(), dispatcher);

        var result = await ((ITimeTickerManager<TimeTickerEntity>)manager)
            .AddAsync(new TimeTickerEntity { Function = ValidFunction, ExecutionTime = T0 }, CancellationToken.None);

        Assert.True(result.IsSucceeded);
        var id = result.Result.Id;
        await dispatcher.Received(1).DispatchAsync(
            Arg.Is<InternalFunctionContext[]>(c => c.Length == 1 && c[0].TickerId == id), Arg.Any<CancellationToken>());
        var read = await provider.GetTimeTickerById(id, CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(TickerStatus.InProgress, read!.Status);
    }
}
