#if NET10_0
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpTicker.DependencyInjection;
using SharpTicker.EntityFrameworkCore.Customizer;
using SharpTicker.EntityFrameworkCore.DbContextFactory;
using SharpTicker.EntityFrameworkCore.DependencyInjection;
using SharpTicker.Sample.ApplicationDbContext.Data;

namespace SharpTicker.Samples.Tests;

/// <summary>
/// The EF samples apply their migrations when they start. EF Core 9 and later refuses to do that when the
/// model has changes no migration covers, so a sample whose migrations fell behind the library model does not
/// start. These tests catch that drift, and a migration that fails to apply, before a person runs the sample.
/// </summary>
public class SampleMigrationTests
{
    [Theory]
    [InlineData("SharpTicker.Sample.WebApi")]
    [InlineData("SharpTicker.Sample.Console")]
    public void SampleUsingSharpTickerDbContext_MigrationsMatchTheModel(string sampleAssembly)
    {
        using var connection = OpenInMemoryDatabase();
        var options = new DbContextOptionsBuilder<SharpTickerDbContext>()
            .UseSqlite(connection, b => b.MigrationsAssembly(sampleAssembly))
            .Options;

        using var context = new SharpTickerDbContext(options);

        AssertMigrationsMatchModelAndApply(context, sampleAssembly);
    }

    [Fact]
    public void SampleWithItsOwnDbContext_MigrationsMatchTheModel()
    {
        const string sampleAssembly = "SharpTicker.Sample.ApplicationDbContext";
        using var connection = OpenInMemoryDatabase();

        // The same registration as the sample's Program.cs.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite(connection, b => b.MigrationsAssembly(sampleAssembly)));
        services.AddSharpTicker(options =>
        {
            options.AddOperationalStore(ef =>
            {
                ef.UseApplicationDbContext<AppDbContext>(ConfigurationType.UseModelCustomizer);
            });
        });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        AssertMigrationsMatchModelAndApply(context, sampleAssembly);
    }

    private static SqliteConnection OpenInMemoryDatabase()
    {
        // An in-memory SQLite database lives as long as its connection stays open.
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static void AssertMigrationsMatchModelAndApply(DbContext context, string sampleAssembly)
    {
        Assert.False(
            context.Database.HasPendingModelChanges(),
            $"{sampleAssembly}: the model has changes no migration covers. " +
            "Add one with: dotnet ef migrations add <Name> --framework net10.0");

        // Throws if a migration cannot be applied to an empty database.
        context.Database.Migrate();

        Assert.Empty(context.Database.GetPendingMigrations());
    }
}
#endif
