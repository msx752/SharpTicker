using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpTicker.DependencyInjection;
using SharpTicker.EntityFrameworkCore.DbContextFactory;
using SharpTicker.EntityFrameworkCore.DependencyInjection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;

namespace SharpTicker.Tests;

/// <summary>
/// Candidate 104: #81 converts the times of delayed chain children given as Local or Unspecified to UTC on add
/// (Unspecified read in the scheduler time zone), as the root's. These tests check the same on
/// <see cref="ITimeTickerManager{TTimeTicker}.UpdateAsync"/> and <c>UpdateBatchAsync</c> of the root, through the public
/// API of a real host, on the in-memory store and on EF Core with SQLite. The scheduler time zone is Tokyo (UTC+9, no
/// daylight saving), so the Unspecified case does not depend on the zone of the machine. The Local case is converted with
/// the zone of the machine (DateTime.ToUniversalTime) and can only show a difference on a machine that is not on UTC.
/// The control updates with the times as the store returns them, which must not move.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class UpdatedChildTimeZoneEndToEndTests : IAsyncLifetime
{
    private const string Function = "UpdatedChildTimeZone.Job";
    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    // Whole seconds, two days ahead: nothing runs it during the test.
    private static readonly DateTime RootUtc = DateTime.SpecifyKind(
        DateTime.UtcNow.Date.AddDays(2).AddHours(6), DateTimeKind.Utc);

    private readonly CapturedLogs _logs = new();
    private WebApplication? _app;
    private SqliteConnection? _connection;
    private TimeZoneInfo? _zoneBefore;

    public Task InitializeAsync()
    {
        _zoneBefore = CronScheduleCache.TimeZoneInfo;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_app != null)
        {
            await ClearStoreAsync(Store);
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        _connection?.Dispose();
        CronScheduleCache.TimeZoneInfo = _zoneBefore!;
    }

    private ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> Store
        => _app!.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();

    private ITimeTickerManager<TimeTickerEntity> Tickers
        => _app!.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();

    private static async Task ClearStoreAsync(ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> store)
    {
        var times = await store.GetTimeTickers(null, default);
        await store.RemoveTimeTickers(times.Select(t => t.Id).ToArray());
    }

    private async Task StartAsync(string store)
    {
        new ServiceCollection().MapTicker(Function, (_, _) => Task.CompletedTask);
        TickerFunctionProvider.Build();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_logs);
        builder.Services.AddSharpTicker(options =>
        {
            options.ConfigureScheduler(scheduler => scheduler.SchedulerTimeZone = Tokyo);
            options.DisableBackgroundServices();
            if (store == "EfCoreSqlite")
            {
                _connection = new SqliteConnection("Data Source=:memory:");
                _connection.Open();
                var schema = new DbContextOptionsBuilder<SharpTickerDbContext>().UseSqlite(_connection).Options;
                using (var context = new SharpTickerDbContext(schema))
                    context.Database.EnsureCreated();
                options.AddOperationalStore(ef => ef.UseSharpTickerDbContext<SharpTickerDbContext>(db => db.UseSqlite(_connection)));
            }
        });

        _app = builder.Build();
        _app.UseSharpTicker();
        await _app.StartAsync();
        var startup = _app.Services.GetRequiredService<TickerExecutionContext>().ExternalProviderStartup;
        if (startup != null) await startup;

        await ClearStoreAsync(Store);
        Assert.Same(Tokyo, CronScheduleCache.TimeZoneInfo);
    }

    // A root with a delayed child an hour after it, added in UTC.
    private async Task<(Guid Root, Guid Child)> AddChainAsync()
    {
        var chain = new TimeTickerEntity
        {
            Function = Function,
            ExecutionTime = RootUtc,
            Children = [new TimeTickerEntity { Function = Function, RunCondition = RunCondition.OnSuccess, ExecutionTime = RootUtc.AddHours(1) }]
        };
        var added = await Tickers.AddAsync(chain);
        Assert.True(added.IsSucceeded, added.Exception?.ToString());
        return (chain.Id, chain.Children.Single().Id);
    }

    private async Task UpdateAsync(TimeTickerEntity root, bool batch)
    {
        var succeeded = batch
            ? await Tickers.UpdateBatchAsync([root]) is { IsSucceeded: true }
            : await Tickers.UpdateAsync(root) is { IsSucceeded: true };
        Assert.True(succeeded, "the update was refused");
    }

    private async Task<DateTime> StoredTimeAsync(Guid id)
        => (await Store.GetTimeTickerById(id)).ExecutionTime!.Value;

    private string Problems() => string.Join("\n", _logs.Entries
        .Where(e => e.Level >= LogLevel.Warning)
        .Select(e => $"{e.Level} {e.Category}: {e.Message} {e.Exception}"));

    public static TheoryData<string, DateTimeKind, bool> Cases()
    {
        var data = new TheoryData<string, DateTimeKind, bool>();
        foreach (var store in new[] { "InMemory", "EfCoreSqlite" })
            foreach (var kind in new[] { DateTimeKind.Local, DateTimeKind.Unspecified })
                foreach (var batch in new[] { false, true })
                    data.Add(store, kind, batch);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task AChildTimeGivenAsLocalOrUnspecifiedOnUpdate_IsStoredAsTheUtcInstant_AsOnAdd(string store, DateTimeKind kind, bool batch)
    {
        await StartAsync(store);
        var (rootId, childId) = await AddChainAsync();

        // The caller moves the child to 15:00 on the day after the root, in its own reading of the time.
        var given = DateTime.SpecifyKind(RootUtc.Date.AddDays(1).AddHours(15), kind);
        var expected = kind == DateTimeKind.Local ? given.ToUniversalTime() : TimeZoneInfo.ConvertTimeToUtc(given, Tokyo);
        var root = new TimeTickerEntity
        {
            Id = rootId,
            Function = Function,
            ExecutionTime = RootUtc,
            Children = [new TimeTickerEntity { Id = childId, ParentId = rootId, Function = Function, RunCondition = RunCondition.OnSuccess, ExecutionTime = given }]
        };

        await UpdateAsync(root, batch);

        var stored = await StoredTimeAsync(childId);
        Assert.True(stored.Ticks == expected.Ticks,
            $"{store}, {kind}, batch {batch}: the child was given {given:yyyy-MM-dd HH:mm} ({kind}); stored {stored:yyyy-MM-dd HH:mm} ({stored.Kind}), "
            + $"the UTC instant is {expected:yyyy-MM-dd HH:mm}, off by {(stored - expected).TotalHours} h");
        Assert.Equal(RootUtc.Ticks, (await StoredTimeAsync(rootId)).Ticks);
        Assert.True(Problems().Length == 0, Problems());
    }

    // Control: the chain read back from the store and updated as it was read keeps the child's time, whatever Kind the
    // store returns.
    [Theory]
    [InlineData("InMemory", false)]
    [InlineData("InMemory", true)]
    [InlineData("EfCoreSqlite", false)]
    [InlineData("EfCoreSqlite", true)]
    public async Task AChildTimeReadBackFromTheStoreAndUpdated_DoesNotMove(string store, bool batch)
    {
        await StartAsync(store);
        var (rootId, childId) = await AddChainAsync();
        var readBack = await Store.GetTimeTickerById(rootId);
        var childBefore = readBack.Children.Single(c => c.Id == childId).ExecutionTime!.Value;
        Assert.Equal(RootUtc.AddHours(1).Ticks, childBefore.Ticks);

        readBack.Description = "edited";
        await UpdateAsync(readBack, batch);

        var stored = await StoredTimeAsync(childId);
        Assert.True(stored.Ticks == childBefore.Ticks,
            $"{store}, batch {batch}: the child read back as {childBefore:yyyy-MM-dd HH:mm} ({childBefore.Kind}) is stored as {stored:yyyy-MM-dd HH:mm} after the update");
        Assert.True(Problems().Length == 0, Problems());
    }

    // The same read-back update, looking at the root, whose time the update converts already (TickerManager.cs:272, :581).
    // It shows what converting a time read back from the store does for each Kind a store returns.
    [Theory]
    [InlineData("InMemory", false)]
    [InlineData("InMemory", true)]
    [InlineData("EfCoreSqlite", false)]
    [InlineData("EfCoreSqlite", true)]
    public async Task ARootTimeReadBackFromTheStoreAndUpdated_DoesNotMove(string store, bool batch)
    {
        await StartAsync(store);
        var (rootId, _) = await AddChainAsync();
        var readBack = await Store.GetTimeTickerById(rootId);
        var rootBefore = readBack.ExecutionTime!.Value;
        Assert.Equal(RootUtc.Ticks, rootBefore.Ticks);

        readBack.Description = "edited";
        await UpdateAsync(readBack, batch);

        var stored = await StoredTimeAsync(rootId);
        Assert.True(stored.Ticks == rootBefore.Ticks,
            $"{store}, batch {batch}: the root read back as {rootBefore:yyyy-MM-dd HH:mm} ({rootBefore.Kind}) is stored as {stored:yyyy-MM-dd HH:mm} ({stored.Kind}) after the update, "
            + $"off by {(stored - rootBefore).TotalHours} h");
    }
}
