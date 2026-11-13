using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpTicker.Caching.StackExchangeRedis.DependencyInjection;
using SharpTicker.DependencyInjection;
using SharpTicker.EntityFrameworkCore.Customizer;
using SharpTicker.EntityFrameworkCore.DependencyInjection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

public class CronListDbContext(DbContextOptions<CronListDbContext> options) : DbContext(options);

/// <summary>
/// The EF Core store with Redis next to it (#93). The store keeps the list of cron expressions the scheduler plans from
/// in Redis. A cron written in code gets its expression from the seed at startup, which does not go through the
/// managers; the list in Redis has to follow it, and has to end by itself after the time the store asks for.
/// SQLite in a file of its own per test, a real Redis.
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class EfCoreStoreWithRedisCacheCronListTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private const string CacheKey = "sharpticker:cron:expressions";
    private const string SeededFunction = "CronList.Seeded";
    private const string HostFunction = "CronList.EndToEnd";
    private const string GoneFunction = "CronList.NobodyRegistersThis";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    private static int _hostRuns;
    private static readonly ConcurrentQueue<string> Handled = new();

    static EfCoreStoreWithRedisCacheCronListTests()
    {
        TickerFunctionProvider.RegisterFunctions(new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>
        {
            [SeededFunction] = (string.Empty, TickerTaskPriority.Normal, (_, _, _) => Task.CompletedTask, 0),
            [HostFunction] = (string.Empty, TickerTaskPriority.Normal, (_, _, _) =>
            {
                Interlocked.Increment(ref _hostRuns);
                return Task.CompletedTask;
            }, 0)
        });
        TickerFunctionProvider.Build();
    }

    private sealed class RecordingExceptionHandler : ITickerExceptionHandler
    {
        public Task HandleExceptionAsync(Exception exception, Guid tickerId, TickerType tickerType)
        {
            Handled.Enqueue($"exception for {tickerType} {tickerId}: {exception}");
            return Task.CompletedTask;
        }

        public Task HandleCanceledExceptionAsync(Exception exception, Guid tickerId, TickerType tickerType)
        {
            Handled.Enqueue($"cancellation for {tickerType} {tickerId}: {exception}");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Text)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<(LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue((logLevel, $"{logLevel} {category}: {formatter(state, exception)}{(exception == null ? "" : " | " + exception)}"));
        }
    }

    private readonly string _databaseFile = Path.Combine(Path.GetTempPath(), $"sharpticker-cronlist-{Guid.NewGuid():N}.db");
    private readonly RecordingLoggerProvider _log = new();
    private readonly List<IHost> _applications = [];

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        Interlocked.Exchange(ref _hostRuns, 0);
        Handled.Clear();
    }

    public async Task DisposeAsync()
    {
        foreach (var application in _applications)
        {
            await application.StopAsync();
            application.Dispose();
        }

        // The registry of functions is shared by the test process: the cron of the end-to-end test is not left in it for
        // the hosts of other tests to seed.
        TickerFunctionProvider.Configure(HostFunction, cronExpression: string.Empty);
        TickerFunctionProvider.Build();

        await fixture.ResetAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_databaseFile))
            File.Delete(_databaseFile);
    }

    // The application: the EF Core store on its own database, Redis next to it, its exception handler and its logging.
    private IHost Application(string node)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_log);
        builder.Services.AddDbContext<CronListDbContext>(options => options.UseSqlite($"Data Source={_databaseFile}"));
        builder.Services.AddSharpTicker(options =>
        {
            options.ConfigureScheduler(scheduler => scheduler.NodeIdentifier = node);
            options.SetExceptionHandler<RecordingExceptionHandler>();
            options.AddOperationalStore(ef => ef.UseApplicationDbContext<CronListDbContext>(ConfigurationType.UseModelCustomizer));
            options.AddStackExchangeRedis(redis => redis.Configuration = fixture.Container.GetConnectionString());
        });

        var application = builder.Build();
        _applications.Add(application);
        using var scope = application.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CronListDbContext>().Database.EnsureCreated();
        return application;
    }

    private static ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> Store(IHost application)
        => application.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();

    private string Report()
        => $"runs: {Volatile.Read(ref _hostRuns)}\nhandled: {string.Join("\n", Handled)}\nlog:\n{string.Join("\n", _log.Entries.Select(e => e.Text))}";

    private async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"not within {WaitLimit}: {what}\n{Report()}");
            await Task.Delay(100);
        }
    }

    private static string[] Expressions(CronTickerEntity[] list, string function)
        => list.Where(c => c.Function == function).Select(c => c.Expression).ToArray();

    // The seed changes the expression of a cron written in code; the list the scheduler reads next has the new one.
    [Fact]
    public async Task TheSeedChangesAnExpression_TheNextReadOfTheListHasTheNewOne()
    {
        var store = Store(Application("cron-list"));
        await store.MigrateDefinedCronTickers([(SeededFunction, "0 0 * * * *")]);
        Assert.Equal(["0 0 * * * *"], Expressions(await store.GetAllCronTickerExpressions(CancellationToken.None), SeededFunction));

        await store.MigrateDefinedCronTickers([(SeededFunction, "0 30 * * * *")]);

        Assert.Equal(["0 30 * * * *"], Expressions(await store.GetAllCronTickerExpressions(CancellationToken.None), SeededFunction));
    }

    // The seed adds a cron that is new in code; the list that was read before it is not kept.
    [Fact]
    public async Task TheSeedAddsACron_TheNextReadOfTheListHasIt()
    {
        var store = Store(Application("cron-list"));
        Assert.Empty(Expressions(await store.GetAllCronTickerExpressions(CancellationToken.None), SeededFunction));

        await store.MigrateDefinedCronTickers([(SeededFunction, "0 0 * * * *")]);

        Assert.Equal(["0 0 * * * *"], Expressions(await store.GetAllCronTickerExpressions(CancellationToken.None), SeededFunction));
    }

    // The seed removes a seeded cron whose function is gone from the code; the list no longer has it.
    [Fact]
    public async Task TheSeedRemovesACronWhoseFunctionIsGone_TheNextReadOfTheListDoesNotHaveIt()
    {
        var store = Store(Application("cron-list"));
        await store.InsertCronTickers(
        [
            new CronTickerEntity
            {
                Id = Guid.NewGuid(), Function = GoneFunction, Expression = "0 0 * * * *",
                InitIdentifier = SeededCronTickers.InitIdentifierFor(GoneFunction), Request = []
            }
        ], CancellationToken.None);
        Assert.Equal(["0 0 * * * *"], Expressions(await store.GetAllCronTickerExpressions(CancellationToken.None), GoneFunction));

        await store.MigrateDefinedCronTickers([]);

        Assert.Empty(Expressions(await store.GetAllCronTickerExpressions(CancellationToken.None), GoneFunction));
    }

    // The store asks for ten minutes; the entry in Redis ends by then.
    [Fact]
    public async Task TheListInRedis_EndsAfterTheTimeTheStoreAsksFor()
    {
        var store = Store(Application("cron-list"));
        await store.GetAllCronTickerExpressions(CancellationToken.None);

        var timeToLive = await fixture.Database.KeyTimeToLiveAsync(CacheKey);

        Assert.True(await fixture.Database.KeyExistsAsync(CacheKey), "the list is not in Redis under the key the test reads");
        Assert.NotNull(timeToLive);
        Assert.InRange(timeToLive.Value, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(10));
    }

    // #24: with the EF Core store alone, a starting node reports the jobs that are InProgress under another node
    // identifier, because nothing releases them. With Redis next to the store its heartbeat does, so there is no report.
    [Fact]
    public async Task WithRedisNextToTheStore_JobsInProgressUnderAnotherIdentifier_AreNotReportedAtStartup()
    {
        var application = Application("cron-list");
        var id = Guid.NewGuid();
        using (var scope = application.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CronListDbContext>();
            db.Set<TimeTickerEntity>().Add(new TimeTickerEntity
            {
                Id = id, Function = SeededFunction, Status = TickerStatus.InProgress, ExecutionTime = DateTime.UtcNow.AddDays(1),
                LockHolder = "a-node-that-is-gone", LockedAt = DateTime.UtcNow.AddHours(-1), Request = []
            });
            await db.SaveChangesAsync();
        }

        application.UseSharpTicker();
        await application.StartAsync();
        var startup = application.Services.GetRequiredService<TickerExecutionContext>().ExternalProviderStartup;
        Assert.NotNull(startup);
        await startup.WaitAsync(WaitLimit);

        Assert.DoesNotContain(_log.Entries, e => e.Text.Contains("under other node identifiers"));
        // There was something to report: the job is still InProgress under the other identifier.
        var stored = await Store(application).GetTimeTickerById(id, CancellationToken.None);
        Assert.Equal(TickerStatus.InProgress, stored.Status);
        Assert.Equal("a-node-that-is-gone", stored.LockHolder);
    }

    // As an application meets it: a cron in code that runs once a year, the application runs and stops, the cron is
    // changed in code to every second, the application starts again. The job has to run on its new schedule.
    [Fact]
    public async Task ACronChangedInCodeBetweenTwoRuns_RunsOnItsNewSchedule()
    {
        TickerFunctionProvider.Configure(HostFunction, cronExpression: "0 0 0 1 1 *");
        var first = Application("cron-list-first");
        first.UseSharpTicker();
        await first.StartAsync();
        // The scheduler has read the list once it is in Redis.
        await WaitUntilAsync(() => fixture.Database.KeyExistsAsync(CacheKey), "the first run read the list of cron expressions");
        await first.StopAsync();
        Assert.Equal(0, Volatile.Read(ref _hostRuns));
        // The log is looked at from here on, and up to the stop of the second run: a stop that comes while the scheduler
        // loop opens a connection cancels that, and EF Core writes an Error for it. This test is not about a stop.
        var loggedByTheFirstRun = _log.Entries.Count;

        TickerFunctionProvider.Configure(HostFunction, cronExpression: "* * * * * *");
        var second = Application("cron-list-second");
        second.UseSharpTicker();
        await second.StartAsync();

        await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref _hostRuns) >= 2), "the job ran twice on its new schedule");

        // Left out: EF Core's own warning about a query of the store that loads more than one collection, which has
        // nothing to do with the list of cron expressions.
        var problems = _log.Entries.Skip(loggedByTheFirstRun)
            .Where(e => e.Level >= LogLevel.Warning && !e.Text.Contains("QuerySplittingBehavior"))
            .Select(e => e.Text).ToList();
        await second.StopAsync();
        Assert.True(problems.Count == 0, $"the log has entries at Warning or above:\n{string.Join("\n", problems)}");
        Assert.True(Handled.IsEmpty, $"the exception handler was called:\n{string.Join("\n", Handled)}");
    }
}
