using System.Collections.Concurrent;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpTicker.Caching.StackExchangeRedis.DependencyInjection;
using SharpTicker.Dashboard.DependencyInjection;
using SharpTicker.DependencyInjection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using StackExchange.Redis;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>
/// A real Redis Cluster of three masters (no replicas) in one container, slots spread over the three. All nodes run in
/// the same container and announce 127.0.0.1 with ports that are published one to one on the host, so the nodes reach
/// each other over the container's loopback and the test process reaches each node through the published port. That
/// avoids the usual trouble with Docker on Windows, where a node announces a container address the host cannot reach.
/// The host ports are the ports inside the container, because the nodes tell the client these ports in their slot map;
/// they are picked free on the host when the fixture starts, so the test runs of several target frameworks at the same
/// time each get their own (bus ports are the port plus 10000 inside the container, so the ports stay below 55536).
/// </summary>
public sealed class RedisClusterFixture : IAsyncLifetime
{
    // One cluster for every class that uses it.
    public const string CollectionName = "RedisCluster";

    private readonly int[] Ports = FreePorts(3);

    private static int[] FreePorts(int count)
    {
        var ports = new List<int>();
        var listeners = new List<System.Net.Sockets.TcpListener>();
        try
        {
            while (ports.Count < count)
            {
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                listener.Start();
                listeners.Add(listener);
                var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
                // The bus port of a node (its port + 10000) must not be the port of another node.
                if (port < 55536 && !ports.Any(other => other == port + 10000 || port == other + 10000))
                    ports.Add(port);
            }
        }
        finally
        {
            foreach (var listener in listeners)
                listener.Stop();
        }
        return ports.ToArray();
    }

    private readonly IContainer _container;

    public RedisClusterFixture()
    {
        var start = string.Join(" ", Ports.Select(p =>
            $"redis-server --port {p} --cluster-enabled yes --cluster-config-file /tmp/nodes-{p}.conf " +
            "--cluster-node-timeout 5000 --appendonly no --save '' --daemonize yes;"));
        var create = "redis-cli --cluster create " + string.Join(" ", Ports.Select(p => $"127.0.0.1:{p}")) +
                     " --cluster-replicas 0 --cluster-yes";
        // A create that fails ends the container, so the start fails instead of waiting for a cluster that never comes.
        var script = $"{start} sleep 1; {create} || exit 1; tail -f /dev/null";

        var builder = new ContainerBuilder("redis:7")
            .WithEntrypoint("sh", "-c", script)
            // Every node, not only the first: right after the create a node can still answer CLUSTERDOWN.
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted(
                ["sh", "-c", string.Join(" && ", Ports.Select(p => $"redis-cli -p {p} cluster info | grep -q cluster_state:ok"))],
                wait => wait.WithTimeout(TimeSpan.FromSeconds(60))));
        foreach (var port in Ports)
            builder = builder.WithPortBinding(port, port);
        _container = builder.Build();
    }

    // The seed list an application gives to the client; the client learns the slot map from any of them.
    public string ConnectionString => string.Join(",", Ports.Select(p => $"127.0.0.1:{p}"));

    public IConnectionMultiplexer Connection { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var options = ConfigurationOptions.Parse(ConnectionString);
        options.AllowAdmin = true;
        Connection = await ConnectionMultiplexer.ConnectAsync(options);

        // A write to each master before any test: the client has the slot map and every node serves its slots.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                for (var i = 0; i < 64; i++)
                    await Connection.GetDatabase().StringSetAsync($"probe:{i}", i);
                break;
            }
            catch (RedisException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }
        }
        await ResetAsync();
    }

    public async Task DisposeAsync()
    {
        if (Connection is not null)
            await Connection.DisposeAsync();
        await _container.DisposeAsync();
    }

    public async Task ResetAsync()
    {
        foreach (var endpoint in Connection.GetEndPoints())
        {
            var server = Connection.GetServer(endpoint);
            if (!server.IsReplica)
                await server.FlushAllDatabasesAsync();
        }
    }
}

/// <summary>
/// The Redis store used the way an application uses it: <c>AddSharpTicker</c> with <c>AddStackExchangeRedis</c> and the
/// dashboard, jobs added through the managers, the scheduler running them, the lists read the way the dashboard reads
/// them, and jobs deleted. On a standalone Redis (<see cref="RedisStandaloneControlEndToEndTests"/>) all of it works. On a
/// Redis Cluster it does not (#225): the store reads several job rows with one MGET, the row keys have no hash tag, and the
/// client refuses a command whose keys are in different hash slots. There the startup writes one Error that says so
/// (<see cref="RedisClusterEndToEndTests"/>). The job tests also fail on a log entry at Warning or above or a call to the
/// exception handler.
/// </summary>
public abstract class RedisTopologyEndToEndTestsBase : IAsyncLifetime
{
    private const string TimeFunction = "Topology.EndToEnd.Time";
    private const string CronFunction = "Topology.EndToEnd.Cron";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(40);

