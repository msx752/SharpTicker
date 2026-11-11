using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using static SharpTicker.Caching.StackExchangeRedis.DependencyInjection.ServiceExtension;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>
/// The startup step of the Redis heartbeat service: it releases what an earlier run of this node left locked, because no
/// other node does that for a node that came back under the same name. Runs the real
/// <see cref="NodeHeartBeatBackgroundService"/> over an in-memory distributed cache on a fake clock; the manager is a
/// substitute, so the tests see which release is asked for, with which node and which start of the run.
/// </summary>
public class NodeHeartbeatEarlierRunReleaseTests
{
    private const string Node = "node-A";
    private static readonly DateTime RunStartedAt = new(2026, 11, 11, 11, 59, 30, DateTimeKind.Utc);
    private static readonly TimeSpan Retry = NodeHeartBeatBackgroundService.EarlierRunReleaseRetryDelay;
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(20);

    private sealed class Host : IAsyncDisposable
    {
        private readonly SemaphoreSlim _attempts = new(0);
        private readonly SemaphoreSlim _checks = new(0);
        private readonly MemoryDistributedCache _cache;
        private int _attemptCount;

        public Host(bool storeIsRedis = true, bool noRunStart = false, int failures = 0, Func<DateTime?>? runStartedAt = null)
        {
            Manager = Substitute.For<IInternalTickerManager>();
            Manager.ReleaseEarlierRunResources(Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    var attempt = Interlocked.Increment(ref _attemptCount);
                    _attempts.Release();
                    return attempt <= failures
                        ? Task.FromException(new InvalidOperationException("The store is out of reach."))
                        : Task.CompletedTask;
                });

