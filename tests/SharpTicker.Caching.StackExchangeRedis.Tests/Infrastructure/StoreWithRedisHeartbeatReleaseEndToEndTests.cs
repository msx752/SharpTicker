using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using SharpTicker.DependencyInjection;
using SharpTicker.EntityFrameworkCore.Customizer;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using Testcontainers.MongoDb;
using EfStore = SharpTicker.EntityFrameworkCore.DependencyInjection.ServiceExtension;
using MongoStore = SharpTicker.MongoDB.DependencyInjection.ServiceExtension;
using RedisStore = SharpTicker.Caching.StackExchangeRedis.DependencyInjection.ServiceExtension;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>A MongoDB container for the tests of one class.</summary>
public sealed class MongoContainerFixture : IAsyncLifetime
{
    // No credentials, as in the MongoDB tests: with a root user the image restarts mongod on the same port during
    // initialisation, which occasionally fails.
    public MongoDbContainer Container { get; } = new MongoDbBuilder("mongo:7")
        .WithUsername(string.Empty)
        .WithPassword(string.Empty)
        .Build();

    public Task InitializeAsync() => Container.StartAsync();

    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}

public class HeartbeatReleaseDbContext(DbContextOptions<HeartbeatReleaseDbContext> options) : DbContext(options);

/// <summary>
/// A job store other than Redis, with Redis next to it only for the node heartbeat, the way an application sets it up:
/// <c>AddSharpTicker</c> with <c>AddOperationalStore</c> and <c>AddStackExchangeRedis</c>, started hosts, jobs added through
/// the manager, a real Redis. Node A runs a long job; then only A's heartbeat write is made to fail (the registry its
/// heartbeat service writes through is wrapped), while A goes on running and reaches its store. Node B checks the
/// heartbeats. Heartbeat interval 1 s, so the TTL is 21 s, and B releases A about two TTLs after A's last heartbeat.
/// <para>
/// The self-fence (a node that cannot write its heartbeat stops its jobs before the others release them) applies to the
/// Redis job store only. With another store, B releases only what A holds and has not started; a job that may still run
/// on A is left to A (#223).
/// </para>
/// </summary>
public abstract class StoreWithRedisHeartbeatReleaseEndToEndTests(RedisTestFixture redis) : IAsyncLifetime
{
    private const string NodeA = "heartbeat-release-node-a";
    private const string NodeB = "heartbeat-release-node-b";
    private const string NodeC = "heartbeat-release-node-c";
    private const string Function = "StoreWithRedisHeartbeat.EndToEnd.Job";
    private const string WaitingFunction = "StoreWithRedisHeartbeat.EndToEnd.Waiting";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Ttl = SharpTickerRedisContext.HeartbeatTtl(Interval);
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(60);

    // Every start of the job: the node it started on, and whether the first run was still running then.
    private static readonly ConcurrentQueue<(string Node, bool FirstStillRunning)> Starts = new();
    private static readonly ConcurrentQueue<string> WaitingRuns = new();
    private static TaskCompletionSource _firstStarted = new();
    private static TaskCompletionSource _firstEnded = new();
    private static TaskCompletionSource _letFirstRunEnd = new();
    private static volatile bool _firstRunWasAskedToStop;
    private static int _startCount;

    private static readonly ConcurrentQueue<string> Handled = new();

