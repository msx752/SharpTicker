using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using SharpTicker.Caching.StackExchangeRedis.DependencyInjection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;

namespace SharpTicker.Caching.StackExchangeRedis;

internal class SharpTickerRedisContext : ISharpTickerRedisContext, INodeHeartbeatRegistry
{
    private const string RegistryKey = "nodes:registry";

    private readonly IDistributedCache _cache;
    private readonly SchedulerOptionsBuilder _schedulerOptions;
    private readonly ServiceExtension.SharpTickerRedisOptionBuilder _sharpTickerRedisOptionBuilder;
    public IDistributedCache DistributedCache { get; }
    public bool HasRedisConnection => true;

    public SharpTickerRedisContext([FromKeyedServices("sharpticker")] IDistributedCache cache, SchedulerOptionsBuilder schedulerOptions, ServiceExtension.SharpTickerRedisOptionBuilder sharpTickerRedisOptionBuilder)
    {
        DistributedCache = cache;
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _schedulerOptions = schedulerOptions ??  throw new ArgumentNullException(nameof(schedulerOptions));
        _sharpTickerRedisOptionBuilder = sharpTickerRedisOptionBuilder;
    }

    /// <summary>How long a heartbeat entry lives: a node that wrote none for this long counts as missing.</summary>
    internal static TimeSpan HeartbeatTtl(TimeSpan interval) => interval + TimeSpan.FromSeconds(20);

    public async Task<JsonElement> WriteHeartbeatAsync()
    {
        var node = _schedulerOptions.NodeIdentifier;
        var payload = new NodeHeartbeatPayload
        {
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Node = node
        };

        await _cache.SetStringAsync($"hb:{node}", JsonSerializer.Serialize(payload, RedisContextJsonSerializerContext.Default.NodeHeartbeatPayload),
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = HeartbeatTtl(_sharpTickerRedisOptionBuilder.NodeHeartbeatInterval)
            });

        await AddNodeToRegistryAsync(node);

        return JsonSerializer.SerializeToElement(payload, RedisContextJsonSerializerContext.Default.NodeHeartbeatPayload);
    }

    public async Task<string[]> GetMissingNodesAsync()
    {
        var nodesJson = await _cache.GetStringAsync(RegistryKey);
        if (string.IsNullOrEmpty(nodesJson)) return [];

        var allNodes = JsonSerializer.Deserialize(nodesJson, RedisContextJsonSerializerContext.Default.HashSetString);
        var missing = new List<string>();

        foreach (var node in allNodes)
        {
            // This node is running this code, so it is not dead, whatever its entry says.
            if (node == _schedulerOptions.NodeIdentifier)
                continue;

            if (!await IsNodeAliveAsync(node))
                missing.Add(node);
        }

        return missing.ToArray();
    }

    public async Task<bool> IsNodeAliveAsync(string node)
        => !string.IsNullOrEmpty(await _cache.GetStringAsync($"hb:{node}"));

    public Task RemoveNodeFromRegistryAsync(string node)
        => RemoveNodesFromRegistryAsync([node]);

    private async Task RemoveNodesFromRegistryAsync(HashSet<string> nodes)
    {
        var nodesJson = await _cache.GetStringAsync(RegistryKey);
        var nodesList = string.IsNullOrEmpty(nodesJson) 
            ? []
            : JsonSerializer.Deserialize(nodesJson, RedisContextJsonSerializerContext.Default.HashSetString);
        
            nodesList.RemoveWhere(nodes.Contains);
            
            await _cache.SetStringAsync(RegistryKey, JsonSerializer.Serialize(nodesList, RedisContextJsonSerializerContext.Default.HashSetString),
                new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(30) });
    }
    
    private async Task AddNodeToRegistryAsync(string node)
    {
        var nodesJson = await _cache.GetStringAsync(RegistryKey);
        var nodesList = string.IsNullOrEmpty(nodesJson) 
            ? []
            : JsonSerializer.Deserialize(nodesJson, RedisContextJsonSerializerContext.Default.HashSetString);

        if (nodesList.Add(node))
        {
            await _cache.SetStringAsync(RegistryKey, JsonSerializer.Serialize(nodesList, RedisContextJsonSerializerContext.Default.HashSetString),
                new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(30) });
        }
    }
    
    public async Task<TResult[]> GetOrSetArrayAsync<TResult>(string cacheKey,
        Func<CancellationToken, Task<TResult[]>> factory, TimeSpan? expiration = null,
        CancellationToken cancellationToken = default) where TResult : class
    {
        var typeInfo = RedisContextJsonSerializerContext.Default.GetTypeInfo(typeof(TResult[]));

        try
        {
            var cachedBytes = await _cache.GetAsync(cacheKey, cancellationToken);
            if (cachedBytes?.Length > 0)
            {
                var cached = (TResult[])JsonSerializer.Deserialize(cachedBytes, typeInfo);

                if (cached != null)
                    return cached;
            }
        }
        catch (Exception)
        {
            // ignored
        }

        var result = await factory(cancellationToken);

        if (result == null)
            return null;

        try
        {
            var bufferWriter = new ArrayBufferWriter<byte>();
            await using var writer = new Utf8JsonWriter(bufferWriter);

            JsonSerializer.Serialize(writer, result, typeInfo);
            await writer.FlushAsync(cancellationToken);

            // Without a lifetime the entry would stay until something removes it.
            await _cache.SetAsync(cacheKey, bufferWriter.WrittenMemory.ToArray(),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = expiration }, cancellationToken);
        }
        catch (Exception)
        {
            // ignored
        }

        return result;
    }
}

internal sealed class NodeHeartbeatPayload
{
    public long Ts { get; set; }
    public string Node { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
// Redis context types
[JsonSerializable(typeof(NodeHeartbeatPayload))]
[JsonSerializable(typeof(HashSet<string>))]
// Entity types
[JsonSerializable(typeof(TimeTickerEntity))]
[JsonSerializable(typeof(TimeTickerEntity[]))]
[JsonSerializable(typeof(List<TimeTickerEntity>))]
[JsonSerializable(typeof(ICollection<TimeTickerEntity>))]
[JsonSerializable(typeof(CronTickerEntity))]
[JsonSerializable(typeof(CronTickerEntity[]))]
[JsonSerializable(typeof(CronTickerOccurrenceEntity<CronTickerEntity>))]
[JsonSerializable(typeof(CronTickerOccurrenceEntity<CronTickerEntity>[]))]
// Enums
[JsonSerializable(typeof(TickerStatus))]
[JsonSerializable(typeof(RunCondition))]
[JsonSerializable(typeof(RunCondition?))]
// Primitives used in entities
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(Guid?))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTime?))]
[JsonSerializable(typeof(byte[]))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(int[]))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(bool))]
internal partial class RedisContextJsonSerializerContext : JsonSerializerContext;