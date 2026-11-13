using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SharpTicker.Caching.StackExchangeRedis.Helpers;
using SharpTicker.Caching.StackExchangeRedis.Infrastructure;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using static SharpTicker.Caching.StackExchangeRedis.DependencyInjection.ServiceExtension;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

public abstract class Shape
{
}

public sealed class Circle : Shape
{
}

/// <summary>
/// An application's own time ticker type with a property of an abstract type and no polymorphism configured. The
/// serializer writes a job that holds a shape (as <c>"Shape":{}</c>) and refuses to read it back.
/// </summary>
public sealed class ShapedTimeTicker : TimeTickerEntity<ShapedTimeTicker>
{
    public Shape? Shape { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ShapedTimeTicker))]
[JsonSerializable(typeof(CronTickerOccurrenceEntity<CronTickerEntity>))]
internal partial class ShapedJsonSerializerContext : JsonSerializerContext;

/// <summary>
/// A stored row the serializer refuses to read for its type (it throws <see cref="NotSupportedException"/>, not a
/// <see cref="JsonException"/>), next to rows that read, over a real Redis: what the reads of the store do with it.
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class RedisRowOfUnsupportedTypeTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private const string Node = "node-A";

    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeTickerClock _clock = new(T0);
    private readonly ListLogger<TickerRedisPersistenceProvider<ShapedTimeTicker, CronTickerEntity>> _log = new();

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TickerRedisPersistenceProvider<ShapedTimeTicker, CronTickerEntity> Provider()
        => new(fixture.Database, _clock, new SchedulerOptionsBuilder { NodeIdentifier = Node },
            new SharpTickerRedisOptionBuilder { JsonSerializerContext = ShapedJsonSerializerContext.Default }, _log);

    private static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    private static ShapedTimeTicker Job(bool withShape, DateTime executionTime) => new()
    {
        Id = Guid.NewGuid(), Function = "shaped-fn", ExecutionTime = executionTime, Status = TickerStatus.Idle,
        CreatedAt = T0.AddHours(-2), UpdatedAt = T0.AddHours(-1), Request = [1], Shape = withShape ? new Circle() : null
    };

    private async Task<Guid> AddAsync(bool withShape, DateTime executionTime)
    {
        var ticker = Job(withShape, executionTime);
        await Provider().AddTimeTickers([ticker], CancellationToken.None);
        return ticker.Id;
    }

    // The row as the key holds it, read without the provider.
    private async Task<(int Status, string? LockHolder)> StoredAsync(Guid id)
    {
        using var doc = JsonDocument.Parse((string)(await fixture.Database.StringGetAsync(RedisKeyBuilder.TimeTickerKey(id)))!);
        var holder = doc.RootElement.GetProperty("LockHolder");
        return (doc.RootElement.GetProperty("Status").GetInt32(), holder.ValueKind == JsonValueKind.Null ? null : holder.GetString());
    }

    private List<(LogLevel Level, string Message, Exception? Exception)> Problems()
        => _log.Entries.Where(e => e.Level >= LogLevel.Warning).ToList();

    // Every entry at Warning or above is about the row that does not read: it names that row's key and carries the
    // serializer's own exception, which says which type and where.
    private void AssertOnlyThisRowIsReported(Guid unreadable)
    {
        var problems = Problems();
        Assert.NotEmpty(problems);
        Assert.All(problems, p =>
        {
            Assert.Equal(LogLevel.Warning, p.Level);
            Assert.Contains(RedisKeyBuilder.TimeTickerKey(unreadable), p.Message);
            Assert.IsType<NotSupportedException>(p.Exception);
        });
    }

    // What the tests below rest on: the job is added without an error and the serializer does not read it back.
    [Fact]
    public async Task AJobThatHoldsAShape_IsStored_AndTheSerializerRefusesToReadIt()
    {
        var id = await AddAsync(withShape: true, T0.AddMinutes(-1));

        var raw = (string)(await fixture.Database.StringGetAsync(RedisKeyBuilder.TimeTickerKey(id)))!;

        Assert.Contains("\"Shape\":{}", raw);
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize(raw, ShapedJsonSerializerContext.Default.ShapedTimeTicker));
    }

    [Fact]
    public async Task TheReadById_OfSuchARow_GivesNothing_AndReportsTheRowWithItsKey()
    {
        var id = await AddAsync(withShape: true, T0.AddMinutes(-1));

        var read = await Provider().GetTimeTickerById(id, CancellationToken.None);

        Assert.Null(read);
        Assert.Single(Problems());
        AssertOnlyThisRowIsReported(id);
    }

    // Control: the same type without a shape reads, and nothing is reported.
    [Fact]
    public async Task TheReadById_OfARowWithoutAShape_GivesTheRow_AndReportsNothing()
    {
        var id = await AddAsync(withShape: false, T0.AddMinutes(-1));

        var read = await Provider().GetTimeTickerById(id, CancellationToken.None);

        Assert.Equal(id, read.Id);
        Assert.Empty(Problems());
    }

    [Fact]
    public async Task TheListing_GivesTheOtherRows_AndReportsTheRowWithItsKey()
    {
        var unreadable = await AddAsync(withShape: true, T0.AddMinutes(-2));
        var first = await AddAsync(withShape: false, T0.AddMinutes(-1));
        var second = await AddAsync(withShape: false, T0.AddMinutes(5));

        var listed = await Provider().GetTimeTickers(null!, CancellationToken.None);

        Assert.Equal(new[] { first, second }.Order(), listed.Select(x => x.Id).Order());
        AssertOnlyThisRowIsReported(unreadable);
    }

    // The row that does not read is due before the others, so the pass meets it first.
    [Fact]
    public async Task TheFallbackPass_HandsOutTheOtherOverdueJobs_AndNeverTheRowThatDoesNotRead()
    {
        var unreadable = await AddAsync(withShape: true, T0.AddMinutes(-3));
        var first = await AddAsync(withShape: false, T0.AddMinutes(-2));
        var second = await AddAsync(withShape: false, T0.AddMinutes(-1));
        var provider = Provider();

        var handedOut = await Drain(provider.QueueTimedOutTimeTickers(CancellationToken.None));

        Assert.Equal(new[] { first, second }.Order(), handedOut.Select(x => x.Id).Order());
        AssertOnlyThisRowIsReported(unreadable);

        // The claim script takes a row the read could not give, as it does for a row whose text does not parse; the row
        // comes back unreadable again, so it is held by the node and not handed out, in this pass and in the next.
        Assert.Equal(((int)TickerStatus.InProgress, Node), await StoredAsync(unreadable));
        Assert.Empty(await Drain(provider.QueueTimedOutTimeTickers(CancellationToken.None)));
        AssertOnlyThisRowIsReported(unreadable);
    }

    // The loop's read: the row that does not read is the earliest pending entry and does not hide the rows after it.
    [Fact]
    public async Task TheLoopsRead_GivesTheRowsDueAfterTheRowThatDoesNotRead()
    {
        var unreadable = await AddAsync(withShape: true, T0.AddSeconds(10));
        var next = await AddAsync(withShape: false, T0.AddSeconds(20));

        var earliest = await Provider().GetEarliestTimeTickers(CancellationToken.None);

        Assert.Equal([next], earliest.Select(x => x.Id));
        AssertOnlyThisRowIsReported(unreadable);
    }

    // Deleting the job ends it: the row and its pending entry are gone, and the later reads report nothing.
    [Fact]
    public async Task ADeleteOfTheRowThatDoesNotRead_RemovesIt_AndTheReadsReportNothingAfterwards()
    {
        var unreadable = await AddAsync(withShape: true, T0.AddMinutes(-2));
        var other = await AddAsync(withShape: false, T0.AddMinutes(-1));

        Assert.Equal(1, await Provider().RemoveTimeTickers([unreadable], CancellationToken.None));
        _log.Entries.Clear();

        Assert.False(await fixture.Database.KeyExistsAsync(RedisKeyBuilder.TimeTickerKey(unreadable)));
        Assert.Equal([other], (await Provider().GetTimeTickers(null!, CancellationToken.None)).Select(x => x.Id));
        Assert.Equal([other], (await Drain(Provider().QueueTimedOutTimeTickers(CancellationToken.None))).Select(x => x.Id));
        Assert.Empty(await Drain(Provider().QueueTimedOutTimeTickers(CancellationToken.None)));
        Assert.Empty(Problems());
    }

    // An update with a body that reads replaces the stored row that does not, as it does for a row whose text does not
    // parse; the job is then read and handed out like any other.
    [Fact]
    public async Task AnUpdateWithABodyThatReads_ReplacesTheStoredRowThatDoesNot()
    {
        var ticker = Job(withShape: true, T0.AddMinutes(-1));
        await Provider().AddTimeTickers([ticker], CancellationToken.None);
        ticker.Shape = null;

        await Provider().UpdateTimeTickers([ticker], CancellationToken.None);

        Assert.DoesNotContain("\"Shape\":{}", (string)(await fixture.Database.StringGetAsync(RedisKeyBuilder.TimeTickerKey(ticker.Id)))!);
        Assert.Equal(ticker.Id, (await Provider().GetTimeTickerById(ticker.Id, CancellationToken.None)).Id);
        Assert.Equal([ticker.Id], (await Drain(Provider().QueueTimedOutTimeTickers(CancellationToken.None))).Select(x => x.Id));
    }

    // Not a stored row: the body of an update that the serializer cannot read back is the caller's entity, and the
    // caller gets the serializer's reason, as before.
    [Fact]
    public async Task AnUpdateWhoseBodyHoldsAShape_StillThrowsTheSerializersExceptionToTheCaller()
    {
        var ticker = Job(withShape: false, T0.AddMinutes(5));
        await Provider().AddTimeTickers([ticker], CancellationToken.None);
        ticker.Shape = new Circle();

        var thrown = await Assert.ThrowsAsync<NotSupportedException>(() => Provider().UpdateTimeTickers([ticker], CancellationToken.None));

        Assert.Contains(nameof(Shape), thrown.Message);
        var stored = await Provider().GetTimeTickerById(ticker.Id, CancellationToken.None);
        Assert.Null(stored.Shape);
        Assert.Empty(Problems());
    }
}
