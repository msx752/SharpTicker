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
        // A delayed child (an execution time of its own) is pending like any row; the claim paths gate it on its parent.
        if (ticker.ExecutionTime.HasValue && CanAcquire(ticker.Status, ticker.LockHolder, _lockHolder))
            t2 = batch.SortedSetAddAsync(TimeTickerPendingKey, idStr, ToScore(ticker.ExecutionTime.Value));
        else
            t2 = batch.SortedSetRemoveAsync(TimeTickerPendingKey, idStr);

        batch.Execute();
        return Task.WhenAll(t1, t2);
    }

    // After a fenced write of a run (IFencedRunWriter): add-only. The row may have been released (and indexed as pending)
    // right after the write; a removal decided on the written copy would race that add and drop the entry of a row that can
    // be claimed. A pending entry left for a row that cannot be claimed is harmless: the claim scripts reject it, and the
    // fallback drops the entry of an ended row.
    internal Task AddTimeTickerIndexesAddOnlyAsync(TTimeTicker ticker)
    {
        var batch = _db.CreateBatch();
        var idStr = (RedisValue)ticker.Id.ToString();

        var t1 = batch.SetAddAsync(TimeTickerIdsKey, idStr);
        var t2 = ticker.ExecutionTime.HasValue && CanAcquire(ticker.Status, ticker.LockHolder, _lockHolder)
            ? batch.SortedSetAddAsync(TimeTickerPendingKey, idStr, ToScore(ticker.ExecutionTime.Value))
            : Task.CompletedTask;

        batch.Execute();
        return Task.WhenAll(t1, t2);
    }

    // Scored by the child's position in its chain, so children read back in the order the chain was built. A child
    // already linked keeps its place.
    internal Task AddTimeTickerChildAsync(Guid parentId, Guid childId, int position)
        => _db.SortedSetAddAsync(TimeTickerChildrenKey(parentId), childId.ToString(), position, When.NotExists);

    // In chain order.
    internal async Task<Guid[]> GetTimeTickerChildIdsAsync(Guid parentId)
        => ParseGuidSet(await _db.SortedSetRangeByRankAsync(TimeTickerChildrenKey(parentId)).ConfigureAwait(false));

    // Also drops the row's own children set and its entry in its parent's children set.
    internal Task RemoveTimeTickerIndexesAsync(Guid id, Guid? parentId = null)
    {
        var batch = _db.CreateBatch();
        var idStr = (RedisValue)id.ToString();

        var t1 = batch.SetRemoveAsync(TimeTickerIdsKey, idStr);
        var t2 = batch.SortedSetRemoveAsync(TimeTickerPendingKey, idStr);
        var t3 = batch.KeyDeleteAsync(TimeTickerChildrenKey(id));
        var t4 = parentId.HasValue ? batch.SortedSetRemoveAsync(TimeTickerChildrenKey(parentId.Value), idStr) : Task.CompletedTask;

        batch.Execute();
        return Task.WhenAll(t1, t2, t3, t4);
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

    // After a fenced write of a run: add-only, as AddTimeTickerIndexesAddOnlyAsync.
    internal Task AddCronOccurrenceIndexesAddOnlyAsync(CronTickerOccurrenceEntity<TCronTicker> occurrence)
    {
        var batch = _db.CreateBatch();
        var idStr = (RedisValue)occurrence.Id.ToString();

        var t1 = batch.SetAddAsync(CronOccurrenceIdsKey, idStr);
        var t2 = batch.SetAddAsync(CronOccurrencesByCronKey(occurrence.CronTickerId), idStr);
        var t3 = CanAcquire(occurrence.Status, occurrence.LockHolder, _lockHolder)
            ? batch.SortedSetAddAsync(CronOccurrencePendingKey, idStr, ToScore(occurrence.ExecutionTime))
            : Task.CompletedTask;

        batch.Execute();
        return Task.WhenAll(t1, t2, t3);
    }

    // For a node that lost the slot's SET NX. Add-only on purpose: AddCronOccurrenceIndexesAsync decides "pending"
    // relative to the caller and would ZREM the holder's healthy entry. The winner's index batch is not atomic, so it
    // can land in part: being in the id set does not mean the row is pending. In the normal case the row is in the id
    // set and pending, and nothing else is touched. Otherwise the row is added to every set a normal update by this
    // node would write. Returns true when it healed.
    internal async Task<bool> HealOrphanCronOccurrenceIndexesAsync(Guid id, Func<Task<CronTickerOccurrenceEntity<TCronTicker>>> loadRow)
    {
        var idStr = (RedisValue)id.ToString();
        var addedToIds = await _db.SetAddAsync(CronOccurrenceIdsKey, idStr).ConfigureAwait(false);
        if (!addedToIds && await _db.SortedSetScoreAsync(CronOccurrencePendingKey, idStr).ConfigureAwait(false) != null)
            return false;

        var row = await loadRow().ConfigureAwait(false);
        if (row == null) return addedToIds;

        var addedToCron = await _db.SetAddAsync(CronOccurrencesByCronKey(row.CronTickerId), idStr).ConfigureAwait(false);
        var addedToPending = CanAcquire(row.Status, row.LockHolder, _lockHolder)
            && await _db.SortedSetAddAsync(CronOccurrencePendingKey, idStr, ToScore(row.ExecutionTime)).ConfigureAwait(false);
        return addedToIds || addedToCron || addedToPending;
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
