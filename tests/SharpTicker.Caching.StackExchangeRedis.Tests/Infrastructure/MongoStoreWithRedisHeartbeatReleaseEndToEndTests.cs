using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpTicker.DependencyInjection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using Testcontainers.MongoDb;
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

/// <summary>
/// MongoDB as the job store and Redis next to it, only for the node heartbeat, the way an application sets it up:
/// <c>AddSharpTicker</c> with <c>AddOperationalStore</c> (MongoDB) and <c>AddStackExchangeRedis</c>, started hosts, a job
/// added through the manager, real MongoDB and Redis containers. Node A runs a long job; then only A's heartbeat write is
/// made to fail (the registry its heartbeat service writes through is wrapped), while A goes on running and reaches both
/// stores. Node B checks the heartbeats. Heartbeat interval 1 s, so the TTL is 21 s, and B would release A about two TTLs
/// after A's last heartbeat.
/// <para>
/// docs/guide/operations.md ("When an instance stops") says of MongoDB: "Not released. MongoDB has no release step, so a
/// job left <c>InProgress</c> by a process that ended stays so until you change it." These tests hold the code to that.
/// </para>
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class MongoStoreWithRedisHeartbeatReleaseEndToEndTests(RedisTestFixture redis, MongoContainerFixture mongo)
    : IAsyncLifetime, IClassFixture<MongoContainerFixture>
{
    private const string NodeA = "mongo-heartbeat-node-a";
    private const string NodeB = "mongo-heartbeat-node-b";
    private const string Function = "MongoStoreWithRedisHeartbeat.EndToEnd.Job";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Ttl = SharpTickerRedisContext.HeartbeatTtl(Interval);
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(60);

    // Every start of the job: the node it started on, and whether the first run was still running then.
    private static readonly ConcurrentQueue<(string Node, bool FirstStillRunning)> Starts = new();
    private static TaskCompletionSource _firstStarted = new();
    private static TaskCompletionSource _firstEnded = new();
    private static TaskCompletionSource _letFirstRunEnd = new();
    private static volatile bool _firstRunWasAskedToStop;
    private static int _startCount;

    private static readonly ConcurrentQueue<string> Handled = new();

    static MongoStoreWithRedisHeartbeatReleaseEndToEndTests()
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

    private sealed class RecordingLoggerProvider(string node, ConcurrentQueue<(LogLevel Level, string Text)> entries) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger($"[{node}] {categoryName}", entries);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<(LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue((logLevel, $"{logLevel} {category}: {formatter(state, exception)}{(exception == null ? "" : " | " + exception.GetType().Name + ": " + exception.Message)}"));
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

    private readonly ConcurrentQueue<(LogLevel Level, string Text)> _log = new();
    private readonly HeartbeatSwitch _heartbeatA = new();
    private readonly string _database = "sharpticker_hb_" + Guid.NewGuid().ToString("N");
    private readonly List<IHost> _hosts = new();

    public async Task InitializeAsync()
    {
        await redis.ResetAsync();
        while (Starts.TryDequeue(out _)) { }
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
    }

    // The application: the MongoDB store, Redis next to it with a one-second heartbeat, its exception handler and logging.
    private async Task<IHost> StartNodeAsync(string node, HeartbeatSwitch heartbeat)
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
            MongoStore.AddOperationalStore(options, store => store.UseSharpTickerMongoClient(mongo.Container.GetConnectionString(), _database));
            RedisStore.AddStackExchangeRedis(options, r =>
            {
                r.Configuration = redis.Container.GetConnectionString();
                r.NodeHeartbeatInterval = Interval;
            });
        });
        // Registered after the library's own, so the heartbeat service gets this one.
        builder.Services.AddSingleton<INodeHeartbeatRegistry>(sp => new SwitchedRegistry(sp.GetRequiredService<SharpTickerRedisContext>(), heartbeat));

        var host = builder.Build();
        host.UseSharpTicker();
        _hosts.Add(host);
        await host.StartAsync();

        Assert.Equal("TickerMongoPersistenceProvider`2",
            host.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>().GetType().Name);
        return host;
    }

    private string Report()
        => $"starts: {string.Join(", ", Starts.Select(s => $"{s.Node} (first run still running: {s.FirstStillRunning})"))}\n" +
           $"first run asked to stop: {_firstRunWasAskedToStop}, first run ended: {_firstEnded.Task.IsCompleted}\n" +
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

    // Waits until the job started a second time or the time is up.
    private static async Task WaitForASecondStartOrAsync(TimeSpan span)
    {
        var deadline = DateTime.UtcNow + span;
        while (DateTime.UtcNow < deadline && Volatile.Read(ref _startCount) < 2)
            await Task.Delay(200);
    }

    // A, still running and reaching MongoDB, cannot write its heartbeat while its job runs. B would release A about two
    // TTLs later. As documented for MongoDB ("Not released"), the job stays InProgress under A and is not started again.
    [Fact]
    public async Task WithMongoDbAsTheStore_TheRunningJobOfANodeWhoseHeartbeatStopped_IsNotReleasedAndNotStartedAgain()
    {
        var a = await StartNodeAsync(NodeA, _heartbeatA);
        var id = await AddJobAsync(a);
        await _firstStarted.Task.WaitAsync(WaitLimit);
        await StartNodeAsync(NodeB, new HeartbeatSwitch());

        _heartbeatA.Fails = true;
        // Two TTLs until B could release A, and more for the checks (10 s apart at most) and several fallback passes.
        await WaitForASecondStartOrAsync(2 * Ttl + TimeSpan.FromSeconds(20));

        var row = await RowAsync(a, id);
        Assert.True(Volatile.Read(ref _startCount) == 1, $"the job of node A started again\n{Report()}");
        Assert.True(row?.Status == TickerStatus.InProgress && row.LockHolder == NodeA,
            $"the row is {row?.Status} held by '{row?.LockHolder}', not InProgress held by {NodeA}\n{Report()}");
    }

    // Control: the same two nodes, A's heartbeat is written throughout. The job stays InProgress under A over the same
    // span, runs once, and ends Done once the test lets it.
    [Fact]
    public async Task Control_WithMongoDbAsTheStore_TheRunningJobOfANodeWhoseHeartbeatIsWritten_RunsOnce()
    {
        var a = await StartNodeAsync(NodeA, _heartbeatA);
        var id = await AddJobAsync(a);
        await _firstStarted.Task.WaitAsync(WaitLimit);
        await StartNodeAsync(NodeB, new HeartbeatSwitch());

        await WaitForASecondStartOrAsync(2 * Ttl + TimeSpan.FromSeconds(20));
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
        var problems = _log.Where(e => e.Level >= LogLevel.Warning).Select(e => e.Text).ToList();
        Assert.True(problems.Count == 0, $"the log has entries at Warning or above:\n{string.Join("\n", problems)}");
    }

    // As documented ("With EF Core or MongoDB as the job store and Redis only for the heartbeat, none of this applies"):
    // with MongoDB as the store a node that cannot write its heartbeat does not ask its jobs to stop.
    [Fact]
    public async Task WithMongoDbAsTheStore_ANodeThatCannotWriteItsHeartbeat_DoesNotStopItsJob()
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
