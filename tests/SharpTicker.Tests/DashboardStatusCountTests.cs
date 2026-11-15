using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpTicker.Dashboard;
using SharpTicker.EntityFrameworkCore.DbContextFactory;
using SharpTicker.EntityFrameworkCore.DependencyInjection;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using Xunit;
using static SharpTicker.Tests.VisibilityFixtureSetup;

namespace SharpTicker.Tests;

/// <summary>
/// The status counts of the dashboard (the time ticker and cron status charts, the overall statistics) and the jobs per
/// machine, on each store, for a user the visibility rule restricts and for an admin. A child of a time ticker is not
/// counted on its own, as before. On EF Core the counts are made by the database: only COUNT queries read the job tables.
/// </summary>
public abstract class DashboardStatusCountTests<TFixture> : IAsyncLifetime where TFixture : DashboardApiFixture, new()
{
    private const int Queued = 1, Done = 3, Failed = 5;

    protected readonly TFixture Api = new();

    public Task InitializeAsync() => Api.InitializeAsync();
    public Task DisposeAsync() => Api.DisposeAsync();

    private static Dictionary<int, int> Counts(JsonElement tuples) =>
        tuples.EnumerateArray().ToDictionary(t => t.GetProperty("item1").GetInt32(), t => t.GetProperty("item2").GetInt32());

    private static Dictionary<int, int> NonZero(Dictionary<int, int> counts) => counts.Where(c => c.Value > 0).ToDictionary(c => c.Key, c => c.Value);

    private static Dictionary<string, int> Machines(JsonElement tuples) =>
        tuples.EnumerateArray().ToDictionary(t => t.GetProperty("item1").GetString()!, t => t.GetProperty("item2").GetInt32());

    private void AssertNothingLogged()
    {
        var logged = Api.Logs.Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => $"{e.Category}: {e.Message}").ToArray();
        Assert.True(logged.Length == 0, "logged: " + string.Join(" | ", logged));
    }

    [Fact]
    public async Task TimeTickerStatusCounts_AreTheSameAsBefore()
    {
        var billing = Counts(await Api.GetAsAsync(Billing, "/time-tickers/graph-data"));
        var admin = Counts(await Api.GetAsAsync(null, "/time-tickers/graph-data", admin: true));

        // Every status is listed, with 0 for the ones that do not occur
        Assert.Equal(Enum.GetValues<TickerStatus>().Select(s => (int)s), billing.Keys);
        Assert.Equal(new Dictionary<int, int> { [Done] = 2, [Failed] = 1 }, NonZero(billing));
        Assert.Equal(new Dictionary<int, int> { [Queued] = 1, [Done] = 4, [Failed] = 2 }, NonZero(admin));
        AssertNothingLogged();
    }

    [Fact]
    public async Task CronStatusCounts_AreTheSameAsBefore()
    {
        var billing = Counts(await Api.GetAsAsync(Billing, "/cron-tickers/graph-data"));
        var admin = Counts(await Api.GetAsAsync(null, "/cron-tickers/graph-data", admin: true));

        Assert.Equal(Enum.GetValues<TickerStatus>().Select(s => (int)s), billing.Keys);
        Assert.Equal(new Dictionary<int, int> { [Done] = 2, [Failed] = 1 }, NonZero(billing));
        Assert.Equal(new Dictionary<int, int> { [Queued] = 1, [Done] = 4, [Failed] = 2 }, NonZero(admin));
        AssertNothingLogged();
    }

    [Fact]
    public async Task OverallStatusCounts_AreTheSameAsBefore()
    {
        var billing = Counts(await Api.GetAsAsync(Billing, "/ticker/statuses/get"));
        var admin = Counts(await Api.GetAsAsync(null, "/ticker/statuses/get", admin: true));

        // Only the statuses that occur are listed
        Assert.Equal(new Dictionary<int, int> { [Done] = 4, [Failed] = 2 }, billing);
        Assert.Equal(new Dictionary<int, int> { [Queued] = 2, [Done] = 8, [Failed] = 4 }, admin);
        AssertNothingLogged();
    }

    [Fact]
    public async Task JobsPerMachine_AreTheSameAsBefore()
    {
        var billing = Machines(await Api.GetAsAsync(Billing, "/ticker/machine/jobs"));
        var ops = Machines(await Api.GetAsAsync(Ops, "/ticker/machine/jobs"));
        var admin = Machines(await Api.GetAsAsync(null, "/ticker/machine/jobs", admin: true));

        Assert.Equal(new Dictionary<string, int> { ["node-A"] = 1, ["node-B"] = 1 }, billing);
        Assert.Empty(ops);
        Assert.Equal(new Dictionary<string, int> { ["node-A"] = 1, ["node-B"] = 1 }, admin);
        AssertNothingLogged();
    }
}

/// <summary>Rows added to the visibility seed: children, which are not counted on their own, and rows a node has locked.</summary>
internal static class StatusCountSeed
{
    public static readonly Guid BillingLockedId = Guid.Parse("00000000-0000-0000-0000-00000000b0c1");

