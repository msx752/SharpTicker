using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using SharpTicker.DependencyInjection;
using SharpTicker.EntityFrameworkCore.Customizer;
using SharpTicker.EntityFrameworkCore.DbContextFactory;
using SharpTicker.EntityFrameworkCore.DependencyInjection;
using SharpTicker.EntityFrameworkCore.Tests.DependencyInjection;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;

namespace SharpTicker.EntityFrameworkCore.Tests.Infrastructure;

/// <summary>
/// #257: the times of SharpTicker's own tables come back from the EF Core store as <see cref="DateTimeKind.Utc"/>, with
/// the value as it was written, in each way an application sets the store up: SharpTicker's own context, its own context
/// with the model customizer, and its own context that applies the configurations itself. The column types stay what
/// the provider gives a DateTime, so no migration is needed.
/// </summary>
public class EfCoreDateTimeKindTests
{
    internal static readonly DateTime Written = new(2026, 6, 15, 6, 30, 15, 123, DateTimeKind.Utc);

    public static TheoryData<string> Setups => new() { "SharpTickerDbContext", "ApplicationDbContext+ModelCustomizer", "ApplicationDbContext+OwnConfigurations" };

    // A context of the given setup over the given database, with the scope that owns it.
    internal static (DbContext Context, IDisposable Owner) Context(string setup, Action<DbContextOptionsBuilder> useDatabase)
    {
        switch (setup)
        {
            case "SharpTickerDbContext":
            {
                var builder = new DbContextOptionsBuilder<SharpTickerDbContext>();
                useDatabase(builder);
                var context = new SharpTickerDbContext(builder.Options);
                return (context, context);
            }
            case "ApplicationDbContext+ModelCustomizer":
            {
                var services = new ServiceCollection();
                services.AddDbContext<TestDbContext>(useDatabase);
                services.AddSharpTicker(o => o.AddOperationalStore(ef => ef.UseApplicationDbContext<TestDbContext>(ConfigurationType.UseModelCustomizer)));
                var provider = services.BuildServiceProvider();
                var scope = provider.CreateScope();
                return (scope.ServiceProvider.GetRequiredService<TestDbContext>(), new Owners(scope, provider));
            }
            case "ApplicationDbContext+OwnConfigurations":
            {
                var builder = new DbContextOptionsBuilder<TestSharpTickerDbContext>();
                useDatabase(builder);
                var context = new TestSharpTickerDbContext(builder.Options);
                return (context, context);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(setup));
        }
    }

    private sealed class Owners(params IDisposable[] owners) : IDisposable
    {
        public void Dispose()
        {
            foreach (var owner in owners)
                owner.Dispose();
        }
    }

    internal static async Task<(Guid Root, Guid Child, Guid Cron, Guid Occurrence)> SeedAsync(DbContext context)
    {
        var cron = new CronTickerEntity
        {
            Id = Guid.NewGuid(), Function = "Kind.Cron", Expression = "0 0 * * * *", IsEnabled = true, Request = [],
            CreatedAt = Written, UpdatedAt = Written
        };
        var child = new TimeTickerEntity
        {
            Id = Guid.NewGuid(), Function = "Kind.Job", RunCondition = RunCondition.OnSuccess, ExecutionTime = Written, Request = [],
            LockedAt = Written, ExecutedAt = Written, CreatedAt = Written, UpdatedAt = Written
        };
        var root = new TimeTickerEntity
        {
            Id = Guid.NewGuid(), Function = "Kind.Job", ExecutionTime = Written, Request = [],
            LockedAt = Written, ExecutedAt = Written, CreatedAt = Written, UpdatedAt = Written, Children = [child]
        };
        var occurrence = new CronTickerOccurrenceEntity<CronTickerEntity>
        {
            Id = Guid.NewGuid(), CronTickerId = cron.Id, ExecutionTime = Written, LockedAt = Written, ExecutedAt = Written,
            CreatedAt = Written, UpdatedAt = Written
        };

        context.AddRange(cron, root, occurrence);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return (root.Id, child.Id, cron.Id, occurrence.Id);
    }

    internal static void AssertUtc(string what, DateTime? value)
    {
        Assert.True(value.HasValue, $"{what}: no value read back");
        Assert.True(value!.Value.Kind == DateTimeKind.Utc && value.Value.Ticks == Written.Ticks,
            $"{what}: written {Written:O}, read back {value.Value:O} ({value.Value.Kind})");
    }