    // The category and a part of the text of the Error the startup writes on a Redis Cluster.
    protected const string ClusterCheckCategory = "SharpTicker.Caching.StackExchangeRedis.NodeHeartBeatBackgroundService";
    protected const string ClusterErrorText = "Redis Cluster is not supported as the job store";

    private static readonly ConcurrentDictionary<Guid, int> Runs = new();
    private static readonly ConcurrentQueue<string> Handled = new();

    static RedisTopologyEndToEndTestsBase()
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

    protected sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Category, string Text)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<(LogLevel, string, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue((logLevel, category, $"{logLevel} {category}: {formatter(state, exception)}{(exception == null ? "" : " | " + exception)}"));
        }
    }

    protected RecordingLoggerProvider Log { get; } = new();
    private WebApplication? _app;

    protected abstract string ConnectionString { get; }

    protected abstract string Node { get; }

    protected abstract Task ResetAsync();

    // The run counts and the handler queue are static (the job functions are) and shared with the other class; a class
    // whose tests run no job leaves them alone, so it can run at the same time as one that does.
    protected virtual bool RunsJobs => true;

    public async Task InitializeAsync()
    {
        await ResetAsync();
        if (!RunsJobs) return;
        Runs.Clear();
        Handled.Clear();
    }

    public async Task DisposeAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
        await ResetAsync();
    }

    // The application with Redis as the job store.
    protected async Task<WebApplication> StartAsync(bool backgroundServices, TimeSpan? heartbeatInterval = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(Log);
        builder.Services.AddSharpTicker(options =>
        {
            if (!backgroundServices)
                options.DisableBackgroundServices();
            options.ConfigureScheduler(scheduler => scheduler.NodeIdentifier = Node);
            options.SetExceptionHandler<RecordingExceptionHandler>();
            options.AddStackExchangeRedis(redis =>
            {
                redis.Configuration = ConnectionString;
                if (heartbeatInterval is { } interval)
                    redis.NodeHeartbeatInterval = interval;
            });
            options.AddDashboard(dashboard => dashboard.WithNoAuth());
        });

        _app = builder.Build();
        _app.UseSharpTicker();
        await _app.StartAsync();
        return _app;
    }

    private static ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> Store(WebApplication app)
        => app.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();

    protected string Report()
        => $"runs: {string.Join(", ", Runs.Select(r => $"{r.Key}={r.Value}"))}\nhandled: {string.Join("\n", Handled)}\n" +
           $"log (Warning and above):\n{string.Join("\n", Log.Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => e.Text))}";

    protected async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"not within {WaitLimit}: {what}\n{Report()}");
            await Task.Delay(100);
        }
    }

    protected (LogLevel Level, string Category, string Text)[] ClusterErrors()
        => Log.Entries.Where(e => e.Text.Contains(ClusterErrorText, StringComparison.Ordinal)).ToArray();

    // Waits until the heartbeat service has written this node's heartbeat the given number of times (each write stores a
    // new timestamp), so the check after each write has had its chance to log.
    protected async Task WaitForHeartbeatWritesAsync(WebApplication app, int writes)
    {
        var cache = app.Services.GetRequiredKeyedService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>("sharpticker");
        var seen = new HashSet<long>();
        await WaitUntilAsync(async () =>
        {
            var entry = await Microsoft.Extensions.Caching.Distributed.DistributedCacheExtensions.GetStringAsync(cache, $"hb:{Node}");
            if (!string.IsNullOrEmpty(entry))
                seen.Add(JsonDocument.Parse(entry).RootElement.EnumerateObject()
                    .Single(p => string.Equals(p.Name, "ts", StringComparison.OrdinalIgnoreCase)).Value.GetInt64());
            return seen.Count >= writes;
        }, $"{writes} heartbeat writes of this node");
        // The check runs right after the write; give it a moment to log.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
    }

    private void AssertNothingWentWrong()
    {
        var problems = Log.Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => e.Text).ToList();
        Assert.True(problems.Count == 0, $"the log has entries at Warning or above:\n{string.Join("\n", problems)}");
        Assert.True(Handled.IsEmpty, $"the exception handler was called:\n{string.Join("\n", Handled)}");
    }

    private static async Task<Guid[]> AddTimeJobsAsync(WebApplication app, int count, DateTime executionTime)
    {
        var manager = app.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var added = await manager.AddAsync(new TimeTickerEntity { Function = TimeFunction, ExecutionTime = executionTime });
            Assert.True(added.IsSucceeded, $"time job {i} was not added: {added.Exception}");
            ids.Add(added.Result.Id);
        }
        return ids.ToArray();
    }

    private static async Task<Guid> AddCronAsync(WebApplication app)
    {
        var manager = app.Services.GetRequiredService<ICronTickerManager<CronTickerEntity>>();
        var added = await manager.AddAsync(new CronTickerEntity { Function = CronFunction, Expression = "* * * * * *" });
        Assert.True(added.IsSucceeded, $"the cron was not added: {added.Exception}");
        return added.Result.Id;
    }

    private static async Task<JsonElement[]> GetArrayAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"GET {path}: {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.EnumerateArray().ToArray();
    }

    // The lists with nothing running: jobs added through the managers, then read back the way the dashboard reads them,
    // then deleted. Every row has a key of its own (tq:tt:<id>, tq:cron:<id>), so a list read touches many keys.
    protected async Task JobsAddedThroughTheManagers_AreListed_AndDeletedAsync()
    {
        var app = await StartAsync(backgroundServices: false);
        var ids = await AddTimeJobsAsync(app, 5, DateTime.UtcNow.AddHours(1));
        var cronId = await AddCronAsync(app);

        var listed = await Store(app).GetTimeTickers(_ => true, CancellationToken.None);
        Assert.Equal(ids.OrderBy(x => x), listed.Select(t => t.Id).OrderBy(x => x));

        var crons = await Store(app).GetCronTickers(_ => true, CancellationToken.None);
        Assert.Equal(cronId, Assert.Single(crons).Id);

        using var client = app.GetTestClient();
        Assert.Equal(ids.Length, (await GetArrayAsync(client, "/ticker/dashboard/api/time-tickers")).Length);
        Assert.Single(await GetArrayAsync(client, "/ticker/dashboard/api/cron-tickers"));

        var timeManager = app.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
        var deleted = await timeManager.DeleteAsync(ids[0]);
        Assert.True(deleted.IsSucceeded, $"the time job was not deleted: {deleted.Exception}");
        Assert.Equal(ids.Length - 1, (await Store(app).GetTimeTickers(_ => true, CancellationToken.None)).Length);

        var cronDeleted = await app.Services.GetRequiredService<ICronTickerManager<CronTickerEntity>>().DeleteAsync(cronId);
        Assert.True(cronDeleted.IsSucceeded, $"the cron was not deleted: {cronDeleted.Exception}");
        Assert.Empty(await Store(app).GetCronTickers(_ => true, CancellationToken.None));

        AssertNothingWentWrong();
    }

    // The scheduler on: three time jobs and a cron run, each once, and end Done; then the occurrences are listed.
    protected async Task TimeJobsAndACron_Run_AndEndDoneAsync()
    {
        var app = await StartAsync(backgroundServices: true);
        var ids = await AddTimeJobsAsync(app, 3, DateTime.UtcNow.AddSeconds(1));
        var cronId = await AddCronAsync(app);

        await WaitUntilAsync(async () =>
        {
            foreach (var id in ids)
                if ((await Store(app).GetTimeTickerById(id, CancellationToken.None))?.Status != TickerStatus.Done)
                    return false;
            return true;
        }, "the three time jobs end Done");

        // Counted from the runs, not from a list read, so this step says whether the scheduler runs the cron.
        await WaitUntilAsync(() => Task.FromResult(Runs.Keys.Count(k => !ids.Contains(k)) >= 2),
            "two occurrences of the cron ran");
        await app.StopAsync();
        Assert.All(ids, id => Assert.Equal(1, Runs.GetValueOrDefault(id)));
        Assert.All(Runs, r => Assert.Equal(1, r.Value));
        AssertNothingWentWrong();

        // The occurrences as the dashboard lists them. Read after the run checks, so a failure here is about the list read.
        var occurrences = await Store(app).GetAllCronTickerOccurrences(o => o.CronTickerId == cronId, CancellationToken.None);
        Assert.True(occurrences.Count(o => o.Status == TickerStatus.Done && Runs.ContainsKey(o.Id)) >= 2, Report());
    }

    // The scheduler's own reads with more than one row: two crons (the scheduler reads every cron row to plan the
    // occurrences) and time jobs due a few seconds later, so that the scheduler finds them in the pending index when they
    // come due rather than at the moment they are added. Checked from the runs and the log only, no list read.
    protected async Task TwoCronsAndTimeJobsDueLater_RunAsync()
    {
        var app = await StartAsync(backgroundServices: true);
        var firstCron = await AddCronAsync(app);
        var secondCron = await AddCronAsync(app);
        var ids = await AddTimeJobsAsync(app, 3, DateTime.UtcNow.AddSeconds(5));

        await WaitUntilAsync(async () =>
        {
            foreach (var id in ids)
                if ((await Store(app).GetTimeTickerById(id, CancellationToken.None))?.Status != TickerStatus.Done)
                    return false;
            return true;
        }, "the three time jobs end Done");

        await WaitUntilAsync(() => Task.FromResult(Runs.Keys.Count(k => !ids.Contains(k)) >= 4),
            "four occurrences of the two crons ran");
        await app.StopAsync();
        Assert.NotEqual(firstCron, secondCron);
        Assert.All(ids, id => Assert.Equal(1, Runs.GetValueOrDefault(id)));
        Assert.All(Runs, r => Assert.Equal(1, r.Value));
        AssertNothingWentWrong();
    }
}

