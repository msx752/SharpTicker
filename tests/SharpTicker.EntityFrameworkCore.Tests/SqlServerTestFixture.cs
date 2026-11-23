using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SharpTicker.EntityFrameworkCore.Tests.Infrastructure;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Interfaces;
using Testcontainers.MsSql;

namespace SharpTicker.EntityFrameworkCore.Tests;

/// <summary>
/// #247: one real SQL Server container for every test class in the "SqlServer" collection, the same way
/// <see cref="PostgresTestFixture"/> serves the "Postgres" collection. The store's tables are in a database of their own
/// (<see cref="DatabaseName"/>), created from the model, no migrations. Start of each test: <see cref="ResetAsync"/>.
/// </summary>
public sealed class SqlServerTestFixture : IAsyncLifetime
{
    public const string CollectionName = "SqlServer";

    public const string DatabaseName = "sharpticker";

    public MsSqlContainer Container { get; } = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    /// <summary>The connection string of the server, on the database <paramref name="database"/>.</summary>
    public string ConnectionString(string database)
        => new SqlConnectionStringBuilder(Container.GetConnectionString()) { InitialCatalog = database }.ConnectionString;

    public string ConnectionString() => ConnectionString(DatabaseName);

    public DbContextOptions<TestSharpTickerDbContext> Options { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Container.StartAsync();

        Options = new DbContextOptionsBuilder<TestSharpTickerDbContext>()
            .UseSqlServer(ConnectionString())
            .Options;

        await using var context = new TestSharpTickerDbContext(Options);
        await context.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await Container.DisposeAsync();
    }

    /// <summary>A short-lived context for seeding rows and reading them back; remember to dispose it.</summary>
    public TestSharpTickerDbContext CreateContext() => new(Options);

    public async Task ResetAsync()
    {
        await using var context = CreateContext();
        await context.Set<CronTickerOccurrenceEntity<CronTickerEntity>>().ExecuteDeleteAsync();
        await context.Set<CronTickerEntity>().ExecuteDeleteAsync();
        await context.Set<TimeTickerEntity>().ExecuteDeleteAsync();
    }

    internal TestableProvider CreateProvider(string nodeIdentifier, ITickerClock clock, IInterceptor? interceptor = null)
    {
        var options = interceptor is null
            ? Options
            : new DbContextOptionsBuilder<TestSharpTickerDbContext>().UseSqlServer(ConnectionString()).AddInterceptors(interceptor).Options;

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
            new PooledDbContextFactory<TestSharpTickerDbContext>(options));

        return new TestableProvider(
            services.BuildServiceProvider(),
            clock,
            new SchedulerOptionsBuilder { NodeIdentifier = nodeIdentifier },
            redisContext);
    }
}

[CollectionDefinition(SqlServerTestFixture.CollectionName)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerTestFixture>;
