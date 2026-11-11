using System.Collections.Concurrent;
using System.Text.Json;
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
/// The dead-node check of the Redis heartbeat: which nodes it hands to ReleaseDeadNodeResources, and when. Runs the real
/// <see cref="NodeHeartBeatBackgroundService"/> and <see cref="SharpTickerRedisContext"/> over an in-memory distributed
/// cache, on a fake clock that drives the timers. <see cref="FaultyCache"/> stands for Redis: it can be "down" (every call
/// throws), and a test removes an hb: key by hand where the server would expire it.
/// Defaults: interval 60 s, so TTL 80 s and a check every 10 s.
/// </summary>
public class NodeHeartbeatDeadNodeTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Ttl = SharpTickerRedisContext.HeartbeatTtl(Interval);
    private static readonly TimeSpan Check = NodeHeartBeatBackgroundService.MaxCheckInterval;
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(20);

    private const string NodeA = "node-A";
    private const string NodeB = "node-B";
    private const string NodeC = "node-C";

    // A node whose own entry is missing (it could not write it for a TTL: queued reads that complete after an outage, a
    // stalled process) is still running its jobs; releasing them lets another claim start them a second time.
    [Fact]
    public async Task ANode_NeverReleasesItsOwnJobs()
    {
        await using var cluster = await Cluster.StartAsync();
        cluster.CacheA.DropAfterWrite(Key(NodeA));
        await cluster.AddDeadPeerAsync(NodeC);

        await cluster.RunChecksAsync(2 * Ttl);

        // C's release shows that the checks ran and a confirmation completed.
        Assert.Equal([NodeC], cluster.Released);
    }

    // After an outage longer than the TTL every entry is gone, the live nodes' too. The first node back must not take a
    // missing entry as proof of death: the peer writes again on its own next try.
    [Fact]
    public async Task AfterAnOutageLongerThanTheTtl_ALivePeerThatWritesAgain_IsNotReleased()
    {
        await using var cluster = await Cluster.StartAsync();
        await cluster.WriteHeartbeatAsync(NodeB);
        await cluster.RunChecksAsync(3 * Check);

        cluster.CacheA.Down = true;
        await cluster.RunChecksAsync(Ttl + Check);
        await cluster.Store.RemoveAsync(Key(NodeA));
        await cluster.Store.RemoveAsync(Key(NodeB));
        cluster.CacheA.Down = false;

        await cluster.RunChecksAsync(2 * Check);
        await cluster.WriteHeartbeatAsync(NodeB);
        await cluster.RunChecksAsync(2 * Ttl);

        Assert.Empty(cluster.Released);
    }

    // Control and boundary: a peer that stays missing is released one TTL after it was first missed, not before.
    [Fact]
    public async Task ADeadPeer_IsReleasedOneTtlAfterItWasFirstMissed()
    {
        await using var cluster = await Cluster.StartAsync();
        await cluster.AddDeadPeerAsync(NodeB);

        await cluster.RunChecksAsync(Ttl);
        Assert.Empty(cluster.Released);

        await cluster.RunChecksAsync(2 * Check);
        Assert.Equal([NodeB], cluster.Released);
        Assert.DoesNotContain(NodeB, await cluster.RegisteredNodesAsync());
    }

    // While the checks fail the node cannot see whether the peer wrote; the confirmation starts again after the failure.
    [Fact]
    public async Task AFailedCheck_StartsTheConfirmationAgain()
    {
        await using var cluster = await Cluster.StartAsync();
        await cluster.AddDeadPeerAsync(NodeB);
        await cluster.RunChecksAsync(Ttl - 2 * Check);

        cluster.CacheA.Down = true;
        await cluster.RunChecksAsync(Check);
        cluster.CacheA.Down = false;

        // Without the restart the first suspicion would have completed by now.
        await cluster.RunChecksAsync(Ttl - Check);
        Assert.Empty(cluster.Released);

        await cluster.RunChecksAsync(3 * Check);
        Assert.Equal([NodeB], cluster.Released);
    }

    // A gap longer than two check intervals (a stalled process) means an outage may have passed unseen.
    [Fact]
    public async Task AGapBetweenTwoChecks_StartsTheConfirmationAgain()
    {
        await using var cluster = await Cluster.StartAsync();
        await cluster.AddDeadPeerAsync(NodeB);
        await cluster.RunChecksAsync(Ttl - 2 * Check);

        // One jump: the timer fires once for it, so the next check starts 3 intervals after the one before.
        await cluster.RunOneCheckAfterAsync(3 * Check);

        await cluster.RunChecksAsync(Ttl - 2 * Check);
        Assert.Empty(cluster.Released);

        await cluster.RunChecksAsync(3 * Check);
        Assert.Equal([NodeB], cluster.Released);
    }

    // A check that itself took longer than a check interval (it waited on the store) proves nothing either.
    [Fact]
    public async Task ASlowCheck_StartsTheConfirmationAgain()
    {
        await using var cluster = await Cluster.StartAsync();
        await cluster.AddDeadPeerAsync(NodeB);
        await cluster.RunChecksAsync(Ttl - 2 * Check);

        // Only the check reads B's entry (the heartbeat of A reads the registry too).
        var gate = cluster.Track(cluster.CacheA.HoldNextRead(Key(NodeB)));
        cluster.Time.Advance(Check);
        await gate.WaitEnteredAsync();
        // 1.5 intervals: longer than one (slow), shorter than two, so the next check does not also see a gap.
        cluster.Time.Advance(Check * 1.5);
        gate.Release();
        // The held check, then the one its timer kept pending.
        await cluster.WaitForCheckAsync();
        await cluster.WaitForCheckAsync();

        await cluster.RunChecksAsync(Ttl - 3 * Check);
        Assert.Empty(cluster.Released);

        await cluster.RunChecksAsync(4 * Check);
        Assert.Equal([NodeB], cluster.Released);
    }

    // The limits for a slow check and for a gap are fixed (10 s, 20 s), not tied to the interval: with a short interval a
    // scan of many nodes can take longer than one interval, and a dead node must still be released.
    [Fact]
    public async Task WithAShortInterval_ACheckLongerThanTheInterval_DoesNotStartTheConfirmationAgain()
    {
        var interval = TimeSpan.FromSeconds(2);
        await using var cluster = await Cluster.StartAsync(interval: interval);
        await cluster.AddDeadPeerAsync(NodeB);
        await cluster.RunChecksAsync(TimeSpan.FromSeconds(10));

        var gate = cluster.Track(cluster.CacheA.HoldNextRead(Key(NodeB)));
        cluster.Time.Advance(interval);
        await gate.WaitEnteredAsync();
        // 3 seconds: longer than the interval, well under the 10 second limit.
        cluster.Time.Advance(TimeSpan.FromSeconds(3));
        gate.Release();
        await cluster.WaitForCheckAsync();
        await cluster.WaitForCheckAsync();

        // B was first missed 2 s after the start; the TTL is 22 s.
        await cluster.RunChecksAsync(TimeSpan.FromSeconds(12));
        Assert.Equal([NodeB], cluster.Released);
    }

    // The entry is read once more right before the release: a peer that wrote in between is kept.
    [Fact]
    public async Task APeerThatWritesRightBeforeItsRelease_IsNotReleased()
    {
        await using var cluster = await Cluster.StartAsync();
        await cluster.AddDeadPeerAsync(NodeB);
        await cluster.RunChecksAsync(Ttl);

        // The scan of the next check, the one that completes the confirmation, reads B as missing; B writes right after.
        var peer = cluster.CreateContext(NodeB);
        var wrote = false;
        cluster.CacheA.AfterNextReadOf(Key(NodeB), () =>
        {
            peer.WriteHeartbeatAsync().GetAwaiter().GetResult();
            wrote = true;
        });
        await cluster.RunChecksAsync(3 * Check);

        Assert.True(wrote);
        Assert.Equal(0, cluster.ReleaseAttempts);
    }

    // A release that fails keeps the node registered, so a later check releases it.
    [Fact]
    public async Task AReleaseThatFails_KeepsTheNodeRegistered_AndIsTriedAgain()
    {
        await using var cluster = await Cluster.StartAsync();
        await cluster.AddDeadPeerAsync(NodeB);
        cluster.FailNextRelease();

        await cluster.RunChecksAsync(Ttl + Check);
        Assert.Equal(1, cluster.ReleaseAttempts);
        Assert.Empty(cluster.Released);
        Assert.Contains(NodeB, await cluster.RegisteredNodesAsync());

        await cluster.RunChecksAsync(2 * Check);
        Assert.Equal([NodeB], cluster.Released);
        Assert.DoesNotContain(NodeB, await cluster.RegisteredNodesAsync());
    }

    // A release can take long (it reads every row); this node's own heartbeat must not wait for it, or its entry expires
    // and the others release this live node.
    [Fact]
    public async Task ALongRelease_DoesNotHoldBackTheHeartbeat()
    {
        await using var cluster = await Cluster.StartAsync();
        await cluster.AddDeadPeerAsync(NodeB);
        var release = cluster.HoldNextRelease();
        await cluster.RunChecksAsync(Ttl);
        // The check that releases B, held inside the release.
        cluster.Time.Advance(Check);
        await release.WaitEnteredAsync();

        var writes = cluster.CacheA.Writes(Key(NodeA));
        for (var i = 0; i < 3; i++)
        {
            cluster.Time.Advance(Interval);
            await cluster.CacheA.WaitForWritesAsync(Key(NodeA), writes + i + 1, WaitLimit);
        }

        release.Release();
        await cluster.WaitForCheckAsync();
        Assert.Equal([NodeB], cluster.Released);
    }

    // A failed write is tried again after 5 seconds, not after a whole interval: two failed writes in a row would
    // otherwise leave the entry missing long enough for the others to release this node.
    [Fact]
    public async Task AFailedHeartbeatWrite_IsTriedAgainAfterFiveSeconds()
    {
        await using var cluster = await Cluster.StartAsync();
        var writes = cluster.CacheA.Writes(Key(NodeA));

        cluster.CacheA.Down = true;
        cluster.Time.Advance(Interval);
        await cluster.CacheA.WaitForWriteFailuresAsync(1, WaitLimit);
        cluster.CacheA.Down = false;

        // Small steps: the retry's delay may start only after the failure was counted.
        var advanced = TimeSpan.Zero;
        while (cluster.CacheA.Writes(Key(NodeA)) <= writes)
        {
            Assert.True(advanced < Interval, "The failed write was not tried again before the next interval.");
            cluster.Time.Advance(TimeSpan.FromSeconds(1));
            advanced += TimeSpan.FromSeconds(1);
            await Task.Delay(10);
        }

        // 5 seconds after the failure, give or take the step in which its delay began.
        Assert.True(advanced <= 2 * NodeHeartBeatBackgroundService.WriteRetryDelay, $"Tried again only after {advanced}.");
    }

    // Telling the dashboard is best effort: a failing hub must not stop the entry from being written.
    [Fact]
    public async Task AHeartbeatTheDashboardRefuses_IsStillWritten()
    {
        await using var cluster = await Cluster.StartAsync(hubFails: true);
        var writes = cluster.CacheA.Writes(Key(NodeA));

        for (var i = 0; i < 3; i++)
        {
            cluster.Time.Advance(Interval);
            await cluster.CacheA.WaitForWritesAsync(Key(NodeA), writes + i + 1, WaitLimit);
        }
    }

    private const string RegistryKey = "nodes:registry";

    private static string Key(string node) => $"hb:{node}";

    /// <summary>Node A running the real service, plus helpers to add peers and drive the fake clock.</summary>
    private sealed class Cluster : IAsyncDisposable
    {
        private readonly NodeHeartBeatBackgroundService _service;
        private readonly ConcurrentQueue<string> _released = new();
        private readonly SemaphoreSlim _checks = new(0);
        private int _releaseAttempts;
        private volatile bool _failNextRelease;
        private Gate? _releaseGate;

        private readonly TimeSpan _interval;
        private readonly TimeSpan _checkStep;

        private Cluster(bool hubFails, TimeSpan interval)
        {
            _interval = interval;
            _checkStep = interval < Check ? interval : Check;
            Store = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
            CacheA = new FaultyCache(Store);

            var manager = Substitute.For<IInternalTickerManager>();
            manager.ReleaseDeadNodeResources(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => ReleaseAsync(call.ArgAt<string>(0)));

            var hub = Substitute.For<ISharpTickerNotificationHubSender>();
            if (hubFails)
                hub.UpdateNodeHeartBeatAsync(Arg.Any<JsonElement>()).Returns<Task>(_ => throw new InvalidOperationException("The hub is gone."));

            _service = new NodeHeartBeatBackgroundService(
                new SharpTickerRedisOptionBuilder { NodeHeartbeatInterval = interval },
                CreateContext(NodeA, CacheA),
                manager,
                hub,
                NullLogger<NodeHeartBeatBackgroundService>.Instance,
                Time);
            _service.CheckCompleted += () => _checks.Release();
        }

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));

        public MemoryDistributedCache Store { get; }

        public FaultyCache CacheA { get; }

        public string[] Released => _released.ToArray();

        public int ReleaseAttempts => Volatile.Read(ref _releaseAttempts);

        public static async Task<Cluster> StartAsync(bool hubFails = false, TimeSpan? interval = null)
        {
            var cluster = new Cluster(hubFails, interval ?? Interval);
            await cluster._service.StartAsync(CancellationToken.None);
            // The registry is the last write of the first heartbeat; a peer registered by the test must not race it.
            await cluster.CacheA.WaitForWritesAsync(RegistryKey, 1, WaitLimit);
            return cluster;
        }

        public SharpTickerRedisContext CreateContext(string node) => CreateContext(node, Store);

        private SharpTickerRedisContext CreateContext(string node, IDistributedCache cache)
            => new(cache, new SchedulerOptionsBuilder { NodeIdentifier = node }, new SharpTickerRedisOptionBuilder { NodeHeartbeatInterval = _interval });

        public Task WriteHeartbeatAsync(string node) => CreateContext(node).WriteHeartbeatAsync();

        /// <summary>A registered node whose entry expired: it wrote once and then died.</summary>
        public async Task AddDeadPeerAsync(string node)
        {
            await WriteHeartbeatAsync(node);
            await Store.RemoveAsync(Key(node));
        }

        public async Task<HashSet<string>> RegisteredNodesAsync()
            => JsonSerializer.Deserialize<HashSet<string>>(await Store.GetStringAsync(RegistryKey) ?? "[]")!;

        /// <summary>Moves the clock in steps of one check interval and waits for each check.</summary>
        public async Task RunChecksAsync(TimeSpan duration)
        {
            for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += _checkStep)
                await RunOneCheckAfterAsync(_checkStep);
        }

        public async Task RunOneCheckAfterAsync(TimeSpan step)
        {
            Time.Advance(step);
            await WaitForCheckAsync();
        }

        public Task WaitForCheckAsync() => _checks.WaitAsync(WaitLimit).ContinueWith(t =>
        {
            if (!t.Result)
                throw new TimeoutException("No check completed.");
        });

        public void FailNextRelease() => _failNextRelease = true;

        public Gate HoldNextRelease() => _releaseGate = Track(new Gate());

        private readonly ConcurrentBag<Gate> _gates = new();

        public Gate Track(Gate gate)
        {
            _gates.Add(gate);
            return gate;
        }

        private async Task ReleaseAsync(string node)
        {
            Interlocked.Increment(ref _releaseAttempts);
            if (_failNextRelease)
            {
                _failNextRelease = false;
                throw new InvalidOperationException("The store refused the release.");
            }

            if (Interlocked.Exchange(ref _releaseGate, null) is { } gate)
                await gate.PassAsync();

            _released.Enqueue(node);
        }

        public async ValueTask DisposeAsync()
        {
            // A failed test can leave a gate shut; open them all so the loops can end.
            foreach (var gate in _gates)
                gate.Release();
            using var limit = new CancellationTokenSource(WaitLimit);
            await _service.StopAsync(limit.Token);
            _service.Dispose();
        }
    }

    /// <summary>Holds one caller until the test releases it.</summary>
    private sealed class Gate
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly SemaphoreSlim _entered = new(0);

        public async Task WaitEnteredAsync()
        {
            if (!await _entered.WaitAsync(WaitLimit))
                throw new TimeoutException("Nothing reached the gate.");
        }

        public Task PassAsync()
        {
            _entered.Release();
            return _release.Task;
        }

        public void Release() => _release.TrySetResult();
    }

    /// <summary>
    /// An <see cref="IDistributedCache"/> over a shared store, for one node. <see cref="Down"/> makes every call throw, as
    /// calls to an unreachable Redis do. Counts writes and failed writes, so a test can wait for them.
    /// </summary>
    private sealed class FaultyCache(IDistributedCache inner) : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, int> _writes = new();
        private readonly SemaphoreSlim _changed = new(0);
        private volatile string? _dropAfterWrite;
        private (string Key, Gate Gate)? _heldRead;
        private (string Key, Action Action)? _afterRead;
        private int _writeFailures;

        public volatile bool Down;

        /// <summary>Removes the key right after every write of it: the node can never keep its entry.</summary>
        public void DropAfterWrite(string key) => _dropAfterWrite = key;

        public Gate HoldNextRead(string key)
        {
            var gate = new Gate();
            _heldRead = (key, gate);
            return gate;
        }

        public void AfterNextReadOf(string key, Action action) => _afterRead = (key, action);

        public int Writes(string key) => _writes.GetValueOrDefault(key);

        public async Task WaitForWritesAsync(string key, int count, TimeSpan limit)
        {
            using var timeout = new CancellationTokenSource(limit);
            while (_writes.GetValueOrDefault(key) < count)
                await _changed.WaitAsync(timeout.Token);
        }

        public async Task WaitForWriteFailuresAsync(int count, TimeSpan limit)
        {
            using var timeout = new CancellationTokenSource(limit);
            while (Volatile.Read(ref _writeFailures) < count)
                await _changed.WaitAsync(timeout.Token);
        }

        private void ThrowIfDown(bool write = false)
        {
            if (!Down)
                return;
            if (write)
                Interlocked.Increment(ref _writeFailures);
            _changed.Release();
            throw new InvalidOperationException("The store cannot be reached.");
        }

        private async Task WroteAsync(string key, CancellationToken token)
        {
            if (key == _dropAfterWrite)
                await inner.RemoveAsync(key, token);
            _writes.AddOrUpdate(key, 1, (_, n) => n + 1);
            _changed.Release();
        }

        public byte[]? Get(string key) => throw new NotSupportedException("The heartbeat reads asynchronously.");

        public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            ThrowIfDown();
            if (_heldRead is { } held && held.Key == key)
            {
                _heldRead = null;
                await held.Gate.PassAsync();
            }

            var value = await inner.GetAsync(key, token);

            if (_afterRead is { } after && after.Key == key)
            {
                _afterRead = null;
                after.Action();
            }

            return value;
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new NotSupportedException("The heartbeat writes asynchronously.");

        public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            ThrowIfDown(write: true);
            await inner.SetAsync(key, value, options, token);
            await WroteAsync(key, token);
        }

        public void Refresh(string key) => throw new NotSupportedException();

        public Task RefreshAsync(string key, CancellationToken token = default) => throw new NotSupportedException();

        public void Remove(string key) => throw new NotSupportedException();

        public async Task RemoveAsync(string key, CancellationToken token = default)
        {
            ThrowIfDown();
            await inner.RemoveAsync(key, token);
        }
    }
}
