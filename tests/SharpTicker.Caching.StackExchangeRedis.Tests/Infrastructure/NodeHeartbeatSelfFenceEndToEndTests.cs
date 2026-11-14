using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpTicker.Caching.StackExchangeRedis.DependencyInjection;
using SharpTicker.DependencyInjection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>
/// An application whose node cannot write its heartbeat while it runs a job (#46), the way an application uses the
/// library: <c>AddSharpTicker</c> with the Redis store, a started host, a job added through the manager, the scheduler and
/// the heartbeat service running over a real Redis. Only the heartbeat write is made to fail (the registry the service
/// writes through is wrapped); the job store answers throughout, which is the harder case for what the node may start.
/// Heartbeat interval 1 s, so the TTL is 21 s. Each test also fails on anything else the application would see go wrong:
/// a call to its exception handler, or a log entry at Warning or above that is not about the heartbeat or this job.
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class NodeHeartbeatSelfFenceEndToEndTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private const string Node = "self-fence-node";
    private const string Function = "SelfFence.EndToEnd.Job";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Ttl = SharpTickerRedisContext.HeartbeatTtl(Interval);
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(60);

    // What the job does on its first run, how often it started, and when a run ended.
    private static int _starts;
    private static TaskCompletionSource _firstStarted = new();
    private static TaskCompletionSource _firstEnded = new();
    private static TaskCompletionSource _letFirstRunEnd = new();

    private static readonly ConcurrentQueue<string> Handled = new();

    static NodeHeartbeatSelfFenceEndToEndTests()
    {
        TickerFunctionProvider.RegisterFunctions(new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>
        {
            [Function] = (string.Empty, TickerTaskPriority.Normal, async (token, _, _) =>
            {
                // The first run goes on until it is asked to stop or the test lets it end; a later run ends at once.
                if (Interlocked.Increment(ref _starts) > 1)
                    return;
                _firstStarted.TrySetResult();
                try
                {
                    await _letFirstRunEnd.Task.WaitAsync(token);
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
                => entries.Enqueue((logLevel, $"{logLevel} {category}: {formatter(state, exception)}"));
        }
    }

    // The registry the heartbeat service writes through; the write fails while the test says so, everything else goes on.
    private sealed class HeartbeatSwitch
    {
        public volatile bool Fails;
    }

    private sealed class SwitchedRegistry(INodeHeartbeatRegistry inner, HeartbeatSwitch heartbeat) : INodeHeartbeatRegistry
    {
        public Task<JsonElement> WriteHeartbeatAsync()
            => heartbeat.Fails ? Task.FromException<JsonElement>(new TimeoutException("The heartbeat write did not reach Redis.")) : inner.WriteHeartbeatAsync();

        public Task<string[]> GetMissingNodesAsync() => inner.GetMissingNodesAsync();
        public Task<bool> IsNodeAliveAsync(string node) => inner.IsNodeAliveAsync(node);
        public Task RemoveNodeFromRegistryAsync(string node) => inner.RemoveNodeFromRegistryAsync(node);
    }

    private readonly RecordingLoggerProvider _log = new();
    private readonly HeartbeatSwitch _heartbeat = new();
    private IHost? _host;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        Handled.Clear();
        _starts = 0;
        _firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _firstEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _letFirstRunEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public async Task DisposeAsync()
    {
        _letFirstRunEnd.TrySetResult();
        await StopAsync();
        await fixture.ResetAsync();
    }

    // The application: the Redis store, a one-second heartbeat, its exception handler, and its logging.
    private async Task<IHost> StartAsync(bool? stopJobsWhenHeartbeatIsLost = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_log);
        builder.Services.AddSharpTicker(options =>
        {
            options.ConfigureScheduler(scheduler =>
            {
                scheduler.NodeIdentifier = Node;
                // Short, so that the waits below span several fallback passes.
                scheduler.FallbackIntervalChecker = TimeSpan.FromSeconds(1);
            });
            options.SetExceptionHandler<RecordingExceptionHandler>();
            options.AddStackExchangeRedis(redis =>
            {
                redis.Configuration = fixture.Container.GetConnectionString();
                redis.NodeHeartbeatInterval = Interval;
                if (stopJobsWhenHeartbeatIsLost is { } value)
                    redis.StopJobsWhenHeartbeatIsLost = value;
            });
        });
        // Registered after the library's own, so the heartbeat service gets this one.
        builder.Services.AddSingleton<INodeHeartbeatRegistry>(sp => new SwitchedRegistry(sp.GetRequiredService<SharpTickerRedisContext>(), _heartbeat));

        _host = builder.Build();
        _host.UseSharpTicker();
        await _host.StartAsync();
        return _host;
    }

    private async Task StopAsync()
    {
        if (_host == null) return;
        await _host.StopAsync();
        _host.Dispose();
        _host = null;
    }

    private string Report()
        => $"starts: {Volatile.Read(ref _starts)}\nhandled: {string.Join("\n", Handled)}\nlog:\n{string.Join("\n", _log.Entries.Where(e => e.Level >= LogLevel.Information).Select(e => e.Text))}";

    private async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"not within {WaitLimit}: {what}\n{Report()}");
            await Task.Delay(100);
        }
    }

    private static async Task<Guid> AddJobAsync(IHost host)
    {
        var manager = host.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
        var added = await manager.AddAsync(new TimeTickerEntity { Function = Function, ExecutionTime = DateTime.UtcNow.AddSeconds(1) });
        Assert.True(added.IsSucceeded, $"the job was not added: {added.Exception}");
        return added.Result.Id;
    }

    private static async Task<TickerStatus?> StatusAsync(IHost host, Guid id)
        => (await host.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>()
            .GetTimeTickerById(id, CancellationToken.None))?.Status;

    private const string HeartbeatNotWritten = "The heartbeat of this node could not be written";
    private const string NodeFenced = "This node could not write its heartbeat for";
    private const string JobStopped = "was asked to stop because this node could not show";

    // Everything at Warning or above is one of: the failed heartbeat writes, the one error that says the node fenced
    // itself, and the warning for this job. Nothing was given to the application's handler.
    private void AssertOnlyTheHeartbeatAndThisJobAreReported(Guid job, int fenced)
    {
        var problems = _log.Entries.Where(e => e.Level >= LogLevel.Warning).ToList();
        var others = problems.Where(e => !(e.Level == LogLevel.Warning && e.Text.Contains(HeartbeatNotWritten))
                                         && !(e.Level == LogLevel.Error && e.Text.Contains(NodeFenced))
                                         && !(e.Level == LogLevel.Warning && e.Text.Contains(JobStopped) && e.Text.Contains(job.ToString())))
            .Select(e => e.Text).ToList();
        Assert.True(others.Count == 0, $"the log has other entries at Warning or above:\n{string.Join("\n", others)}");
        Assert.Equal(fenced, problems.Count(e => e.Text.Contains(NodeFenced)));
        Assert.Equal(fenced, problems.Count(e => e.Text.Contains(JobStopped)));
        Assert.True(Handled.IsEmpty, $"the exception handler was called:\n{string.Join("\n", Handled)}");
    }

    // The node cannot write its heartbeat while its job runs. About one TTL later the job is asked to stop; it is not
    // recorded as cancelled, it is put back, and it is not started again while the heartbeat still fails, although the job
    // store answers. Once the heartbeat is written again the job runs a second time and ends.
    [Fact]
    public async Task AJobOfANodeThatCannotWriteItsHeartbeat_IsStoppedAfterAboutOneTtl_AndRunsAgainOnceTheHeartbeatIsWritten()
    {
        var host = await StartAsync();
        var id = await AddJobAsync(host);
        await _firstStarted.Task.WaitAsync(WaitLimit);

        var lost = Stopwatch.StartNew();
        _heartbeat.Fails = true;
        await _firstEnded.Task.WaitAsync(WaitLimit);
        var stoppedAfter = lost.Elapsed;

        // Not before the entry can be gone from the store (one TTL after a heartbeat written at most one interval earlier),
        // and well before the others could release the node (two TTLs).
        Assert.InRange(stoppedAfter, Ttl - Interval - TimeSpan.FromSeconds(1), Ttl + TimeSpan.FromSeconds(10));

        await WaitUntilAsync(async () => await StatusAsync(host, id) == TickerStatus.Idle, "the row is put back");
        // Long enough for several fallback passes to meet the row.
        await Task.Delay(TimeSpan.FromSeconds(4));
        Assert.Equal(1, Volatile.Read(ref _starts));
        Assert.Equal(TickerStatus.Idle, await StatusAsync(host, id));

        _heartbeat.Fails = false;
        await WaitUntilAsync(async () => await StatusAsync(host, id) is TickerStatus.Done or TickerStatus.DueDone, "the job ran again and ended");
        await Task.Delay(TimeSpan.FromSeconds(3));
        await StopAsync();

        Assert.Equal(2, Volatile.Read(ref _starts));
        AssertOnlyTheHeartbeatAndThisJobAreReported(id, fenced: 1);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Information && e.Text.Contains("starts jobs again"));
    }

    // With the option switched off the node lets its job run on, as before: nothing is stopped and nothing is said beyond
    // the failed heartbeat writes.
    [Fact]
    public async Task WithTheOptionSwitchedOff_TheJobRunsOn_WhileTheHeartbeatCannotBeWritten()
    {
        var host = await StartAsync(stopJobsWhenHeartbeatIsLost: false);
        var id = await AddJobAsync(host);
        await _firstStarted.Task.WaitAsync(WaitLimit);

        _heartbeat.Fails = true;
        await Task.Delay(Ttl + TimeSpan.FromSeconds(8));

        Assert.False(_firstEnded.Task.IsCompleted, Report());
        Assert.Equal(TickerStatus.InProgress, await StatusAsync(host, id));

        _heartbeat.Fails = false;
        _letFirstRunEnd.TrySetResult();
        await WaitUntilAsync(async () => await StatusAsync(host, id) is TickerStatus.Done or TickerStatus.DueDone, "the job ended");
        await StopAsync();

        Assert.Equal(1, Volatile.Read(ref _starts));
        AssertOnlyTheHeartbeatAndThisJobAreReported(id, fenced: 0);
    }
}
