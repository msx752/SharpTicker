using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using SharpTicker.Caching.StackExchangeRedis.Infrastructure;
using SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Interfaces;
using Testcontainers.Redis;
using static SharpTicker.Caching.StackExchangeRedis.DependencyInjection.ServiceExtension;

namespace SharpTicker.Caching.StackExchangeRedis.Tests;

/// <summary>
/// A clock the test moves by hand, so two provider instances can disagree about "now" or be pushed past a threshold.
/// </summary>
public sealed class FakeTickerClock(DateTime start) : ITickerClock
{
    public DateTime UtcNow { get; set; } = start;

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>
/// One real Redis container for every test class in the "Redis" collection. Start of each test:
/// <see cref="ResetAsync"/>. <see cref="CreateProvider"/> builds a real provider over the shared server, so a
/// test can run several nodes (distinct node identifier and clock) against one store.
/// </summary>
public sealed class RedisTestFixture : IAsyncLifetime
{
    public const string CollectionName = "Redis";

    public RedisContainer Container { get; } = new RedisBuilder("redis:7").Build();

    public IConnectionMultiplexer Connection { get; private set; } = null!;

    public IDatabase Database => Connection.GetDatabase();

    public async Task InitializeAsync()
    {
        await Container.StartAsync();

        // allowAdmin lets ResetAsync flush the server between tests.
        var options = ConfigurationOptions.Parse(Container.GetConnectionString());
        options.AllowAdmin = true;
        Connection = await ConnectionMultiplexer.ConnectAsync(options);
    }

    public async Task DisposeAsync()
    {
        if (Connection is not null)
            await Connection.DisposeAsync();
        await Container.DisposeAsync();
    }

    public async Task ResetAsync()
    {
        foreach (var endpoint in Connection.GetEndPoints())
            await Connection.GetServer(endpoint).FlushAllDatabasesAsync();
    }

    internal TickerRedisPersistenceProvider<TimeTickerEntity, CronTickerEntity> CreateProvider(string nodeIdentifier, ITickerClock clock)
        => new(
            Database,
            clock,
            new SchedulerOptionsBuilder { NodeIdentifier = nodeIdentifier },
            new SharpTickerRedisOptionBuilder { JsonSerializerContext = TestJsonSerializerContext.Default },
            NullLogger<TickerRedisPersistenceProvider<TimeTickerEntity, CronTickerEntity>>.Instance);
}

[CollectionDefinition(RedisTestFixture.CollectionName)]
public sealed class RedisCollection : ICollectionFixture<RedisTestFixture>;
