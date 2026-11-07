#nullable disable
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using SharpTicker.Caching.StackExchangeRedis.Helpers;
using static SharpTicker.Caching.StackExchangeRedis.DependencyInjection.ServiceExtension;
using static SharpTicker.Caching.StackExchangeRedis.Helpers.RedisKeyBuilder;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Caching.StackExchangeRedis.Infrastructure;

internal abstract class BaseRedisPersistenceProvider<TTimeTicker, TCronTicker>
    where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
    where TCronTicker : CronTickerEntity, new()
{
    protected readonly IDatabase Db;
    protected readonly ITickerClock Clock;
    protected readonly string LockHolder;
    protected readonly RedisSerializer Serializer;
    protected readonly RedisIndexManager<TTimeTicker, TCronTicker> IndexManager;
    private readonly ILogger _logger;

    // Lua status constants matching TickerStatus enum values
    private static readonly RedisValue LuaStatusIdle = (int)TickerStatus.Idle;
    private static readonly RedisValue LuaStatusQueued = (int)TickerStatus.Queued;
    private static readonly RedisValue LuaStatusInProgress = (int)TickerStatus.InProgress;

    // Raw script strings with KEYS[]/ARGV[] notation (AOT-safe)
    private static readonly string AcquireScript = LuaScriptLoader.Load("Acquire");
    private static readonly string ReleaseScript = LuaScriptLoader.Load("Release");
    private static readonly string RecoverDeadNodeScript = LuaScriptLoader.Load("RecoverDeadNode");
    private static readonly string MarkInProgressScript = LuaScriptLoader.Load("MarkInProgress");

    protected BaseRedisPersistenceProvider(
        IDatabase db,
        ITickerClock clock,
        SchedulerOptionsBuilder optionsBuilder,
        SharpTickerRedisOptionBuilder redisOptions,
        ILogger logger)
    {
        Db = db ?? throw new ArgumentNullException(nameof(db));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        LockHolder = optionsBuilder?.NodeIdentifier ?? Environment.MachineName;

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            TypeInfoResolverChain = { RedisContextJsonSerializerContext.Default }
        };

        if (redisOptions?.JsonSerializerContext != null)
            jsonOptions.TypeInfoResolverChain.Insert(0, redisOptions.JsonSerializerContext);

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Serializer = new RedisSerializer(db, jsonOptions, _logger);
        IndexManager = new RedisIndexManager<TTimeTicker, TCronTicker>(db, LockHolder);
    }

    #region Lua script operations
    protected async Task<T> TryAcquireAsync<T>(string key, TickerStatus targetStatus, string expectedUpdatedAt = "") where T : class
    {
        var now = Clock.UtcNow;
        var result = await Db.ScriptEvaluateAsync(
            AcquireScript,
            [(RedisKey)key],
            [(RedisValue)LockHolder, (RedisValue)now.ToString("O"), (RedisValue)(int)targetStatus,
             (RedisValue)(expectedUpdatedAt ?? ""), LuaStatusIdle, LuaStatusQueued]
        ).ConfigureAwait(false);

        if (result.IsNull) return null;
        return Serializer.DeserializeOrNull<T>((string)result);
    }

    protected async Task<T> TryReleaseAsync<T>(string key) where T : class
    {
        var now = Clock.UtcNow;
        var result = await Db.ScriptEvaluateAsync(
            ReleaseScript,
            [(RedisKey)key],
            [(RedisValue)LockHolder, (RedisValue)now.ToString("O"), LuaStatusIdle, LuaStatusQueued]
        ).ConfigureAwait(false);

        if (result.IsNull) return null;
        return Serializer.DeserializeOrNull<T>((string)result);
    }

    protected async Task<T> TryRecoverDeadNodeAsync<T>(string key, string deadNodeId) where T : class
    {
        var now = Clock.UtcNow;
        var result = await Db.ScriptEvaluateAsync(
            RecoverDeadNodeScript,
            [(RedisKey)key],
            [(RedisValue)deadNodeId, (RedisValue)now.ToString("O"), LuaStatusIdle, LuaStatusQueued, LuaStatusInProgress]
        ).ConfigureAwait(false);

        if (result.IsNull) return null;
        return Serializer.DeserializeOrNull<T>((string)result);
    }

    // The script has committed the claim, so an index failure must not turn into a failed claim: the caller would not
    // run a row that is already InProgress. A stale pending entry is harmless, Acquire.lua rejects it.
    protected async Task TryUpdateIndexAfterClaim(Guid id, Func<Task> updateIndex)
    {
        try
        {
            await updateIndex().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Row {Id} was claimed InProgress but its pending index entry could not be updated.", id);
        }
    }

    // Fenced Queued -> InProgress claim. Claimed is true when the script moved the row; Row can still be null if the
    // stored JSON does not deserialize, which does not undo the claim.
    protected async Task<(bool Claimed, T Row)> TryMarkInProgressAsync<T>(string key) where T : class
    {
        var now = Clock.UtcNow;
        var result = await Db.ScriptEvaluateAsync(
            MarkInProgressScript,
            [(RedisKey)key],
            [(RedisValue)LockHolder, (RedisValue)now.ToString("O"), LuaStatusQueued, LuaStatusInProgress]
        ).ConfigureAwait(false);

        if (result.IsNull) return (false, null);
        return (true, Serializer.DeserializeOrNull<T>((string)result));
    }
    #endregion

    #region FunctionContext mapping
    private void ApplyFunctionContext(
        InternalFunctionContext context,
        Action<TickerStatus> setStatus,
        Action<string> setSkippedReason,
        Action<DateTime?> setExecutedAt,
        Action<string> setExceptionMessage,
        Action<long> setElapsedTime,
        Action<int> setRetryCount,
        Action releaseLock,
        Action<DateTime> setUpdatedAt)
    {
        var propsToUpdate = context.GetPropsToUpdate();

        if (propsToUpdate.Contains(nameof(InternalFunctionContext.Status)) &&
            context.Status != TickerStatus.Skipped)
        {
            setStatus(context.Status);
        }
        else if (propsToUpdate.Contains(nameof(InternalFunctionContext.Status)))
        {
            setStatus(context.Status);
            setSkippedReason(context.ExceptionDetails);
        }

        if (propsToUpdate.Contains(nameof(InternalFunctionContext.ExecutedAt)))
            setExecutedAt(context.ExecutedAt);

        if (propsToUpdate.Contains(nameof(InternalFunctionContext.ExceptionDetails)) &&
            context.Status != TickerStatus.Skipped)
            setExceptionMessage(context.ExceptionDetails);

        if (propsToUpdate.Contains(nameof(InternalFunctionContext.ElapsedTime)))
            setElapsedTime(context.ElapsedTime);

        if (propsToUpdate.Contains(nameof(InternalFunctionContext.RetryCount)))
            setRetryCount(context.RetryCount);

        if (propsToUpdate.Contains(nameof(InternalFunctionContext.ReleaseLock)))
            releaseLock();

        setUpdatedAt(Clock.UtcNow);
    }

    // Mirrors what ApplyFunctionContext applies. A context with none of these (for example one that only carries
    // ExecutionTime) would still rewrite the whole row to touch UpdatedAt, and that read-modify-write can put back
    // a Queued copy over a claim made in between.
    private static bool HasApplicableProps(InternalFunctionContext context)
    {
        var props = context.GetPropsToUpdate();
        return props.Contains(nameof(InternalFunctionContext.Status)) ||
               props.Contains(nameof(InternalFunctionContext.ExecutedAt)) ||
               (props.Contains(nameof(InternalFunctionContext.ExceptionDetails)) && context.Status != TickerStatus.Skipped) ||
               props.Contains(nameof(InternalFunctionContext.ElapsedTime)) ||
               props.Contains(nameof(InternalFunctionContext.RetryCount)) ||
               props.Contains(nameof(InternalFunctionContext.ReleaseLock));
    }

    protected void ApplyFunctionContextToTicker(TTimeTicker ticker, InternalFunctionContext context)
    {
        ApplyFunctionContext(context,
            status => ticker.Status = status,
            reason => ticker.SkippedReason = reason,
            at => ticker.ExecutedAt = at,
            msg => ticker.ExceptionMessage = msg,
            elapsed => ticker.ElapsedTime = elapsed,
            count => ticker.RetryCount = count,
            () => { ticker.LockHolder = null; ticker.LockedAt = null; },
            at => ticker.UpdatedAt = at);
    }

    protected void ApplyFunctionContextToCronOccurrence(CronTickerOccurrenceEntity<TCronTicker> occurrence, InternalFunctionContext context)
    {
        ApplyFunctionContext(context,
            status => occurrence.Status = status,
            reason => occurrence.SkippedReason = reason,
            at => occurrence.ExecutedAt = at,
            msg => occurrence.ExceptionMessage = msg,
            elapsed => occurrence.ElapsedTime = elapsed,
            count => occurrence.RetryCount = count,
            () => { occurrence.LockHolder = null; occurrence.LockedAt = null; },
            at => occurrence.UpdatedAt = at);
    }

    protected static TimeTickerEntity MapForQueue(TTimeTicker ticker)
    {
        return new TimeTickerEntity
        {
            Id = ticker.Id,
            Function = ticker.Function,
            Retries = ticker.Retries,
            RetryIntervals = ticker.RetryIntervals,
            UpdatedAt = ticker.UpdatedAt,
            ParentId = ticker.ParentId,
            ExecutionTime = ticker.ExecutionTime,
            Status = ticker.Status,
            LockHolder = ticker.LockHolder,
            LockedAt = ticker.LockedAt,
            Children = (ticker.Children ?? []).Select(ch => new TimeTickerEntity
            {
                Id = ch.Id,
                Function = ch.Function,
                Retries = ch.Retries,
                RetryIntervals = ch.RetryIntervals,
                RunCondition = ch.RunCondition,
                ParentId = ch.ParentId,
                Children = (ch.Children ?? []).Select(gch => new TimeTickerEntity
                {
                    Id = gch.Id,
                    Function = gch.Function,
                    Retries = gch.Retries,
                    RetryIntervals = gch.RetryIntervals,
                    RunCondition = gch.RunCondition,
                    ParentId = gch.ParentId
                }).ToArray()
            }).ToArray()
        };
    }
    #endregion

    #region Core_Time_Ticker_Methods
    public async IAsyncEnumerable<TimeTickerEntity> QueueTimeTickers(TimeTickerEntity[] timeTickers, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var timeTicker in timeTickers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var acquired = await TryAcquireAsync<TTimeTicker>(
                TimeTickerKey(timeTicker.Id),
                TickerStatus.Queued,
                timeTicker.UpdatedAt.ToString("O")).ConfigureAwait(false);

            if (acquired == null) continue;

            await TryUpdateIndexAfterClaim(acquired.Id, () => IndexManager.AddTimeTickerIndexesAsync(acquired)).ConfigureAwait(false);

            timeTicker.LockHolder = acquired.LockHolder;
            timeTicker.LockedAt = acquired.LockedAt;
            timeTicker.UpdatedAt = acquired.UpdatedAt;
            timeTicker.Status = acquired.Status;

            yield return timeTicker;
        }
    }

    public async IAsyncEnumerable<TimeTickerEntity> QueueTimedOutTimeTickers([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var threshold = Clock.UtcNow.AddSeconds(-1);

        var dueIds = await Db.SortedSetRangeByScoreAsync(TimeTickerPendingKey, double.NegativeInfinity, ToScore(threshold)).ConfigureAwait(false);

        foreach (var redisValue in dueIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParse(redisValue.ToString(), out var id)) continue;

            var acquired = await TryAcquireAsync<TTimeTicker>(
                TimeTickerKey(id),
                TickerStatus.InProgress).ConfigureAwait(false);

            if (acquired == null) continue;

            await TryUpdateIndexAfterClaim(acquired.Id, () => IndexManager.AddTimeTickerIndexesAsync(acquired)).ConfigureAwait(false);

            yield return MapForQueue(acquired);
        }
    }

    public async Task ReleaseAcquiredTimeTickers(Guid[] timeTickerIds, CancellationToken cancellationToken = default)
    {
        var ids = timeTickerIds.Length == 0
            ? ParseGuidSet(await Db.SetMembersAsync(TimeTickerIdsKey).ConfigureAwait(false))
            : timeTickerIds;

        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var released = await TryReleaseAsync<TTimeTicker>(TimeTickerKey(id)).ConfigureAwait(false);
            if (released == null) continue;

            await IndexManager.AddTimeTickerIndexesAsync(released).ConfigureAwait(false);
        }
    }

    public async Task<bool> TryMarkTimeTickerInProgress(Guid id, CancellationToken cancellationToken = default)
    {
        var (claimed, row) = await TryMarkInProgressAsync<TTimeTicker>(TimeTickerKey(id)).ConfigureAwait(false);
        if (!claimed) return false;

        if (row != null)
            await TryUpdateIndexAfterClaim(id, () => IndexManager.AddTimeTickerIndexesAsync(row)).ConfigureAwait(false);
        return true;
    }

    public async Task<TimeTickerEntity[]> GetEarliestTimeTickers(CancellationToken cancellationToken = default)
    {
        var now = Clock.UtcNow;
        var oneSecondAgoScore = ToScore(now.AddSeconds(-1));

        var first = await Db.SortedSetRangeByScoreWithScoresAsync(TimeTickerPendingKey, oneSecondAgoScore, double.PositiveInfinity, Exclude.None, Order.Ascending, 0, 1).ConfigureAwait(false);
        if (first.Length == 0) return [];

        var earliestScore = first[0].Score;
        var minSecond = new DateTime((long)earliestScore, DateTimeKind.Utc);
        var maxScore = ToScore(minSecond.AddSeconds(1));

        var entries = await Db.SortedSetRangeByScoreAsync(TimeTickerPendingKey, earliestScore, maxScore).ConfigureAwait(false);
        var idsInWindow = ParseGuidSet(entries);

        var tickers = await Serializer.LoadByIdsAsync<TTimeTicker>(idsInWindow, TimeTickerKey, cancellationToken).ConfigureAwait(false);

        return tickers
            .Where(t => CanAcquire(t.Status, t.LockHolder, LockHolder))
            .Select(MapForQueue)
            .ToArray();
    }

    public async Task<int> UpdateTimeTicker(InternalFunctionContext functionContexts, CancellationToken cancellationToken = default)
    {
        if (!HasApplicableProps(functionContexts)) return 0;

        var ticker = await Serializer.GetAsync<TTimeTicker>(TimeTickerKey(functionContexts.TickerId)).ConfigureAwait(false);
        if (ticker == null) return 0;

        ApplyFunctionContextToTicker(ticker, functionContexts);
        await Serializer.SetAsync(TimeTickerKey(ticker.Id), ticker).ConfigureAwait(false);
        await IndexManager.AddTimeTickerIndexesAsync(ticker).ConfigureAwait(false);
        return 1;
    }

    public async Task<byte[]> GetTimeTickerRequest(Guid id, CancellationToken cancellationToken)
    {
        var ticker = await Serializer.GetAsync<TTimeTicker>(TimeTickerKey(id)).ConfigureAwait(false);
        return ticker?.Request;
    }

    public async Task UpdateTimeTickersWithUnifiedContext(Guid[] timeTickerIds, InternalFunctionContext functionContext, CancellationToken cancellationToken = default)
    {
        var tickers = await Serializer.LoadByIdsAsync<TTimeTicker>(timeTickerIds, TimeTickerKey, cancellationToken).ConfigureAwait(false);

        foreach (var ticker in tickers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyFunctionContextToTicker(ticker, functionContext);
            await Serializer.SetAsync(TimeTickerKey(ticker.Id), ticker).ConfigureAwait(false);
            await IndexManager.AddTimeTickerIndexesAsync(ticker).ConfigureAwait(false);
        }
    }

    public async Task<TimeTickerEntity[]> AcquireImmediateTimeTickersAsync(Guid[] ids, CancellationToken cancellationToken = default)
    {
        if (ids == null || ids.Length == 0) return [];

        var acquired = new List<TimeTickerEntity>();
        foreach (var id in ids)
        {
            // A cancelled caller stops the loop: a row won here is already InProgress and must be returned to be
            // dispatched, the rows not written yet stay Idle for the fallback.
            if (cancellationToken.IsCancellationRequested) break;

            var ticker = await TryAcquireAsync<TTimeTicker>(
                TimeTickerKey(id),
                TickerStatus.InProgress).ConfigureAwait(false);

            if (ticker == null) continue;

            await TryUpdateIndexAfterClaim(ticker.Id, () => IndexManager.AddTimeTickerIndexesAsync(ticker)).ConfigureAwait(false);
            acquired.Add(MapForQueue(ticker));
        }

        return acquired.ToArray();
    }

    public async Task ReleaseDeadNodeTimeTickerResources(string instanceIdentifier, CancellationToken cancellationToken = default)
    {
        var members = await Db.SetMembersAsync(TimeTickerIdsKey).ConfigureAwait(false);
        var ids = ParseGuidSet(members);

        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var recovered = await TryRecoverDeadNodeAsync<TTimeTicker>(TimeTickerKey(id), instanceIdentifier).ConfigureAwait(false);
            if (recovered == null) continue;

            await IndexManager.AddTimeTickerIndexesAsync(recovered).ConfigureAwait(false);
        }
    }
    #endregion

    #region Core_Cron_Ticker_Methods
    public async Task MigrateDefinedCronTickers((string Function, string Expression)[] cronTickers, CancellationToken cancellationToken = default)
    {
        var now = Clock.UtcNow;
        const string seedPrefix = "MemoryTicker_Seeded_";

        var allRegisteredFunctions = TickerFunctionProvider.TickerFunctions.Keys
            .ToHashSet(StringComparer.Ordinal);

        var existingList = await Serializer.LoadAllFromSetAsync<TCronTicker>(CronIdsKey, CronKey, cancellationToken).ConfigureAwait(false);

        var orphanedToDelete = existingList
            .Where(c => !allRegisteredFunctions.Contains(c.Function))
            .Select(c => c.Id).ToArray();

        foreach (var id in orphanedToDelete)
        {
            await IndexManager.RemoveCronIndexesAsync(id).ConfigureAwait(false);
            await IndexManager.RemoveCronOccurrencesByParentAsync(id).ConfigureAwait(false);
            await Db.KeyDeleteAsync(CronKey(id)).ConfigureAwait(false);
        }

        var orphanedSet = orphanedToDelete.ToHashSet();
        var existingByFunction = existingList
            .Where(c => !orphanedSet.Contains(c.Id))
            .ToDictionary(c => c.Function, c => c, StringComparer.Ordinal);

        foreach (var (function, expression) in cronTickers)
        {
            if (existingByFunction.TryGetValue(function, out var cron))
            {
                if (!string.Equals(cron.Expression, expression, StringComparison.Ordinal))
                {
                    cron.Expression = expression;
                    cron.UpdatedAt = now;
                    await Serializer.SetAsync(CronKey(cron.Id), cron).ConfigureAwait(false);
                }
            }
            else
            {
                var entity = new TCronTicker
                {
                    Id = Guid.NewGuid(),
                    Function = function,
                    Expression = expression,
                    InitIdentifier = $"{seedPrefix}{function}",
                    CreatedAt = now,
                    UpdatedAt = now,
                    Request = []
                };
                await Serializer.SetAsync(CronKey(entity.Id), entity).ConfigureAwait(false);
                await IndexManager.AddCronIndexesAsync(entity).ConfigureAwait(false);
            }
        }
    }

    public async Task<CronTickerEntity[]> GetAllCronTickerExpressions(CancellationToken cancellationToken = default)
    {
        var list = await Serializer.LoadAllFromSetAsync<TCronTicker>(CronIdsKey, CronKey, cancellationToken).ConfigureAwait(false);
        return list
            .Where(c => c.IsEnabled)
            .Select(c => new CronTickerEntity
            {
                Id = c.Id,
                Expression = c.Expression,
                Function = c.Function,
                RetryIntervals = c.RetryIntervals,
                Retries = c.Retries
            })
            .ToArray();
    }
    #endregion

    #region Core_Cron_TickerOccurrence_Methods
    public async Task<CronTickerOccurrenceEntity<TCronTicker>> GetEarliestAvailableCronOccurrence(Guid[] ids, CancellationToken cancellationToken = default)
    {
        if (ids == null || ids.Length == 0) return null;

        var idSet = ids.ToHashSet();
        long cursor = 0;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scanResult = await Db.SortedSetRangeByScoreAsync(
                CronOccurrencePendingKey,
                double.NegativeInfinity, double.PositiveInfinity,
                Exclude.None, Order.Ascending,
                cursor, 100).ConfigureAwait(false);

            if (scanResult.Length == 0) break;

            var batchIds = ParseGuidSet(scanResult);
            var occurrences = await Serializer.LoadByIdsAsync<CronTickerOccurrenceEntity<TCronTicker>>(batchIds, CronOccurrenceKey, cancellationToken).ConfigureAwait(false);

            foreach (var occurrence in occurrences)
            {
                if (!idSet.Contains(occurrence.CronTickerId)) continue;
                if (!CanAcquire(occurrence.Status, occurrence.LockHolder, LockHolder)) continue;
                return occurrence;
            }

            cursor += scanResult.Length;
        } while (true);

        return null;
    }

    public async IAsyncEnumerable<CronTickerOccurrenceEntity<TCronTicker>> QueueCronTickerOccurrences((DateTime Key, InternalManagerContext[] Items) cronTickerOccurrences, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var now = Clock.UtcNow;
        var executionTime = cronTickerOccurrences.Key;

        foreach (var item in cronTickerOccurrences.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.NextCronOccurrence is null)
            {
                var occurrence = new CronTickerOccurrenceEntity<TCronTicker>
                {
                    Id = SlotId(item.Id, executionTime),
                    Status = TickerStatus.Queued,
                    LockHolder = LockHolder,
                    ExecutionTime = executionTime,
                    CronTickerId = item.Id,
                    LockedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now,
                    CronTicker = new TCronTicker
                    {
                        Id = item.Id,
                        Function = item.FunctionName,
                        Expression = item.Expression,
                        Retries = item.Retries,
                        RetryIntervals = item.RetryIntervals
                    }
                };

                // The key is derived from the slot, so a node that lost the race finds it taken and skips the slot.
                // If the winner's index update never landed (failure or lost ack), nothing else would ever find the row.
                if (!await Serializer.TryCreateAsync(CronOccurrenceKey(occurrence.Id), occurrence).ConfigureAwait(false))
                {
                    await IndexManager.HealOrphanCronOccurrenceIndexesAsync(occurrence.Id,
                        () => Serializer.GetAsync<CronTickerOccurrenceEntity<TCronTicker>>(CronOccurrenceKey(occurrence.Id))).ConfigureAwait(false);
                    continue;
                }

                await IndexManager.AddCronOccurrenceIndexesAsync(occurrence).ConfigureAwait(false);
                yield return occurrence;
            }
            else
            {
                var acquired = await TryAcquireAsync<CronTickerOccurrenceEntity<TCronTicker>>(
                    CronOccurrenceKey(item.NextCronOccurrence.Id),
                    TickerStatus.Queued).ConfigureAwait(false);

                if (acquired == null) continue;

                acquired.ExecutionTime = executionTime;

                if (acquired.CronTicker == null)
                {
                    acquired.CronTicker = new TCronTicker
                    {
                        Id = item.Id,
                        Function = item.FunctionName,
                        Expression = item.Expression,
                        Retries = item.Retries,
                        RetryIntervals = item.RetryIntervals
                    };
                }

                // Not written back: the acquire script already stored the row, and a full write here would overwrite a
                // fallback claim made since. ExecutionTime and CronTicker are set on the returned copy only.
                await TryUpdateIndexAfterClaim(acquired.Id, () => IndexManager.AddCronOccurrenceIndexesAsync(acquired)).ConfigureAwait(false);
                yield return acquired;
            }
        }
    }

    public async IAsyncEnumerable<CronTickerOccurrenceEntity<TCronTicker>> QueueTimedOutCronTickerOccurrences([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var threshold = Clock.UtcNow.AddSeconds(-1);

        var dueIds = await Db.SortedSetRangeByScoreAsync(CronOccurrencePendingKey, double.NegativeInfinity, ToScore(threshold)).ConfigureAwait(false);
        foreach (var redisValue in dueIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParse(redisValue.ToString(), out var id)) continue;

            var acquired = await TryAcquireAsync<CronTickerOccurrenceEntity<TCronTicker>>(
                CronOccurrenceKey(id),
                TickerStatus.InProgress).ConfigureAwait(false);

            if (acquired == null) continue;

            await TryUpdateIndexAfterClaim(acquired.Id, () => IndexManager.AddCronOccurrenceIndexesAsync(acquired)).ConfigureAwait(false);
            yield return acquired;
        }
    }

    public async Task<bool> TryMarkCronOccurrenceInProgress(Guid id, CancellationToken cancellationToken = default)
    {
        var (claimed, row) = await TryMarkInProgressAsync<CronTickerOccurrenceEntity<TCronTicker>>(CronOccurrenceKey(id)).ConfigureAwait(false);
        if (!claimed) return false;

        if (row != null)
            await TryUpdateIndexAfterClaim(id, () => IndexManager.AddCronOccurrenceIndexesAsync(row)).ConfigureAwait(false);
        return true;
    }

    public async Task UpdateCronTickerOccurrence(InternalFunctionContext functionContext, CancellationToken cancellationToken = default)
    {
        if (!HasApplicableProps(functionContext)) return;

        var occurrence = await Serializer.GetAsync<CronTickerOccurrenceEntity<TCronTicker>>(CronOccurrenceKey(functionContext.TickerId)).ConfigureAwait(false);
        if (occurrence == null) return;

        ApplyFunctionContextToCronOccurrence(occurrence, functionContext);
        await Serializer.SetAsync(CronOccurrenceKey(occurrence.Id), occurrence).ConfigureAwait(false);
        await IndexManager.AddCronOccurrenceIndexesAsync(occurrence).ConfigureAwait(false);
    }

    public async Task ReleaseAcquiredCronTickerOccurrences(Guid[] occurrenceIds, CancellationToken cancellationToken = default)
    {
        var ids = occurrenceIds.Length == 0
            ? ParseGuidSet(await Db.SetMembersAsync(CronOccurrenceIdsKey).ConfigureAwait(false))
            : occurrenceIds;

        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var released = await TryReleaseAsync<CronTickerOccurrenceEntity<TCronTicker>>(CronOccurrenceKey(id)).ConfigureAwait(false);
            if (released == null) continue;

            await IndexManager.AddCronOccurrenceIndexesAsync(released).ConfigureAwait(false);
        }
    }

    public async Task<byte[]> GetCronTickerOccurrenceRequest(Guid tickerId, CancellationToken cancellationToken = default)
    {
        var occurrence = await Serializer.GetAsync<CronTickerOccurrenceEntity<TCronTicker>>(CronOccurrenceKey(tickerId)).ConfigureAwait(false);
        return occurrence?.CronTicker?.Request;
    }

    public async Task UpdateCronTickerOccurrencesWithUnifiedContext(Guid[] cronOccurrenceIds, InternalFunctionContext functionContext, CancellationToken cancellationToken = default)
    {
        var occurrences = await Serializer.LoadByIdsAsync<CronTickerOccurrenceEntity<TCronTicker>>(cronOccurrenceIds, CronOccurrenceKey, cancellationToken).ConfigureAwait(false);

        foreach (var occurrence in occurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyFunctionContextToCronOccurrence(occurrence, functionContext);
            await Serializer.SetAsync(CronOccurrenceKey(occurrence.Id), occurrence).ConfigureAwait(false);
            await IndexManager.AddCronOccurrenceIndexesAsync(occurrence).ConfigureAwait(false);
        }
    }

    public async Task ReleaseDeadNodeOccurrenceResources(string instanceIdentifier, CancellationToken cancellationToken = default)
    {
        var members = await Db.SetMembersAsync(CronOccurrenceIdsKey).ConfigureAwait(false);
        var ids = ParseGuidSet(members);

        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var recovered = await TryRecoverDeadNodeAsync<CronTickerOccurrenceEntity<TCronTicker>>(
                CronOccurrenceKey(id), instanceIdentifier).ConfigureAwait(false);

            if (recovered == null) continue;

            await IndexManager.AddCronOccurrenceIndexesAsync(recovered).ConfigureAwait(false);
        }
    }
    #endregion
}
