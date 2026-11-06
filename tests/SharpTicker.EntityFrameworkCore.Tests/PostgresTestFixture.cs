using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SharpTicker.EntityFrameworkCore.Tests.Infrastructure;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Interfaces;
using Testcontainers.PostgreSql;

namespace SharpTicker.EntityFrameworkCore.Tests;

/// <summary>
/// A clock the test moves by hand, so two provider instances can disagree about "now" or be pushed past a threshold.
/// </summary>
public sealed class FakeTickerClock(DateTime start) : ITickerClock
{
    public DateTime UtcNow { get; set; } = start;

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>
/// One real PostgreSQL container for every test class in the "Postgres" collection. Start of each test:
/// <see cref="ResetAsync"/>. <see cref="CreateProvider"/> builds a real provider over the shared database, so a
/// test can run several nodes (distinct node identifier and clock) against one store.
/// </summary>
public sealed class PostgresTestFixture : IAsyncLifetime
{
    public const string CollectionName = "Postgres";

    public PostgreSqlContainer Container { get; } = new PostgreSqlBuilder("postgres:16").Build();

    public DbContextOptions<TestSharpTickerDbContext> Options { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Container.StartAsync();

        Options = new DbContextOptionsBuilder<TestSharpTickerDbContext>()
            .UseNpgsql(Container.GetConnectionString())
            .Options;

        // The same schema the SQLite tests get: created from the model, no migrations.
        await using var context = new TestSharpTickerDbContext(Options);
        await context.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await Container.DisposeAsync();

    /// <summary>A short-lived context for seeding rows and reading them back; remember to dispose it.</summary>
    public TestSharpTickerDbContext CreateContext() => new(Options);

    public async Task ResetAsync()
    {
        await using var context = CreateContext();
        await context.Set<CronTickerOccurrenceEntity<CronTickerEntity>>().ExecuteDeleteAsync();
        await context.Set<CronTickerEntity>().ExecuteDeleteAsync();
        await context.Set<TimeTickerEntity>().ExecuteDeleteAsync();
    }

    internal TestableProvider CreateProvider(string nodeIdentifier, ITickerClock clock)
    {
        var redisContext = Substitute.For<ISharpTickerRedisContext>();
        redisContext.HasRedisConnection.Returns(false);
        redisContext.GetOrSetArrayAsync(
            Arg.Any<string>(),
            Arg.Any<Func<CancellationToken, Task<CronTickerEntity[]>>>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>()
        ).Returns(callInfo => callInfo.ArgAt<Func<CancellationToken, Task<CronTickerEntity[]>>>(1)(CancellationToken.None));

        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<TestSharpTickerDbContext>>(
            new PooledDbContextFactory<TestSharpTickerDbContext>(Options));

        return new TestableProvider(
            services.BuildServiceProvider(),
            clock,
            new SchedulerOptionsBuilder { NodeIdentifier = nodeIdentifier },
            redisContext);
    }
}

[CollectionDefinition(PostgresTestFixture.CollectionName)]
public sealed class PostgresCollection : ICollectionFixture<PostgresTestFixture>;
