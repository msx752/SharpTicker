using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace SharpTicker.Caching.StackExchangeRedis;

/// <summary>
/// Says once, in the log, that Redis as the job store does not work on a Redis Cluster (#225). The store reads several job
/// records with one command, and the keys of the records have no hash tag, so on a cluster they are in different hash
/// slots and the client refuses the command. The heartbeat, the node registry and the cron list entry that Redis holds
/// next to another job store are single-key calls, which a cluster serves; with another job store nothing is logged.
/// The check changes nothing else and never throws: a failure of the check itself is logged at Debug.
/// </summary>
/// <param name="StoreIsRedis">True when the jobs are stored in Redis.</param>
/// <param name="Connection">The connection of the store; called only after a heartbeat was written through it.</param>
internal sealed class RedisClusterCheck(bool StoreIsRedis, Func<IConnectionMultiplexer> Connection)
{
    internal const string ClusterNotSupportedMessage =
        "Redis Cluster is not supported as the job store: the Redis store reads several job records with one command, " +
        "and a Redis Cluster refuses a command whose keys are in different hash slots, so the scheduler, the fallback and " +
        "the job lists fail when they read more than one job. Use a standalone Redis, Sentinel or a managed primary/replica " +
        "setup. See https://msx752.github.io/SharpTicker/guide/storage.html#redis and https://github.com/msx752/SharpTicker/issues/225.";

    private int _done;

    /// <summary>Checks the first time it is called, and does nothing after that.</summary>
    internal void RunOnce(ILogger logger)
    {
        if (!StoreIsRedis || Interlocked.Exchange(ref _done, 1) != 0)
            return;

        try
        {
            if (Connection().GetServers().Any(server => server.ServerType == ServerType.Cluster))
                logger.LogError(ClusterNotSupportedMessage);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Whether the Redis connection is a Redis Cluster could not be checked.");
        }
    }
}
