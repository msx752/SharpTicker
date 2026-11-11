using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpTicker.Caching.StackExchangeRedis.DependencyInjection;
using SharpTicker.DependencyInjection;
using SharpTicker.EntityFrameworkCore.Customizer;
using SharpTicker.EntityFrameworkCore.DependencyInjection;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Interfaces;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.DependencyInjection;

public class TestDbContext : DbContext
{
    public TestDbContext(DbContextOptions<TestDbContext> options) : base(options)
    {
    }
}

public class PersistenceProviderRegistrationTests
{
    [Fact]
    public void AddSharpTicker_WithoutExternalProviders_RegistersInMemoryProvider()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddSharpTicker();

        // Assert
        var provider = services.BuildServiceProvider();
        var persistenceProvider = provider.GetService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();

        Assert.NotNull(persistenceProvider);
        Assert.Contains("InMemory", persistenceProvider.GetType().Name);
    }

    [Fact]
    public void AddSharpTicker_WithRedis_RegistersRedisPersistenceProvider()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddSharpTicker(options =>
        {
            options.AddStackExchangeRedis(redis =>
            {
                redis.Configuration = "localhost:16379,abortConnect=false";
            });
        });

        // Assert — check service descriptor to avoid requiring a live Redis connection
        var descriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>));

        Assert.NotNull(descriptor);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void AddSharpTicker_WithEfCore_RegistersEfCorePersistenceProvider()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddDbContext<TestDbContext>(options =>
        {
            options.UseSqlite("Data Source=PersistenceTest;Mode=Memory;Cache=Shared");
        });

        // Act
        services.AddSharpTicker(options =>
        {
            options.AddOperationalStore(ef =>
            {
                ef.UseApplicationDbContext<TestDbContext>(ConfigurationType.UseModelCustomizer);
            });
        });

        // Assert
        var provider = services.BuildServiceProvider();
        var persistenceProvider = provider.GetService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();

        Assert.NotNull(persistenceProvider);
        Assert.Contains("EfCore", persistenceProvider.GetType().Name);
    }

    [Fact]
    public void AddSharpTicker_WithEfCoreAndRedis_EfCoreTakesPrecedence()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddDbContext<TestDbContext>(options =>
        {
            options.UseSqlite("Data Source=PrecedenceTest;Mode=Memory;Cache=Shared");
        });

        // Act — EF Core registered first, then Redis
        services.AddSharpTicker(options =>
        {
            options.AddOperationalStore(ef =>
            {
                ef.UseApplicationDbContext<TestDbContext>(ConfigurationType.UseModelCustomizer);
            });

            options.AddStackExchangeRedis(redis =>
            {
                redis.Configuration = "localhost:16379";
            });
        });

        // Assert — EF Core should win because it uses AddSingleton, Redis uses TryAddSingleton
        var provider = services.BuildServiceProvider();
        var persistenceProvider = provider.GetService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();

        Assert.NotNull(persistenceProvider);
        Assert.Contains("EfCore", persistenceProvider.GetType().Name);
    }

    [Fact]
    public void AddSharpTicker_WithRedisAndEfCore_EfCoreTakesPrecedence()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddDbContext<TestDbContext>(options =>
        {
            options.UseSqlite("Data Source=ReversePrecedenceTest;Mode=Memory;Cache=Shared");
        });

        // Act — Redis registered first, then EF Core (reversed order)
        services.AddSharpTicker(options =>
        {
            options.AddStackExchangeRedis(redis =>
            {
                redis.Configuration = "localhost:16379";
            });

            options.AddOperationalStore(ef =>
            {
                ef.UseApplicationDbContext<TestDbContext>(ConfigurationType.UseModelCustomizer);
            });
        });

        // Assert — EF Core should still win regardless of registration order
        // because EF Core uses AddSingleton (replaces any prior) and Redis uses TryAddSingleton (skips if exists)
        var provider = services.BuildServiceProvider();
        var persistenceProvider = provider.GetService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();

        Assert.NotNull(persistenceProvider);
        Assert.Contains("EfCore", persistenceProvider.GetType().Name);
    }

    // The startup release of an earlier run's jobs belongs to the store that holds them: with the Redis store it is the
    // heartbeat service's step, with EF Core (and the Redis heartbeat) it is EF Core's own.
    [Fact]
    public void WithTheRedisStore_TheHeartbeatServiceReleasesAnEarlierRunsJobs()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        using var started = new CancellationTokenSource();
        services.AddSingleton(HostLifetime(started.Token));
        services.AddSharpTicker(options =>
        {
            options.AddStackExchangeRedis(redis => redis.ConnectionMultiplexer = NSubstitute.Substitute.For<StackExchange.Redis.IConnectionMultiplexer>());
        });
        var provider = services.BuildServiceProvider();
        Assert.Contains("Redis", provider.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>().GetType().Name);

        var release = provider.GetRequiredService<EarlierRunRelease>();

        Assert.True(release.StoreIsRedis);
        Assert.Equal(provider.GetRequiredService<SharpTicker.Utilities.SchedulerOptionsBuilder>().NodeIdentifier, release.Node);
        // The start of the run is read when the step runs, not when the service is built: the initializer records it later.
        Assert.Null(release.RunStartedAt());
        var runStartedAt = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
        provider.GetRequiredService<SharpTicker.Utilities.TickerExecutionContext>().RunStartedAt = runStartedAt;
        Assert.Equal(runStartedAt, release.RunStartedAt());
        // The host's own signal, so the step waits until the initializer has recorded the start of the run.
        Assert.Equal(started.Token, release.ApplicationStarted);
    }

    private static Microsoft.Extensions.Hosting.IHostApplicationLifetime HostLifetime(CancellationToken started)
    {
        var lifetime = NSubstitute.Substitute.For<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();
        NSubstitute.SubstituteExtensions.Returns(lifetime.ApplicationStarted, started);
        return lifetime;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithEfCoreAsTheStore_TheRedisHeartbeatServiceReleasesNothingAtStartup(bool efCoreFirst)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(HostLifetime(CancellationToken.None));
        services.AddDbContext<TestDbContext>(options => options.UseSqlite($"Data Source=StartupRelease{efCoreFirst};Mode=Memory;Cache=Shared"));
        services.AddSharpTicker(options =>
        {
            if (efCoreFirst)
                options.AddOperationalStore(ef => ef.UseApplicationDbContext<TestDbContext>(ConfigurationType.UseModelCustomizer));
            options.AddStackExchangeRedis(redis => redis.ConnectionMultiplexer = NSubstitute.Substitute.For<StackExchange.Redis.IConnectionMultiplexer>());
            if (!efCoreFirst)
                options.AddOperationalStore(ef => ef.UseApplicationDbContext<TestDbContext>(ConfigurationType.UseModelCustomizer));
        });
        var provider = services.BuildServiceProvider();

        Assert.False(provider.GetRequiredService<EarlierRunRelease>().StoreIsRedis);
    }
}
