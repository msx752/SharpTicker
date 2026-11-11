using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpTicker.Caching.StackExchangeRedis.DependencyInjection;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;

namespace SharpTicker.Caching.StackExchangeRedis;

/// <summary>
/// Writes this node's heartbeat and releases the jobs of nodes whose heartbeat stopped.
/// <para>
/// A missing heartbeat entry alone is not proof that a node died: after a Redis outage longer than the TTL every entry is
/// gone, the live nodes' too, until each one writes again. So a node that is missing is only suspected. It is released
/// only when every check over one whole TTL found it missing: a node that wrote at any time in that span is seen at the
/// next check and kept. A live node writes every interval, and 5 seconds after a failed write it tries again, so once the
/// store answers again it writes within seconds. Checks run every 10 seconds at most (every interval when that is
/// shorter). A check that fails, a check that takes longer than 10 seconds, or more than 20 seconds between two checks
/// (TTL - interval, the time a live node's entry outlives its last write) clears every suspicion, because the store may
/// have been out of reach meanwhile.
/// </para>
/// <para>
/// With Redis as the job store it also releases, once at startup, what an earlier run of this node left locked; see
/// <see cref="ReleaseEarlierRunAsync"/>.
/// </para>
/// </summary>
internal class NodeHeartBeatBackgroundService : BackgroundService
{
    internal static readonly TimeSpan WriteRetryDelay = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaxCheckInterval = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan EarlierRunReleaseRetryDelay = TimeSpan.FromSeconds(30);

    private int _started;
    private readonly EarlierRunRelease _earlierRunRelease;
    private readonly INodeHeartbeatRegistry _registry;
    private readonly IInternalTickerManager _internalTickerManager;
    private readonly ISharpTickerNotificationHubSender _notificationHubSender;
    private readonly ILogger<NodeHeartBeatBackgroundService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _checkInterval;

    // Only the check loop touches these.
    private readonly Dictionary<string, long> _suspectedSince = new();
    private long? _lastCheckStart;

    public NodeHeartBeatBackgroundService(ServiceExtension.SharpTickerRedisOptionBuilder redisOptions, INodeHeartbeatRegistry registry,
        IInternalTickerManager internalTickerManager, ISharpTickerNotificationHubSender notificationHubSender, ILogger<NodeHeartBeatBackgroundService> logger,
        EarlierRunRelease earlierRunRelease)
        : this(redisOptions, registry, internalTickerManager, notificationHubSender, logger, TimeProvider.System, earlierRunRelease)
    {
    }

    // Tests pass a fake TimeProvider; it drives the timers and the delays as well. Not public, so dependency injection
    // never picks up a TimeProvider the application registered.
    internal NodeHeartBeatBackgroundService(ServiceExtension.SharpTickerRedisOptionBuilder redisOptions, INodeHeartbeatRegistry registry,
        IInternalTickerManager internalTickerManager, ISharpTickerNotificationHubSender notificationHubSender, ILogger<NodeHeartBeatBackgroundService> logger,
        TimeProvider timeProvider, EarlierRunRelease earlierRunRelease = null)
    {
        _registry = registry;
        _internalTickerManager = internalTickerManager;
        _notificationHubSender = notificationHubSender;
        _logger = logger;
        _timeProvider = timeProvider;
        _earlierRunRelease = earlierRunRelease;
        _interval = redisOptions.NodeHeartbeatInterval;
        _ttl = SharpTickerRedisContext.HeartbeatTtl(_interval);
        _checkInterval = _interval < MaxCheckInterval ? _interval : MaxCheckInterval;
    }

    /// <summary>Raised after each check of the other nodes, whatever its result. For tests.</summary>
    internal event Action CheckCompleted;

    public override Task StartAsync(CancellationToken ct)
    {
        return Interlocked.CompareExchange(ref _started, 1, 0) != 0
            ? Task.CompletedTask : base.StartAsync(ct);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A stop and a start reuse this instance: what was seen before the stop says nothing now.
        _suspectedSince.Clear();
        _lastCheckStart = null;

        // Two loops, so that a long release never holds back this node's own heartbeat. Both timers exist before the
        // first write, so the check interval counts from before this node's first heartbeat.
        var writeTimer = new PeriodicTimer(_interval, _timeProvider);
        var checkTimer = new PeriodicTimer(_checkInterval, _timeProvider);
        EarlierRunReleaseTask = ReleaseEarlierRunAsync(stoppingToken);
        return Task.WhenAll(WriteLoopAsync(writeTimer, stoppingToken), CheckLoopAsync(checkTimer, stoppingToken), EarlierRunReleaseTask);
    }

