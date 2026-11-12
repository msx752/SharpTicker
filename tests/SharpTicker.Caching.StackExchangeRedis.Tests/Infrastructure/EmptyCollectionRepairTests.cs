using System.Text.Json;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;
using SharpTicker.Caching.StackExchangeRedis.Helpers;
using SharpTicker.Utilities.Entities;
using static SharpTicker.Caching.StackExchangeRedis.DependencyInjection.ServiceExtension;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>
/// The read of a row whose empty collections were written as objects (what cjson does to an empty array), through
/// RedisSerializer with the source-generated contexts the store uses. No Redis is needed: the text is given as it is stored.
/// </summary>
public class EmptyCollectionRepairTests
{
    private const string Key = "tq:tt:the-key";
    private const string Id = "44944170-cb7b-46ce-b381-476f567ca54b";

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private readonly ListLogger _log = new();
    private readonly RedisSerializer _serializer;

    public EmptyCollectionRepairTests()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            TypeInfoResolverChain = { TestJsonSerializerContext.Default, RedisContextJsonSerializerContext.Default }
        };
        _serializer = new RedisSerializer(Substitute.For<IDatabase>(), options, _log);
    }

    private T Read<T>(string json) where T : class => _serializer.DeserializeOrNull<T>(json, Key);

    [Fact]
    public void ARowThatReads_IsReadAsBefore_AndNothingIsLogged()
    {
        var row = Read<TimeTickerEntity>($$$"""{"Id":"{{{Id}}}","Children":[],"RetryIntervals":[2,2],"Function":"fn"}""");

        Assert.Equal(Guid.Parse(Id), row.Id);
        Assert.Equal([2, 2], row.RetryIntervals);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void EmptyChildrenWrittenAsAnObject_IsReadAsNoChildren()
    {
        var row = Read<TimeTickerEntity>($$$"""{"Id":"{{{Id}}}","Children":{},"Function":"fn"}""");

        Assert.Equal("fn", row.Function);
        Assert.Empty(row.Children);
        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains(Key, entry.Message);
    }

    [Fact]
    public void EmptyRetryIntervalsWrittenAsAnObject_IsReadAsNoIntervals()
    {
        var row = Read<TimeTickerEntity>($$$"""{"Id":"{{{Id}}}","RetryIntervals":{},"Children":[]}""");

        Assert.Empty(row.RetryIntervals);
    }

    [Fact]
    public void AChildHeldInTheRow_WhoseOwnChildrenAreAnObject_IsRead()
    {
        var row = Read<TimeTickerEntity>(
            $$$"""{"Id":"{{{Id}}}","Children":[{"Id":"54944170-cb7b-46ce-b381-476f567ca54b","Function":"child","Children":{},"RetryIntervals":{}}]}""");

        var child = Assert.Single(row.Children);
        Assert.Equal("child", child.Function);
        Assert.Empty(child.Children);
        Assert.Empty(child.RetryIntervals);
    }

    [Fact]
    public void AnOccurrence_WhoseCronTickerHasItsIntervalsAsAnObject_IsRead()
    {
        var row = Read<CronTickerOccurrenceEntity<CronTickerEntity>>(
            $$$"""{"Id":"{{{Id}}}","Status":2,"CronTicker":{"RetryIntervals":{},"Expression":"* * * * *","Function":"cron-fn"}}""");

        Assert.Equal("cron-fn", row.CronTicker.Function);
        Assert.Empty(row.CronTicker.RetryIntervals);
    }

    [Fact]
    public void PropertyNamesInCamelCase_AreReadTheSameWay()
    {
        var row = Read<TimeTickerEntity>($$$"""{"id":"{{{Id}}}","children":{},"retryIntervals":{},"function":"fn"}""");

        Assert.Equal("fn", row.Function);
        Assert.Empty(row.Children);
        Assert.Empty(row.RetryIntervals);
    }

    // Everything else in the row is read exactly as from the same text with arrays: other characters, the request bytes,
    // a time with seven digits, a number above 2^53, and a description that holds the very text that is looked for.
    [Fact]
    public void TheRestOfTheRow_IsReadExactlyAsFromTheSameTextWithArrays()
    {
        const string template = """{"Id":"44944170-cb7b-46ce-b381-476f567ca54b","Description":"çğü ✓ \"Children\":{} 'x'","Request":"AQID/v8=","CreatedAt":"2025-06-15T10:00:00Z","UpdatedAt":"2025-06-15T11:00:00.1234567Z","ExecutionTime":"2025-06-15T11:59:00Z","ElapsedTime":9007199254740993,"Retries":3,"Status":2,"LockHolder":"node-A","Children":EMPTY,"RetryIntervals":EMPTY}""";

        var repaired = Read<TimeTickerEntity>(template.Replace("EMPTY", "{}"));
        var expected = Read<TimeTickerEntity>(template.Replace("EMPTY", "[]"));

        Assert.Equal(_serializer.Serialize(expected), _serializer.Serialize(repaired));
        Assert.Equal("çğü ✓ \"Children\":{} 'x'", repaired.Description);
        Assert.Equal(9007199254740993, repaired.ElapsedTime);
        Assert.Equal([1, 2, 3, 254, 255], repaired.Request);
        Assert.Equal(new DateTime(2025, 6, 15, 11, 0, 0, DateTimeKind.Utc).AddTicks(1234567), repaired.UpdatedAt);
    }

    [Fact]
    public void ARowWithManyEmptyCollections_IsRead()
    {
        var children = string.Join(",", Enumerable.Range(0, 200).Select(_ =>
            $$$"""{"Id":"{{{Guid.NewGuid()}}}","Children":{},"RetryIntervals":{}}"""));

        var row = Read<TimeTickerEntity>($$$"""{"Id":"{{{Id}}}","RetryIntervals":{},"Children":[{{{children}}}]}""");

        Assert.Equal(200, row.Children.Count);
        Assert.All(row.Children, child => { Assert.Empty(child.Children); Assert.Empty(child.RetryIntervals); });
    }

    // Rows that are not readable for another reason are reported as before, once, with the key and the first failure.
    public static TheoryData<string, string?> RowsBrokenForAnotherReason => new()
    {
        { """{"Id":{},"Children":[]}""", "$.Id" },
        { """{"Children":{},"Id":{}}""", "$.Children" },
        { """{"Children":{},"Status":"not-a-number"}""", "$.Children" },
        { """{"Request":{},"Children":[]}""", "$.Request" },
        { """{"Children":{"a":1}}""", "$.Children" },
        { """{"Id":"44944170-cb7b-46ce-b381-476f567ca54b","Children":{}""", null },
        { """{"Children":[],"RetryIntervals":""", null },
        { "not json", null }
    };

    [Theory]
    [MemberData(nameof(RowsBrokenForAnotherReason))]
    public void ARowBrokenForAnotherReason_IsNotRead_AndIsReportedOnceWithItsKey(string json, string? firstFailurePath)
    {
        Assert.Null(Read<TimeTickerEntity>(json));

        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(Key, entry.Message);
        var failure = Assert.IsAssignableFrom<JsonException>(entry.Exception);
        if (firstFailurePath != null)
            Assert.Equal(firstFailurePath, failure.Path);
    }

    // A key twice in one object: whatever the runtime makes of it, the read does not throw.
    [Fact]
    public void AKeyTwiceInOneObject_DoesNotThrow()
    {
        var row = Read<TimeTickerEntity>($$$"""{"Id":"{{{Id}}}","Children":{},"Children":{}}""");

        if (row == null)
            Assert.Equal(LogLevel.Warning, Assert.Single(_log.Entries).Level);
        else
            Assert.Empty(row.Children);
    }

    [Fact]
    public void TextWithoutAKey_IsReportedWithoutOne()
    {
        Assert.Null(_serializer.DeserializeOrNull<TimeTickerEntity>("""{"Id":{}}"""));

        Assert.Equal("Failed to deserialize JSON payload as TimeTickerEntity.", Assert.Single(_log.Entries).Message);
    }
}
