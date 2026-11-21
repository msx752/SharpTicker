using System.Collections.Concurrent;
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
/// #221: the self-fence (#46) and a chain child without a time of its own, over a real Redis. Node A runs a chain (a parent
/// and a child that runs on until it is stopped), then cannot write its heartbeat any more; node B, where a test has one, is
/// a second application on the same store whose heartbeat is fine. B releases A's rows once A's heartbeat stayed missing
/// for a TTL; B's fallback takes the child once its parent ended 30 seconds ago (#158), or the parent's next run starts it.
/// The nodes start the way an application does (<c>AddSharpTicker</c> with the Redis store, a started host); only A's
/// heartbeat write is made to fail. Heartbeat interval 1 s, so the TTL is 21 s. The child function counts how many of its
/// runs are running at the same time.
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class NodeHeartbeatSelfFenceInlineChildTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private const string NodeA = "fence-105-node-a";
    private const string NodeB = "fence-105-node-b";
    private const string ParentFunction = "Fence105.Parent";
    private const string ChildFunction = "Fence105.Child";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(120);

    private static int _childStarts;
    private static int _childRunning;
    private static int _childMostAtOnce;
    private static readonly ConcurrentQueue<string> ChildRuns = new();
    private static TaskCompletionSource _firstChildStarted = new();
    private static TaskCompletionSource _secondChildStarted = new();
    private static TaskCompletionSource _letFirstChildEnd = new();

    // A parent whose first run ignores its token and goes on until the test lets it end; a later run ends at once.
    private static volatile bool _parentIgnoresItsToken;
    private static int _parentStarts;
    private static TaskCompletionSource _letFirstParentEnd = new();

    static NodeHeartbeatSelfFenceInlineChildTests()
    {
        TickerFunctionProvider.RegisterFunctions(new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>
        {
            [ParentFunction] = (string.Empty, TickerTaskPriority.Normal, async (_, _, _) =>
            {
                if (Interlocked.Increment(ref _parentStarts) == 1 && _parentIgnoresItsToken)
                    await _letFirstParentEnd.Task;
            }, 0),
            [ChildFunction] = (string.Empty, TickerTaskPriority.Normal, async (token, services, _) =>
            {
                var start = Interlocked.Increment(ref _childStarts);
                var running = Interlocked.Increment(ref _childRunning);
                int most;
                while (running > (most = Volatile.Read(ref _childMostAtOnce)) && Interlocked.CompareExchange(ref _childMostAtOnce, running, most) != most)
                {
                }

                var node = services.GetService<SchedulerOptionsBuilder>()?.NodeIdentifier;
                ChildRuns.Enqueue($"run {start} on {node} at {DateTime.UtcNow:HH:mm:ss.fff}, {running} running");
                try
                {
                    // The first run goes on until it is asked to stop or the test lets it end; a later run ends at once.
                    if (start == 1)
                    {
                        _firstChildStarted.TrySetResult();
                        await _letFirstChildEnd.Task.WaitAsync(token);
                    }
                    else
                        _secondChildStarted.TrySetResult();
                }
                finally
                {
                    Interlocked.Decrement(ref _childRunning);
                    ChildRuns.Enqueue($"run {start} on {node} ended at {DateTime.UtcNow:HH:mm:ss.fff}");
                }
            }, 0)
        });
        TickerFunctionProvider.Build();
    }

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

    private sealed class RecordingLoggerProvider(string node) : ILoggerProvider
    {
        public static readonly ConcurrentQueue<(LogLevel Level, string Text)> Entries = new();

        public ILogger CreateLogger(string categoryName) => new Logger(node, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(string node, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => Entries.Enqueue((logLevel, $"{DateTime.UtcNow:HH:mm:ss.fff} [{node}] {logLevel} {category}: {formatter(state, exception)}"));
        }
    }

    private readonly HeartbeatSwitch _heartbeatOfA = new();
    private readonly List<IHost> _hosts = [];

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        _childStarts = 0;
        _childRunning = 0;
        _childMostAtOnce = 0;
        _parentStarts = 0;
        _parentIgnoresItsToken = false;
        ChildRuns.Clear();
        RecordingLoggerProvider.Entries.Clear();
        _firstChildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _secondChildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _letFirstChildEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _letFirstParentEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public async Task DisposeAsync()
    {
        _letFirstChildEnd.TrySetResult();
        _letFirstParentEnd.TrySetResult();
        foreach (var host in _hosts)
        {
            await host.StopAsync();
            host.Dispose();
        }

        await fixture.ResetAsync();
    }

    private async Task<IHost> StartAsync(string node, HeartbeatSwitch? heartbeat = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddProvider(new RecordingLoggerProvider(node));
        builder.Services.AddSharpTicker(options =>
        {
            options.ConfigureScheduler(scheduler =>
            {
                scheduler.NodeIdentifier = node;
                scheduler.FallbackIntervalChecker = TimeSpan.FromSeconds(1);
            });
            options.AddStackExchangeRedis(redis =>
            {
                redis.Configuration = fixture.Container.GetConnectionString();
                redis.NodeHeartbeatInterval = Interval;
            });
        });
        if (heartbeat != null)
            builder.Services.AddSingleton<INodeHeartbeatRegistry>(sp => new SwitchedRegistry(sp.GetRequiredService<SharpTickerRedisContext>(), heartbeat));

        var host = builder.Build();
        host.UseSharpTicker();
        await host.StartAsync();
        _hosts.Add(host);
        return host;
    }

    private static string Report()
        => $"child starts: {Volatile.Read(ref _childStarts)}, most runs at once: {Volatile.Read(ref _childMostAtOnce)}, parent starts: {Volatile.Read(ref _parentStarts)}\n"
           + $"child runs:\n{string.Join("\n", ChildRuns)}\nlog:\n{string.Join("\n", RecordingLoggerProvider.Entries.Select(e => e.Text))}";

    private static async Task<TimeTickerEntity?> StoredAsync(IHost host, Guid id)
        => await host.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>()
            .GetTimeTickerById(id, CancellationToken.None);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"not within {WaitLimit}: {what}\n{Report()}");
            await Task.Delay(200);
        }
    }

    // Node A runs a parent with one child; returns the ids once the child's first run has started on A.
    private async Task<(IHost A, Guid Parent, Guid Child)> StartChainOnAAsync(RunCondition childCondition)
    {
        var a = await StartAsync(NodeA, _heartbeatOfA);
        var manager = a.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
        var parent = new TimeTickerEntity
        {
            Function = ParentFunction,
            ExecutionTime = DateTime.UtcNow.AddSeconds(1),
            Children = [new TimeTickerEntity { Function = ChildFunction, RunCondition = childCondition }]
        };
        var added = await manager.AddAsync(parent);
        Assert.True(added.IsSucceeded, $"the chain was not added: {added.Exception}");

        await _firstChildStarted.Task.WaitAsync(WaitLimit);
        var child = Assert.Single(added.Result.Children).Id;
        Assert.NotEqual(Guid.Empty, child);
        Assert.Equal(TickerStatus.InProgress, (await StoredAsync(a, child))?.Status);
        return (a, added.Result.Id, child);
    }

    private static async Task WaitForTheSecondChildRunAsync()
    {
        try
        {
            await _secondChildStarted.Task.WaitAsync(WaitLimit);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"the child did not run a second time within {WaitLimit}\n{Report()}");
        }
    }

    private static void AssertTheChildNeverRanTwiceAtOnce()
        => Assert.True(Volatile.Read(ref _childMostAtOnce) == 1,
            $"the child ran twice at the same time: its first run on {NodeA} was still running when it was started again\n{Report()}");

    // A runs the chain; once its child runs, B starts and A's heartbeat fails for the rest of the test. The child runs a
    // second time (B's fallback after A's rows were put back or released); the test then says how many of its runs ran at
    // once.
    private async Task RunChainOnACutOffNodeAsync(RunCondition childCondition)
    {
        var (a, parent, _) = await StartChainOnAAsync(childCondition);
        Assert.True((await StoredAsync(a, parent))?.Status is TickerStatus.Done or TickerStatus.DueDone, Report());

        await StartAsync(NodeB);
        _heartbeatOfA.Fails = true;

        await WaitForTheSecondChildRunAsync();
        AssertTheChildNeverRanTwiceAtOnce();
    }

    // #221 (candidate 105): the child that started with its parent is asked to stop by A's fence like any other run, so
    // B's fallback, which takes it after A's rows were put back or released, does not start it while A's run goes on.
    // Up to 1.6.3 the fence left it alone and it ran twice at the same time.
    [Fact]
    public Task AChildThatStartedWithItsParent_OnANodeThatCannotWriteItsHeartbeat_DoesNotRunTwiceAtTheSameTime()
        => RunChainOnACutOffNodeAsync(RunCondition.InProgress);

    // Control, the same chain and nodes with a child that starts after its parent ended: the fence asks that run to stop,
    // its row is put back, and B's fallback runs it once A's run has ended.
    [Fact]
    public Task AChildThatStartedAfterItsParent_OnANodeThatCannotWriteItsHeartbeat_DoesNotRunTwiceAtTheSameTime()
        => RunChainOnACutOffNodeAsync(RunCondition.OnSuccess);

    // #221, the other way the child is started again: its parent ignores its token, so it runs on on A and its row is
    // released with A's rows; B runs the parent again, and that run starts the child inline. A's run of the child was asked
    // to stop by the fence, so the two runs of the child do not overlap. (The parent itself runs on: a job that ignores its
    // token is not stopped.)
    [Fact]
    public async Task AChildThatStartedWithItsParent_WhoseParentRunsOnAndIsRunAgainElsewhere_DoesNotRunTwiceAtTheSameTime()
    {
        _parentIgnoresItsToken = true;
        await StartChainOnAAsync(RunCondition.InProgress);

        await StartAsync(NodeB);
        _heartbeatOfA.Fails = true;

        await WaitForTheSecondChildRunAsync();
        Assert.True(Volatile.Read(ref _parentStarts) >= 2, $"the child's second run did not come from the parent's next run\n{Report()}");
        AssertTheChildNeverRanTwiceAtOnce();
    }

    // #221: one node. The child that started with its parent is stopped by the fence and put back: it is not recorded as
    // Cancelled, not skipped and not started again while the heartbeat still fails. Once the heartbeat is written again the
    // fallback runs it once, after its parent ended 30 seconds ago, and it ends.
    [Fact]
    public async Task AChildThatStartedWithItsParent_IsPutBackWhenTheNodeIsFenced_AndRunsOnceAfterTheHeartbeatIsWrittenAgain()
    {
        var (a, parent, child) = await StartChainOnAAsync(RunCondition.InProgress);
        _heartbeatOfA.Fails = true;

        await WaitUntilAsync(async () => Volatile.Read(ref _childRunning) == 0 && (await StoredAsync(a, child))?.Status == TickerStatus.Idle,
            "the child's run is stopped and its row put back");
        var putBack = await StoredAsync(a, child);
        Assert.Null(putBack!.LockHolder);
        Assert.Null(putBack.ExecutionTime);
        Assert.Null(putBack.ExecutedAt);

        // Several fallback passes while the fence is still raised: the child is not started again.
        await Task.Delay(TimeSpan.FromSeconds(4));
        Assert.Equal(1, Volatile.Read(ref _childStarts));
        Assert.Equal(TickerStatus.Idle, (await StoredAsync(a, child))?.Status);

        _heartbeatOfA.Fails = false;
        await WaitUntilAsync(async () => (await StoredAsync(a, child))?.Status is TickerStatus.Done or TickerStatus.DueDone, "the child ran again and ended");
        // A few more fallback passes: it is not run a third time.
        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.True(Volatile.Read(ref _childStarts) == 2, Report());
        AssertTheChildNeverRanTwiceAtOnce();
        Assert.Equal(1, Volatile.Read(ref _parentStarts));
        Assert.True((await StoredAsync(a, parent))?.Status is TickerStatus.Done or TickerStatus.DueDone, Report());
        Assert.Contains(RecordingLoggerProvider.Entries, e => e.Level == LogLevel.Warning && e.Text.Contains("was asked to stop because this node could not show") && e.Text.Contains(child.ToString()));
        Assert.DoesNotContain(RecordingLoggerProvider.Entries, e => e.Level >= LogLevel.Error && !e.Text.Contains("This node could not write its heartbeat for"));
    }
}