    static StoreWithRedisHeartbeatReleaseEndToEndTests()
    {
        TickerFunctionProvider.RegisterFunctions(new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>
        {
            [Function] = (string.Empty, TickerTaskPriority.Normal, async (token, services, _) =>
            {
                var node = services.GetRequiredService<SchedulerOptionsBuilder>().NodeIdentifier;
                var first = Interlocked.Increment(ref _startCount) == 1;
                Starts.Enqueue((node, !first && !_firstEnded.Task.IsCompleted));
                // The first run goes on until the test lets it end or it is asked to stop; a later run ends at once.
                if (!first)
                    return;
                _firstStarted.TrySetResult();
                try
                {
                    await _letFirstRunEnd.Task.WaitAsync(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    _firstRunWasAskedToStop = true;
                    throw;
                }
                finally
                {
                    _firstEnded.TrySetResult();
                }
            }, 0),
            [WaitingFunction] = (string.Empty, TickerTaskPriority.Normal, (_, services, _) =>
            {
                WaitingRuns.Enqueue(services.GetRequiredService<SchedulerOptionsBuilder>().NodeIdentifier);
                return Task.CompletedTask;
            }, 0),
        });
        TickerFunctionProvider.Build();
    }

    /// <summary>The name of the store's provider type, to check that the application runs on it.</summary>
    protected abstract string ProviderTypeName { get; }

    /// <summary>Adds the job store under test to the application.</summary>
    protected abstract void AddStore(IServiceCollection services, TickerOptionsBuilder<TimeTickerEntity, CronTickerEntity> options);

    /// <summary>Called once, before the first host is started.</summary>
    protected virtual Task PrepareStoreAsync(IHost host) => Task.CompletedTask;

    /// <summary>Writes a row straight into the store, as a node's claim leaves it.</summary>
    protected abstract Task InsertAsync(IHost host, TimeTickerEntity row);

    protected virtual Task DisposeStoreAsync() => Task.CompletedTask;

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

    private sealed class RecordingLoggerProvider(string node, ConcurrentQueue<(LogLevel Level, string Text, string Category, int EventId)> entries) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, $"[{node}] {categoryName}", entries);

        public void Dispose()
        {
        }