/// <summary>
/// Redis as the job store on a Redis Cluster (#225). The store is not supported there; the startup says so once.
/// </summary>
[Collection(RedisClusterFixture.CollectionName)]
public sealed class RedisClusterEndToEndTests(RedisClusterFixture fixture) : RedisTopologyEndToEndTestsBase
{
    // Its one test that runs jobs is skipped.
    protected override bool RunsJobs => false;

    protected override string ConnectionString => fixture.ConnectionString;

    protected override string Node => "cluster-node";

    protected override Task ResetAsync() => fixture.ResetAsync();

    // One Error, in the heartbeat service's category, after the first heartbeat, and not again after the heartbeats that
    // follow (a 1 second interval, three writes); nothing else at Warning or above while the store holds no job (with
    // jobs, the scheduler loop and the fallback fail as well; see the skipped test).
    [Fact]
    public async Task OnACluster_TheStartupWritesOneError_ThatTheClusterIsNotSupported()
    {
        var app = await StartAsync(backgroundServices: true, heartbeatInterval: TimeSpan.FromSeconds(1));
        await WaitForHeartbeatWritesAsync(app, writes: 3);

        var entry = Assert.Single(ClusterErrors());
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(ClusterCheckCategory, entry.Category);
        Assert.Contains("https://github.com/msx752/SharpTicker/issues/225", entry.Text, StringComparison.Ordinal);
        var others = Log.Entries.Where(e => e.Level >= LogLevel.Warning).Except([entry]).Select(e => e.Text).ToArray();
        Assert.True(others.Length == 0, string.Join(Environment.NewLine, others));
    }

