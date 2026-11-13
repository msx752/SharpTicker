using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharpTicker.Caching.StackExchangeRedis.DependencyInjection;
using SharpTicker.DependencyInjection;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace SharpTicker.Caching.StackExchangeRedis.OwnTypes.Tests;

/// <summary>
/// The stored records of an application's own ticker types, read and written through the store of an application that
/// gives its source-generated context and of one that gives none (#162). The entities fill some members themselves
/// (internal setter, [JsonInclude]); the application's context cannot reach them, so the tests here set them in the
/// stored record and look at what the store reads and writes. No host is started: nothing is to run.
/// </summary>
[Collection(OwnTypesTests.CollectionName)]
public class OwnTypesStoredRecordTests : IAsyncLifetime
{
    private static readonly DateTime Created = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime Updated = new(2026, 1, 2, 3, 4, 6, DateTimeKind.Utc);
    private static readonly DateTime Locked = new(2026, 1, 2, 3, 4, 7, DateTimeKind.Utc);
    private static readonly DateTime Executed = new(2026, 1, 2, 3, 4, 8, DateTimeKind.Utc);
    private static readonly Guid Parent = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private readonly RedisContainer _redis = new RedisBuilder("redis:7").Build();
    private readonly List<IHost> _applications = [];
    private IConnectionMultiplexer _connection = null!;

    public async Task InitializeAsync()
    {
        await _redis.StartAsync();
        _connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        foreach (var application in _applications)
            application.Dispose();
        await _connection.DisposeAsync();
        await _redis.DisposeAsync();
    }