        private sealed class Logger(string categoryName, string category, ConcurrentQueue<(LogLevel, string, string, int)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue((logLevel, $"{logLevel} {category}: {formatter(state, exception)}{(exception == null ? "" : " | " + exception.GetType().Name + ": " + exception.Message)}", categoryName, eventId.Id));
        }
    }

    private sealed class HeartbeatSwitch
    {
        public volatile bool Fails;
    }

    // The registry the heartbeat service writes through; the write fails while the test says so, everything else goes on.
    private sealed class SwitchedRegistry(INodeHeartbeatRegistry inner, HeartbeatSwitch heartbeat) : INodeHeartbeatRegistry
    {
        public Task<JsonElement> WriteHeartbeatAsync()
            => heartbeat.Fails ? Task.FromException<JsonElement>(new TimeoutException("The heartbeat write did not reach Redis.")) : inner.WriteHeartbeatAsync();

        public Task<string[]> GetMissingNodesAsync() => inner.GetMissingNodesAsync();
        public Task<bool> IsNodeAliveAsync(string node) => inner.IsNodeAliveAsync(node);
        public Task RemoveNodeFromRegistryAsync(string node) => inner.RemoveNodeFromRegistryAsync(node);
    }

    private readonly ConcurrentQueue<(LogLevel Level, string Text, string Category, int EventId)> _log = new();
    private readonly HeartbeatSwitch _heartbeatA = new();
    private readonly List<IHost> _hosts = new();
    private bool _prepared;

    public async Task InitializeAsync()
    {
        await redis.ResetAsync();
        while (Starts.TryDequeue(out _)) { }
        while (WaitingRuns.TryDequeue(out _)) { }
        Handled.Clear();
        _startCount = 0;
        _firstRunWasAskedToStop = false;
        _firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _firstEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _letFirstRunEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public async Task DisposeAsync()
    {
        _letFirstRunEnd.TrySetResult();
        foreach (var host in _hosts)
        {
            await host.StopAsync();
            host.Dispose();
        }

        _hosts.Clear();
        await redis.ResetAsync();
        await DisposeStoreAsync();
    }

    // The application: the store under test, Redis next to it with a one-second heartbeat, its exception handler and
    // logging. A node without background services writes its heartbeat and claims nothing.
    private async Task<IHost> StartNodeAsync(string node, HeartbeatSwitch heartbeat, bool backgroundServices = true)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(new RecordingLoggerProvider(node, _log));
        builder.Services.AddSharpTicker(options =>
        {
            options.ConfigureScheduler(scheduler =>
            {
                scheduler.NodeIdentifier = node;
                // Short, so that the waits below span several fallback passes.
                scheduler.FallbackIntervalChecker = TimeSpan.FromSeconds(1);
            });
            options.SetExceptionHandler<RecordingExceptionHandler>();
            if (!backgroundServices)
                options.DisableBackgroundServices();
            AddStore(builder.Services, options);
            RedisStore.AddStackExchangeRedis(options, r =>
            {
                r.Configuration = redis.Container.GetConnectionString();
                r.NodeHeartbeatInterval = Interval;
            });
        });
        // Registered after the library's own, so the heartbeat service gets this one.
        builder.Services.AddSingleton<INodeHeartbeatRegistry>(sp => new SwitchedRegistry(sp.GetRequiredService<SharpTickerRedisContext>(), heartbeat));

        var host = builder.Build();
        if (!_prepared)
        {
            await PrepareStoreAsync(host);
            _prepared = true;
        }

        host.UseSharpTicker();
        _hosts.Add(host);
        await host.StartAsync();

        Assert.Equal(ProviderTypeName,
            host.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>().GetType().Name);
        return host;
    }

    private string Report()
        => $"starts: {string.Join(", ", Starts.Select(s => $"{s.Node} (first run still running: {s.FirstStillRunning})"))}\n" +
           $"first run asked to stop: {_firstRunWasAskedToStop}, first run ended: {_firstEnded.Task.IsCompleted}\n" +
           $"waiting job ran on: {string.Join(", ", WaitingRuns)}\n" +
           $"handled: {string.Join("\n", Handled)}\n" +
           $"log:\n{string.Join("\n", _log.Where(e => e.Level >= LogLevel.Information && !e.Text.Contains("Microsoft.Hosting")).Select(e => e.Text))}";

    private static async Task<Guid> AddJobAsync(IHost host)
    {
        var manager = host.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
        var added = await manager.AddAsync(new TimeTickerEntity { Function = Function, ExecutionTime = DateTime.UtcNow.AddSeconds(1) });
        Assert.True(added.IsSucceeded, $"the job was not added: {added.Exception}");
        return added.Result.Id;
    }

    private static async Task<TimeTickerEntity?> RowAsync(IHost host, Guid id)
        => await host.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>()
            .GetTimeTickerById(id, CancellationToken.None);

    // Waits until the condition holds or the time is up.
    private static async Task WaitUntilOrAsync(Func<bool> condition, TimeSpan span)
    {
        var deadline = DateTime.UtcNow + span;
        while (DateTime.UtcNow < deadline && !condition())
            await Task.Delay(200);
    }

    // Two TTLs until B can release a node, and more for the checks (10 s apart at most) and several fallback passes.
    private static TimeSpan UntilAReleaseAndMore => 2 * Ttl + TimeSpan.FromSeconds(20);

    // A, still running and reaching its store, cannot write its heartbeat while its job runs. B releases A about two TTLs
    // later. A job that may still run on A is not released: it stays InProgress under A and is not started again.
    [Fact]
    public async Task TheRunningJobOfANodeWhoseHeartbeatStopped_IsNotReleasedAndNotStartedAgain()
    {
        var a = await StartNodeAsync(NodeA, _heartbeatA);
        var id = await AddJobAsync(a);
        await _firstStarted.Task.WaitAsync(WaitLimit);
        await StartNodeAsync(NodeB, new HeartbeatSwitch());

        _heartbeatA.Fails = true;
        await WaitUntilOrAsync(() => Volatile.Read(ref _startCount) >= 2, UntilAReleaseAndMore);

        var row = await RowAsync(a, id);
        Assert.True(Volatile.Read(ref _startCount) == 1, $"the job of node A started again\n{Report()}");
        Assert.True(row?.Status == TickerStatus.InProgress && row.LockHolder == NodeA,
            $"the row is {row?.Status} held by '{row?.LockHolder}', not InProgress held by {NodeA}\n{Report()}");
    }

    // C holds an overdue job it has not started (as a claim leaves it), and its heartbeat stops. The job is not left held:
    // B's fallback takes an overdue waiting row whoever holds it, and the job runs once on B, whether or not the
    // heartbeat releases C. C has no background services, so it does not run the job itself.
    [Fact]
    public async Task AnOverdueJobHeldButNotStartedByANodeWhoseHeartbeatStopped_RunsOnce()
    {
        var heartbeatC = new HeartbeatSwitch();
        var c = await StartNodeAsync(NodeC, heartbeatC, backgroundServices: false);
        heartbeatC.Fails = true;
        var id = Guid.NewGuid();
        var past = DateTime.UtcNow.AddSeconds(-5);
        await InsertAsync(c, new TimeTickerEntity
        {
            Id = id, Function = WaitingFunction, Status = TickerStatus.Queued, ExecutionTime = past,
            LockHolder = NodeC, LockedAt = past, CreatedAt = past, UpdatedAt = past, Request = [],
        });
        await StartNodeAsync(NodeB, new HeartbeatSwitch());

        await WaitUntilOrAsync(() => !WaitingRuns.IsEmpty, UntilAReleaseAndMore);
        // Over the release of C as well, so a run after it would be seen.
        await WaitUntilOrAsync(() => WaitingRuns.Count > 1, UntilAReleaseAndMore);

        Assert.True(WaitingRuns.Count == 1 && WaitingRuns.Single() == NodeB, $"the job held by {NodeC} did not run once on {NodeB}\n{Report()}");
        // Overdue when it ran, so DueDone.
        Assert.Equal(TickerStatus.DueDone, (await RowAsync(c, id))?.Status);
    }

    // Control: the same two nodes, A's heartbeat is written throughout. The job stays InProgress under A over the same
    // span, runs once, and ends Done once the test lets it.
    [Fact]
    public async Task Control_TheRunningJobOfANodeWhoseHeartbeatIsWritten_RunsOnce()
    {
        var a = await StartNodeAsync(NodeA, _heartbeatA);
        var id = await AddJobAsync(a);
        await _firstStarted.Task.WaitAsync(WaitLimit);
        await StartNodeAsync(NodeB, new HeartbeatSwitch());

        await WaitUntilOrAsync(() => Volatile.Read(ref _startCount) >= 2, UntilAReleaseAndMore);
        Assert.True(Volatile.Read(ref _startCount) == 1, $"the job started again\n{Report()}");
        var row = await RowAsync(a, id);
        Assert.True(row?.Status == TickerStatus.InProgress && row.LockHolder == NodeA, $"the row is {row?.Status} held by '{row?.LockHolder}'\n{Report()}");

        _letFirstRunEnd.TrySetResult();
        var deadline = DateTime.UtcNow + WaitLimit;
        while ((await RowAsync(a, id))?.Status is not (TickerStatus.Done or TickerStatus.DueDone))
        {
            Assert.True(DateTime.UtcNow < deadline, $"the job did not end\n{Report()}");
            await Task.Delay(200);
        }

        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.True(Volatile.Read(ref _startCount) == 1, $"the job started again\n{Report()}");
        Assert.False(_firstRunWasAskedToStop, Report());
        Assert.True(Handled.IsEmpty, $"the exception handler was called\n{Report()}");
        // Left out: EF Core's own Error for a connection whose opening was cancelled (EF Core 8 and 9 log it, EF Core 10 does
        // not). The scheduler loop is restarted when a job is added and once after startup; when that comes while the loop
        // opens a connection, EF Core logs it (see Tracing and logs in the docs). CI run 45405706448 (net8.0) had one here.
        // The cancel tests of the EF Core store cover the case on their own.
        var problems = _log
            .Where(e => e.Level >= LogLevel.Warning && !(e.Category == "Microsoft.EntityFrameworkCore.Database.Connection" && e.EventId == 20004))
            .Select(e => e.Text).ToList();
        Assert.True(problems.Count == 0, $"the log has entries at Warning or above:\n{string.Join("\n", problems)}");
    }

    // As documented ("With EF Core or MongoDB as the job store and Redis only for the heartbeat, none of this applies"):
    // a node that cannot write its heartbeat does not ask its jobs to stop.
    [Fact]
    public async Task ANodeThatCannotWriteItsHeartbeat_DoesNotStopItsJob()
    {
        var a = await StartNodeAsync(NodeA, _heartbeatA);
        var id = await AddJobAsync(a);
        await _firstStarted.Task.WaitAsync(WaitLimit);

        _heartbeatA.Fails = true;
        await Task.Delay(Ttl + TimeSpan.FromSeconds(8));

        Assert.False(_firstEnded.Task.IsCompleted, Report());
        Assert.False(_firstRunWasAskedToStop, Report());
        Assert.Equal(TickerStatus.InProgress, (await RowAsync(a, id))?.Status);
        Assert.DoesNotContain(_log, e => e.Text.Contains("This node could not write its heartbeat for"));
    }
}

