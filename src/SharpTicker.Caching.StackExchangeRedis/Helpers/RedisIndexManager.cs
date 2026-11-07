#nullable disable
using System;
using System.Threading.Tasks;
using StackExchange.Redis;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using static SharpTicker.Caching.StackExchangeRedis.Helpers.RedisKeyBuilder;

namespace SharpTicker.Caching.StackExchangeRedis.Helpers;

internal sealed class RedisIndexManager<TTimeTicker, TCronTicker>
    where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
    where TCronTicker : CronTickerEntity, new()
{
    private readonly IDatabase _db;
    private readonly string _lockHolder;

    internal RedisIndexManager(IDatabase db, string lockHolder)
    {
        _db = db;
        _lockHolder = lockHolder;
    }

    internal Task AddTimeTickerIndexesAsync(TTimeTicker ticker)
    {
        var batch = _db.CreateBatch();
        var idStr = (RedisValue)ticker.Id.ToString();

        var t1 = batch.SetAddAsync(TimeTickerIdsKey, idStr);
        Task t2;
        if (ticker.ExecutionTime.HasValue && CanAcquire(ticker.Status, ticker.LockHolder, _lockHolder))
            t2 = batch.SortedSetAddAsync(TimeTickerPendingKey, idStr, ToScore(ticker.ExecutionTime.Value));
        else
            t2 = batch.SortedSetRemoveAsync(TimeTickerPendingKey, idStr);

        batch.Execute();
        return Task.WhenAll(t1, t2);
    }

    internal Task RemoveTimeTickerIndexesAsync(Guid id)
    {
        var batch = _db.CreateBatch();
        var idStr = (RedisValue)id.ToString();

        var t1 = batch.SetRemoveAsync(TimeTickerIdsKey, idStr);
        var t2 = batch.SortedSetRemoveAsync(TimeTickerPendingKey, idStr);

        batch.Execute();
        return Task.WhenAll(t1, t2);
    }

    internal Task AddCronIndexesAsync(TCronTicker ticker)
    {
        return _db.SetAddAsync(CronIdsKey, ticker.Id.ToString());
    }

    internal Task RemoveCronIndexesAsync(Guid id)
    {
        return _db.SetRemoveAsync(CronIdsKey, id.ToString());
    }

    internal Task AddCronOccurrenceIndexesAsync(CronTickerOccurrenceEntity<TCronTicker> occurrence)
    {
        var batch = _db.CreateBatch();
        var idStr = (RedisValue)occurrence.Id.ToString();

        var t1 = batch.SetAddAsync(CronOccurrenceIdsKey, idStr);
        var t2 = batch.SetAddAsync(CronOccurrencesByCronKey(occurrence.CronTickerId), idStr);
        Task t3;
        if (CanAcquire(occurrence.Status, occurrence.LockHolder, _lockHolder))
            t3 = batch.SortedSetAddAsync(CronOccurrencePendingKey, idStr, ToScore(occurrence.ExecutionTime));
        else
            t3 = batch.SortedSetRemoveAsync(CronOccurrencePendingKey, idStr);

        batch.Execute();
        return Task.WhenAll(t1, t2, t3);
    }

    // For a node that lost the slot's SET NX. Add-only on purpose: AddCronOccurrenceIndexesAsync decides "pending"
    // relative to the caller and would ZREM the holder's healthy entry. SADD returning false means the row is
    // already indexed (the normal case) and nothing else is touched. True means the winner never finished
    // indexing, so the row is added to every set a normal update writes. Returns true when it healed.
    internal async Task<bool> HealOrphanCronOccurrenceIndexesAsync(Guid id, Func<Task<CronTickerOccurrenceEntity<TCronTicker>>> loadRow)
    {
        var idStr = (RedisValue)id.ToString();
        if (!await _db.SetAddAsync(CronOccurrenceIdsKey, idStr).ConfigureAwait(false))
            return false;

        var row = await loadRow().ConfigureAwait(false);
        if (row == null) return true;

        await _db.SetAddAsync(CronOccurrencesByCronKey(row.CronTickerId), idStr).ConfigureAwait(false);
        if (CanAcquire(row.Status, row.LockHolder, _lockHolder))
            await _db.SortedSetAddAsync(CronOccurrencePendingKey, idStr, ToScore(row.ExecutionTime)).ConfigureAwait(false);
        return true;
    }

    internal Task RemoveCronOccurrenceIndexesAsync(Guid id, Guid cronTickerId)
    {
        var batch = _db.CreateBatch();
        var idStr = (RedisValue)id.ToString();

        var t1 = batch.SetRemoveAsync(CronOccurrenceIdsKey, idStr);
        var t2 = batch.SortedSetRemoveAsync(CronOccurrencePendingKey, idStr);
        var t3 = batch.SetRemoveAsync(CronOccurrencesByCronKey(cronTickerId), idStr);

        batch.Execute();
        return Task.WhenAll(t1, t2, t3);
    }

    internal async Task RemoveCronOccurrencesByParentAsync(Guid cronId)
    {
        var reverseKey = CronOccurrencesByCronKey(cronId);
        var members = await _db.SetMembersAsync(reverseKey).ConfigureAwait(false);
        var occurrenceIds = ParseGuidSet(members);

        foreach (var occId in occurrenceIds)
        {
            await RemoveCronOccurrenceIndexesAsync(occId, cronId).ConfigureAwait(false);
            await _db.KeyDeleteAsync(CronOccurrenceKey(occId)).ConfigureAwait(false);
        }

        await _db.KeyDeleteAsync(reverseKey).ConfigureAwait(false);
    }
}