    /// <summary>The startup release of this run: ends when it released, had nothing to do, or the host stopped. For tests.</summary>
    internal Task EarlierRunReleaseTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Releases what an earlier run of this node left locked. The other nodes cannot do it: this node writes its heartbeat
    /// again under the same name, so to them it never stopped (and a single node has no other node at all). Only rows
    /// locked before this run started are released, so the jobs this run has claimed keep their locks. Without the start
    /// of this run nothing is released: any later moment would take this run's own claims.
    /// A release that fails (the store is out of reach at start) is tried again until it succeeds or the host stops; it is
    /// safe to repeat, because the start of the run does not move.
    /// </summary>
    private async Task ReleaseEarlierRunAsync(CancellationToken stoppingToken)
    {
        if (_earlierRunRelease is not { StoreIsRedis: true } settings)
            return;

        try
        {
            if (!await WaitForAsync(settings.ApplicationStarted, stoppingToken))
                return;

            if (settings.RunStartedAt() is not { } runStartedAt)
            {
                _logger.LogInformation("The start of this run is not known, so jobs an earlier run of node {Node} may have left locked are not released at startup.", settings.Node);
                return;
            }

            while (true)
            {
                try
                {
                    await _internalTickerManager.ReleaseEarlierRunResources(settings.Node, runStartedAt, stoppingToken);
                    _logger.LogInformation("Released the jobs an earlier run of node {Node} had left locked, if any.", settings.Node);
                    return;
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "The jobs an earlier run of node {Node} left locked could not be released; it is tried again in {Delay}.", settings.Node, EarlierRunReleaseRetryDelay);
                }

                await Task.Delay(EarlierRunReleaseRetryDelay, _timeProvider, stoppingToken);
            }
        }
        catch (Exception ex)
        {
            // The host stops: the next start releases what is left. Anything else is unexpected, and must not end the
            // heartbeat with it: this task is awaited together with the two loops.
            if (!stoppingToken.IsCancellationRequested)
                _logger.LogError(ex, "The startup release of the jobs an earlier run of node {Node} left locked ended unexpectedly; they stay locked until the next start.", settings.Node);
        }
    }

    // True once the signal came, false when the host stopped first.
    private static async Task<bool> WaitForAsync(CancellationToken signal, CancellationToken stoppingToken)
    {
        var signalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var onSignal = signal.Register(() => signalled.TrySetResult(true));
        await using var onStop = stoppingToken.Register(() => signalled.TrySetResult(false));
        return await signalled.Task;
    }

    private async Task WriteLoopAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        using var _ = timer;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (await TryWriteHeartbeatAsync())
                {
                    if (!await timer.WaitForNextTickAsync(stoppingToken))
                        return;
                }
                else
                {
                    await Task.Delay(WriteRetryDelay, _timeProvider, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> TryWriteHeartbeatAsync()
    {
        System.Text.Json.JsonElement payload;
        try
        {
            payload = await _registry.WriteHeartbeatAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The heartbeat of this node could not be written; it is tried again in {Delay}.", WriteRetryDelay);
            return false;
        }

        // The entry is written; telling the dashboard is best effort.
        try
        {
            await _notificationHubSender.UpdateNodeHeartBeatAsync(payload);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The dashboard could not be told about this node's heartbeat.");
        }

        return true;
    }

    private async Task CheckLoopAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        using var _ = timer;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await CheckOtherNodesAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    ForgetSuspicions();
                    _logger.LogWarning(ex, "The heartbeats of the other nodes could not be checked; nodes that were missing are checked again from the start.");
                }
                finally
                {
                    CheckCompleted?.Invoke();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task CheckOtherNodesAsync(CancellationToken stoppingToken)
    {
        var start = _timeProvider.GetTimestamp();
        var previousStart = _lastCheckStart;
        _lastCheckStart = start;

        var missing = await _registry.GetMissingNodesAsync();
        var end = _timeProvider.GetTimestamp();

        // Not watched for a while (a stalled process, a check that waited on the store): an outage may have passed unseen,
        // so a missing entry proves nothing yet. Fixed limits: they come from the 20 seconds a live entry outlives its
        // last write, not from the interval.
        if (previousStart is { } previous && _timeProvider.GetElapsedTime(previous, start) > 2 * MaxCheckInterval
            || _timeProvider.GetElapsedTime(start, end) > MaxCheckInterval)
        {
            ForgetSuspicions();
            _logger.LogInformation("The heartbeats of the other nodes were not checked for a while; nodes that are missing are checked again from the start.");
            return;
        }

        foreach (var node in _suspectedSince.Keys.Where(node => !missing.Contains(node)).ToArray())
            _suspectedSince.Remove(node);

        foreach (var node in missing)
        {
            if (!_suspectedSince.TryGetValue(node, out var since))
            {
                _suspectedSince[node] = end;
                continue;
            }

            if (_timeProvider.GetElapsedTime(since, end) < _ttl)
                continue;

            // A release before this one may have taken long: read the entry once more right before releasing.
            if (await _registry.IsNodeAliveAsync(node))
            {
                _suspectedSince.Remove(node);
                continue;
            }

            try
            {
                await _internalTickerManager.ReleaseDeadNodeResources(node, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // The node stays registered and suspected, so a later check tries again.
                _logger.LogError(ex, "The jobs of node {Node}, whose heartbeat stopped, could not be released; a later check tries again.", node);
                continue;
            }

            _suspectedSince.Remove(node);
            try
            {
                await _registry.RemoveNodeFromRegistryAsync(node);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Harmless: the node stays registered, and a later check releases it again (nothing of it is left).
                _logger.LogWarning(ex, "Node {Node} was released but could not be removed from the node registry.", node);
            }

            _logger.LogInformation("Released the jobs of node {Node}: its heartbeat was missing for {Missing}.", node, _timeProvider.GetElapsedTime(since, end));
        }
    }

    private void ForgetSuspicions() => _suspectedSince.Clear();

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _started, 0);
        await base.StopAsync(cancellationToken);
    }
}