    // The store of an application that is built and not started.
    private ITickerPersistenceProvider<OwnTimeTicker, OwnCronTicker> Store(JsonSerializerContext? context)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSharpTicker<OwnTimeTicker, OwnCronTicker>(options => options.AddStackExchangeRedis(redis =>
        {
            redis.Configuration = _redis.GetConnectionString();
            if (context != null)
                redis.JsonSerializerContext = context;
        }));
        var application = builder.Build();
        _applications.Add(application);
        return application.Services.GetRequiredService<ITickerPersistenceProvider<OwnTimeTicker, OwnCronTicker>>();
    }

    private async Task<JsonObject> RecordAsync(string key)
        => JsonNode.Parse((string)(await _connection.GetDatabase().StringGetAsync(key))!)!.AsObject();

    private Task WriteRecordAsync(string key, JsonObject record)
        => _connection.GetDatabase().StringSetAsync(key, record.ToJsonString());

    // The members of a type that the entities fill themselves: [JsonInclude] and a setter the application cannot call.
    private static string[] MembersWithAnInternalSetter(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<JsonIncludeAttribute>(inherit: true) != null && p.GetSetMethod(nonPublic: false) == null)
            .Select(p => p.Name).Order().ToArray();

    // Every such member of a time job is set in the stored record, read by the store, written back by it and is in the
    // record again. The list is compared with the entity, so a member added there later is missed here until it is covered.
    [Fact]
    public async Task EveryMemberATimeJobFillsItself_IsReadAndWritten_WithTheApplicationsContext()
    {
        var store = Store(OwnTypesJsonSerializerContext.Default);
        var job = new OwnTimeTicker { Function = "OwnTypes.Record.Time", ExecutionTime = Created.AddYears(1), Team = "billing", Labels = ["a"] };
        await store.AddTimeTickers([job], CancellationToken.None);
        var key = $"tq:tt:{job.Id}";
        var record = await RecordAsync(key);
        var set = new Dictionary<string, JsonNode?>
        {
            ["InitIdentifier"] = "seeded", ["CreatedAt"] = Created, ["UpdatedAt"] = Updated,
            ["Status"] = (int)TickerStatus.Failed, ["LockHolder"] = "node-a", ["LockedAt"] = Locked, ["ExecutedAt"] = Executed,
            ["ExceptionMessage"] = "it failed", ["SkippedReason"] = "it was skipped", ["ElapsedTime"] = 42L, ["RetryCount"] = 2,
            ["ParentId"] = Parent
        };
        Assert.Equal(MembersWithAnInternalSetter(typeof(OwnTimeTicker)), set.Keys.Order().ToArray());
        foreach (var (name, value) in set)
            record[name] = value;
        await WriteRecordAsync(key, record);

        var read = await store.GetTimeTickerById(job.Id, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal("seeded", read.InitIdentifier);
        Assert.Equal(Created, read.CreatedAt);
        Assert.Equal(Updated, read.UpdatedAt);
        Assert.Equal(TickerStatus.Failed, read.Status);
        Assert.Equal("node-a", read.LockHolder);
        Assert.Equal(Locked, read.LockedAt);
        Assert.Equal(Executed, read.ExecutedAt);
        Assert.Equal("it failed", read.ExceptionMessage);
        Assert.Equal("it was skipped", read.SkippedReason);
        Assert.Equal(42L, read.ElapsedTime);
        Assert.Equal(2, read.RetryCount);
        Assert.Equal(Parent, read.ParentId);
        Assert.Equal("billing", read.Team);
        Assert.Equal(["a"], read.Labels);

        // Written back: the update sets UpdatedAt itself; every other member is in the record as it was read.
        await store.UpdateTimeTickers([read], CancellationToken.None);
        var written = await RecordAsync(key);
        Assert.Equal("seeded", (string?)written["InitIdentifier"]);
        Assert.Equal(Created, (DateTime)written["CreatedAt"]!);
        Assert.True((DateTime)written["UpdatedAt"]! > Updated, $"UpdatedAt in the record: {written["UpdatedAt"]}");
        Assert.Equal((int)TickerStatus.Failed, (int)written["Status"]!);
        Assert.Equal("node-a", (string?)written["LockHolder"]);
        Assert.Equal(Locked, (DateTime)written["LockedAt"]!);
        Assert.Equal(Executed, (DateTime)written["ExecutedAt"]!);
        Assert.Equal("it failed", (string?)written["ExceptionMessage"]);
        Assert.Equal("it was skipped", (string?)written["SkippedReason"]);
        Assert.Equal(42L, (long)written["ElapsedTime"]!);
        Assert.Equal(2, (int)written["RetryCount"]!);
        Assert.Equal(Parent, (Guid)written["ParentId"]!);
    }

    // The same for a cron of the application's own type, which has the members every ticker has.
    [Fact]
    public async Task EveryMemberACronFillsItself_IsReadAndWritten_WithTheApplicationsContext()
    {
        var store = Store(OwnTypesJsonSerializerContext.Default);
        var cron = new OwnCronTicker { Function = "OwnTypes.Record.Cron", Expression = "0 0 0 1 1 *", Team = "billing", Labels = ["a"] };
        await store.InsertCronTickers([cron], CancellationToken.None);
        var key = $"tq:cron:{cron.Id}";
        var record = await RecordAsync(key);
        var set = new Dictionary<string, JsonNode?> { ["InitIdentifier"] = "seeded", ["CreatedAt"] = Created, ["UpdatedAt"] = Updated };
        Assert.Equal(MembersWithAnInternalSetter(typeof(OwnCronTicker)), set.Keys.Order().ToArray());
        foreach (var (name, value) in set)
            record[name] = value;
        await WriteRecordAsync(key, record);

        var read = await store.GetCronTickerById(cron.Id, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal("seeded", read.InitIdentifier);
        Assert.Equal(Created, read.CreatedAt);
        Assert.Equal(Updated, read.UpdatedAt);
        Assert.Equal("billing", read.Team);
        Assert.Equal(["a"], read.Labels);

        await store.UpdateCronTickers([read], CancellationToken.None);
        var written = await RecordAsync(key);
        Assert.Equal("seeded", (string?)written["InitIdentifier"]);
        Assert.Equal(Created, (DateTime)written["CreatedAt"]!);
        Assert.NotNull(written["UpdatedAt"]);
    }

    // An application that changes how it sets the serializer up reads what it stored before: a record written through
    // reflection is read with the context, and the other way round, and both records have the same members under the same names.
    [Fact]
    public async Task ARecordWrittenWithoutAContext_IsReadWithOne_AndTheOtherWayRound()
    {
        var withoutContext = Store(null);
        var withContext = Store(OwnTypesJsonSerializerContext.Default);
        var byReflection = new OwnTimeTicker { Function = "OwnTypes.Record.Time", ExecutionTime = Created.AddYears(1), Team = "billing", Labels = ["a", "b"] };
        var byContext = new OwnTimeTicker { Function = "OwnTypes.Record.Time", ExecutionTime = Created.AddYears(1), Team = "billing", Labels = ["a", "b"] };
        await withoutContext.AddTimeTickers([byReflection], CancellationToken.None);
        await withContext.AddTimeTickers([byContext], CancellationToken.None);

        var readWithContext = await withContext.GetTimeTickerById(byReflection.Id, CancellationToken.None);
        var readWithoutContext = await withoutContext.GetTimeTickerById(byContext.Id, CancellationToken.None);

        foreach (var read in new[] { readWithContext, readWithoutContext })
        {
            Assert.NotNull(read);
            Assert.Equal("OwnTypes.Record.Time", read.Function);
            Assert.Equal(Created.AddYears(1), read.ExecutionTime);
            Assert.Equal(TickerStatus.Idle, read.Status);
            Assert.Equal("billing", read.Team);
            Assert.Equal(["a", "b"], read.Labels);
        }

        // The same members under the same names in both records.
        var first = await RecordAsync($"tq:tt:{byReflection.Id}");
        var second = await RecordAsync($"tq:tt:{byContext.Id}");
        Assert.Equal(first.Select(p => p.Key).Order(), second.Select(p => p.Key).Order());
    }
}
