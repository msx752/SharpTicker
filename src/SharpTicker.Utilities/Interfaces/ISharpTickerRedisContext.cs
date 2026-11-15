using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;

namespace SharpTicker.Utilities.Interfaces;

internal interface ISharpTickerRedisContext
{
    IDistributedCache DistributedCache { get; }
    public bool HasRedisConnection { get; }

    /// <summary>
    /// Whether the node has a heartbeat entry now, that is, it wrote one within the heartbeat TTL. Null when there is no
    /// heartbeat to look at (no Redis).
    /// </summary>
    Task<bool?> HasHeartbeatAsync(string node, CancellationToken cancellationToken = default);
    Task<TResult[]> GetOrSetArrayAsync<TResult>(
        string cacheKey,
        Func<CancellationToken, Task<TResult[]>> factory,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default) where TResult : class;
}