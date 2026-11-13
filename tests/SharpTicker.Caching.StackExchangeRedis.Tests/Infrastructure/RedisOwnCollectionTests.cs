using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using SharpTicker.Caching.StackExchangeRedis.Helpers;
using SharpTicker.Caching.StackExchangeRedis.Infrastructure;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Models;
using static SharpTicker.Caching.StackExchangeRedis.DependencyInjection.ServiceExtension;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>
/// An application's own time ticker type: a list, a list of lists, and a dictionary.
/// </summary>
public sealed class TaggedTimeTicker : TimeTickerEntity<TaggedTimeTicker>
{
    public List<string> Tags { get; set; } = [];

    public List<List<string>> Groups { get; set; } = [];

    public Dictionary<string, string> Labels { get; set; } = [];
}

/// <summary>
/// An application's own cron ticker type with a list. A new instance has the list empty.
/// </summary>
public sealed class TaggedCronTicker : CronTickerEntity
{
    public List<string> Tags { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(TaggedTimeTicker))]
[JsonSerializable(typeof(TaggedCronTicker))]
[JsonSerializable(typeof(CronTickerOccurrenceEntity<TaggedCronTicker>))]
internal partial class TaggedJsonSerializerContext : JsonSerializerContext;

/// <summary>
/// Jobs of an application's own ticker types whose own collections are empty, over a real Redis. The scripts write an
/// empty collection as an object and restore only the collections they know by name, so these rows come back from a
/// script, and stay in the store, with <c>{}</c> where the type has a list.
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class RedisOwnCollectionTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private const string Node = "node-A";

    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeTickerClock _clock = new(T0);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TickerRedisPersistenceProvider<TaggedTimeTicker, TaggedCronTicker> Provider(string node = Node)
        => new(fixture.Database, _clock, new SchedulerOptionsBuilder { NodeIdentifier = node },
            new SharpTickerRedisOptionBuilder { JsonSerializerContext = TaggedJsonSerializerContext.Default },
            NullLogger<TickerRedisPersistenceProvider<TaggedTimeTicker, TaggedCronTicker>>.Instance);

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

    #region A time job

    private static readonly byte[] RequestBytes = [1, 2, 3];

    // A time job, due unless a time is given. With empty: true every list of the row is empty; the controls fill them all.
    private async Task<Guid> AddAsync(bool empty, DateTime? executionTime = null)
    {
        var ticker = new TaggedTimeTicker
        {
            Id = Guid.NewGuid(), Function = "tagged-fn", ExecutionTime = executionTime ?? T0.AddMinutes(-1), Status = TickerStatus.Idle,
            CreatedAt = T0.AddHours(-2), UpdatedAt = T0.AddHours(-1).AddTicks(1234567), Request = RequestBytes,
            Tags = empty ? [] : ["a"], Groups = empty ? [] : [["g"]]
        };
        await Provider().AddTimeTickers([ticker], CancellationToken.None);
        return ticker.Id;
    }

    private Task<string> RawTimeTickerAsync(Guid id) => RawAsync(RedisKeyBuilder.TimeTickerKey(id));

    // What the tests below rest on: a script writes an empty list as an object, and a filled row holds no such object.
    private async Task AssertStoredShapeAsync(Guid id, bool empty)
    {
        var raw = await RawTimeTickerAsync(id);
        if (empty)
            Assert.Contains("\"Tags\":{}", raw);
        else
            Assert.DoesNotContain("{}", raw.Replace("\"Labels\":{}", ""));
    }

    // The fallback hands the job out one time, its outcome is stored, and that write stores the lists as arrays again;
    // the dictionary stays an object throughout.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADueJob_IsHandedOutOnceByTheFallback_AndItsOutcomeIsStored(bool empty)
    {
        var id = await AddAsync(empty);
        var provider = Provider();

        Assert.Equal([id], (await Drain(provider.QueueTimedOutTimeTickers(CancellationToken.None))).Select(x => x.Id));
        await AssertStoredShapeAsync(id, empty);
        Assert.Empty(await Drain(provider.QueueTimedOutTimeTickers(CancellationToken.None)));

        Assert.Equal(1, await provider.UpdateTimeTicker(StatusContext(id, TickerStatus.Done), CancellationToken.None));

        var stored = await RawTimeTickerAsync(id);
        Assert.Equal((int)TickerStatus.Done, (await StoredAsync(RedisKeyBuilder.TimeTickerKey(id))).Status);
        Assert.Contains(empty ? "\"Tags\":[]" : "\"Tags\":[\"a\"]", stored);
        Assert.Contains(empty ? "\"Groups\":[]" : "\"Groups\":[[\"g\"]]", stored);
        Assert.Contains("\"Labels\":{}", stored);
    }

    // The scheduler's own path: taken as Queued, marked InProgress, read while it runs (the row and its request), and
    // its outcome stored by the write a run makes, which is fenced by the run's marker.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AJob_GoesThroughTheLoop_QueuedThenInProgressThenDone(bool empty)
    {
        var id = await AddAsync(empty);
        var provider = Provider();
        var added = await provider.GetTimeTickerById(id, CancellationToken.None);

        var queued = await Drain(provider.QueueTimeTickers([new TimeTickerEntity { Id = id, UpdatedAt = added.UpdatedAt }], CancellationToken.None));

        Assert.Equal(TickerStatus.Queued, Assert.Single(queued).Status);
        await AssertStoredShapeAsync(id, empty);
        var marker = _clock.NextClaimMarker();
        Assert.True(await provider.TryMarkTimeTickerInProgress(id, marker, CancellationToken.None));

        var running = await provider.GetTimeTickerById(id, CancellationToken.None);
        Assert.NotNull(running);
        Assert.Equal(TickerStatus.InProgress, running.Status);
        Assert.Equal(empty ? 0 : 1, running.Tags.Count);
        Assert.Equal(empty ? 0 : 1, running.Groups.Count);
        Assert.Equal(RequestBytes, await provider.GetTimeTickerRequest(id, CancellationToken.None));

        Assert.Equal(1, await provider.UpdateTimeTickerFencedAsync(StatusContext(id, TickerStatus.Done), marker, onlyWhileRunning: true));
        Assert.Equal(((int)TickerStatus.Done, Node), await StoredAsync(RedisKeyBuilder.TimeTickerKey(id)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AJob_IsHandedOutWhenRunNowAsksForIt(bool empty)
    {
        var id = await AddAsync(empty);

        var handedOut = await Provider().AcquireImmediateTimeTickersAsync([id], CancellationToken.None);

        Assert.Equal([id], handedOut.Select(x => x.Id));
        await AssertStoredShapeAsync(id, empty);
    }

    // Once a script has written the row, the reads that list rows still return it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AJob_IsListedAfterAScriptWroteIt(bool empty)
    {
        var id = await AddAsync(empty);
        var provider = Provider();
        Assert.Single(await Drain(provider.QueueTimedOutTimeTickers(CancellationToken.None)));

        var row = Assert.Single(await provider.GetTimeTickers(null!, CancellationToken.None));

        Assert.Equal(id, row.Id);
        Assert.Equal(empty ? 0 : 1, row.Tags.Count);
    }

    // A claim whose job did not start is put back.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AClaimedJob_IsPutBackByTheReleaseOfItsClaim(bool empty)
    {
        var id = await AddAsync(empty);
        var provider = Provider();
        var claimed = Assert.Single(await Drain(provider.QueueTimedOutTimeTickers(CancellationToken.None)));
        await AssertStoredShapeAsync(id, empty);

        Assert.True(await provider.TryReleaseTimeTickerClaim(id, claimed.UpdatedAt, CancellationToken.None));

        Assert.Equal(Released, await StoredAsync(RedisKeyBuilder.TimeTickerKey(id)));
    }

    // A job its node left InProgress is released when that node starts again.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AJobLeftInProgress_IsReleasedByTheStartOfItsNode(bool empty)
    {
        var id = await AddAsync(empty);
        var provider = Provider();
        Assert.Single(await Drain(provider.QueueTimedOutTimeTickers(CancellationToken.None)));

        await provider.ReleaseDeadNodeTimeTickerResources(Node, T0.AddMinutes(5), CancellationToken.None);

        Assert.Equal(Released, await StoredAsync(RedisKeyBuilder.TimeTickerKey(id)));
    }

    // Another node that finds the holder dead releases the job, and its fallback then hands it out one time.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AJobLeftInProgress_IsReleasedWhenAnotherNodeFindsItsHolderDead_AndHandedOutOnce(bool empty)
    {
        var id = await AddAsync(empty);
        Assert.Single(await Drain(Provider().QueueTimedOutTimeTickers(CancellationToken.None)));
        var other = Provider("node-B");

        await other.ReleaseDeadNodeTimeTickerResources(Node, CancellationToken.None);

        await AssertStoredShapeAsync(id, empty);
        Assert.Equal([id], (await Drain(other.QueueTimedOutTimeTickers(CancellationToken.None))).Select(x => x.Id));
        Assert.Empty(await Drain(other.QueueTimedOutTimeTickers(CancellationToken.None)));
    }

    // A job that waits for its time, after the release of everything a node holds rewrote it: the window read returns it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AWaitingJob_IsInTheWindowAfterAReleaseRewroteIt(bool empty)
    {
        var id = await AddAsync(empty, T0.AddSeconds(30));
        var provider = Provider();
        await provider.ReleaseAcquiredTimeTickers([], CancellationToken.None);
        await AssertStoredShapeAsync(id, empty);

        var window = await provider.GetEarliestTimeTickers(CancellationToken.None);

        Assert.Equal([id], window.Select(x => x.Id));
    }

    // A list inside a list: the script writes the empty inner list as an object that is an element of an array.
    [Fact]
    public async Task ADueJobWithAnEmptyListInsideAList_IsHandedOutByTheFallback()
    {
        var ticker = new TaggedTimeTicker
        {
            Id = Guid.NewGuid(), Function = "tagged-fn", ExecutionTime = T0.AddMinutes(-1), Status = TickerStatus.Idle,
            CreatedAt = T0.AddHours(-2), UpdatedAt = T0.AddHours(-1), Request = [], Tags = ["a"], Groups = [[], ["g"]]
        };
        var provider = Provider();
        await provider.AddTimeTickers([ticker], CancellationToken.None);

        Assert.Equal([ticker.Id], (await Drain(provider.QueueTimedOutTimeTickers(CancellationToken.None))).Select(x => x.Id));

        Assert.Contains("\"Groups\":[{},[\"g\"]]", await RawTimeTickerAsync(ticker.Id));
        var read = await provider.GetTimeTickerById(ticker.Id, CancellationToken.None);
        Assert.Equal(2, read.Groups.Count);
        Assert.Empty(read.Groups[0]);
        Assert.Equal(["g"], read.Groups[1]);
    }

    #endregion

    #region An occurrence of a cron ticker type with a list of its own

    // The occurrence of a slot holds a new cron ticker of the application's type, so its list is empty in every occurrence.
    private static (DateTime Key, InternalManagerContext[] Items) Slot(Guid cronId)
        => (T0, [new InternalManagerContext(cronId) { FunctionName = "tagged-cron-fn", Expression = "* * * * *" }]);

    private async Task<Guid> QueueOccurrenceAsync(Guid? cronId = null)
        => Assert.Single(await Drain(Provider().QueueCronTickerOccurrences(Slot(cronId ?? Guid.NewGuid()), CancellationToken.None))).Id;

    private async Task AssertOccurrenceHoldsAnObjectAsync(Guid id)
        => Assert.Contains("\"Tags\":{}", await RawAsync(RedisKeyBuilder.CronOccurrenceKey(id)));

    // Taken as Queued, marked InProgress (the script writes the list as an object), read while it runs, and its outcome
    // stored by the fenced write a run makes. Without that write the occurrence would stay InProgress after it ran.
    [Fact]
    public async Task AnOccurrence_GoesThroughTheLoop_QueuedThenInProgressThenDone()
    {
        var provider = Provider();
        var id = await QueueOccurrenceAsync();
        var key = RedisKeyBuilder.CronOccurrenceKey(id);
        var marker = _clock.NextClaimMarker();

        Assert.True(await provider.TryMarkCronOccurrenceInProgress(id, marker, CancellationToken.None));
        await AssertOccurrenceHoldsAnObjectAsync(id);

        var running = Assert.Single(await provider.GetAllCronTickerOccurrences(null!, CancellationToken.None));
        Assert.Equal(id, running.Id);
        Assert.Equal(TickerStatus.InProgress, running.Status);
        Assert.Empty(running.CronTicker.Tags);

        Assert.Equal(1, await provider.UpdateCronOccurrenceFencedAsync(StatusContext(id, TickerStatus.Done), marker, onlyWhileRunning: true));
        Assert.Equal(((int)TickerStatus.Done, Node), await StoredAsync(key));
    }

    // An occurrence that was put back is handed out by the fallback, one time.
    [Fact]
    public async Task AnOccurrenceThatWasPutBack_IsHandedOutOnceByTheFallback()
    {
        var provider = Provider();
        var id = await QueueOccurrenceAsync();
        await provider.ReleaseAcquiredCronTickerOccurrences([id], CancellationToken.None);
        await AssertOccurrenceHoldsAnObjectAsync(id);
        _clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal([id], (await Drain(provider.QueueTimedOutCronTickerOccurrences(CancellationToken.None))).Select(x => x.Id));
        Assert.Empty(await Drain(provider.QueueTimedOutCronTickerOccurrences(CancellationToken.None)));
    }

    // An occurrence that was put back is taken again by the loop for its slot.
    [Fact]
    public async Task AnOccurrenceThatWasPutBack_IsTakenAgainByTheLoop()
    {
        var provider = Provider();
        var cronId = Guid.NewGuid();
        var id = await QueueOccurrenceAsync(cronId);
        await provider.ReleaseAcquiredCronTickerOccurrences([id], CancellationToken.None);
        await AssertOccurrenceHoldsAnObjectAsync(id);
        var again = Slot(cronId);
        again.Items[0].NextCronOccurrence = new NextCronOccurrence(id, T0);

        var taken = Assert.Single(await Drain(provider.QueueCronTickerOccurrences(again, CancellationToken.None)));

        Assert.Equal(id, taken.Id);
        Assert.Equal(TickerStatus.Queued, taken.Status);
    }

    [Fact]
    public async Task AnOccurrenceThatWasPutBack_IsHandedOutWhenRunNowAsksForIt()
    {
        var provider = Provider();
        var id = await QueueOccurrenceAsync();
        await provider.ReleaseAcquiredCronTickerOccurrences([id], CancellationToken.None);
        await AssertOccurrenceHoldsAnObjectAsync(id);

        var handedOut = await provider.AcquireImmediateCronOccurrencesAsync([id], CancellationToken.None);

        Assert.Equal([id], handedOut.Select(x => x.Id));
    }

    // An occurrence its node left InProgress is released when that node starts again, and the fallback then hands it out
    // one time. (An occurrence that a version without this read left InProgress had run; it runs once more this way.)
    [Fact]
    public async Task AnOccurrenceLeftInProgress_IsReleasedByTheStartOfItsNode_AndHandedOutOnce()
    {
        var provider = Provider();
        var id = await QueueOccurrenceAsync();
        Assert.True(await provider.TryMarkCronOccurrenceInProgress(id, _clock.NextClaimMarker(), CancellationToken.None));
        await AssertOccurrenceHoldsAnObjectAsync(id);

        await provider.ReleaseDeadNodeOccurrenceResources(Node, T0.AddMinutes(5), CancellationToken.None);

        Assert.Equal(Released, await StoredAsync(RedisKeyBuilder.CronOccurrenceKey(id)));
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal([id], (await Drain(provider.QueueTimedOutCronTickerOccurrences(CancellationToken.None))).Select(x => x.Id));
        Assert.Empty(await Drain(provider.QueueTimedOutCronTickerOccurrences(CancellationToken.None)));
    }

    #endregion
}
