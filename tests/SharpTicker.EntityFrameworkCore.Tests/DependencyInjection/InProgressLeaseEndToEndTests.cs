using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpTicker.DependencyInjection;
using SharpTicker.EntityFrameworkCore.Customizer;
using SharpTicker.EntityFrameworkCore.DependencyInjection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;

namespace SharpTicker.EntityFrameworkCore.Tests.DependencyInjection;

public class InProgressLeaseDbContext(DbContextOptions<InProgressLeaseDbContext> options) : DbContext(options);

/// <summary>
/// The lease on InProgress jobs (#24) in an application on the EF Core store alone: started hosts (<c>AddSharpTicker</c>,
/// <c>AddOperationalStore</c>, the scheduler and the fallback) over one SQLite file per test, with a short lease. A job
/// left InProgress under a node identifier that does not come back runs again, and the chain after it follows; a job
/// that runs longer than the lease on a live instance is not taken from it. Every test also fails on a log entry at
/// Warning or above and on a call to the application's exception handler.
/// </summary>
public class InProgressLeaseEndToEndTests : IAsyncLifetime
{
    private const string Quick = "InProgressLease.EndToEnd.Quick";
    private const string Long = "InProgressLease.EndToEnd.Long";
    private const string OneAtATime = "InProgressLease.EndToEnd.OneAtATime";
    private const string Cron = "InProgressLease.EndToEnd.Cron";

    private static readonly TimeSpan ShortLease = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LongRun = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan SlotRun = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    // How often each job ran, by its id; the cron occurrences of the cron test apart.
    private static readonly ConcurrentDictionary<Guid, int> Runs = new();
    private static readonly ConcurrentDictionary<Guid, int> CronRuns = new();
    private static readonly ConcurrentQueue<string> Handled = new();

    static InProgressLeaseEndToEndTests()
    {
        TickerFunctionProvider.RegisterFunctions(new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>
        {
            [Quick] = (string.Empty, TickerTaskPriority.Normal, (_, _, context) =>
            {
                Runs.AddOrUpdate(context.Id, 1, (_, n) => n + 1);
                return Task.CompletedTask;
            }, 0),
            [Long] = (string.Empty, TickerTaskPriority.Normal, async (_, _, context) =>
            {
                Runs.AddOrUpdate(context.Id, 1, (_, n) => n + 1);
                await Task.Delay(LongRun, CancellationToken.None);
            }, 0),
            [OneAtATime] = (string.Empty, TickerTaskPriority.Normal, async (_, _, context) =>
            {
                Runs.AddOrUpdate(context.Id, 1, (_, n) => n + 1);
                await Task.Delay(SlotRun, CancellationToken.None);
            }, 1),
            [Cron] = (string.Empty, TickerTaskPriority.Normal, (_, _, context) =>
            {
                CronRuns.AddOrUpdate(context.Id, 1, (_, n) => n + 1);
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

    private readonly string _databaseFile = Path.Combine(Path.GetTempPath(), $"sharpticker-lease-{Guid.NewGuid():N}.db");
    private readonly RecordingLoggerProvider _log = new();
    private readonly List<IHost> _hosts = new();

    public Task InitializeAsync()
    {
        Runs.Clear();
        CronRuns.Clear();
        Handled.Clear();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            try
            {
                await host.StopAsync();
            }
            finally
            {
                host.Dispose();
            }
        }

        SqliteConnection.ClearAllPools();
        if (File.Exists(_databaseFile))
            File.Delete(_databaseFile);
    }

    // An application as a user writes it, built and not started; the database has its tables.
    private IHost Application(string node, TimeSpan? lease)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_log);
        // Two EF Core warnings that are not about the lease are turned off as an application would: SQLite has no schemas,
        // and the store reads a chain's children and grandchildren in one query.
        builder.Services.AddDbContext<InProgressLeaseDbContext>(options => options.UseSqlite($"Data Source={_databaseFile}")
            .ConfigureWarnings(w => w.Ignore(SqliteEventId.SchemaConfiguredWarning, RelationalEventId.MultipleCollectionIncludeWarning)));
        builder.Services.AddSharpTicker(options =>
        {
            options.ConfigureScheduler(scheduler =>
            {
                scheduler.NodeIdentifier = node;
                // Short, so that a released job is taken again within the wait below.
                scheduler.FallbackIntervalChecker = TimeSpan.FromSeconds(1);
            });
            options.SetExceptionHandler<RecordingExceptionHandler>();
            options.AddOperationalStore(ef =>
            {
                ef.UseApplicationDbContext<InProgressLeaseDbContext>(ConfigurationType.UseModelCustomizer);
                if (lease is { } value)
                    ef.SetInProgressLease(value);
            });
        });

        var host = builder.Build();
        _hosts.Add(host);
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<InProgressLeaseDbContext>().Database.EnsureCreated();
        return host;
    }

    private static async Task StartAsync(IHost host)
    {
        host.UseSharpTicker();
        await host.StartAsync();
    }

    private static async Task SeedAsync(IHost host, params object[] rows)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InProgressLeaseDbContext>();
        db.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static async Task<TimeTickerEntity> StoredAsync(IHost host, Guid id)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<InProgressLeaseDbContext>().Set<TimeTickerEntity>().AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return true;
            await Task.Delay(100);
        }

        return await condition();
    }

