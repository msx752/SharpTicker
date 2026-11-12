#nullable disable
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace SharpTicker.Caching.StackExchangeRedis.Helpers;

internal sealed class RedisSerializer
{
    private readonly IDatabase _db;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger _logger;

    internal RedisSerializer(IDatabase db, JsonSerializerOptions jsonOptions, ILogger logger)
    {
        _db = db;
        _jsonOptions = jsonOptions;
        _logger = logger;
    }

    internal async Task<T> GetAsync<T>(string key) where T : class
    {
        var val = await _db.StringGetAsync(key).ConfigureAwait(false);
        if (val.IsNullOrEmpty) return null;
        return DeserializeOrNull<T>((string)val, key);
    }

    internal Task SetAsync<T>(string key, T value) where T : class
    {
        var payload = JsonSerializer.Serialize(value, GetTypeInfo<T>());
        return _db.StringSetAsync(key, payload);
    }

    internal string Serialize<T>(T value) where T : class
        => JsonSerializer.Serialize(value, GetTypeInfo<T>());

    // SET NX: false means the key already exists and nothing was written.
    internal Task<bool> TryCreateAsync<T>(string key, T value) where T : class
    {
        var payload = JsonSerializer.Serialize(value, GetTypeInfo<T>());
        return _db.StringSetAsync(key, payload, when: When.NotExists);
    }

    // key is the Redis key the text was read from, for the log; null when the text has no key of its own.
    // A row that does not deserialize is tried once more with its empty objects read as empty collections (see
    // EmptyCollectionRepair); the text itself is not changed, so a compare-and-set still compares what the key holds.
    internal T DeserializeOrNull<T>(string json, string key = null) where T : class
    {
        if (string.IsNullOrEmpty(json)) return null;
        var typeInfo = GetTypeInfo<T>();
        try
        {
            return (T)JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException ex)
        {
            if (EmptyCollectionRepair.TryRead(json, typeInfo, ex) is T repaired)
            {
                _logger.LogDebug("Redis key '{Key}' holds an empty collection written as an object ({Path}); it was read as empty.", key, ex.Path);
                return repaired;
            }

            if (key != null)
                _logger.LogWarning(ex, "Failed to deserialize Redis key '{Key}' as {Type}. Data may be corrupted.", key, typeof(T).Name);
            else
                _logger.LogWarning(ex, "Failed to deserialize JSON payload as {Type}.", typeof(T).Name);
            return null;
        }
    }

    internal async Task<List<T>> LoadAllFromSetAsync<T>(string setKey, Func<Guid, string> keyBuilder, CancellationToken cancellationToken, Func<T, bool> predicate = null) where T : class
    {
        var members = await _db.SetMembersAsync(setKey).ConfigureAwait(false);
        if (members.Length == 0) return [];

        var keys = new RedisKey[members.Length];
        var validCount = 0;

        for (var i = 0; i < members.Length; i++)
        {
            if (!Guid.TryParse(members[i].ToString(), out var id)) continue;
            keys[validCount] = keyBuilder(id);
            validCount++;
        }

        if (validCount == 0) return [];

        cancellationToken.ThrowIfCancellationRequested();
        var values = await _db.StringGetAsync(keys[..validCount]).ConfigureAwait(false);
        var list = new List<T>(validCount);

        for (var i = 0; i < values.Length; i++)
        {
            if (values[i].IsNullOrEmpty) continue;
            var item = DeserializeOrNull<T>((string)values[i], keys[i]);
            if (item != null && (predicate == null || predicate(item)))
                list.Add(item);
        }

        return list;
    }

    internal async Task<List<T>> LoadByIdsAsync<T>(Guid[] ids, Func<Guid, string> keyBuilder, CancellationToken cancellationToken) where T : class
    {
        if (ids.Length == 0) return [];

        var keys = new RedisKey[ids.Length];
        for (var i = 0; i < ids.Length; i++)
            keys[i] = keyBuilder(ids[i]);

        cancellationToken.ThrowIfCancellationRequested();
        var values = await _db.StringGetAsync(keys).ConfigureAwait(false);
        var list = new List<T>(ids.Length);

        for (var i = 0; i < values.Length; i++)
        {
            if (values[i].IsNullOrEmpty) continue;
            var item = DeserializeOrNull<T>((string)values[i], keys[i]);
            if (item != null) list.Add(item);
        }

        return list;
    }

    private JsonTypeInfo GetTypeInfo<T>() => _jsonOptions.GetTypeInfo(typeof(T));
}
