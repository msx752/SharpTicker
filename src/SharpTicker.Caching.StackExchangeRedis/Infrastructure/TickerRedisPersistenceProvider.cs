#nullable disable
using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using static SharpTicker.Caching.StackExchangeRedis.DependencyInjection.ServiceExtension;
using static SharpTicker.Caching.StackExchangeRedis.Helpers.RedisKeyBuilder;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Infrastructure;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Caching.StackExchangeRedis.Infrastructure;

internal sealed class TickerRedisPersistenceProvider<TTimeTicker, TCronTicker> :
    BaseRedisPersistenceProvider<TTimeTicker, TCronTicker>,
    ITickerPersistenceProvider<TTimeTicker, TCronTicker>
    where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
    where TCronTicker : CronTickerEntity, new()
{
    public TickerRedisPersistenceProvider(
        [FromKeyedServices("sharpticker")] IDatabase db,
        ITickerClock clock,
        SchedulerOptionsBuilder optionsBuilder,
        SharpTickerRedisOptionBuilder redisOptions,
        ILogger<TickerRedisPersistenceProvider<TTimeTicker, TCronTicker>> logger)
        : base(db, clock, optionsBuilder, redisOptions, logger) { }

    #region Queryable_Methods
    public ITickerQueryable<TTimeTicker> TimeTickersQuery()
    {
        return new InMemoryTickerQueryable<TTimeTicker>(async ct =>
        {
            var list = await Serializer.LoadAllFromSetAsync<TTimeTicker>(
                TimeTickerIdsKey, TimeTickerKey, ct).ConfigureAwait(false);
            await LinkLoadedChildrenAsync(list).ConfigureAwait(false);
            return list;
        });
    }

    public ITickerQueryable<TCronTicker> CronTickersQuery()
    {
        return new InMemoryTickerQueryable<TCronTicker>(async ct =>
        {
            var list = await Serializer.LoadAllFromSetAsync<TCronTicker>(
                CronIdsKey, CronKey, ct).ConfigureAwait(false);
            return list;
        });
    }

    public ITickerQueryable<CronTickerOccurrenceEntity<TCronTicker>> CronTickerOccurrencesQuery()
    {
        return new InMemoryTickerQueryable<CronTickerOccurrenceEntity<TCronTicker>>(async ct =>
        {
            var list = await Serializer.LoadAllFromSetAsync<CronTickerOccurrenceEntity<TCronTicker>>(
                CronOccurrenceIdsKey, CronOccurrenceKey, ct).ConfigureAwait(false);
            return list;
        });
    }
    #endregion

    #region Time_Ticker_Shared_Methods
    public async Task<TTimeTicker> GetTimeTickerById(Guid id, CancellationToken cancellationToken = default)
    {
        var ticker = await Serializer.GetAsync<TTimeTicker>(TimeTickerKey(id)).ConfigureAwait(false);
        if (ticker != null)
            await AttachChildrenAsync([ticker], cancellationToken).ConfigureAwait(false);
        return ticker;
    }

    public async Task<TTimeTicker[]> GetTimeTickers(Expression<Func<TTimeTicker, bool>> predicate, CancellationToken cancellationToken)
    {
        var compiled = predicate?.Compile();

        // Children are rows too: every row is linked first, so the predicate sees a root with its children.
        var list = await Serializer.LoadAllFromSetAsync<TTimeTicker>(
            TimeTickerIdsKey, TimeTickerKey, cancellationToken).ConfigureAwait(false);
        await LinkLoadedChildrenAsync(list).ConfigureAwait(false);

        return list
            .Where(t => t.ParentId == null && (compiled == null || compiled(t)))
            .OrderByDescending(x => x.ExecutionTime)
            .ToArray();
    }

    public async Task<PaginationResult<TTimeTicker>> GetTimeTickersPaginated(Expression<Func<TTimeTicker, bool>> predicate, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
    {
        var all = await GetTimeTickers(predicate, cancellationToken).ConfigureAwait(false);
        var total = all.Length;
        var items = all.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToArray();
        return new PaginationResult<TTimeTicker>(items, total, pageNumber, pageSize);
    }

    public async Task<int> AddTimeTickers(TTimeTicker[] tickers, CancellationToken cancellationToken = default)
    {
        var now = Clock.UtcNow;
        foreach (var ticker in tickers)
            await WriteChainAsync(ticker, now, adding: true, depth: 0, cancellationToken).ConfigureAwait(false);
        return tickers.Length;
    }

    // An update also writes the chain nodes it carries, as rows of their own. Children it does not carry are kept; a row
    // stored by an older version with its children nested is split into rows, with the nested children when the update
    // carries none.
    public async Task<int> UpdateTimeTickers(TTimeTicker[] tickers, CancellationToken cancellationToken = default)
    {
        var now = Clock.UtcNow;
        foreach (var ticker in tickers)
            await WriteChainAsync(ticker, now, adding: false, depth: 0, cancellationToken).ConfigureAwait(false);
        return tickers.Length;
    }

    // Every chain node is a row of its own. Children are written before their parent, so a parent is never stored
    // without them, and a child is linked to its parent before its row exists: a link without a row is skipped on read.
    // Cancellation is checked only before a chain starts, so a cancel does not leave half a chain behind.
    private async Task WriteChainAsync(TTimeTicker ticker, DateTime now, bool adding, int depth, CancellationToken cancellationToken)
    {
        if (depth == 0)
            cancellationToken.ThrowIfCancellationRequested();

        var children = depth < MaxChainDepth ? ticker.Children?.Where(c => c != null).ToList() ?? [] : [];

        if (!adding && children.Count == 0)
        {
            // A row of an older version holds its children nested: an update that carries none splits those into rows.
            var stored = await Serializer.GetAsync<TTimeTicker>(TimeTickerKey(ticker.Id)).ConfigureAwait(false);
            if (stored?.Children is { Count: > 0 } nested)
                children = nested.Where(c => c != null).ToList();
        }

        for (var position = 0; position < children.Count; position++)
        {
            var child = children[position];
            child.ParentId = ticker.Id;
            await IndexManager.AddTimeTickerChildAsync(ticker.Id, child.Id, position).ConfigureAwait(false);
            await WriteChainAsync(child, now, adding, depth + 1, cancellationToken).ConfigureAwait(false);
        }

        ticker.CreatedAt = ticker.CreatedAt == default ? now : ticker.CreatedAt;
        ticker.UpdatedAt = adding && ticker.UpdatedAt != default ? ticker.UpdatedAt : now;

        // The row is stored without its children; they are rows of their own. The caller's entity keeps them.
        var carried = ticker.Children;
        ticker.Children = new List<TTimeTicker>();
        TTimeTicker written;
        try
        {
            if (adding)
            {
                await Serializer.SetAsync(TimeTickerKey(ticker.Id), ticker).ConfigureAwait(false);
                written = ticker;
            }
            else
            {
                written = await UpdateChainRowAsync(ticker).ConfigureAwait(false);
                KeepStoredState(ticker, written);
            }
        }
        finally
        {
            ticker.Children = carried;
        }
        await IndexManager.AddTimeTickerIndexesAsync(written).ConfigureAwait(false);
    }

    // Writes the update's row (without its children) over the stored one through the compare-and-set: each round starts
    // again from a copy of what the caller sent and keeps the state the store owns of the row it replaces. A row that is
    // not there yet (a new chain node) is created, with no run state of the body's.
    private async Task<TTimeTicker> UpdateChainRowAsync(TTimeTicker ticker)
    {
        var body = Serializer.Serialize(ticker);
        TTimeTicker Copy() => Serializer.DeserializeOrNull<TTimeTicker>(body);

        return await CompareAndSetAsync<TTimeTicker>(TimeTickerKey(ticker.Id),
            stored =>
            {
                var row = Copy();
                if (stored == null)
                    RunStateOnEdit.Apply(row, NoStoredRow);
                else
                {
                    KeepStoredChainState(row, stored);
                    RunStateOnEdit.Apply(row, stored);
                }
                return row;
            },
            create: () =>
            {
                var row = Copy();
                RunStateOnEdit.Apply(row, NoStoredRow);
                return row;
            }).ConfigureAwait(false);
    }

    // What a body is checked against when no row is stored (or it does not read): a run it shows is not brought in.
    private static readonly TTimeTicker NoStoredRow = new() { Status = TickerStatus.Idle };

    // The caller's entity reflects what was stored, as it did when the store's state was applied to it directly.
    private static void KeepStoredState(TTimeTicker ticker, TTimeTicker written)
    {
        ticker.ParentId = written.ParentId;
        ticker.RunCondition = written.RunCondition;
        ticker.Status = written.Status;
        ticker.LockHolder = written.LockHolder;
        ticker.LockedAt = written.LockedAt;
        ticker.ExecutedAt = written.ExecutedAt;
        ticker.ExceptionMessage = written.ExceptionMessage;
        ticker.SkippedReason = written.SkippedReason;
        ticker.ElapsedTime = written.ElapsedTime;
        ticker.RetryCount = written.RetryCount;
        ticker.UpdatedAt = written.UpdatedAt;
    }

    // A chain child's link and run belong to the store: an update carries a copy read earlier, whose run state may be
    // stale, and the dashboard's edit form carries no ParentId or RunCondition. The update's definition is applied. A
    // root keeps the update's run state, as before, except the run of a row that is InProgress, or one the body still
    // shows after it ended (RunStateOnEdit, applied after this to every row).
    private static void KeepStoredChainState(TTimeTicker ticker, TTimeTicker stored)
    {
        if (stored.ParentId == null) return;

        ticker.ParentId = stored.ParentId;
        ticker.RunCondition ??= stored.RunCondition;
        ticker.Status = stored.Status;
        ticker.LockHolder = stored.LockHolder;
        ticker.LockedAt = stored.LockedAt;
        ticker.ExecutedAt = stored.ExecutedAt;
        ticker.ExceptionMessage = stored.ExceptionMessage;
        ticker.SkippedReason = stored.SkippedReason;
        ticker.ElapsedTime = stored.ElapsedTime;
        ticker.RetryCount = stored.RetryCount;
    }

    // Removes the rows with every descendant, as the other stores do: a child left without its parent would never run.
    public async Task<int> RemoveTimeTickers(Guid[] tickerIds, CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var id in tickerIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = await Serializer.GetAsync<TTimeTicker>(TimeTickerKey(id)).ConfigureAwait(false);
            await RemoveDescendantsAsync(id, depth: 0).ConfigureAwait(false);
            await IndexManager.RemoveTimeTickerIndexesAsync(id, row?.ParentId).ConfigureAwait(false);
            if (await Db.KeyDeleteAsync(TimeTickerKey(id)).ConfigureAwait(false))
                count++;
        }
        return count;
    }

    private async Task RemoveDescendantsAsync(Guid parentId, int depth)
    {
        if (depth >= MaxChainDepth) return;

        foreach (var childId in await IndexManager.GetTimeTickerChildIdsAsync(parentId).ConfigureAwait(false))
        {
            await RemoveDescendantsAsync(childId, depth + 1).ConfigureAwait(false);
            await IndexManager.RemoveTimeTickerIndexesAsync(childId).ConfigureAwait(false);
            await Db.KeyDeleteAsync(TimeTickerKey(childId)).ConfigureAwait(false);
        }
    }
    #endregion

    #region Cron_Ticker_Shared_Methods
    public async Task<TCronTicker> GetCronTickerById(Guid id, CancellationToken cancellationToken)
    {
        return await Serializer.GetAsync<TCronTicker>(CronKey(id)).ConfigureAwait(false);
    }

    public async Task<TCronTicker[]> GetCronTickers(Expression<Func<TCronTicker, bool>> predicate, CancellationToken cancellationToken)
    {
        var list = await Serializer.LoadAllFromSetAsync<TCronTicker>(
            CronIdsKey, CronKey, cancellationToken, predicate?.Compile()).ConfigureAwait(false);

        return list.OrderByDescending(x => x.CreatedAt).ToArray();
    }

    public async Task<PaginationResult<TCronTicker>> GetCronTickersPaginated(Expression<Func<TCronTicker, bool>> predicate, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
    {
        var all = await GetCronTickers(predicate, cancellationToken).ConfigureAwait(false);
        var total = all.Length;
        var items = all.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToArray();
        return new PaginationResult<TCronTicker>(items, total, pageNumber, pageSize);
    }

    public async Task<int> InsertCronTickers(TCronTicker[] tickers, CancellationToken cancellationToken)
    {
        var now = Clock.UtcNow;
        foreach (var ticker in tickers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ticker.CreatedAt = ticker.CreatedAt == default ? now : ticker.CreatedAt;
            ticker.UpdatedAt = ticker.UpdatedAt == default ? now : ticker.UpdatedAt;
            await Serializer.SetAsync(CronKey(ticker.Id), ticker).ConfigureAwait(false);
            await IndexManager.AddCronIndexesAsync(ticker).ConfigureAwait(false);
        }
        return tickers.Length;
    }

    public async Task<int> UpdateCronTickers(TCronTicker[] cronTicker, CancellationToken cancellationToken)
    {
        var now = Clock.UtcNow;
        foreach (var ticker in cronTicker)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ticker.UpdatedAt = now;
            await Serializer.SetAsync(CronKey(ticker.Id), ticker).ConfigureAwait(false);
            await IndexManager.AddCronIndexesAsync(ticker).ConfigureAwait(false);
        }
        return cronTicker.Length;
    }

    public async Task<int> RemoveCronTickers(Guid[] cronTickerIds, CancellationToken cancellationToken)
    {
        var removed = 0;
        foreach (var id in cronTickerIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await IndexManager.RemoveCronIndexesAsync(id).ConfigureAwait(false);
            await IndexManager.RemoveCronOccurrencesByParentAsync(id).ConfigureAwait(false);
            if (await Db.KeyDeleteAsync(CronKey(id)).ConfigureAwait(false))
                removed++;
        }
        return removed;
    }
    #endregion

    #region Cron_TickerOccurrence_Shared_Methods
    public async Task<CronTickerOccurrenceEntity<TCronTicker>[]> GetAllCronTickerOccurrences(Expression<Func<CronTickerOccurrenceEntity<TCronTicker>, bool>> predicate, CancellationToken cancellationToken = default)
    {
        var list = await Serializer.LoadAllFromSetAsync<CronTickerOccurrenceEntity<TCronTicker>>(
            CronOccurrenceIdsKey, CronOccurrenceKey, cancellationToken, predicate?.Compile()).ConfigureAwait(false);

        return list.OrderByDescending(x => x.ExecutionTime).ToArray();
    }

    public async Task<PaginationResult<CronTickerOccurrenceEntity<TCronTicker>>> GetAllCronTickerOccurrencesPaginated(Expression<Func<CronTickerOccurrenceEntity<TCronTicker>, bool>> predicate, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
    {
        var all = await GetAllCronTickerOccurrences(predicate, cancellationToken).ConfigureAwait(false);
        var total = all.Length;
        var items = all.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToArray();
        return new PaginationResult<CronTickerOccurrenceEntity<TCronTicker>>(items, total, pageNumber, pageSize);
    }

    public async Task<int> InsertCronTickerOccurrences(CronTickerOccurrenceEntity<TCronTicker>[] cronTickerOccurrences, CancellationToken cancellationToken)
    {
        foreach (var occurrence in cronTickerOccurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Serializer.SetAsync(CronOccurrenceKey(occurrence.Id), occurrence).ConfigureAwait(false);
            await IndexManager.AddCronOccurrenceIndexesAsync(occurrence).ConfigureAwait(false);
        }
        return cronTickerOccurrences.Length;
    }

    public async Task<int> RemoveCronTickerOccurrences(Guid[] cronTickerOccurrences, CancellationToken cancellationToken)
    {
        var removed = 0;
        foreach (var id in cronTickerOccurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var occurrence = await Serializer.GetAsync<CronTickerOccurrenceEntity<TCronTicker>>(CronOccurrenceKey(id)).ConfigureAwait(false);
            if (occurrence != null)
                await IndexManager.RemoveCronOccurrenceIndexesAsync(id, occurrence.CronTickerId).ConfigureAwait(false);
            if (await Db.KeyDeleteAsync(CronOccurrenceKey(id)).ConfigureAwait(false))
                removed++;
        }
        return removed;
    }

    public async Task<CronTickerOccurrenceEntity<TCronTicker>[]> AcquireImmediateCronOccurrencesAsync(Guid[] occurrenceIds, CancellationToken cancellationToken = default)
    {
        if (occurrenceIds == null || occurrenceIds.Length == 0)
            return [];

        var acquired = new List<CronTickerOccurrenceEntity<TCronTicker>>();
        var won = new Dictionary<Guid, DateTime>(occurrenceIds.Length);
        try
        {
            foreach (var id in occurrenceIds)
            {
                // A cancelled caller stops the loop: a row won here is already InProgress and must be returned to be
                // dispatched, the rows not written yet stay Idle for the fallback.
                if (cancellationToken.IsCancellationRequested) break;

                var occurrence = await TryClaimInProgressAsync<CronTickerOccurrenceEntity<TCronTicker>>(
                    CronOccurrenceKey(id), id, TryReleaseCronOccurrenceClaim).ConfigureAwait(false);

                if (occurrence == null) continue;

                won[occurrence.Id] = occurrence.UpdatedAt;
                if (occurrence.CronTicker == null && occurrence.CronTickerId != Guid.Empty)
                    occurrence.CronTicker = await Serializer.GetAsync<TCronTicker>(CronKey(occurrence.CronTickerId)).ConfigureAwait(false);

                await TryUpdateIndexAfterClaim(occurrence.Id, () => IndexManager.AddCronOccurrenceIndexesAsync(occurrence)).ConfigureAwait(false);
                acquired.Add(occurrence);
            }

            return acquired.ToArray();
        }
        catch (Exception failure) when (won.Count > 0)
        {
            // The caller gets no rows to run, so the rows won so far would stay InProgress until this node restarts.
            await ClaimPutBack.PutBackOrThrowAsync(won, TickerType.CronTickerOccurrence, failure, TryReleaseCronOccurrenceClaim).ConfigureAwait(false);
            throw;
        }
    }
    #endregion
}
