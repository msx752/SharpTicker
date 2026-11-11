using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SharpTicker.EntityFrameworkCore.DbContextFactory;
using SharpTicker.Utilities.Entities;

namespace SharpTicker.Scenarios.Host;

// Two contexts with one migrations set each (the migration scenarios): one context cannot hold two sets with the same migration ids.
// Both sets are InitialSharpTickerOperationalStore (CronTickers without IsEnabled / IsSystemPaused), then
// AddCronTickerEnabledAndSystemPaused. "Documented" has the AddColumn for IsEnabled edited as MIGRATING.md says
// (defaultValue: true); "Unedited" keeps what EF generates (defaultValue: false).

public sealed class DocumentedContext(DbContextOptions<DocumentedContext> options)
    : SharpTickerDbContext<TimeTickerEntity, CronTickerEntity>(options);

public sealed class UneditedContext(DbContextOptions<UneditedContext> options)
    : SharpTickerDbContext<TimeTickerEntity, CronTickerEntity>(options);

// Used by dotnet ef only (no database is opened).
public sealed class DocumentedContextFactory : IDesignTimeDbContextFactory<DocumentedContext>
{
    public DocumentedContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<DocumentedContext>().UseNpgsql("Host=design-time").Options);
}

public sealed class UneditedContextFactory : IDesignTimeDbContextFactory<UneditedContext>
{
    public UneditedContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<UneditedContext>().UseNpgsql("Host=design-time").Options);
}
