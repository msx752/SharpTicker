using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpTicker.DependencyInjection;
using SharpTicker.EntityFrameworkCore.Customizer;
using SharpTicker.EntityFrameworkCore.DbContextFactory;
using SharpTicker.EntityFrameworkCore.DependencyInjection;
using SharpTicker.Utilities.Entities;

namespace SharpTicker.EntityFrameworkCore.Tests.DependencyInjection;

// An application context whose constructor takes the non-generic DbContextOptions.
public class NonGenericOptionsDbContext : DbContext
{
    public NonGenericOptionsDbContext(DbContextOptions options) : base(options)
    {
    }
}

// An application context with a parameterless constructor that configures itself in OnConfiguring, and a constructor
// that takes the non-generic DbContextOptions; the application registers it by hand, without AddDbContext.
public class SelfConfiguredDbContext : DbContext
{
    public const string ConnectionString = "Data Source=self-configured-109.db";

    public SelfConfiguredDbContext()
    {
    }

    public SelfConfiguredDbContext(DbContextOptions options) : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
            optionsBuilder.UseSqlite(ConnectionString);
    }
}

/// <summary>
/// The EF Core tools (dotnet ef migrations add, dbcontext info) build the application's host and look for a context in its
/// services: every registered DbContextOptions names a context type they can create, and they create it through the
/// registered IDbContextFactory of that type when there is one (DbContextOperations.FindContextTypes and
/// CreateContextFromServiceProvider in Microsoft.EntityFrameworkCore.Design). A context that is not named there is found
/// only when its class is in the project or a migration of the project points to it (#89).
/// </summary>
public class DesignTimeDiscoveryTests
{
    // The lookup the tools make in the application service provider.
    private static Type[] ContextTypesTheToolsFind(IServiceProvider services)
        => services.GetServices<DbContextOptions>().Select(o => o.ContextType).ToArray();

    [Fact]
    public void UseApplicationDbContext_TheToolsFindTheContextInTheApplicationServices()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddDbContext<TestDbContext>(options => options.UseSqlite("Data Source=:memory:"));
        builder.Services.AddSharpTicker(options =>
            options.AddOperationalStore(ef => ef.UseApplicationDbContext<TestDbContext>(ConfigurationType.UseModelCustomizer)));

        using var app = builder.Build();

        Assert.Contains(typeof(TestDbContext), ContextTypesTheToolsFind(app.Services));
    }

    // The library's own context lives in the library, not in the application project, so before the first migration
    // this lookup is the only way the tools can find it ("No DbContext was found in assembly ...").
    [Fact]
    public void UseSharpTickerDbContext_TheToolsFindTheContextInTheApplicationServices()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSharpTicker(options =>
            options.AddOperationalStore(ef => ef.UseSharpTickerDbContext<SharpTickerDbContext>(db => db.UseSqlite("Data Source=:memory:"))));

        using var app = builder.Build();

        Assert.Contains(typeof(SharpTickerDbContext), ContextTypesTheToolsFind(app.Services));
    }

    // The tools then create the context through the registered factory: it has the options given to UseSharpTickerDbContext.
    [Fact]
    public void UseSharpTickerDbContext_TheContextTheToolsCreateHasTheConfiguredOptionsAndTheTickerTables()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSharpTicker(options =>
            options.AddOperationalStore(ef => ef.UseSharpTickerDbContext<SharpTickerDbContext>(db => db.UseSqlite("Data Source=design-time-89.db"))));

        using var app = builder.Build();

        Assert.Contains(typeof(SharpTickerDbContext), ContextTypesTheToolsFind(app.Services));
        using var context = app.Services.GetRequiredService<IDbContextFactory<SharpTickerDbContext>>().CreateDbContext();

        Assert.Equal("Data Source=design-time-89.db", context.Database.GetConnectionString());
        Assert.NotNull(context.Model.FindEntityType(typeof(TimeTickerEntity)));
        Assert.NotNull(context.Model.FindEntityType(typeof(CronTickerEntity)));
    }

    // An application context that takes the non-generic DbContextOptions gets the last registered one from the container.
    // Registering the store's options must not take that place when AddDbContext comes before AddSharpTicker; in the
    // other order the application's registration is the last one anyway.
    [Fact]
    public void UseSharpTickerDbContext_AnApplicationContextWithNonGenericOptionsKeepsItsOwnOptions()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddDbContext<NonGenericOptionsDbContext>(options => options.UseSqlite("Data Source=application-89.db"));
        builder.Services.AddSharpTicker(options =>
            options.AddOperationalStore(ef => ef.UseSharpTickerDbContext<SharpTickerDbContext>(db => db.UseSqlite("Data Source=scheduler-89.db"))));

        using var app = builder.Build();
        using var scope = app.Services.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<NonGenericOptionsDbContext>();

        Assert.Equal("Data Source=application-89.db", context.Database.GetConnectionString());
        Assert.Contains(typeof(SharpTickerDbContext), ContextTypesTheToolsFind(app.Services));
    }

    // A context registered by hand (AddScoped, no AddDbContext) has no DbContextOptions of its own in the container. The
    // container takes the constructor with the most parameters it can fill, so the store's options must not make it pick
    // the DbContextOptions constructor over the parameterless one.
    [Fact]
    public void UseSharpTickerDbContext_AnApplicationContextRegisteredByHandStillConfiguresItself()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped<SelfConfiguredDbContext>();
        builder.Services.AddSharpTicker(options =>
            options.AddOperationalStore(ef => ef.UseSharpTickerDbContext<SharpTickerDbContext>(db => db.UseSqlite("Data Source=scheduler-109.db"))));

        using var app = builder.Build();
        using var scope = app.Services.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<SelfConfiguredDbContext>();

        Assert.Equal(SelfConfiguredDbContext.ConnectionString, context.Database.GetConnectionString());
    }
}
