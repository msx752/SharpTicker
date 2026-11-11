using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using SharpTicker.Utilities.Interfaces;

namespace SharpTicker.Utilities.Temps;

internal class NoOpSharpTickerRedisContext : ISharpTickerRedisContext
{
    public IDistributedCache DistributedCache => null;
    public bool HasRedisConnection => false;

    public Task<TResult[]> GetOrSetArrayAsync<TResult>(string cacheKey, Func<CancellationToken, Task<TResult[]>> factory, TimeSpan? expiration = null,
        CancellationToken cancellationToken = default) where TResult : class
    {
        return factory(cancellationToken);
    }
}