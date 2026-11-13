using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Models;
using static SharpTicker.Caching.StackExchangeRedis.DependencyInjection.ServiceExtension;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>
/// The heartbeat service fences its own node when it cannot write its heartbeat (#46): the real
/// <see cref="NodeHeartBeatBackgroundService"/> and the real <see cref="NodeFence"/> on a fake clock that drives the
/// timers; the registry is a stand-in whose writes the test lets succeed, fail or hang.
/// Interval 60 s, so the TTL is 80 s. The registry of running jobs is process-wide, so the tests share one collection.
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class NodeHeartbeatSelfFenceTests : IAsyncLifetime
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Ttl = SharpTickerRedisContext.HeartbeatTtl(Interval);
    private static readonly TimeSpan Second = NodeHeartBeatBackgroundService.SelfCheckInterval;
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(20);

    // What the next heartbeat writes do: the test swaps it.
    private sealed class Registry : INodeHeartbeatRegistry
    {
        private int _writes;
        public Func<Task<JsonElement>> Write { get; set; } = () => Task.FromResult(default(JsonElement));
        public int Writes => Volatile.Read(ref _writes);

        public Task<JsonElement> WriteHeartbeatAsync()
        {
            Interlocked.Increment(ref _writes);
            return Write();
        }

        public Task<string[]> GetMissingNodesAsync() => Task.FromResult(Array.Empty<string>());
        public Task<bool> IsNodeAliveAsync(string node) => Task.FromResult(true);
        public Task RemoveNodeFromRegistryAsync(string node) => Task.CompletedTask;

        public static Task<JsonElement> Fails() => Task.FromException<JsonElement>(new TimeoutException("Redis did not answer."));
    }

    private readonly FakeTimeProvider _time = new();
    private readonly Registry _registry = new();
    private readonly NodeFence _fence = new();
    private readonly object _owner = new();
    private readonly ListLogger<NodeHeartBeatBackgroundService> _log = new();
    private readonly List<Guid> _jobs = [];
    private NodeHeartBeatBackgroundService? _service;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_service != null)
        {
            using var limit = new CancellationTokenSource(WaitLimit);
            await _service.StopAsync(limit.Token);
            _service.Dispose();
        }

        foreach (var id in _jobs)
            TickerCancellationTokenManager.RemoveTickerCancellationToken(id);
    }

    private async Task<NodeHeartBeatBackgroundService> StartAsync(bool enabled = true, NodeFence? fence = null, object? owner = null, bool noFence = false, bool noOwner = false)
    {
        _service = new NodeHeartBeatBackgroundService(new SharpTickerRedisOptionBuilder { NodeHeartbeatInterval = Interval }, _registry,
            Substitute.For<IInternalTickerManager>(), Substitute.For<ISharpTickerNotificationHubSender>(), _log, _time, earlierRunRelease: null,
            new NodeSelfFence(enabled, noFence ? null : fence ?? _fence, noOwner ? null : owner ?? _owner));
        await _service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _registry.Writes >= 1, "the first heartbeat write");
        return _service;
    }

    // Moves the clock a second at a time and lets the look at the node's own heartbeat run after each step.
    private async Task AdvanceAsync(TimeSpan span)
    {
        for (var passed = TimeSpan.Zero; passed < span; passed += Second)
        {
            var looked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnLooked() => looked.TrySetResult();
            _service!.SelfCheckCompleted += OnLooked;
            try
            {
                _time.Advance(Second);
                await looked.Task.WaitAsync(WaitLimit);
            }
            finally
            {
                _service.SelfCheckCompleted -= OnLooked;
            }
        }
    }

    // Moves the clock on, a second at a time, until the condition holds. For what the write loop does: its timers are
    // set by the loop itself, so the clock must not run ahead of it by a fixed span.
    private async Task AdvanceUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"not within {WaitLimit}: {what}");
            _time.Advance(Second);
            await Task.Delay(5);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"not within {WaitLimit}: {what}");
            await Task.Delay(10);
        }
    }

    // A job this host is running: registered the way the execution handler registers a claimed run.
    private (CancellationTokenSource Source, TickerCancellationTokenDetails Run) RunningJob(object? owner = null)
    {
        var source = new CancellationTokenSource();
        var context = new InternalFunctionContext { TickerId = Guid.NewGuid(), FunctionName = "fn", Type = TickerType.TimeTicker, RunMarker = DateTime.UtcNow };
        _jobs.Add(context.TickerId);
        return (source, TickerCancellationTokenManager.AddTickerCancellationToken(source, context, isDue: false, owner ?? _owner));
    }

    private List<(LogLevel Level, string Message, Exception? Exception)> Errors() => _log.Entries.Where(e => e.Level >= LogLevel.Error).ToList();

    // The last heartbeat was written at the start. One TTL later the entry is gone from the store, and the node asks its
    // jobs to stop and starts none; not a second earlier. It says so once.
    [Fact]
    public async Task OneTtlAfterTheLastWrittenHeartbeat_TheNodeIsFenced_ItsJobsAreAskedToStop_AndOneErrorIsLogged()
    {
        await StartAsync();
        var job = RunningJob();
        var ofAnotherHost = RunningJob(owner: new object());
        _registry.Write = Registry.Fails;

        await AdvanceAsync(Ttl - Second);
        Assert.False(_fence.IsRaised);
        Assert.False(job.Source.IsCancellationRequested);
        Assert.Empty(Errors());

        await AdvanceAsync(Second);
        Assert.True(_fence.IsRaised);
        Assert.True(job.Run.Fenced);
        await WaitUntilAsync(() => job.Source.IsCancellationRequested, "the job is asked to stop");
        Assert.False(ofAnotherHost.Source.IsCancellationRequested);

        await AdvanceAsync(TimeSpan.FromSeconds(30));
        var error = Assert.Single(Errors());
        Assert.Contains("heartbeat", error.Message);
        Assert.Contains("1 running job", error.Message);
    }

    // A heartbeat that is written again lets the node start jobs again, and the log says so.
    [Fact]
    public async Task TheNextHeartbeatThatIsWritten_ClearsTheFence()
    {
        await StartAsync();
        _registry.Write = Registry.Fails;
        await AdvanceAsync(Ttl);
        Assert.True(_fence.IsRaised);

        _registry.Write = () => Task.FromResult(default(JsonElement));
        await AdvanceUntilAsync(() => !_fence.IsRaised, "the fence is cleared");

        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("starts jobs again"));

        // And it is raised again once a TTL has passed without a written heartbeat.
        _registry.Write = Registry.Fails;
        await AdvanceAsync(Interval);
        Assert.False(_fence.IsRaised);
        await AdvanceAsync(Ttl);
        Assert.True(_fence.IsRaised);
        Assert.Equal(2, Errors().Count);
    }

    // A heartbeat whose reply comes more than one TTL after it was sent says nothing about now: its entry may be gone
    // again. The fence stays raised, without a second error, until a heartbeat is written in time.
    [Fact]
    public async Task AHeartbeatWhoseReplyComesMoreThanOneTtlAfterItWasSent_DoesNotClearTheFence()
    {
        await StartAsync();
        var slow = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _registry.Write = () => slow.Task;
        await AdvanceAsync(Interval);
        Assert.Equal(2, _registry.Writes);

        // Sent at 60 s; fenced at 80 s; its reply comes at 150 s, 90 s after it was sent. Every write after it fails.
        await AdvanceAsync(TimeSpan.FromSeconds(90));
        Assert.True(_fence.IsRaised);
        _registry.Write = Registry.Fails;
        slow.TrySetResult(default);
        await AdvanceUntilAsync(() => _registry.Writes >= 4, "the writes after the late reply");

        Assert.True(_fence.IsRaised);
        Assert.Single(Errors());
        Assert.DoesNotContain(_log.Entries, e => e.Message.Contains("starts jobs again"));

        _registry.Write = () => Task.FromResult(default(JsonElement));
        await AdvanceUntilAsync(() => !_fence.IsRaised, "the fence is cleared by a heartbeat written in time");
    }

    // A write that never returns (the connection hangs): the look at the heartbeat has a timer of its own.
    [Fact]
    public async Task AHeartbeatWriteThatHangs_DoesNotKeepTheNodeFromFencingItself()
    {
        await StartAsync();
        var hung = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _registry.Write = () => hung.Task;

        await AdvanceAsync(Ttl);

        Assert.True(_fence.IsRaised);
        Assert.Equal(2, _registry.Writes);
        hung.TrySetResult(default);
    }

    // The entry lives one TTL from when the store took the write, which is not before it was sent: the TTL is counted from
    // the moment the last written heartbeat was sent, not from when its reply came.
    [Fact]
    public async Task TheTtlIsCountedFromWhenTheLastWrittenHeartbeatWasSent()
    {
        await StartAsync();
        var slow = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _registry.Write = () => slow.Task;
        await AdvanceAsync(Interval);
        Assert.Equal(2, _registry.Writes);

        // The write sent at 60 s gets its reply at 100 s; every write after it fails.
        await AdvanceAsync(TimeSpan.FromSeconds(19));
        Assert.False(_fence.IsRaised);
        await AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.True(_fence.IsRaised);
        await AdvanceAsync(TimeSpan.FromSeconds(20));
        _registry.Write = Registry.Fails;
        slow.TrySetResult(default);
        await WaitUntilAsync(() => !_fence.IsRaised, "the fence is cleared by the late reply");

        // Sent at 60 s: fenced again at 140 s, not at 180 s.
        await AdvanceAsync(TimeSpan.FromSeconds(39));
        Assert.False(_fence.IsRaised);
        await AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.True(_fence.IsRaised);
    }

    // Another job store, or the option switched off: the heartbeat fences nothing. The same when the core gave no fence or
    // no owner.
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task WithoutAnEnabledFence_TheNodeIsNeverFenced(bool enabled, bool noFence, bool noOwner)
    {
        var service = await StartAsync(enabled, noFence: noFence, noOwner: noOwner);
        var job = RunningJob();
        _registry.Write = Registry.Fails;
        var looks = 0;
        service.SelfCheckCompleted += () => looks++;

        // Two TTLs, and then until the failed write has been tried again a few times.
        for (var passed = TimeSpan.Zero; passed < 2 * Ttl; passed += Second)
        {
            _time.Advance(Second);
            await Task.Delay(2);
        }
        await AdvanceUntilAsync(() => _registry.Writes >= 5, "the failed write is tried again");

        Assert.False(_fence.IsRaised);
        Assert.False(job.Source.IsCancellationRequested);
        Assert.Equal(0, looks);
        Assert.Empty(Errors());
    }

    // A stop does not clear the fence: the scheduler and the fallback stop after this service and must not claim a job in
    // between. The first heartbeat written after the next start clears it.
    [Fact]
    public async Task AStopLeavesTheFenceRaised_AndTheFirstHeartbeatWrittenAfterTheNextStartClearsIt()
    {
        var service = await StartAsync();
        _registry.Write = Registry.Fails;
        await AdvanceAsync(Ttl);
        Assert.True(_fence.IsRaised);

        await service.StopAsync(CancellationToken.None);
        Assert.True(_fence.IsRaised);

        var writes = _registry.Writes;
        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _registry.Writes > writes, "the first heartbeat write of the new start");
        Assert.True(_fence.IsRaised);

        _registry.Write = () => Task.FromResult(default(JsonElement));
        await AdvanceUntilAsync(() => !_fence.IsRaised, "the fence is cleared");
        Assert.Single(Errors());
    }

    // A start counts the TTL from its own beginning, not from a heartbeat an earlier run of the service wrote.
    [Fact]
    public async Task AStartCountsTheTtlFromItsOwnBeginning()
    {
        var service = await StartAsync();
        await AdvanceAsync(TimeSpan.FromSeconds(50));
        await service.StopAsync(CancellationToken.None);
        _registry.Write = Registry.Fails;

        var writes = _registry.Writes;
        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _registry.Writes > writes, "the first heartbeat write of the new start");

        // The earlier run wrote at 0 s; this one started at 50 s and has written nothing.
        await AdvanceAsync(Ttl - Second);
        Assert.False(_fence.IsRaised);
        await AdvanceAsync(Second);
        Assert.True(_fence.IsRaised);
    }

    // A callback a job hung on its token throws: the service goes on, a warning names the job, and the next written
    // heartbeat still clears the fence.
    [Fact]
    public async Task ATokenCallbackOfAJobThatThrows_DoesNotEndTheService()
    {
        var service = await StartAsync();
        var job = RunningJob();
        job.Source.Token.Register(() => throw new InvalidOperationException("callback"));
        _registry.Write = Registry.Fails;

        await AdvanceAsync(Ttl);

        Assert.True(_fence.IsRaised);
        await WaitUntilAsync(() => job.Source.IsCancellationRequested, "the job is asked to stop");
        await WaitUntilAsync(() => _log.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains(_jobs[0].ToString())), "the warning that names the job");
        Assert.False(service.ExecuteTask!.IsCompleted);

        _registry.Write = () => Task.FromResult(default(JsonElement));
        await AdvanceUntilAsync(() => !_fence.IsRaised, "the fence is cleared");
    }
}
