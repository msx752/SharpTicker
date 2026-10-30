using System.Text;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Models;

namespace SharpTicker.EntityFrameworkCore.Tests.Infrastructure;

/// <summary>
/// What the scheduler writes back to the database through EF Core after a run: status, timing, exception, retry count and
/// the release of the lock, only for the properties it marked for update. Also the startup work on cron tickers, the
/// cron expressions the scheduler reads and the occurrences that were pending when the application stopped.
/// </summary>
public class EfCoreExecutionResultTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Earlier = Now.AddHours(-3);

    private SqliteConnection _connection = null!;
    private DbContextOptions<TestSharpTickerDbContext> _options = null!;
    private TestSharpTickerDbContext _seed = null!;
    private TestableProvider _provider = null!;
    private int _sequence;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var clock = Substitute.For<ITickerClock>();
        clock.UtcNow.Returns(Now);

        // The cron expressions are cached through Redis when it is connected; here the factory is called directly.
        var redis = Substitute.For<ISharpTickerRedisContext>();
        redis.GetOrSetArrayAsync(
            Arg.Any<string>(),
            Arg.Any<Func<CancellationToken, Task<CronTickerEntity[]>>>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<CronTickerEntity[]>>>(1)(CancellationToken.None));

        _options = new DbContextOptionsBuilder<TestSharpTickerDbContext>().UseSqlite(_connection).Options;
        _seed = new TestSharpTickerDbContext(_options);
        await _seed.Database.EnsureCreatedAsync();

        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<TestSharpTickerDbContext>>(new PooledDbContextFactory<TestSharpTickerDbContext>(_options));
        _provider = new TestableProvider(services.BuildServiceProvider(), clock, new SchedulerOptionsBuilder { NodeIdentifier = "node-1" }, redis);

        // Orphan cleanup compares against the registered functions.
        TickerFunctionProvider.RegisterFunctions(new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>
        {
            ["KnownFunction"] = ("", TickerTaskPriority.Normal, (_, _, _) => Task.CompletedTask, 0),
            ["NewlyDefined"] = ("", TickerTaskPriority.Normal, (_, _, _) => Task.CompletedTask, 0),
        });
        TickerFunctionProvider.Build();
    }

    public async Task DisposeAsync()
    {
        await _seed.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private TestSharpTickerDbContext Verify() => new(_options);

    private async Task<TimeTickerEntity> AddLockedTimeTickerAsync(string? request = null)
    {
        var ticker = new TimeTickerEntity
        {
            Id = Guid.NewGuid(), Function = "KnownFunction", Status = TickerStatus.InProgress, ExecutionTime = Earlier,
            LockHolder = "node-1", LockedAt = Earlier, Description = "keep me", Retries = 3,
            CreatedAt = Earlier, UpdatedAt = Earlier,
            Request = request == null ? null : Encoding.UTF8.GetBytes(request),
        };
        _seed.Set<TimeTickerEntity>().Add(ticker);
        await _seed.SaveChangesAsync();
        return ticker;
    }

    private async Task<CronTickerEntity> AddCronTickerAsync(
        string function = "KnownFunction", string expression = "0 0 * * * *", bool enabled = true, string? initIdentifier = null, string? request = null)
    {
        var cron = new CronTickerEntity
        {
            Id = Guid.NewGuid(), Function = function, Expression = expression, IsEnabled = enabled,
            InitIdentifier = initIdentifier, CreatedAt = Earlier, UpdatedAt = Earlier,
            Request = request == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(request),
        };
        _seed.Set<CronTickerEntity>().Add(cron);
        await _seed.SaveChangesAsync();
        return cron;
    }

    private async Task<CronTickerOccurrenceEntity<CronTickerEntity>> AddOccurrenceAsync(
        Guid cronId, TickerStatus status = TickerStatus.InProgress, DateTime? executionTime = null, bool locked = true)
    {
        var occurrence = new CronTickerOccurrenceEntity<CronTickerEntity>
        {
            Id = Guid.NewGuid(), CronTickerId = cronId, Status = status, ExecutionTime = executionTime ?? Earlier.AddMinutes(++_sequence),
            LockHolder = locked ? "node-1" : null, LockedAt = locked ? Earlier : null, CreatedAt = Earlier, UpdatedAt = Earlier,
        };
        _seed.Set<CronTickerOccurrenceEntity<CronTickerEntity>>().Add(occurrence);
        await _seed.SaveChangesAsync();
        return occurrence;
    }

    private static InternalFunctionContext Result(Guid id) => new() { TickerId = id };

    #region A finished time ticker

    [Fact]
    public async Task ADoneResult_IsRecordedWithItsTimingAndTheLockIsReleased()
    {
        var ticker = await AddLockedTimeTickerAsync();
        var context = Result(ticker.Id)
            .SetProperty(x => x.Status, TickerStatus.Done)
            .SetProperty(x => x.ExecutedAt, Now.AddSeconds(-2))
            .SetProperty(x => x.ElapsedTime, 1500L)
            .SetProperty(x => x.ReleaseLock, true);

        var rows = await _provider.UpdateTimeTicker(context, default);

        Assert.Equal(1, rows);
        await using var db = Verify();
        var stored = await db.Set<TimeTickerEntity>().AsNoTracking().SingleAsync(x => x.Id == ticker.Id);
        Assert.Equal(TickerStatus.Done, stored.Status);
        Assert.Equal(Now.AddSeconds(-2), stored.ExecutedAt);
        Assert.Equal(1500, stored.ElapsedTime);
        Assert.Null(stored.LockHolder);
        Assert.Null(stored.LockedAt);
        Assert.Equal(Now, stored.UpdatedAt);
    }

    [Fact]
    public async Task AFailedResult_KeepsTheExceptionAndTheRetryCount()
    {
        var ticker = await AddLockedTimeTickerAsync();
        var context = Result(ticker.Id)
            .SetProperty(x => x.Status, TickerStatus.Failed)
            .SetProperty(x => x.ExceptionDetails, "System.InvalidOperationException: boom")
            .SetProperty(x => x.RetryCount, 3);

        await _provider.UpdateTimeTicker(context, default);

        await using var db = Verify();
        var stored = await db.Set<TimeTickerEntity>().AsNoTracking().SingleAsync(x => x.Id == ticker.Id);
        Assert.Equal(TickerStatus.Failed, stored.Status);
        Assert.Equal("System.InvalidOperationException: boom", stored.ExceptionMessage);
        Assert.Equal(3, stored.RetryCount);
        Assert.Null(stored.SkippedReason);
    }

    [Fact]
    public async Task ASkippedResult_KeepsTheReasonAsTheSkipReason_NotAsAnException()
    {
        var ticker = await AddLockedTimeTickerAsync();
        var context = Result(ticker.Id)
            .SetProperty(x => x.Status, TickerStatus.Skipped)
            .SetProperty(x => x.ExceptionDetails, "Skipped: a previous run was still active");

        await _provider.UpdateTimeTicker(context, default);

        await using var db = Verify();
        var stored = await db.Set<TimeTickerEntity>().AsNoTracking().SingleAsync(x => x.Id == ticker.Id);
        Assert.Equal(TickerStatus.Skipped, stored.Status);
        Assert.Equal("Skipped: a previous run was still active", stored.SkippedReason);
        Assert.Null(stored.ExceptionMessage);
    }

    [Fact]
    public async Task OnlyThePropertiesMarkedForUpdate_AreChanged()
    {
        var ticker = await AddLockedTimeTickerAsync();
        var context = Result(ticker.Id).SetProperty(x => x.Status, TickerStatus.Done);
        context.ElapsedTime = 999;
        context.RetryCount = 9;
        context.ExceptionDetails = "not marked";
        context.ReleaseLock = true;

        await _provider.UpdateTimeTicker(context, default);

        await using var db = Verify();
        var stored = await db.Set<TimeTickerEntity>().AsNoTracking().SingleAsync(x => x.Id == ticker.Id);
        Assert.Equal(TickerStatus.Done, stored.Status);
        Assert.Equal(0, stored.ElapsedTime);
        Assert.Equal(0, stored.RetryCount);
        Assert.Null(stored.ExceptionMessage);
        Assert.Equal("node-1", stored.LockHolder);
        Assert.Equal("keep me", stored.Description);
        Assert.Equal(3, stored.Retries);
    }

    [Fact]
    public async Task AResultThatMarksNoStatus_LeavesTheStatusAndTheSkipReasonAlone()
    {
        var ticker = await AddLockedTimeTickerAsync();
        _seed.Set<TimeTickerEntity>().Update(ticker);
        ticker.SkippedReason = "earlier reason";
        await _seed.SaveChangesAsync();
        var context = Result(ticker.Id).SetProperty(x => x.ReleaseLock, true);

        await _provider.UpdateTimeTicker(context, default);

        await using var db = Verify();
        var stored = await db.Set<TimeTickerEntity>().AsNoTracking().SingleAsync(x => x.Id == ticker.Id);
        Assert.Equal(TickerStatus.InProgress, stored.Status);
        Assert.Equal("earlier reason", stored.SkippedReason);
        Assert.Null(stored.LockHolder);
    }

    [Fact]
    public async Task AResultForATickerThatNoLongerExists_ChangesNothing()
    {
        var rows = await _provider.UpdateTimeTicker(Result(Guid.NewGuid()).SetProperty(x => x.Status, TickerStatus.Done), default);

        Assert.Equal(0, rows);
    }

    [Fact]
    public async Task AUnifiedResult_IsAppliedToEveryListedTicker_AndToNoOther()
    {
        var first = await AddLockedTimeTickerAsync();
        var second = await AddLockedTimeTickerAsync();
        var untouched = await AddLockedTimeTickerAsync();
        var context = new InternalFunctionContext()
            .SetProperty(x => x.Status, TickerStatus.Cancelled)
            .SetProperty(x => x.ReleaseLock, true);

        await _provider.UpdateTimeTickersWithUnifiedContext(new[] { first.Id, second.Id, Guid.NewGuid() }, context, default);

        await using var db = Verify();
        var all = await db.Set<TimeTickerEntity>().AsNoTracking().ToDictionaryAsync(x => x.Id);
        Assert.Equal(TickerStatus.Cancelled, all[first.Id].Status);
        Assert.Equal(TickerStatus.Cancelled, all[second.Id].Status);
        Assert.Null(all[second.Id].LockHolder);
        Assert.Equal(TickerStatus.InProgress, all[untouched.Id].Status);
    }

    #endregion

    #region A finished cron occurrence

    [Fact]
    public async Task ADoneOccurrence_IsRecordedWithItsTimingAndTheLockIsReleased()
    {
        var cron = await AddCronTickerAsync();
        var occurrence = await AddOccurrenceAsync(cron.Id);
        var context = Result(occurrence.Id)
            .SetProperty(x => x.Status, TickerStatus.Done)
            .SetProperty(x => x.ExecutedAt, Now.AddSeconds(-1))
            .SetProperty(x => x.ElapsedTime, 800L)
            .SetProperty(x => x.ReleaseLock, true);

        await _provider.UpdateCronTickerOccurrence(context, default);

        await using var db = Verify();
        var stored = await db.Set<CronTickerOccurrenceEntity<CronTickerEntity>>().AsNoTracking().SingleAsync(x => x.Id == occurrence.Id);
        Assert.Equal(TickerStatus.Done, stored.Status);
        Assert.Equal(Now.AddSeconds(-1), stored.ExecutedAt);
        Assert.Equal(800, stored.ElapsedTime);
        Assert.Null(stored.LockHolder);
        Assert.Equal(Now, stored.UpdatedAt);
    }

    [Fact]
    public async Task AFailedOccurrence_KeepsTheExceptionAndTheRetryCount()
    {
        var cron = await AddCronTickerAsync();
        var occurrence = await AddOccurrenceAsync(cron.Id);
        var context = Result(occurrence.Id)
            .SetProperty(x => x.Status, TickerStatus.Failed)
            .SetProperty(x => x.ExceptionDetails, "boom")
            .SetProperty(x => x.RetryCount, 2);

        await _provider.UpdateCronTickerOccurrence(context, default);

        await using var db = Verify();
        var stored = await db.Set<CronTickerOccurrenceEntity<CronTickerEntity>>().AsNoTracking().SingleAsync(x => x.Id == occurrence.Id);
        Assert.Equal("boom", stored.ExceptionMessage);
        Assert.Equal(2, stored.RetryCount);
        Assert.Null(stored.SkippedReason);
    }

    [Fact]
    public async Task ASkippedOccurrence_KeepsTheReasonAsTheSkipReason()
    {
        var cron = await AddCronTickerAsync();
        var occurrence = await AddOccurrenceAsync(cron.Id);
        var context = Result(occurrence.Id)
            .SetProperty(x => x.Status, TickerStatus.Skipped)
            .SetProperty(x => x.ExceptionDetails, "still running");

        await _provider.UpdateCronTickerOccurrence(context, default);

        await using var db = Verify();
        var stored = await db.Set<CronTickerOccurrenceEntity<CronTickerEntity>>().AsNoTracking().SingleAsync(x => x.Id == occurrence.Id);
        Assert.Equal("still running", stored.SkippedReason);
        Assert.Null(stored.ExceptionMessage);
    }

    [Fact]
    public async Task OnlyThePropertiesMarkedForUpdate_AreChangedOnAnOccurrence()
    {
        var cron = await AddCronTickerAsync();
        var occurrence = await AddOccurrenceAsync(cron.Id);
        var context = Result(occurrence.Id).SetProperty(x => x.Status, TickerStatus.Done);
        context.ElapsedTime = 999;
        context.ReleaseLock = true;

        await _provider.UpdateCronTickerOccurrence(context, default);

        await using var db = Verify();
        var stored = await db.Set<CronTickerOccurrenceEntity<CronTickerEntity>>().AsNoTracking().SingleAsync(x => x.Id == occurrence.Id);
        Assert.Equal(0, stored.ElapsedTime);
        Assert.Equal("node-1", stored.LockHolder);
    }

    [Fact]
    public async Task AnOccurrenceResultThatMarksNoStatus_LeavesTheStatusAndTheSkipReasonAlone()
    {
        // The manager moves the next occurrence of a cron that was edited by marking only its execution time.
        var cron = await AddCronTickerAsync();
        var occurrence = await AddOccurrenceAsync(cron.Id, TickerStatus.Queued);
        var later = Now.AddHours(1);
        var context = Result(occurrence.Id).SetProperty(x => x.ExecutionTime, later);

        await _provider.UpdateCronTickerOccurrence(context, default);

        await using var db = Verify();
        var stored = await db.Set<CronTickerOccurrenceEntity<CronTickerEntity>>().AsNoTracking().SingleAsync(x => x.Id == occurrence.Id);
        Assert.Equal(TickerStatus.Queued, stored.Status);
        Assert.Equal(later, stored.ExecutionTime);
    }

    [Fact]
    public async Task AUnifiedResult_IsAppliedToEveryListedOccurrence_AndToNoOther()
    {
        var cron = await AddCronTickerAsync();
        var first = await AddOccurrenceAsync(cron.Id);
        var second = await AddOccurrenceAsync(cron.Id);
        var untouched = await AddOccurrenceAsync(cron.Id);
        var context = new InternalFunctionContext()
            .SetProperty(x => x.Status, TickerStatus.Cancelled)
            .SetProperty(x => x.ReleaseLock, true);

        await _provider.UpdateCronTickerOccurrencesWithUnifiedContext(new[] { first.Id, second.Id }, context, default);

        await using var db = Verify();
        var all = await db.Set<CronTickerOccurrenceEntity<CronTickerEntity>>().AsNoTracking().ToDictionaryAsync(x => x.Id);
        Assert.Equal(TickerStatus.Cancelled, all[first.Id].Status);
        Assert.Equal(TickerStatus.Cancelled, all[second.Id].Status);
        Assert.Null(all[first.Id].LockHolder);
        Assert.Equal(TickerStatus.InProgress, all[untouched.Id].Status);
    }

    #endregion

    #region Reading the request a job runs with

    [Fact]
    public async Task ATimeTickersRequest_IsReadBack_OrNullWhenThereIsNone()
    {
        var withRequest = await AddLockedTimeTickerAsync("{\"a\":1}");
        var without = await AddLockedTimeTickerAsync();

        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString((await _provider.GetTimeTickerRequest(withRequest.Id, default))!));
        Assert.Null(await _provider.GetTimeTickerRequest(without.Id, default));
        Assert.Null(await _provider.GetTimeTickerRequest(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task AnOccurrencesRequest_IsTheOneOfItsCronTicker()
    {
        var cron = await AddCronTickerAsync(request: "{\"b\":2}");
        var occurrence = await AddOccurrenceAsync(cron.Id);

        var request = await _provider.GetCronTickerOccurrenceRequest(occurrence.Id, default);

        Assert.Equal("{\"b\":2}", Encoding.UTF8.GetString(request!));
        Assert.Null(await _provider.GetCronTickerOccurrenceRequest(Guid.NewGuid(), default));
    }

    #endregion

    #region Cron expressions the scheduler reads

    [Fact]
    public async Task TheCronExpressionsTheSchedulerReads_LeaveOutDisabledCronTickers()
    {
        var enabled = await AddCronTickerAsync("KnownFunction", "0 0 * * * *", enabled: true);
        var disabled = await AddCronTickerAsync("KnownFunction", "0 5 * * * *", enabled: false);

        var expressions = await _provider.GetAllCronTickerExpressions(default);

        Assert.Contains(expressions, c => c.Id == enabled.Id && c.Expression == "0 0 * * * *");
        Assert.DoesNotContain(expressions, c => c.Id == disabled.Id);
    }

    #endregion

    #region Seeding the cron tickers defined in code

    [Fact]
    public async Task ANewCronDefinedInCode_IsSeededWithItsSeedIdentifier()
    {
        await _provider.MigrateDefinedCronTickers(new[] { ("NewlyDefined", "0 0 3 * * *") }, default);

        await using var db = Verify();
        var seeded = await db.Set<CronTickerEntity>().AsNoTracking().SingleAsync(x => x.Function == "NewlyDefined");
        Assert.Equal("0 0 3 * * *", seeded.Expression);
        Assert.Equal("MemoryTicker_Seeded_NewlyDefined", seeded.InitIdentifier);
        Assert.Equal(Now, seeded.CreatedAt);
    }

    [Fact]
    public async Task SeedingTwice_DoesNotAddTheCronAgain()
    {
        await _provider.MigrateDefinedCronTickers(new[] { ("NewlyDefined", "0 0 3 * * *") }, default);
        await _provider.MigrateDefinedCronTickers(new[] { ("NewlyDefined", "0 0 3 * * *") }, default);

        await using var db = Verify();
        Assert.Equal(1, await db.Set<CronTickerEntity>().CountAsync(x => x.Function == "NewlyDefined"));
    }

    [Fact]
    public async Task ACronWhoseExpressionChangedInCode_KeepsItsRow_AndGetsTheNewExpression()
    {
        var existing = await AddCronTickerAsync("NewlyDefined", "0 0 3 * * *", initIdentifier: "MemoryTicker_Seeded_NewlyDefined");

        await _provider.MigrateDefinedCronTickers(new[] { ("NewlyDefined", "0 0 4 * * *") }, default);

        await using var db = Verify();
        var stored = await db.Set<CronTickerEntity>().AsNoTracking().SingleAsync(x => x.Function == "NewlyDefined");
        Assert.Equal(existing.Id, stored.Id);
        Assert.Equal("0 0 4 * * *", stored.Expression);
        Assert.Equal(Now, stored.UpdatedAt);
    }

    [Fact]
    public async Task ASeededCronWhoseFunctionIsGone_IsRemovedWithItsOccurrences()
    {
        var gone = await AddCronTickerAsync("RemovedFromCode", "0 0 1 * * *", initIdentifier: "MemoryTicker_Seeded_RemovedFromCode");
        var occurrence = await AddOccurrenceAsync(gone.Id);

        await _provider.MigrateDefinedCronTickers(new[] { ("NewlyDefined", "0 0 3 * * *") }, default);

        await using var db = Verify();
        Assert.False(await db.Set<CronTickerEntity>().AnyAsync(x => x.Id == gone.Id));
        Assert.False(await db.Set<CronTickerOccurrenceEntity<CronTickerEntity>>().AnyAsync(x => x.Id == occurrence.Id));
    }

    [Fact]
    public async Task ACronCreatedThroughTheApi_IsNeverRemoved_EvenIfItsFunctionIsNotRegistered()
    {
        var created = await AddCronTickerAsync("NotRegisteredAnywhere", "0 0 1 * * *", initIdentifier: null);

        await _provider.MigrateDefinedCronTickers(new[] { ("NewlyDefined", "0 0 3 * * *") }, default);

        await using var db = Verify();
        Assert.True(await db.Set<CronTickerEntity>().AnyAsync(x => x.Id == created.Id));
    }

    #endregion

    #region Occurrences that were pending when the application stopped

    [Fact]
    public async Task APendingOccurrenceOlderThanTheThreshold_IsSkipped_AndCounted()
    {
        var cron = await AddCronTickerAsync();
        var stale = await AddOccurrenceAsync(cron.Id, TickerStatus.Queued, Now.AddHours(-2), locked: false);
        var staleIdle = await AddOccurrenceAsync(cron.Id, TickerStatus.Idle, Now.AddHours(-2).AddMinutes(1), locked: false);

        var skipped = await _provider.SkipStaleCronOccurrencesAsync(TimeSpan.FromMinutes(1), default);

        Assert.Equal(2, skipped);
        await using var db = Verify();
        var all = await db.Set<CronTickerOccurrenceEntity<CronTickerEntity>>().AsNoTracking().ToDictionaryAsync(x => x.Id);
        Assert.All(new[] { stale.Id, staleIdle.Id }, id =>
        {
            Assert.Equal(TickerStatus.Skipped, all[id].Status);
            Assert.Contains("restarted", all[id].SkippedReason);
            Assert.Equal(Now, all[id].UpdatedAt);
        });
    }

    [Fact]
    public async Task OccurrencesThatAreRecentOrAlreadyFinished_AreLeftAlone()
    {
        var cron = await AddCronTickerAsync();
        var recent = await AddOccurrenceAsync(cron.Id, TickerStatus.Queued, Now.AddSeconds(-5), locked: false);
        var done = await AddOccurrenceAsync(cron.Id, TickerStatus.Done, Now.AddHours(-2), locked: false);
        var running = await AddOccurrenceAsync(cron.Id, TickerStatus.InProgress, Now.AddHours(-2).AddMinutes(1));

        var skipped = await _provider.SkipStaleCronOccurrencesAsync(TimeSpan.FromMinutes(1), default);

        Assert.Equal(0, skipped);
        await using var db = Verify();
        var all = await db.Set<CronTickerOccurrenceEntity<CronTickerEntity>>().AsNoTracking().ToDictionaryAsync(x => x.Id);
        Assert.Equal(TickerStatus.Queued, all[recent.Id].Status);
        Assert.Equal(TickerStatus.Done, all[done.Id].Status);
        Assert.Equal(TickerStatus.InProgress, all[running.Id].Status);
    }

    [Fact]
    public async Task ASkipThresholdOfZero_SkipsNothing()
    {
        var cron = await AddCronTickerAsync();
        await AddOccurrenceAsync(cron.Id, TickerStatus.Queued, Now.AddHours(-2), locked: false);

        Assert.Equal(0, await _provider.SkipStaleCronOccurrencesAsync(TimeSpan.Zero, default));
    }

    #endregion
}