/// <summary>MongoDB as the job store, a real MongoDB container.</summary>
[Collection(RedisTestFixture.CollectionName)]
public class MongoStoreWithRedisHeartbeatReleaseEndToEndTests(RedisTestFixture redis, MongoContainerFixture mongo)
    : StoreWithRedisHeartbeatReleaseEndToEndTests(redis), IClassFixture<MongoContainerFixture>
{
    private readonly string _database = "sharpticker_hb_" + Guid.NewGuid().ToString("N");

    protected override string ProviderTypeName => "TickerMongoPersistenceProvider`2";

    protected override void AddStore(IServiceCollection services, TickerOptionsBuilder<TimeTickerEntity, CronTickerEntity> options)
        => MongoStore.AddOperationalStore(options, store => store.UseSharpTickerMongoClient(mongo.Container.GetConnectionString(), _database));

    // The store's default collection prefix is "ticker_"; the class map of the entity is registered by the host.
    protected override Task InsertAsync(IHost host, TimeTickerEntity row)
        => new MongoClient(mongo.Container.GetConnectionString()).GetDatabase(_database)
            .GetCollection<TimeTickerEntity>("ticker_TimeTickers").InsertOneAsync(row);
}

/// <summary>EF Core as the job store, SQLite in a file of its own per test.</summary>
[Collection(RedisTestFixture.CollectionName)]
public class EfCoreStoreWithRedisHeartbeatReleaseEndToEndTests(RedisTestFixture redis)
    : StoreWithRedisHeartbeatReleaseEndToEndTests(redis)
{
    private readonly string _databaseFile = Path.Combine(Path.GetTempPath(), $"sharpticker-hb-release-{Guid.NewGuid():N}.db");

    protected override string ProviderTypeName => "TickerEfCorePersistenceProvider`3";

    protected override void AddStore(IServiceCollection services, TickerOptionsBuilder<TimeTickerEntity, CronTickerEntity> options)
    {
        // SQLite has no schemas: EF Core's warning about the store's schema is about the test's database, not the code.
        services.AddDbContext<HeartbeatReleaseDbContext>(db => db
            .UseSqlite($"Data Source={_databaseFile};Default Timeout=30")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.SqliteEventId.SchemaConfiguredWarning)));
        EfStore.AddOperationalStore(options, ef => ef.UseApplicationDbContext<HeartbeatReleaseDbContext>(ConfigurationType.UseModelCustomizer));
    }

    protected override Task PrepareStoreAsync(IHost host)
    {
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<HeartbeatReleaseDbContext>().Database.EnsureCreated();
        return Task.CompletedTask;
    }

    protected override async Task InsertAsync(IHost host, TimeTickerEntity row)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HeartbeatReleaseDbContext>();
        db.Set<TimeTickerEntity>().Add(row);
        await db.SaveChangesAsync();
    }

    protected override Task DisposeStoreAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_databaseFile))
            File.Delete(_databaseFile);
        return Task.CompletedTask;
    }
}
