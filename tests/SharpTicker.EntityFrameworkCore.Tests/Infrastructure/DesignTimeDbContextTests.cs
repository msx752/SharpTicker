using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using SharpTicker.EntityFrameworkCore.Customizer;
using SharpTicker.EntityFrameworkCore.DbContextFactory;
using SharpTicker.Utilities.Entities;

namespace SharpTicker.EntityFrameworkCore.Tests.Infrastructure;

/// <summary>
/// Tests that SharpTickerDbContext and TickerModelCustomizer work at design-time
/// when SharpTickerEfCoreOptionBuilder is not available in the service provider.
/// Regression: design-time migrations failed with NullReferenceException.
/// </summary>
public class DesignTimeDbContextTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public DesignTimeDbContextTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    public void Dispose() => _connection.Dispose();

    #region SharpTickerDbContext — OnModelCreating without DI

    [Fact]
    public void OnModelCreating_WithoutOptionBuilder_UsesDefaultSchema()
    {
        // Simulate design-time: create DbContext without registering SharpTickerEfCoreOptionBuilder
        var options = new DbContextOptionsBuilder<SharpTickerDbContext>()
            .UseSqlite(_connection)
            .EnableServiceProviderCaching(false)
            .Options;

        using var context = new SharpTickerDbContext(options);

        // OnModelCreating should not throw — it should fall back to Constants.DefaultSchema
        var model = context.Model;

        // Verify the tables use the default "ticker" schema
        var timeTickerEntity = model.FindEntityType(typeof(TimeTickerEntity));
        Assert.NotNull(timeTickerEntity);
        Assert.Equal("ticker", timeTickerEntity.GetSchema());

        var cronTickerEntity = model.FindEntityType(typeof(CronTickerEntity));
        Assert.NotNull(cronTickerEntity);
        Assert.Equal("ticker", cronTickerEntity.GetSchema());
    }

    [Fact]
    public void OnModelCreating_WithOptionBuilder_UsesConfiguredSchema()
    {
        // Simulate runtime: SharpTickerEfCoreOptionBuilder is available with custom schema
        var optionBuilder = new SharpTickerEfCoreOptionBuilder<TimeTickerEntity, CronTickerEntity>();
        optionBuilder.SetSchema("custom_schema");

        var services = new ServiceCollection();
        services.AddSingleton(optionBuilder);
        var serviceProvider = services.BuildServiceProvider();

        var options = new DbContextOptionsBuilder<SharpTickerDbContext>()
            .UseSqlite(_connection)
            .UseApplicationServiceProvider(serviceProvider)
            .EnableServiceProviderCaching(false)
            .Options;

        using var context = new SharpTickerDbContext(options);
        var model = context.Model;

        var timeTickerEntity = model.FindEntityType(typeof(TimeTickerEntity));
        Assert.NotNull(timeTickerEntity);
        Assert.Equal("custom_schema", timeTickerEntity.GetSchema());
    }

    [Fact]
    public void OnModelCreating_WithoutOptionBuilder_CanCreateDatabase()
    {
        // Verify the full design-time flow: create context → ensure created (simulates migration)
        var options = new DbContextOptionsBuilder<SharpTickerDbContext>()
            .UseSqlite(_connection)
            .EnableServiceProviderCaching(false)
            .Options;

        using var context = new SharpTickerDbContext(options);

        // This exercises the full model building + DDL generation path
        var created = context.Database.EnsureCreated();
        Assert.True(created);
    }

    #endregion

    #region TickerModelCustomizer — Customize without DI

    [Fact]
    public void TickerModelCustomizer_WithoutOptionBuilder_UsesDefaultSchema()
    {
        // Simulate design-time with UseApplicationDbContext path:
        // TickerModelCustomizer replaces IModelCustomizer, but SharpTickerEfCoreOptionBuilder is missing
        var options = new DbContextOptionsBuilder<CustomAppDbContext>()
            .UseSqlite(_connection)
            .ReplaceService<IModelCustomizer, TickerModelCustomizer<TimeTickerEntity, CronTickerEntity>>()
            .EnableServiceProviderCaching(false)
            .Options;

        using var context = new CustomAppDbContext(options);
        var model = context.Model;

        // SharpTicker entities should be configured with default schema
        var timeTickerEntity = model.FindEntityType(typeof(TimeTickerEntity));
        Assert.NotNull(timeTickerEntity);
        Assert.Equal("ticker", timeTickerEntity.GetSchema());
    }

    [Fact]
    public void TickerModelCustomizer_WithOptionBuilder_UsesConfiguredSchema()
    {
        var optionBuilder = new SharpTickerEfCoreOptionBuilder<TimeTickerEntity, CronTickerEntity>();
        optionBuilder.SetSchema("app_schema");

        var services = new ServiceCollection();
        services.AddSingleton(optionBuilder);
        var serviceProvider = services.BuildServiceProvider();

        var options = new DbContextOptionsBuilder<CustomAppDbContext>()
            .UseSqlite(_connection)
            .ReplaceService<IModelCustomizer, TickerModelCustomizer<TimeTickerEntity, CronTickerEntity>>()
            .UseApplicationServiceProvider(serviceProvider)
            .EnableServiceProviderCaching(false)
            .Options;

        using var context = new CustomAppDbContext(options);
        var model = context.Model;

        var timeTickerEntity = model.FindEntityType(typeof(TimeTickerEntity));
        Assert.NotNull(timeTickerEntity);
        Assert.Equal("app_schema", timeTickerEntity.GetSchema());
    }

    #endregion
}

/// <summary>
/// Simulates a user's application DbContext that uses UseApplicationDbContext path.
/// </summary>
public class CustomAppDbContext : DbContext
{
    public CustomAppDbContext(DbContextOptions<CustomAppDbContext> options) : base(options) { }
}