    // What fails on a cluster: kept as the record of #225, skipped because it fails by design until the store supports a
    // cluster. Without the skip it fails with "Multi-key operations must involve a single slot" from the scheduler loop
    // (GetEarliestTimeTickers) and the fallback (QueueTimedOutTimeTickers), and no job runs.
    [Fact(Skip = "Redis Cluster is not supported as the job store: https://github.com/msx752/SharpTicker/issues/225")]
    public Task TwoCronsAndTimeJobsDueLater_Run() => TwoCronsAndTimeJobsDueLater_RunAsync();
}

/// <summary>The control: the same application on the standalone Redis that the other tests use.</summary>
[Collection(RedisTestFixture.CollectionName)]
public sealed class RedisStandaloneControlEndToEndTests(RedisTestFixture fixture) : RedisTopologyEndToEndTestsBase
{
    protected override string ConnectionString => fixture.Container.GetConnectionString();

    protected override string Node => "standalone-node";

    protected override Task ResetAsync() => fixture.ResetAsync();

    [Fact]
    public async Task OnAStandaloneRedis_TheStartupWritesNoClusterError()
    {
        var app = await StartAsync(backgroundServices: true, heartbeatInterval: TimeSpan.FromSeconds(1));
        await WaitForHeartbeatWritesAsync(app, writes: 2);
        Assert.Empty(ClusterErrors());
    }

    [Fact]
    public Task JobsAddedThroughTheManagers_AreListed_AndDeleted() => JobsAddedThroughTheManagers_AreListed_AndDeletedAsync();

    [Fact]
    public Task TimeJobsAndACron_Run_AndEndDone() => TimeJobsAndACron_Run_AndEndDoneAsync();

    [Fact]
    public Task TwoCronsAndTimeJobsDueLater_Run() => TwoCronsAndTimeJobsDueLater_RunAsync();
}

[CollectionDefinition(RedisClusterFixture.CollectionName)]
public sealed class RedisClusterCollection : ICollectionFixture<RedisClusterFixture>;