    internal static async Task AssertEveryTimeReadsBackAsUtcAsync(DbContext context, (Guid Root, Guid Child, Guid Cron, Guid Occurrence) ids)
    {
        var root = await context.Set<TimeTickerEntity>().AsNoTracking().Include(x => x.Children).SingleAsync(x => x.Id == ids.Root);
        foreach (var (name, ticker) in new[] { ("root", root), ("child", root.Children.Single()) })
        {
            AssertUtc($"{name}.ExecutionTime", ticker.ExecutionTime);
            AssertUtc($"{name}.LockedAt", ticker.LockedAt);
            AssertUtc($"{name}.ExecutedAt", ticker.ExecutedAt);
            AssertUtc($"{name}.CreatedAt", ticker.CreatedAt);
            AssertUtc($"{name}.UpdatedAt", ticker.UpdatedAt);
        }

        var cron = await context.Set<CronTickerEntity>().AsNoTracking().SingleAsync(x => x.Id == ids.Cron);
        AssertUtc("cron.CreatedAt", cron.CreatedAt);
        AssertUtc("cron.UpdatedAt", cron.UpdatedAt);

        var occurrence = await context.Set<CronTickerOccurrenceEntity<CronTickerEntity>>().AsNoTracking().SingleAsync(x => x.Id == ids.Occurrence);
        AssertUtc("occurrence.ExecutionTime", occurrence.ExecutionTime);
        AssertUtc("occurrence.LockedAt", occurrence.LockedAt);
        AssertUtc("occurrence.ExecutedAt", occurrence.ExecutedAt);
        AssertUtc("occurrence.CreatedAt", occurrence.CreatedAt);
        AssertUtc("occurrence.UpdatedAt", occurrence.UpdatedAt);

        // A projection of one column, as the store's own queries read the earliest time.
        AssertUtc("projected ExecutionTime", await context.Set<TimeTickerEntity>().Where(x => x.Id == ids.Root).Select(x => x.ExecutionTime).SingleAsync());

        // A comparison with a UTC value the code holds still finds the row (the claim marker in UpdatedAt is compared so).
        Assert.Equal(1, await context.Set<TimeTickerEntity>().CountAsync(x => x.Id == ids.Root && x.UpdatedAt == Written));
    }

    [Theory]
    [MemberData(nameof(Setups))]
    public async Task OnSqlite_EveryTimeOfTheStoresTables_ReadsBackAsUtc_WithTheValueWritten(string setup)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var (context, owner) = Context(setup, o => o.UseSqlite(connection));
        using (owner)
        {
            await context.Database.EnsureCreatedAsync();
            var ids = await SeedAsync(context);
            await AssertEveryTimeReadsBackAsUtcAsync(context, ids);
        }
    }

    // An application whose own convention stores every DateTime another way (here as a number) keeps it on the store's
    // tables too: their column types do not change.
    public sealed class BinaryTimesDbContext(DbContextOptions<BinaryTimesDbContext> options) : DbContext(options)
    {
        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            configurationBuilder.Properties<DateTime>().HaveConversion<DateTimeToBinaryConverter>();
            configurationBuilder.Properties<DateTime?>().HaveConversion<DateTimeToBinaryConverter>();
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new SharpTicker.EntityFrameworkCore.Configurations.TimeTickerConfigurations<TimeTickerEntity>());
            modelBuilder.ApplyConfiguration(new SharpTicker.EntityFrameworkCore.Configurations.CronTickerConfigurations<CronTickerEntity>());
            modelBuilder.ApplyConfiguration(new SharpTicker.EntityFrameworkCore.Configurations.CronTickerOccurrenceConfigurations<CronTickerEntity>());
        }
    }

    [Fact]
    public void AnApplicationsOwnConversionForDateTime_IsKept_OnTheStoresTables()
    {
        using var context = new BinaryTimesDbContext(new DbContextOptionsBuilder<BinaryTimesDbContext>().UseSqlite("Data Source=:memory:").Options);
        var times = context.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties())
            .Where(p => p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?))
            .ToArray();

        Assert.Equal(12, times.Length);
        foreach (var time in times)
        {
            Assert.True(time.GetTypeMapping().Converter is DateTimeToBinaryConverter,
                $"{time.DeclaringType.ClrType.Name}.{time.Name} has converter {time.GetTypeMapping().Converter?.GetType().Name ?? "none"}, the application's is DateTimeToBinaryConverter");
            Assert.Equal("INTEGER", time.GetColumnType());
        }
    }

    // The column types are the ones the provider gives a DateTime, as before the converter: no migration.
    [Theory]
    [MemberData(nameof(Setups))]
    public void TheColumnTypesOfTheTimes_AreTheProvidersForADateTime(string setup)
    {
        foreach (var (database, useDatabase) in new (string, Action<DbContextOptionsBuilder>)[]
                 {
                     ("SQLite", o => o.UseSqlite("Data Source=:memory:")),
                     ("PostgreSQL", o => o.UseNpgsql("Host=localhost"))
                 })
        {
            var (context, owner) = Context(setup, useDatabase);
            using (owner)
            {
                var plain = context.GetService<IRelationalTypeMappingSource>().FindMapping(typeof(DateTime))!.StoreType;
                var times = context.Model.GetEntityTypes()
                    .Where(e => e.ClrType == typeof(TimeTickerEntity) || e.ClrType == typeof(CronTickerEntity) || e.ClrType == typeof(CronTickerOccurrenceEntity<CronTickerEntity>))
                    .SelectMany(e => e.GetProperties())
                    .Where(p => p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?))
                    .ToArray();

                Assert.Equal(12, times.Length);
                foreach (var time in times)
                {
                    Assert.True(time.GetColumnType() == plain,
                        $"{database}, {setup}: {time.DeclaringType.ClrType.Name}.{time.Name} has column type {time.GetColumnType()}, a DateTime has {plain}");
                    Assert.Equal(typeof(DateTime), time.GetTypeMapping().Converter?.ProviderClrType ?? typeof(DateTime));
                }
            }
        }
    }
}