    private void AssertNoWarningAndNoHandlerCall()
    {
        Assert.Empty(Handled);
        var warnings = _log.Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => e.Text).ToArray();
        Assert.True(warnings.Length == 0, string.Join(Environment.NewLine, warnings));
    }

    // A time job as a node that ended without a signal leaves it: claimed an hour ago under a name that does not come
    // back, with a delayed child that runs on its success.
    private static (TimeTickerEntity Parent, TimeTickerEntity Child) AStuckParentWithAChild()
    {
        var earlier = DateTime.UtcNow.AddHours(-1);
        var parent = new TimeTickerEntity
        {
            Id = Guid.NewGuid(), Function = Quick, ExecutionTime = earlier, Status = TickerStatus.InProgress,
            LockHolder = "pod-a", LockedAt = earlier, CreatedAt = earlier, UpdatedAt = earlier, Request = []
        };
        var child = new TimeTickerEntity
        {
            Id = Guid.NewGuid(), Function = Quick, ExecutionTime = earlier, ParentId = parent.Id, RunCondition = RunCondition.OnSuccess,
            Status = TickerStatus.Idle, CreatedAt = earlier, UpdatedAt = earlier, Request = []
        };
        return (parent, child);
    }

    // T7: the parent left InProgress under pod-a is released after the lease by pod-b, runs again, and its child follows.
    [Fact]
    public async Task AJobLeftInProgressUnderANameThatDoesNotComeBack_RunsAgainAfterTheLease_AndItsChildFollows()
    {
        var application = Application("pod-b", ShortLease);
        var (parent, child) = AStuckParentWithAChild();
        await SeedAsync(application, parent, child);

        await StartAsync(application);

        var childRan = await WaitUntilAsync(() => Task.FromResult(Runs.GetValueOrDefault(child.Id) > 0), WaitLimit);
        Assert.True(childRan, $"The child did not run; the parent ran {Runs.GetValueOrDefault(parent.Id)} time(s) and is {(await StoredAsync(application, parent.Id)).Status}.");
        // A few more passes: neither runs a second time.
        await Task.Delay(3 * ShortLease);
        Assert.Equal(1, Runs.GetValueOrDefault(parent.Id));
        Assert.Equal(1, Runs.GetValueOrDefault(child.Id));
        Assert.Equal(TickerStatus.DueDone, (await StoredAsync(application, parent.Id)).Status);
        AssertNoWarningAndNoHandlerCall();
    }

    // The control: with the lease turned off nothing releases the parent, as up to 1.5.2.
    [Fact]
    public async Task WithTheLeaseTurnedOff_AJobLeftInProgressUnderANameThatDoesNotComeBack_StaysInProgress()
    {
        var application = Application("pod-b", TimeSpan.Zero);
        var (parent, child) = AStuckParentWithAChild();
        await SeedAsync(application, parent, child);

        await StartAsync(application);
        await Task.Delay(4 * ShortLease);

        var stored = await StoredAsync(application, parent.Id);
        Assert.Equal(TickerStatus.InProgress, stored.Status);
        Assert.Equal("pod-a", stored.LockHolder);
        Assert.Equal(0, Runs.GetValueOrDefault(parent.Id));
        Assert.Equal(0, Runs.GetValueOrDefault(child.Id));
        AssertNoWarningAndNoHandlerCall();
    }

    // Starts pod-a, adds the jobs through the public API, waits until pod-a holds them all InProgress, then starts pod-b.
    private async Task<(IHost A, Guid[] Ids)> AJobsRunningOnPodA_ThenPodBStartsAsync(string function, int count)
    {
        var podA = Application("pod-a", ShortLease);
        var podB = Application("pod-b", ShortLease);
        await StartAsync(podA);

        var manager = podA.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var added = await manager.AddAsync(new TimeTickerEntity { Function = function, ExecutionTime = DateTime.UtcNow.AddSeconds(1) });
            Assert.True(added.IsSucceeded, added.Exception?.ToString());
            ids.Add(added.Result.Id);
        }

        var heldByA = await WaitUntilAsync(async () =>
        {
            foreach (var id in ids)
            {
                var row = await StoredAsync(podA, id);
                if (row.Status != TickerStatus.InProgress || row.LockHolder != "pod-a")
                    return false;
            }

            return Runs.GetValueOrDefault(ids[0]) > 0;
        }, WaitLimit);
        Assert.True(heldByA, "pod-a did not take the jobs.");

        await StartAsync(podB);
        return (podA, ids.ToArray());
    }

    // T2: a job that runs three leases long on a live instance is not released by the other one; it runs once and ends
    // Done by the instance that ran it.
    [Fact]
    public async Task AJobThatRunsLongerThanTheLease_OnALiveInstance_IsNotTakenByAnother_AndRunsOnce()
    {
        var (podA, ids) = await AJobsRunningOnPodA_ThenPodBStartsAsync(Long, 1);
        var id = ids[0];

        var ended = await WaitUntilAsync(async () => (await StoredAsync(podA, id)).Status == TickerStatus.Done, WaitLimit);
        Assert.True(ended, $"The job is {(await StoredAsync(podA, id)).Status}, and ran {Runs.GetValueOrDefault(id)} time(s).");
        await Task.Delay(3 * ShortLease);

        Assert.Equal(1, Runs.GetValueOrDefault(id));
        Assert.Equal(TickerStatus.Done, (await StoredAsync(podA, id)).Status);
        AssertNoWarningAndNoHandlerCall();
    }

    // T9: a job claimed by pod-a that waits for the one slot of its function (maxConcurrency 1) for longer than the lease
    // is not released by pod-b; each job runs once.
    [Fact]
    public async Task AClaimThatWaitsForASlotLongerThanTheLease_IsNotTakenByAnother_AndEachJobRunsOnce()
    {
        var (podA, ids) = await AJobsRunningOnPodA_ThenPodBStartsAsync(OneAtATime, 2);

        var ended = await WaitUntilAsync(async () =>
        {
            foreach (var id in ids)
                if ((await StoredAsync(podA, id)).Status != TickerStatus.Done)
                    return false;
            return true;
        }, WaitLimit);
        Assert.True(ended, string.Join(", ", ids.Select(id => $"{id}: ran {Runs.GetValueOrDefault(id)} time(s)")));
        await Task.Delay(3 * ShortLease);

        Assert.All(ids, id => Assert.Equal(1, Runs.GetValueOrDefault(id)));
        AssertNoWarningAndNoHandlerCall();
    }

    // T6 (a precondition, true before the lease): a cron occurrence left InProgress under a name that does not come back
    // does not hold back the next occurrences of its cron. The default lease, so the old occurrence is not released here.
    [Fact]
    public async Task ACronOccurrenceLeftInProgress_DoesNotHoldBackTheNextOccurrences()
    {
        var application = Application("pod-b", null);
        var earlier = DateTime.UtcNow.AddMinutes(-1);
        var cron = new CronTickerEntity
        {
            Id = Guid.NewGuid(), Function = Cron, Expression = "* * * * * *", CreatedAt = earlier, UpdatedAt = earlier, Request = []
        };
        var stuck = new CronTickerOccurrenceEntity<CronTickerEntity>
        {
            Id = Guid.NewGuid(), CronTickerId = cron.Id, ExecutionTime = earlier, Status = TickerStatus.InProgress,
            LockHolder = "pod-a", LockedAt = earlier, CreatedAt = earlier, UpdatedAt = earlier
        };
        await SeedAsync(application, cron, stuck);

        await StartAsync(application);

        var ran = await WaitUntilAsync(() => Task.FromResult(CronRuns.Count >= 2), WaitLimit);
        Assert.True(ran, $"{CronRuns.Count} occurrence(s) of the cron ran.");
        Assert.False(CronRuns.ContainsKey(stuck.Id));
        using var scope = application.Services.CreateScope();
        var occurrence = await scope.ServiceProvider.GetRequiredService<InProgressLeaseDbContext>()
            .Set<CronTickerOccurrenceEntity<CronTickerEntity>>().AsNoTracking().SingleAsync(x => x.Id == stuck.Id);
        Assert.Equal(TickerStatus.InProgress, occurrence.Status);
        Assert.Equal("pod-a", occurrence.LockHolder);
        AssertNoWarningAndNoHandlerCall();
    }
}
