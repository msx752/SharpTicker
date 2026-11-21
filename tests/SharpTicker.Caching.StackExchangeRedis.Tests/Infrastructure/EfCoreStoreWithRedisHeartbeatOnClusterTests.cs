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
using SharpTicker.Utilities.Interfaces.Managers;
using StackExchange.Redis;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

public class ClusterHeartbeatDbContext(DbContextOptions<ClusterHeartbeatDbContext> options) : DbContext(options);

/// <summary>
/// The EF Core job store with Redis next to it for the heartbeat and the cron list, on a Redis Cluster (#225). Redis
/// serves only single-key calls here (the heartbeat entries, the node registry, the cron list entry), which a cluster
/// routes like any other, so this configuration works there: the jobs run, and the startup writes no cluster Error. An
/// application started as a host, jobs added through the managers; the test fails on any log entry at Warning or above.
/// </summary>
[Collection(RedisClusterFixture.CollectionName)]
public sealed class EfCoreStoreWithRedisHeartbeatOnClusterTests(RedisClusterFixture fixture) : IAsyncLifetime
{
    private const string Node = "ef-cluster-node";
    private const string TimeFunction = "EfOnCluster.Time";
    private const string CronFunction = "EfOnCluster.Cron";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(40);
    private static readonly ConcurrentDictionary<Guid, int> Runs = new();

    static EfCoreStoreWithRedisHeartbeatOnClusterTests()
    {
        static Task Count(Guid id)
        {
            Runs.AddOrUpdate(id, 1, (_, n) => n + 1);
            return Task.CompletedTask;
        }

        TickerFunctionProvider.RegisterFunctions(new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>
        {
            [TimeFunction] = (string.Empty, TickerTaskPriority.Normal, (_, _, context) => Count(context.Id), 0),
            [CronFunction] = (string.Empty, TickerTaskPriority.Normal, (_, _, context) => Count(context.Id), 0)
        });
        TickerFunctionProvider.Build();
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

    private readonly RecordingLoggerProvider _log = new();
    private readonly string _databaseFile = Path.Combine(Path.GetTempPath(), $"sharpticker-ef-cluster-{Guid.NewGuid():N}.db");
    private IHost? _host;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        Runs.Clear();
    }

    public async Task DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        await fixture.ResetAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_databaseFile))
            File.Delete(_databaseFile);
    }

    private string Report()
        => $"runs: {Runs.Count}\nlog (Warning and above):\n{string.Join("\n", _log.Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => e.Text))}";

    private async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"not within {WaitLimit}: {what}\n{Report()}");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task OnACluster_TheEfCoreStoreWithTheRedisHeartbeat_RunsItsJobs_AndWritesNoClusterError()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_log);
        builder.Services.AddDbContext<ClusterHeartbeatDbContext>(options => options.UseSqlite($"Data Source={_databaseFile}"));
        builder.Services.AddSharpTicker(options =>
        {
            options.ConfigureScheduler(scheduler => scheduler.NodeIdentifier = Node);
            options.AddOperationalStore(ef => ef.UseApplicationDbContext<ClusterHeartbeatDbContext>(ConfigurationType.UseModelCustomizer));
            options.AddStackExchangeRedis(redis => redis.Configuration = fixture.ConnectionString);
        });
        _host = builder.Build();
        using (var scope = _host.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<ClusterHeartbeatDbContext>().Database.EnsureCreated();
        _host.UseSharpTicker();
        await _host.StartAsync();

        var cronManager = _host.Services.GetRequiredService<ICronTickerManager<CronTickerEntity>>();
        foreach (var _ in new[] { 1, 2 })
        {
            var added = await cronManager.AddAsync(new CronTickerEntity { Function = CronFunction, Expression = "* * * * * *" });
            Assert.True(added.IsSucceeded, $"a cron was not added: {added.Exception}");
        }
        var timeManager = _host.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
        var ids = new List<Guid>();
        foreach (var _ in new[] { 1, 2, 3 })
        {
            var added = await timeManager.AddAsync(new TimeTickerEntity { Function = TimeFunction, ExecutionTime = DateTime.UtcNow.AddSeconds(5) });
            Assert.True(added.IsSucceeded, $"a time job was not added: {added.Exception}");
            ids.Add(added.Result.Id);
        }

        var connection = _host.Services.GetRequiredKeyedService<IConnectionMultiplexer>("sharpticker");
        await WaitUntilAsync(async () => await connection.GetDatabase().KeyExistsAsync($"sharpticker:hb:{Node}"),
            "the heartbeat of this node is written");
        await WaitUntilAsync(() => Task.FromResult(ids.All(Runs.ContainsKey) && Runs.Keys.Count(k => !ids.Contains(k)) >= 4),
            "the three time jobs and four occurrences of the two crons ran");

        var store = _host.Services.GetRequiredService<SharpTicker.Utilities.Interfaces.ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();
        await WaitUntilAsync(async () =>
        {
            foreach (var id in ids)
                if ((await store.GetTimeTickerById(id, CancellationToken.None))?.Status != TickerStatus.Done)
                    return false;
            return true;
        }, "the three time jobs are stored as Done");
        await _host.StopAsync();

        Assert.All(Runs, r => Assert.Equal(1, r.Value));
        // The warnings of EF Core about SQLite (no schemas) and about query splitting are about the test database. The crons
        // run every second, so the stop can come while an occurrence runs; the scheduler says so at Warning.
        var problems = _log.Entries
            .Where(e => e.Level >= LogLevel.Warning
                        && !e.Text.StartsWith($"{e.Level} Microsoft.EntityFrameworkCore.", StringComparison.Ordinal)
                        && !e.Text.StartsWith("Warning SharpTicker.BackgroundServices.SharpTickerSchedulerBackgroundService: The application is stopping and", StringComparison.Ordinal))
            .Select(e => e.Text).ToList();
        Assert.True(problems.Count == 0, $"the log has entries at Warning or above:\n{string.Join("\n", problems)}");
    }
}