            _cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
            var redisOptions = new SharpTickerRedisOptionBuilder { NodeHeartbeatInterval = TimeSpan.FromSeconds(60) };
            DateTime? start = noRunStart ? null : RunStartedAt;
            Service = new NodeHeartBeatBackgroundService(
                redisOptions,
                new SharpTickerRedisContext(_cache, new SchedulerOptionsBuilder { NodeIdentifier = Node }, redisOptions),
                Manager,
                Substitute.For<ISharpTickerNotificationHubSender>(),
                NullLogger<NodeHeartBeatBackgroundService>.Instance,
                Time,
                new EarlierRunRelease(storeIsRedis, Node, runStartedAt ?? (() => start), Started.Token));
            Service.CheckCompleted += () => _checks.Release();
        }

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));

        public CancellationTokenSource Started { get; } = new();

        public IInternalTickerManager Manager { get; }

        public NodeHeartBeatBackgroundService Service { get; }

        public int Attempts => Volatile.Read(ref _attemptCount);

        // StartAsync returns before the service runs (its loops start on a background task), and a stop that comes first
        // cancels that task unrun. The first heartbeat shows that the service is running.
        public async Task StartAndWaitUntilItRunsAsync()
        {
            await Service.StartAsync(CancellationToken.None);
            var deadline = DateTime.UtcNow + WaitLimit;
            while (await _cache.GetStringAsync($"hb:{Node}") == null)
            {
                Assert.True(DateTime.UtcNow < deadline, "the service wrote no heartbeat");
                await Task.Delay(10);
            }
        }

        /// <summary>Ends when the startup step has ended: released, nothing to do, or stopped.</summary>
        public Task StepEndedAsync() => Service.EarlierRunReleaseTask.WaitAsync(WaitLimit);

        /// <summary>Lets one check of the other nodes run, which shows that the service has been running for a while.</summary>
        public async Task RunOneCheckAsync()
        {
            Time.Advance(NodeHeartBeatBackgroundService.MaxCheckInterval);
            Assert.True(await _checks.WaitAsync(WaitLimit), "no check of the other nodes ran");
        }

        public async Task WaitForAttemptAsync()
            => Assert.True(await _attempts.WaitAsync(WaitLimit), $"no release attempt within {WaitLimit}; attempts so far: {Attempts}");

        // Moves the clock on second by second until the next attempt comes, and returns how far it had to move.
        public async Task<TimeSpan> AdvanceUntilTheNextAttemptAsync()
        {
            var step = TimeSpan.FromSeconds(1);
            var moved = TimeSpan.Zero;
            while (moved < 10 * Retry)
            {
                Time.Advance(step);
                moved += step;
                if (await _attempts.WaitAsync(TimeSpan.FromMilliseconds(30)))
                    return moved;
            }

            Assert.Fail($"no further release attempt while the clock moved {moved}; attempts so far: {Attempts}");
            return moved;
        }

        public async ValueTask DisposeAsync()
        {
            await Service.StopAsync(CancellationToken.None);
            Service.Dispose();
            Started.Dispose();
        }
    }

    [Fact]
    public async Task WithTheRedisStore_WhatAnEarlierRunLeftLocked_IsReleasedOnceTheApplicationHasStarted()
    {
        await using var host = new Host();
        await host.StartAndWaitUntilItRunsAsync();

        // The start of the run is recorded while the host starts; before that there is nothing to fence the release with.
        await host.RunOneCheckAsync();
        Assert.Equal(0, host.Attempts);
        Assert.False(host.Service.EarlierRunReleaseTask.IsCompleted, "the step must wait for the application to start");

        host.Started.Cancel();
        await host.WaitForAttemptAsync();

        await host.Manager.Received(1).ReleaseEarlierRunResources(Node, RunStartedAt, Arg.Any<CancellationToken>());
        // Never the unfenced release for its own name: that one takes the jobs this run is running.
        await host.Manager.DidNotReceive().ReleaseDeadNodeResources(Node, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AfterASuccessfulRelease_ItIsNotAskedForAgain()
    {
        await using var host = new Host();
        host.Started.Cancel();
        await host.StartAndWaitUntilItRunsAsync();
        await host.WaitForAttemptAsync();
        await host.StepEndedAsync();

        host.Time.Advance(10 * Retry);

        Assert.Equal(1, host.Attempts);
    }

    // Any later moment than the start of the run would release the jobs this run has claimed.
    [Fact]
    public async Task WithoutTheStartOfThisRun_NothingIsReleased()
    {
        await using var host = new Host(noRunStart: true);
        host.Started.Cancel();
        await host.StartAndWaitUntilItRunsAsync();
        await host.StepEndedAsync();

        host.Time.Advance(10 * Retry);

        Assert.Equal(0, host.Attempts);
        Assert.False(host.Service.ExecuteTask!.IsCompleted, "the heartbeat must go on");
    }

    // EF Core with the Redis heartbeat, or a custom store: that store has its own startup step.
    [Fact]
    public async Task WithAnotherStore_NothingIsReleased()
    {
        await using var host = new Host(storeIsRedis: false);
        host.Started.Cancel();
        await host.StartAndWaitUntilItRunsAsync();
        await host.StepEndedAsync();

        host.Time.Advance(10 * Retry);

        Assert.Equal(0, host.Attempts);
    }

    // The store is out of reach when the node starts. No other node releases these jobs, so one attempt is not enough.
    [Fact]
    public async Task AReleaseThatFails_IsTriedAgainAfterTheDelay_UntilItSucceeds_AndTheHeartbeatGoesOn()
    {
        await using var host = new Host(failures: 2);
        host.Started.Cancel();
        await host.StartAndWaitUntilItRunsAsync();
        await host.WaitForAttemptAsync();
        Assert.Equal(1, host.Attempts);

        // Not before the delay, and soon after it. The upper bound has slack: the service sets its timer a moment after
        // the failed attempt, and the clock may have moved on by then.
        var second = await host.AdvanceUntilTheNextAttemptAsync();
        Assert.InRange(second, Retry, Retry + Retry);
        var third = await host.AdvanceUntilTheNextAttemptAsync();
        Assert.InRange(third, Retry, Retry + Retry);
        Assert.Equal(3, host.Attempts);

        // The third attempt succeeded.
        await host.StepEndedAsync();
        host.Time.Advance(10 * Retry);
        Assert.Equal(3, host.Attempts);
        Assert.False(host.Service.ExecuteTask!.IsCompleted, "a failing release must not end the heartbeat service");
        await host.Manager.Received(3).ReleaseEarlierRunResources(Node, RunStartedAt, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AfterTheHostStops_AFailedReleaseIsNotTriedAgain_AndTheServiceEndsCleanly()
    {
        await using var host = new Host(failures: int.MaxValue);
        host.Started.Cancel();
        await host.StartAndWaitUntilItRunsAsync();
        await host.WaitForAttemptAsync();
        var execution = host.Service.ExecuteTask!;

        await host.Service.StopAsync(CancellationToken.None);
        await host.StepEndedAsync();
        host.Time.Advance(10 * Retry);

        Assert.Equal(1, host.Attempts);
        Assert.True(execution.IsCompletedSuccessfully, $"the service ended as {execution.Status}");
    }

    [Fact]
    public async Task AHostThatStopsBeforeTheApplicationStarted_EndsCleanly_WithoutARelease()
    {
        await using var host = new Host();
        await host.StartAndWaitUntilItRunsAsync();
        var execution = host.Service.ExecuteTask!;

        await host.Service.StopAsync(CancellationToken.None);

        Assert.Equal(0, host.Attempts);
        Assert.True(execution.IsCompletedSuccessfully, $"the service ended as {execution.Status}");
    }

    // The step is awaited together with the heartbeat loops: whatever goes wrong in it must not end them.
    [Fact]
    public async Task AStepThatEndsUnexpectedly_DoesNotEndTheHeartbeat()
    {
        await using var host = new Host(runStartedAt: () => throw new ObjectDisposedException("services"));
        host.Started.Cancel();
        await host.StartAndWaitUntilItRunsAsync();

        await host.StepEndedAsync();
        await host.RunOneCheckAsync();

        Assert.Equal(0, host.Attempts);
        Assert.False(host.Service.ExecuteTask!.IsCompleted, $"the service ended as {host.Service.ExecuteTask.Status}");
    }
}