    public static async Task SeedAsync(DashboardApiFixture api)
    {
        await VisibilityFixtureSetup.SeedAsync(api);

        var locked = api.Time(BillingLockedId, "billing.Locked", TickerStatus.Done, api.Today);
        locked.LockHolder = "node-A";
        locked.LockedAt = api.Today;
        await api.Store.AddTimeTickers(new[] { locked }, default);

        var child = api.Time(Guid.NewGuid(), "billing.Child", TickerStatus.Idle, api.Today);
        child.ParentId = BillingInvoicesId;
        var lockedChild = api.Time(Guid.NewGuid(), "billing.Child", TickerStatus.Failed, api.Today);
        lockedChild.ParentId = BillingLockedId;
        lockedChild.LockHolder = "node-C";
        lockedChild.LockedAt = api.Today;
        var opsChild = api.Time(Guid.NewGuid(), "ops.Child", TickerStatus.Done, api.Today);
        opsChild.ParentId = OpsCleanupId;
        await api.Store.AddTimeTickers(new[] { child, lockedChild, opsChild }, default);

        await api.Store.InsertCronTickerOccurrences(new[]
        {
            new CronTickerOccurrenceEntity<CronTickerEntity>
            {
                Id = Guid.NewGuid(), CronTickerId = BillingCronId, Status = TickerStatus.Done,
                ExecutionTime = api.Today.AddHours(13), ExecutedAt = api.Today.AddHours(13),
                LockHolder = "node-B", LockedAt = api.Today, CreatedAt = api.Today, UpdatedAt = api.Today,
            },
        }, default);
    }
}

/// <summary>Records the SQL the EF Core store sends, so a test can see which queries a request made.</summary>
public sealed class RecordedCommands : DbCommandInterceptor
{
    private readonly object _gate = new();
    private readonly List<string> _texts = new();

    public string[] Texts
    {
        get { lock (_gate) return _texts.ToArray(); }
    }

    public void Clear()
    {
        lock (_gate) _texts.Clear();
    }

    private void Add(DbCommand command)
    {
        lock (_gate) _texts.Add(command.CommandText);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Add(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Add(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Add(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Add(command);
        return ValueTask.FromResult(result);
    }
}

public sealed class InMemoryStatusCountFixture : InMemoryDashboardApiFixture
{
    protected override void ConfigureHostServices(IServiceCollection services) => VisibilityFixtureSetup.ConfigureServices(services);
    protected override void UseBeforeSharpTicker(WebApplication app) => VisibilityFixtureSetup.UseAuthentication(app);
    protected override void ConfigureDashboard(DashboardOptionsBuilder dashboard) => VisibilityFixtureSetup.ConfigureDashboard(dashboard);
    protected override Task SeedMoreAsync() => StatusCountSeed.SeedAsync(this);
}

/// <summary>EF Core on SQLite, with the SQL of the store recorded.</summary>
public sealed class EfCoreStatusCountFixture : DashboardApiFixture
{
    private SqliteConnection? _connection;

    public RecordedCommands Commands { get; } = new();

    protected override void ConfigureStore(SharpTicker.Utilities.TickerOptionsBuilder<TimeTickerEntity, CronTickerEntity> options)
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var schema = new DbContextOptionsBuilder<SharpTickerDbContext>().UseSqlite(_connection).Options;
        using (var context = new SharpTickerDbContext(schema))
            context.Database.EnsureCreated();

        options.AddOperationalStore(ef => ef.UseSharpTickerDbContext<SharpTickerDbContext>(db => db.UseSqlite(_connection).AddInterceptors(Commands)));
    }

    protected override void DisposeStore() => _connection?.Dispose();

    protected override void ConfigureHostServices(IServiceCollection services) => VisibilityFixtureSetup.ConfigureServices(services);
    protected override void UseBeforeSharpTicker(WebApplication app) => VisibilityFixtureSetup.UseAuthentication(app);
    protected override void ConfigureDashboard(DashboardOptionsBuilder dashboard) => VisibilityFixtureSetup.ConfigureDashboard(dashboard);
    protected override Task SeedMoreAsync() => StatusCountSeed.SeedAsync(this);
}

[Collection(SharedStaticStateCollection.Name)]
public class InMemoryDashboardStatusCountTests : DashboardStatusCountTests<InMemoryStatusCountFixture>
{
}

[Collection(SharedStaticStateCollection.Name)]
public class EfCoreDashboardStatusCountTests : DashboardStatusCountTests<EfCoreStatusCountFixture>
{
    // The queries a request sent that read the time ticker or occurrence table. The cron table is read too, for the ids of
    // the crons a restricted user may see: that table holds the crons, not their runs.
    private string[] JobTableQueries() => Api.Commands.Texts
        .Where(t => t.Contains("\"TimeTickers\"") || t.Contains("\"CronTickerOccurrences\""))
        .ToArray();

    [Theory]
    [InlineData("/time-tickers/graph-data")]
    [InlineData("/cron-tickers/graph-data")]
    [InlineData("/ticker/statuses/get")]
    public async Task StatusCounts_AreCountedByTheDatabase_WithoutReadingTheRows(string path)
    {
        foreach (var (team, admin) in new[] { (Billing, false), ((string?)null, true) })
        {
            Api.Commands.Clear();

            await Api.GetAsAsync(team, path, admin);

            var queries = JobTableQueries();
            Assert.NotEmpty(queries);
            Assert.All(queries, q => Assert.StartsWith("SELECT COUNT(*)", q.TrimStart()));
        }
    }

    [Fact]
    public async Task JobsPerMachine_DoNotReadTheChildrenOrTheCronOfEachRow()
    {
        foreach (var (team, admin) in new[] { (Billing, false), ((string?)null, true) })
        {
            Api.Commands.Clear();

            await Api.GetAsAsync(team, "/ticker/machine/jobs", admin);

            var queries = JobTableQueries();
            Assert.NotEmpty(queries);
            Assert.All(queries, q => Assert.DoesNotContain("JOIN", q));
        }
    }
}
