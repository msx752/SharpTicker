using System.Text.Json;
using StackExchange.Redis;

namespace SharpTicker.Scenarios.Harness;

/// <summary>A row's state as the Redis store holds it.</summary>
public sealed record RedisRow(TickerStatus Status, DateTime? ExecutionTime);

/// <summary>The Redis store's rows and index sets at one moment (read key by key, so not atomic).</summary>
public sealed class RedisSnapshot
{
    public HashSet<Guid> TimeTickerIds { get; } = new();
    public HashSet<Guid> OccurrenceIds { get; } = new();
    public HashSet<Guid> TimeTickerPending { get; } = new();
    public HashSet<Guid> OccurrencePending { get; } = new();
    public Dictionary<Guid, RedisRow> TimeTickers { get; } = new();
    public Dictionary<Guid, RedisRow> Occurrences { get; } = new();
    /// <summary>tq:cron:{id}:occurrences per cron.</summary>
    public Dictionary<Guid, HashSet<Guid>> CronOccurrences { get; } = new();
    /// <summary>tq:tt:{id}:children per parent.</summary>
    public Dictionary<Guid, HashSet<Guid>> Children { get; } = new();
}

/// <summary>
/// The soak's Redis check: no stale index entries and no lost pending entries in the Redis store at the end of the soak.
/// </summary>
public static class RedisIndexCheck
{
    /// <summary>Every problem found, one line each; empty when the indexes match the rows.</summary>
    public static IReadOnlyList<string> Problems(RedisSnapshot s)
    {
        var problems = new List<string>();
        foreach (var id in s.TimeTickerIds.Where(id => !s.TimeTickers.ContainsKey(id)))
            problems.Add($"tq:tt:ids holds {id}, its row is gone");
        foreach (var id in s.OccurrenceIds.Where(id => !s.Occurrences.ContainsKey(id)))
            problems.Add($"tq:co:ids holds {id}, its row is gone");
        foreach (var (cron, ids) in s.CronOccurrences)
        foreach (var id in ids.Where(id => !s.Occurrences.ContainsKey(id)))
            problems.Add($"tq:cron:{cron}:occurrences holds {id}, its row is gone");
        foreach (var (parent, ids) in s.Children)
        foreach (var id in ids.Where(id => !s.TimeTickers.ContainsKey(id)))
            problems.Add($"tq:tt:{parent}:children holds {id}, its row is gone");
        foreach (var id in s.TimeTickerPending)
        {
            if (!s.TimeTickers.TryGetValue(id, out var row) || !IsPending(row.Status))
                problems.Add($"tq:tt:pending holds {id}, its row is {(row == null ? "gone" : row.Status.ToString())}");
        }

        foreach (var id in s.OccurrencePending)
        {
            if (!s.Occurrences.TryGetValue(id, out var row) || !IsPending(row.Status))
                problems.Add($"tq:co:pending holds {id}, its row is {(row == null ? "gone" : row.Status.ToString())}");
        }

        // The reverse: a time ticker waiting to run that no claim will find (a lost job).
        foreach (var (id, row) in s.TimeTickers.Where(p => IsPending(p.Value.Status) && p.Value.ExecutionTime != null && !s.TimeTickerPending.Contains(p.Key)))
            problems.Add($"time ticker {id} is {row.Status} at {row.ExecutionTime:O} but not in tq:tt:pending (lost)");

        return problems;
    }

    /// <summary>
    /// Problems seen in two snapshots <paramref name="gap"/> apart: the store is live and its index writes are not atomic,
    /// so a problem seen once may be a write half done; one that stays is not.
    /// </summary>
    public static async Task<IReadOnlyList<string>> LastingProblemsAsync(string connection, TimeSpan gap)
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(connection);
        var first = Problems(await ReadAsync(redis)).ToHashSet();
        await Task.Delay(gap);
        return Problems(await ReadAsync(redis)).Where(first.Contains).ToList();
    }

    public static async Task<RedisSnapshot> ReadAsync(IConnectionMultiplexer redis)
    {
        var db = redis.GetDatabase();
        var server = redis.GetServer(redis.GetEndPoints()[0]);
        var snapshot = new RedisSnapshot();
        // Keys are read with any prefix in front of "tq:" (an InstanceName, if one is ever applied to these keys).
        await foreach (var key in server.KeysAsync(pattern: "*tq:*", pageSize: 1000))
        {
            var full = key.ToString();
            var name = full[full.IndexOf("tq:", StringComparison.Ordinal)..];
            var parts = name.Split(':');
            switch (parts)
            {
                case ["tq", "tt", "ids"]:
                    Add(snapshot.TimeTickerIds, await db.SetMembersAsync(key));
                    break;
                case ["tq", "co", "ids"]:
                    Add(snapshot.OccurrenceIds, await db.SetMembersAsync(key));
                    break;
                case ["tq", "tt", "pending"]:
                    Add(snapshot.TimeTickerPending, await db.SortedSetRangeByRankAsync(key));
                    break;
                case ["tq", "co", "pending"]:
                    Add(snapshot.OccurrencePending, await db.SortedSetRangeByRankAsync(key));
                    break;
                case ["tq", "tt", var id] when Guid.TryParse(id, out var g):
                    if (Row(await db.StringGetAsync(key)) is { } tt)
                        snapshot.TimeTickers[g] = tt;
                    break;
                case ["tq", "co", var id] when Guid.TryParse(id, out var g):
                    if (Row(await db.StringGetAsync(key)) is { } co)
                        snapshot.Occurrences[g] = co;
                    break;
                case ["tq", "tt", var id, "children"] when Guid.TryParse(id, out var g):
                    Add(snapshot.Children[g] = new(), await db.SortedSetRangeByRankAsync(key));
                    break;
                case ["tq", "cron", var id, "occurrences"] when Guid.TryParse(id, out var g):
                    Add(snapshot.CronOccurrences[g] = new(), await db.SetMembersAsync(key));
                    break;
            }
        }

        return snapshot;
    }

    private static bool IsPending(TickerStatus status) => status is TickerStatus.Idle or TickerStatus.Queued;

    private static void Add(HashSet<Guid> set, RedisValue[] members)
    {
        foreach (var m in members)
        {
            if (Guid.TryParse(m.ToString(), out var id))
                set.Add(id);
        }
    }

    /// <summary>A row's JSON (status as a number or a name, property names in any case); null when the key is gone.</summary>
    internal static RedisRow? Row(RedisValue value)
    {
        if (value.IsNullOrEmpty)
            return null;
        using var doc = JsonDocument.Parse(value.ToString());
        TickerStatus? status = null;
        DateTime? at = null;
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Name.Equals("Status", StringComparison.OrdinalIgnoreCase))
                status = p.Value.ValueKind == JsonValueKind.Number ? (TickerStatus)p.Value.GetInt32() : Enum.Parse<TickerStatus>(p.Value.GetString()!, ignoreCase: true);
            else if (p.Name.Equals("ExecutionTime", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                at = p.Value.GetDateTime();
        }

        return status is { } s ? new RedisRow(s, at) : throw new InvalidOperationException($"row without a status: {value}");
    }
}
