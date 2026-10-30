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
}
