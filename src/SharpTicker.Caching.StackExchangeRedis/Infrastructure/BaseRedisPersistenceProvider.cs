#nullable disable
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
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

internal abstract class BaseRedisPersistenceProvider<TTimeTicker, TCronTicker> : IClaimFanOut, IChildSkipWriter, IFencedRunWriter
    where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
    where TCronTicker : CronTickerEntity, new()
{
    // Each claim is one script on one key; the connection multiplexes concurrent calls.
    public ValueTask<int> GetMaxConcurrentClaimsAsync(CancellationToken cancellationToken = default) => new(ClaimFanOut.Default);

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
    private static readonly string SkipNotStartedScript = LuaScriptLoader.Load("SkipNotStarted");
    private static readonly string CompareAndSetScript = LuaScriptLoader.Load("CompareAndSet");

    // Test seams, null in production: called between the read and the set of a compare-and-set, with the key, and after a
    // fenced write landed, before its index update, with the row id. They let a test put a concurrent write into that gap.
    internal Func<string, Task> BeforeCompareAndSetHook { get; set; }
    internal Func<Guid, Task> AfterFencedWriteHook { get; set; }

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
            // A context generated in the application's assembly cannot reach the members the entities fill themselves.
            jsonOptions.TypeInfoResolverChain.Insert(0, redisOptions.JsonSerializerContext.WithEntityMembers<TTimeTicker>());

        // A type neither context knows, a ticker type of the application's own for example, is read through reflection.
        // A trimmed or AOT application has reflection switched off for the serializer and gives its context instead.
        if (JsonSerializer.IsReflectionEnabledByDefault)
            jsonOptions.TypeInfoResolverChain.Add(ReflectionResolver());

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Serializer = new RedisSerializer(db, jsonOptions, _logger);
        IndexManager = new RedisIndexManager<TTimeTicker, TCronTicker>(db, LockHolder);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Called only when JsonSerializer.IsReflectionEnabledByDefault is true.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Called only when JsonSerializer.IsReflectionEnabledByDefault is true.")]
    private static IJsonTypeInfoResolver ReflectionResolver() => new DefaultJsonTypeInfoResolver();

    #region Lua script operations
    // A claim to InProgress (the fallback, run-now). The marker is chosen before the script runs: when the call fails, the
    // script may have landed (a lost reply), so the row is put back with that marker before the failure goes on. When it
    // did not land, the fenced release finds nothing to change.
    protected async Task<T> TryClaimInProgressAsync<T>(string key, Guid id, Func<Guid, DateTime, CancellationToken, Task<bool>> release)
        where T : class
    {
        var claimMarker = Clock.NextClaimMarker();
        try
        {
            return await TryAcquireAsync<T>(key, TickerStatus.InProgress, claimMarker: claimMarker).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            await ClaimPutBack.PutBackOrThrowAsync(id, claimMarker,
                typeof(T) == typeof(TTimeTicker) ? TickerType.TimeTicker : TickerType.CronTickerOccurrence, failure, release).ConfigureAwait(false);
            throw;
        }
    }

    // The text System.Text.Json writes for a DateTime: the round-trip format without the trailing zeros of the fraction,
    // and without the point when no digit is left. The claim of the scheduler loop compares UpdatedAt as text, and a row
    // the application wrote holds this spelling.
    internal static string AsTheSerializerWrites(DateTime value)
    {
        var text = value.ToString("O");
        const int point = 19, end = 27;
        var keep = end;
        while (keep > point + 1 && text[keep - 1] == '0')
            keep--;
        if (keep == point + 1)
            keep = point;
        return string.Concat(text.AsSpan(0, keep), text.AsSpan(end));
    }

    protected async Task<T> TryAcquireAsync<T>(string key, TickerStatus targetStatus, string expectedUpdatedAt = "", DateTime? claimMarker = null,
        string expectedUpdatedAtAsStored = "") where T : class
    {
        // A move to InProgress (the fallback, run-now) carries this node's next claim marker, like every InProgress write.
        var now = claimMarker ?? (targetStatus == TickerStatus.InProgress ? Clock.NextClaimMarker() : Clock.UtcNow);
        var result = await Db.ScriptEvaluateAsync(
            AcquireScript,
            [(RedisKey)key],
            [(RedisValue)LockHolder, (RedisValue)now.ToString("O"), (RedisValue)(int)targetStatus,
             (RedisValue)(expectedUpdatedAt ?? ""), LuaStatusIdle, LuaStatusQueued, (RedisValue)(expectedUpdatedAtAsStored ?? "")]
        ).ConfigureAwait(false);

        if (result.IsNull) return null;
        return Serializer.DeserializeOrNull<T>((string)result, key);
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
        return Serializer.DeserializeOrNull<T>((string)result, key);
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
        return Serializer.DeserializeOrNull<T>((string)result, key);
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
    protected async Task<(bool Claimed, T Row)> TryMarkInProgressAsync<T>(string key, DateTime claimMarker) where T : class
    {
        var result = await Db.ScriptEvaluateAsync(
            MarkInProgressScript,
            [(RedisKey)key],
            [(RedisValue)LockHolder, (RedisValue)claimMarker.ToString("O"), LuaStatusQueued, LuaStatusInProgress]
        ).ConfigureAwait(false);

        if (result.IsNull) return (false, null);
        return (true, Serializer.DeserializeOrNull<T>((string)result, key));
    }

    // Puts back a claim whose job never started. The fallback and the loop find rows through the pending index, so the
    // index entries of the put-back row are written first: while the row is still InProgress they are harmless, Acquire.lua
    // rejects it. Written after the put-back, a failed index write would leave an Idle row that nothing looks at; this way a
    // failure leaves the row InProgress and the caller tries again, and a put-back whose reply was lost is already indexed.
    // The row key and the index keys live in different hash slots, so they cannot share one script.
    protected async Task<bool> TryReleaseClaimAsync<T>(string key, Func<T, bool> isThisClaim, Action<T> asIdle, Func<T, Task> indexAsIdle,
        Func<T, TickerStatus> statusOf)
        where T : class
    {
        // A row that is not this claim is left alone without any write; the compare-and-set below checks again.
        var row = await Serializer.GetAsync<T>(key).ConfigureAwait(false);
        if (row == null || !isThisClaim(row))
            return false;

        asIdle(row);
        await indexAsIdle(row).ConfigureAwait(false);

        // The marker is compared as a date on the deserialized row, not as text: a claim script writes it in one text form
        // and a later write of the row from C# (an inline child's start, an edit while it is claimed) in another.
        var released = await CompareAndSetAsync<T>(key, stored =>
        {
            if (stored == null || !isThisClaim(stored)) return null;
            asIdle(stored);
            return stored;
        }).ConfigureAwait(false);

        if (released != null)
            return true;

        // The row moved on between the read and the set. When it ended, the pending entry written above would stay on it for
        // good, so its index is written again as the row is now (#57), as the fallback does for an ended time ticker. A row
        // that has not ended is left alone: it may be another node's by now, whose entry must not be removed, and its own
        // writes keep its index. A failure here does not undo anything: the fallback drops the entry of an ended row.
        try
        {
            var current = await Serializer.GetAsync<T>(key).ConfigureAwait(false);
            if (current != null && statusOf(current) is not (TickerStatus.Idle or TickerStatus.Queued or TickerStatus.InProgress))
                await indexAsIdle(current).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "A put-back of {Key} found the row changed, and its pending index entry could not be written again.", key);
        }
        return false;
    }

    private const int MaxCompareAndSetRounds = 5;

    // Every C# read-modify-write of a time ticker or occurrence row goes through here, so a write made between the read
    // and the set is never lost. change gets the row as read (null when the stored JSON does not deserialize) and returns
    // the row to write, or null to write nothing; its rules are checked in C# on the deserialized values, so dates are
    // compared as DateTime, never as text. The set lands only while the key still holds the exact text that was read
    // (CompareAndSet.lua); when it changed, the row is read and checked again. create gives the row to write when the key
    // does not exist (SET NX); without it a missing key writes nothing.
    // Returns the row as written, or null when nothing was written: the key is missing, or change refused the row. Rounds
    // that keep conflicting throw: null means the row is gone or does not match, which a caller may act on.
    protected async Task<T> CompareAndSetAsync<T>(string key, Func<T, T> change, Func<T> create = null) where T : class
    {
        for (var round = 0; round < MaxCompareAndSetRounds; round++)
        {
            var read = await Db.StringGetAsync(key).ConfigureAwait(false);
            if (read.IsNull)
            {
                var created = create?.Invoke();
                if (created == null) return null;
                if (await Serializer.TryCreateAsync(key, created).ConfigureAwait(false)) return created;
                continue;
            }

            var raw = (string)read;
            var row = change(Serializer.DeserializeOrNull<T>(raw, key));
            if (row == null) return null;
            var payload = Serializer.Serialize(row);

            if (BeforeCompareAndSetHook != null)
                await BeforeCompareAndSetHook(key).ConfigureAwait(false);

            var result = (int)await Db.ScriptEvaluateAsync(CompareAndSetScript, [(RedisKey)key], [(RedisValue)raw, (RedisValue)payload])
                .ConfigureAwait(false);
            if (result == 1) return row;
            if (result == -1 && create == null) return null;

            // Another writer changed the key in between: wait a short, random moment before reading it again, so writers
            // that keep meeting each other spread out instead of using up their rounds together.
            if (round + 1 < MaxCompareAndSetRounds)
                await Task.Delay(Random.Shared.Next(1, 4 << round)).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"Redis key '{key}' kept changing while it was being written ({MaxCompareAndSetRounds} rounds); it was not written.");
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
        Action<DateTime> setUpdatedAt,
        DateTime updatedAt)
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

        setUpdatedAt(updatedAt);
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

    // updatedAt: the run marker for a fenced write of a run, which keeps it; now for any other write.
    protected void ApplyFunctionContextToTicker(TTimeTicker ticker, InternalFunctionContext context, DateTime? updatedAt = null)
    {
        ApplyFunctionContext(context,
            status => ticker.Status = status,
            reason => ticker.SkippedReason = reason,
            at => ticker.ExecutedAt = at,
            msg => ticker.ExceptionMessage = msg,
            elapsed => ticker.ElapsedTime = elapsed,
            count => ticker.RetryCount = count,
            () => { ticker.LockHolder = null; ticker.LockedAt = null; },
            at => ticker.UpdatedAt = at,
            updatedAt ?? Clock.UtcNow);
    }

    protected void ApplyFunctionContextToCronOccurrence(CronTickerOccurrenceEntity<TCronTicker> occurrence, InternalFunctionContext context, DateTime? updatedAt = null)
    {
        ApplyFunctionContext(context,
            status => occurrence.Status = status,
            reason => occurrence.SkippedReason = reason,
            at => occurrence.ExecutedAt = at,
            msg => occurrence.ExceptionMessage = msg,
            elapsed => occurrence.ElapsedTime = elapsed,
            count => occurrence.RetryCount = count,
            () => { occurrence.LockHolder = null; occurrence.LockedAt = null; },
            at => occurrence.UpdatedAt = at,
            updatedAt ?? Clock.UtcNow);
    }

    #region Chain rows
    // Chains are at most root, child, grandchild; the bound only guards against a corrupt link loop.
    protected const int MaxChainDepth = 4;

    // Each chain node is a row of its own, linked to its parent through the parent's children set. A row written by an
    // older version holds its children nested and has no children set; it is returned as it was stored.
    protected async Task AttachChildrenAsync(IReadOnlyCollection<TTimeTicker> rows, CancellationToken cancellationToken)
    {
        var level = rows.Where(r => r.Children == null || r.Children.Count == 0).ToList();

        for (var depth = 0; level.Count > 0 && depth < MaxChainDepth; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var order = await ReadChildOrderAsync(level.Select(r => r.Id)).ConfigureAwait(false);
            var childIds = order.Keys.ToArray();
            if (childIds.Length == 0) return;

            var byParent = (await Serializer.LoadByIdsAsync<TTimeTicker>(childIds, TimeTickerKey, cancellationToken).ConfigureAwait(false))
                .Where(c => c.ParentId.HasValue)
                .ToLookup(c => c.ParentId.Value);

            var next = new List<TTimeTicker>();
            foreach (var row in level.Where(r => byParent.Contains(r.Id)))
            {
                row.Children = InChildOrder(byParent[row.Id], order);
                next.AddRange(row.Children);
            }
            level = next;
        }
    }

    // Each child's position under its parent, read from the parents' children sets in one pipelined round trip.
    private async Task<Dictionary<Guid, int>> ReadChildOrderAsync(IEnumerable<Guid> parentIds)
    {
        var reads = parentIds.Select(id => Db.SortedSetRangeByRankAsync(TimeTickerChildrenKey(id))).ToArray();
        var order = new Dictionary<Guid, int>();
        foreach (var members in await Task.WhenAll(reads).ConfigureAwait(false))
        {
            var ids = ParseGuidSet(members);
            for (var i = 0; i < ids.Length; i++)
                order.TryAdd(ids[i], i);
        }
        return order;
    }

    // The children of the row with this id. The claim paths read them before the row is claimed: a failed read then
    // leaves nothing claimed.
    protected async Task<ICollection<TTimeTicker>> LoadChildrenAsync(Guid id, CancellationToken cancellationToken)
    {
        var holder = new TTimeTicker { Id = id };
        await AttachChildrenAsync([holder], cancellationToken).ConfigureAwait(false);
        return holder.Children;
    }

    // For reads that load every row anyway: links them by ParentId, reading only the order of the children.
    protected async Task LinkLoadedChildrenAsync(IReadOnlyCollection<TTimeTicker> rows)
    {
        var byParent = rows.Where(r => r.ParentId.HasValue).ToLookup(r => r.ParentId.Value);
        var parents = rows.Where(r => byParent.Contains(r.Id)).ToList();
        if (parents.Count == 0) return;

        var order = await ReadChildOrderAsync(parents.Select(p => p.Id)).ConfigureAwait(false);
        foreach (var row in parents)
            row.Children = InChildOrder(byParent[row.Id], order);
    }

    // A claimed row as the handler gets it, with its children as rows. A row that still holds them nested (older format)
    // is mapped as it is; the claim paths write such children as rows first (SplitNestedChainAsync).
    protected static TimeTickerEntity ForQueue(TTimeTicker row, ICollection<TTimeTicker> children)
    {
        if (HoldsNestedChildren(row))
            return MapForQueue(row, childrenAreRows: false);
        if (children.Count > 0)
            row.Children = children;
        return MapForQueue(row, childrenAreRows: true);
    }

    // A row written by an older version holds its children nested; they have no rows of their own.
    protected static bool HoldsNestedChildren(TTimeTicker row) => row.Children is { Count: > 0 };

    // A chain stored by a version before 1.3.0 is one record: the children are held inside the parent's row. A child is
    // started by a claim on its own row, so such a child could not start. The node that has claimed the parent writes the
    // nodes as rows of their own before the parent is handed out. Only a claimed parent is written: an ended one is never
    // claimed, so its children stay inside its row and do not run.
    // Returns the children as rows, to hand out. Null when the parent must not be handed out: a write failed and the
    // claim was put back through putBack (the next pass starts again; every step can be repeated), or the claim was lost
    // on the way.
    protected async Task<ICollection<TTimeTicker>> SplitNestedChainAsync(TTimeTicker claimed, Func<Exception, Task> putBack)
    {
        try
        {
            await WriteNestedChildrenAsRowsAsync(claimed).ConfigureAwait(false);

            // The parent's row gives up its copy of the children, only while it is still this claim, and keeps its
            // UpdatedAt: that is the claim's marker.
            var written = await CompareAndSetAsync<TTimeTicker>(TimeTickerKey(claimed.Id), row =>
            {
                if (row == null || row.Status != claimed.Status || row.LockHolder != LockHolder || row.UpdatedAt != claimed.UpdatedAt) return null;
                row.Children = new List<TTimeTicker>();
                return row;
            }).ConfigureAwait(false);

            if (written == null)
            {
                _logger.LogDebug("Chain {Id} is not handed out: its claim was lost while its children were written as rows of their own.", claimed.Id);
                return null;
            }

            claimed.Children = new List<TTimeTicker>();
            var children = await LoadChildrenAsync(claimed.Id, CancellationToken.None).ConfigureAwait(false);
            _logger.LogDebug("Chain {Id} was stored with its children inside its row (a version before 1.3.0); they were written as rows of their own.", claimed.Id);
            return children;
        }
        catch (Exception failure)
        {
            // Logged first: when the put-back fails too, its exception leaves here and this one would be lost.
            _logger.LogWarning(failure, "Chain {Id} is stored with its children inside its row (a version before 1.3.0) and writing them as rows of their own failed; its claim is put back and the next pass tries again.", claimed.Id);
            await putBack(failure).ConfigureAwait(false);
            return null;
        }
    }

    // Each node below this one, in the order WriteChainAsync uses: the link to its parent, then its own children, then
    // its row. The row is written only when there is none (SET NX): one that exists is the child, whatever the copy inside
    // the parent says, and it may have run. The index entries are added from the row the key holds, and never removed.
    private async Task WriteNestedChildrenAsRowsAsync(TTimeTicker parent)
    {
        var position = 0;
        foreach (var child in (parent.Children ?? []).Where(c => c != null))
        {
            await IndexManager.AddTimeTickerChildAsync(parent.Id, child.Id, position++).ConfigureAwait(false);
            await WriteNestedChildrenAsRowsAsync(child).ConfigureAwait(false);

            child.ParentId = parent.Id;
            child.Children = new List<TTimeTicker>();
            var key = TimeTickerKey(child.Id);
            var row = await Serializer.TryCreateAsync(key, child).ConfigureAwait(false)
                ? child
                : await Serializer.GetAsync<TTimeTicker>(key).ConfigureAwait(false);
            if (row == null) continue;

            await IndexManager.AddTimeTickerIndexesAddOnlyAsync(row).ConfigureAwait(false);
            if (RedisIndexManager<TTimeTicker, TCronTicker>.WaitsForItsInlineStart(row))
                await IndexManager.AddWaitingChildrenAsync([new HashEntry(row.Id.ToString(), row.ParentId.Value.ToString())]).ConfigureAwait(false);
        }
    }

    private Func<Exception, Task> PutBackTimeTickerClaim(TTimeTicker claimed)
        => failure => ClaimPutBack.PutBackOrThrowAsync(claimed.Id, claimed.UpdatedAt, TickerType.TimeTicker, failure, TryReleaseTimeTickerClaim);

    // The status of each parent of the given rows, for the chain gate. A parent that is not found is left out, so its
    // children wait.
    protected async Task<Dictionary<Guid, TickerStatus>> LoadParentStatusesAsync(IEnumerable<TTimeTicker> rows, CancellationToken cancellationToken)
    {
        var ids = rows.Where(r => r?.ParentId != null).Select(r => r.ParentId.Value).Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, TickerStatus>();
        var parents = await Serializer.LoadByIdsAsync<TTimeTicker>(ids, TimeTickerKey, cancellationToken).ConfigureAwait(false);
        return parents.ToDictionary(p => p.Id, p => p.Status);
    }

    // A row may be handed out on its own when it has no parent, or when it is a delayed child (an execution time of its
    // own) whose parent's status lets it run (ChainRunGate). A child without a time of its own runs only inside its
    // parent's run. A finished status never changes back, so the gate read before a claim still holds at the claim.
    protected static bool ChainGateOpen(TTimeTicker row, Dictionary<Guid, TickerStatus> parentStatuses)
        => row.ParentId == null
           || (row.ExecutionTime.HasValue
               && parentStatuses.TryGetValue(row.ParentId.Value, out var status)
               && ChainRunGate.Allows(row.RunCondition, status));

    // The gate of a claim by id (ChainRunGate.AllowsRunNow): unlike the window and the fallback, it also hands out a child
    // without a time of its own, once its parent has ended with an outcome it runs on.
    protected static bool RunNowGateOpen(TTimeTicker row, Dictionary<Guid, TickerStatus> parentStatuses)
        => row.ParentId == null
           || (parentStatuses.TryGetValue(row.ParentId.Value, out var status)
               && ChainRunGate.AllowsRunNow(row.RunCondition, status, row.ExecutionTime.HasValue));

    // A delayed child whose parent ended with an outcome it does not run on never will.
    protected static bool ParentEndedWithoutMatch(TTimeTicker row, Dictionary<Guid, TickerStatus> parentStatuses)
        => row.ParentId != null
           && row.ExecutionTime.HasValue
           && parentStatuses.TryGetValue(row.ParentId.Value, out var status)
           && ChainRunGate.IsFinal(status)
           && !ChainRunGate.Allows(row.RunCondition, status);

    // IChildSkipWriter: only a row that has not started is skipped (SkipNotStarted.lua), so a delayed child this store
    // already handed out keeps the status of its run.
    public async Task SkipNotStartedTimeTickersAsync(Guid[] timeTickerIds, InternalFunctionContext skip, CancellationToken cancellationToken = default)
    {
        var now = Clock.UtcNow;
        foreach (var id in timeTickerIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SkipNotStartedAsync(id, skip.ExceptionDetails ?? ChainRunGate.NotMatchedReason, skip.ExecutedAt == default ? now : skip.ExecutedAt, now).ConfigureAwait(false);
        }
    }

    private async Task SkipNotStartedAsync(Guid id, string reason, DateTime executedAt, DateTime now)
    {
        var result = await Db.ScriptEvaluateAsync(
            SkipNotStartedScript,
            [(RedisKey)TimeTickerKey(id)],
            [LuaStatusIdle, LuaStatusQueued, (RedisValue)(int)TickerStatus.Skipped, (RedisValue)reason,
             (RedisValue)executedAt.ToString("O"), (RedisValue)now.ToString("O")]
        ).ConfigureAwait(false);

        if (result.IsNull) return;

        // The row key and the index keys live in different hash slots; a stale pending entry is harmless, Acquire.lua
        // rejects a skipped row.
        var skipped = Serializer.DeserializeOrNull<TTimeTicker>((string)result, TimeTickerKey(id));
        if (skipped != null)
            await IndexManager.AddTimeTickerIndexesAsync(skipped).ConfigureAwait(false);
    }

    // A child missing from the order (its link was dropped) goes last.
    private static List<TTimeTicker> InChildOrder(IEnumerable<TTimeTicker> children, Dictionary<Guid, int> order)
        => children.OrderBy(c => order.TryGetValue(c.Id, out var position) ? position : int.MaxValue)
            .ThenBy(c => c.CreatedAt).ThenBy(c => c.Id).ToList();
    #endregion

    // A delayed child (one with an execution time of its own) is carried with its time, so the handler leaves it to the
    // store. Children still nested in a row of an older version have no rows yet (a read before the claim; the claim
    // writes them as rows, see SplitNestedChainAsync): they are carried without their time.
    protected static TimeTickerEntity MapForQueue(TTimeTicker ticker, bool childrenAreRows)
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
                ExecutionTime = childrenAreRows ? ch.ExecutionTime : null,
                Children = (ch.Children ?? []).Select(gch => new TimeTickerEntity
                {
                    Id = gch.Id,
                    Function = gch.Function,
                    Retries = gch.Retries,
                    RetryIntervals = gch.RetryIntervals,
                    RunCondition = gch.RunCondition,
                    ParentId = gch.ParentId,
                    ExecutionTime = childrenAreRows ? gch.ExecutionTime : null
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
                timeTicker.UpdatedAt.ToString("O"),
                expectedUpdatedAtAsStored: AsTheSerializerWrites(timeTicker.UpdatedAt)).ConfigureAwait(false);

            if (acquired == null) continue;

            if (HoldsNestedChildren(acquired))
            {
                var children = await SplitNestedChainAsync(acquired, _ => ReleaseAcquiredTimeTickers([acquired.Id], CancellationToken.None)).ConfigureAwait(false);
                if (children == null) continue;
                timeTicker.Children = ForQueue(acquired, children).Children;
            }

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
        var now = Clock.UtcNow;
        var threshold = now.AddSeconds(-1);

        var dueIds = ParseGuidSet(await Db.SortedSetRangeByScoreAsync(TimeTickerPendingKey, double.NegativeInfinity, ToScore(threshold)).ConfigureAwait(false));

        // Rows claimed in this pass: their delayed children wait for their run, also when read on a later page.
        var claimedThisPass = new HashSet<Guid>();

        foreach (var page in dueIds.Chunk(FallbackPage))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A delayed child is claimed only once its parent's status lets it run; one whose parent ended without a match
            // is skipped; one whose parent has not ended waits. The parents are read before the page's claims.
            var rows = (await Serializer.LoadByIdsAsync<TTimeTicker>(page, TimeTickerKey, cancellationToken).ConfigureAwait(false))
                .ToDictionary(r => r.Id);
            var parentStatuses = await LoadParentStatusesAsync(rows.Values, cancellationToken).ConfigureAwait(false);
            foreach (var claimedParent in claimedThisPass)
                parentStatuses.Remove(claimedParent);

            await DropEndedPendingEntriesAsync(rows.Values, cancellationToken).ConfigureAwait(false);
            await SkipChildrenWhoseParentEndedWithoutMatchAsync(rows.Values, parentStatuses, now, cancellationToken).ConfigureAwait(false);

            foreach (var id in page)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // A row that does not deserialize still goes to the claim script, which rewrites it in a readable form;
                // its gate is checked on the row the script returns.
                if (rows.TryGetValue(id, out var row) && !ChainGateOpen(row, parentStatuses)) continue;

                var children = await LoadChildrenAsync(id, cancellationToken).ConfigureAwait(false);
                var acquired = await TryClaimInProgressAsync<TTimeTicker>(TimeTickerKey(id), id, TryReleaseTimeTickerClaim).ConfigureAwait(false);

                if (acquired == null) continue;
                if (row == null && !await KeepClaimOfUnreadRowAsync(acquired, claimedThisPass).ConfigureAwait(false)) continue;

                if (HoldsNestedChildren(acquired))
                {
                    children = await SplitNestedChainAsync(acquired, PutBackTimeTickerClaim(acquired)).ConfigureAwait(false);
                    if (children == null) continue;
                }

                claimedThisPass.Add(acquired.Id);
                await TryUpdateIndexAfterClaim(acquired.Id, () => IndexManager.AddTimeTickerIndexesAsync(acquired)).ConfigureAwait(false);

                yield return ForQueue(acquired, children);
            }
        }

        // A child without a time of its own is started by its parent's run. One that still waits some time after its
        // parent ended was never started (the node stopped, or its start claim failed): it is claimed here when the
        // parent's outcome lets it run, and skipped when not. Its claim is the one of the inline start, so one of them wins.
        await IndexWaitingChildrenOnceAsync(cancellationToken).ConfigureAwait(false);
        var waitingByParent = (await IndexManager.GetWaitingChildrenAsync().ConfigureAwait(false)).ToLookup(x => x.ParentId, x => x.ChildId);

        foreach (var page in waitingByParent.Select(x => x.Key).Chunk(FallbackPage))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var endedParents = (await Serializer.LoadByIdsAsync<TTimeTicker>(page, TimeTickerKey, cancellationToken).ConfigureAwait(false))
                .Where(parent => ChainRunGate.FallbackMayDecideChildWithoutTime(parent.Status, parent.ExecutedAt, now))
                .ToDictionary(parent => parent.Id, parent => parent.Status);
            if (endedParents.Count == 0) continue;

            var entries = endedParents.Keys.SelectMany(parentId => waitingByParent[parentId]).ToArray();
            var leftWaiting = await Serializer.LoadByIdsAsync<TTimeTicker>(entries, TimeTickerKey, cancellationToken).ConfigureAwait(false);

            foreach (var child in leftWaiting)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The index is written after the row and may be behind it: the row decides, and its entry is corrected.
                if (!RedisIndexManager<TTimeTicker, TCronTicker>.WaitsForItsInlineStart(child) || !endedParents.TryGetValue(child.ParentId.Value, out var parentStatus))
                {
                    await TryUpdateIndexAfterClaim(child.Id, () => IndexManager.AddTimeTickerIndexesAsync(child)).ConfigureAwait(false);
                    continue;
                }

                if (!ChainRunGate.AllowsRunNow(child.RunCondition, parentStatus, hasOwnTime: false))
                {
                    try
                    {
                        await SkipNotStartedAsync(child.Id, ChainRunGate.NotMatchedReason, now, now).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning(ex, "Skipping chain child {Id} without a time whose ended parent does not let it run failed; the next fallback pass tries again.", child.Id);
                    }

                    continue;
                }

                var claimMarker = Clock.NextClaimMarker();
                TTimeTicker acquired;
                try
                {
                    acquired = await ClaimWaitingChildAsync(child.Id, claimMarker, now).ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    await ClaimPutBack.PutBackOrThrowAsync(child.Id, claimMarker, TickerType.TimeTicker, failure, TryReleaseTimeTickerClaim).ConfigureAwait(false);
                    throw;
                }

                if (acquired == null) continue;

                await TryUpdateIndexAfterClaim(acquired.Id, () => IndexManager.AddTimeTickerIndexesAsync(acquired)).ConfigureAwait(false);
                yield return ForQueue(acquired, await LoadChildrenAsync(acquired.Id, cancellationToken).ConfigureAwait(false));
            }
        }
    }

    private bool _waitingChildrenIndexed;

    // Once per process, on its first fallback pass: every child that waits for its inline start gets its entry in the
    // waiting-children index. Rows stored by a version before 1.4.0 have none, and an index write can fail after its row
    // was written. A failure does not stop the pass; the next pass tries again.
    private async Task IndexWaitingChildrenOnceAsync(CancellationToken cancellationToken)
    {
        if (_waitingChildrenIndexed) return;

        try
        {
            var ids = ParseGuidSet(await Db.SetMembersAsync(TimeTickerIdsKey).ConfigureAwait(false));
            foreach (var page in ids.Chunk(FallbackPage))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var rows = await Serializer.LoadByIdsAsync<TTimeTicker>(page, TimeTickerKey, cancellationToken).ConfigureAwait(false);
                await IndexManager.AddWaitingChildrenAsync(rows
                    .Where(RedisIndexManager<TTimeTicker, TCronTicker>.WaitsForItsInlineStart)
                    .Select(row => new HashEntry(row.Id.ToString(), row.ParentId.Value.ToString()))
                    .ToArray()).ConfigureAwait(false);
            }

            _waitingChildrenIndexed = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Indexing the chain children that wait for their start failed; the next fallback pass tries again.");
        }
    }

    // The claim of a child that still waits for its start (see IFencedRunWriter.StartInlineChildAsync); null when refused.
    // The check runs again on every round of the compare-and-set, so a claim that lost the row to another one is refused.
    private Task<TTimeTicker> ClaimWaitingChildAsync(Guid timeTickerId, DateTime claimMarker, DateTime now)
        => CompareAndSetAsync<TTimeTicker>(TimeTickerKey(timeTickerId), row =>
        {
            if (row == null) return null;
            if (row.Status != TickerStatus.Idle || !string.IsNullOrEmpty(row.LockHolder) || row.ExecutionTime != null) return null;
            row.Status = TickerStatus.InProgress;
            row.LockHolder = LockHolder;
            row.LockedAt = now;
            row.UpdatedAt = claimMarker;
            return row;
        });

    private const int FallbackPage = 256;

    // A pending entry whose row is no longer Idle or Queued (an index write that never landed after a skip or a run) is
    // dropped: the row never runs again. Its status is final for every node, so the drop does not depend on the lock.
    private async Task DropEndedPendingEntriesAsync(IEnumerable<TTimeTicker> rows, CancellationToken cancellationToken)
    {
        foreach (var row in rows.Where(r => r.Status is not (TickerStatus.Idle or TickerStatus.Queued or TickerStatus.InProgress)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await TryUpdateIndexAfterClaim(row.Id, () => IndexManager.AddTimeTickerIndexesAsync(row)).ConfigureAwait(false);
        }
    }

    // The parent's run skips a delayed child its outcome does not let run; this covers a node that stopped before doing
    // so. A failure here must not stop the pass from taking the overdue jobs: the rows it would skip can never be
    // claimed, and the next pass tries again. Their own delayed children follow on a later pass.
    private async Task SkipChildrenWhoseParentEndedWithoutMatchAsync(IEnumerable<TTimeTicker> rows, Dictionary<Guid, TickerStatus> parentStatuses,
        DateTime now, CancellationToken cancellationToken)
    {
        var ids = rows.Where(r => r.Status is TickerStatus.Idle or TickerStatus.Queued && ParentEndedWithoutMatch(r, parentStatuses))
            .Select(r => r.Id).ToArray();
        if (ids.Length == 0) return;

        try
        {
            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SkipNotStartedAsync(id, ChainRunGate.NotMatchedReason, now, now).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Skipping delayed chain children whose parent's outcome does not match failed; the next fallback pass tries again.");
        }
    }

    // A row read only after its claim (it did not deserialize before) is kept when it may run on its own; a delayed child
    // whose gate is closed is put back for a later pass, and a child without a time of its own is put back too.
    private async Task<bool> KeepClaimOfUnreadRowAsync(TTimeTicker acquired, ICollection<Guid> claimedThisPass)
    {
        if (acquired.ParentId == null) return true;

        var parentStatuses = await LoadParentStatusesAsync([acquired], CancellationToken.None).ConfigureAwait(false);
        if (!claimedThisPass.Contains(acquired.ParentId.Value) && ChainGateOpen(acquired, parentStatuses)) return true;

        await ClaimPutBack.PutBackOrThrowAsync(acquired.Id, acquired.UpdatedAt, TickerType.TimeTicker,
            new InvalidOperationException("A chain child was claimed before its gate was checked."), TryReleaseTimeTickerClaim).ConfigureAwait(false);
        return false;
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

    public async Task<bool> TryMarkTimeTickerInProgress(Guid id, DateTime claimMarker, CancellationToken cancellationToken = default)
    {
        var (claimed, row) = await TryMarkInProgressAsync<TTimeTicker>(TimeTickerKey(id), claimMarker).ConfigureAwait(false);
        if (!claimed) return false;

        if (row != null)
            await TryUpdateIndexAfterClaim(id, () => IndexManager.AddTimeTickerIndexesAsync(row)).ConfigureAwait(false);
        return true;
    }

    public Task<bool> TryReleaseTimeTickerClaim(Guid id, DateTime claimMarker, CancellationToken cancellationToken = default)
        => TryReleaseClaimAsync<TTimeTicker>(
            TimeTickerKey(id),
            row => row.Status == TickerStatus.InProgress && row.LockHolder == LockHolder && row.UpdatedAt == claimMarker,
            row => { row.Status = TickerStatus.Idle; row.LockHolder = null; row.LockedAt = null; row.UpdatedAt = Clock.UtcNow; },
            row => IndexManager.AddTimeTickerIndexesAsync(row),
            row => row.Status);

    public async Task<TimeTickerEntity[]> GetEarliestTimeTickers(CancellationToken cancellationToken = default)
    {
        var now = Clock.UtcNow;
        var oneSecondAgoScore = ToScore(now.AddSeconds(-1));

        // The first entry whose gate is open defines the window. A delayed child that still waits for its parent must not
        // hide the rows due after it, so the scan reads past such entries, a page at a time and up to a limit. Rows past
        // the limit are left to the next read, or to the fallback once overdue.
        double? earliestScore = null;
        for (long offset = 0; earliestScore == null && offset < WindowScanLimit; offset += WindowScanPage)
        {
            var page = await Db.SortedSetRangeByScoreWithScoresAsync(TimeTickerPendingKey, oneSecondAgoScore, double.PositiveInfinity,
                Exclude.None, Order.Ascending, offset, WindowScanPage).ConfigureAwait(false);
            if (page.Length == 0) return [];

            var pageRows = await LoadGatedAsync(page.Select(e => e.Element), cancellationToken).ConfigureAwait(false);
            foreach (var entry in page)
            {
                if (Guid.TryParse(entry.Element.ToString(), out var id) && pageRows.ContainsKey(id))
                {
                    earliestScore = entry.Score;
                    break;
                }
            }
        }
        if (earliestScore == null) return [];

        var minSecond = new DateTime((long)earliestScore.Value, DateTimeKind.Utc);
        var maxScore = ToScore(minSecond.AddSeconds(1));

        var entries = await Db.SortedSetRangeByScoreAsync(TimeTickerPendingKey, earliestScore.Value, maxScore).ConfigureAwait(false);
        var open = (await LoadGatedAsync(entries, cancellationToken).ConfigureAwait(false)).Values.ToList();

        var nested = open.Where(HoldsNestedChildren).Select(r => r.Id).ToHashSet();
        await AttachChildrenAsync(open, cancellationToken).ConfigureAwait(false);

        return open.Select(r => MapForQueue(r, childrenAreRows: !nested.Contains(r.Id))).ToArray();
    }

    private const int WindowScanPage = 64;
    private const int WindowScanLimit = 1024;

    // The rows of these pending entries that this node may claim and whose chain gate is open.
    private async Task<Dictionary<Guid, TTimeTicker>> LoadGatedAsync(IEnumerable<RedisValue> entries, CancellationToken cancellationToken)
    {
        var rows = (await Serializer.LoadByIdsAsync<TTimeTicker>(ParseGuidSet(entries.ToArray()), TimeTickerKey, cancellationToken).ConfigureAwait(false))
            .Where(t => CanAcquire(t.Status, t.LockHolder, LockHolder))
            .ToList();
        var parentStatuses = await LoadParentStatusesAsync(rows, cancellationToken).ConfigureAwait(false);
        return rows.Where(r => ChainGateOpen(r, parentStatuses)).ToDictionary(r => r.Id);
    }

    public async Task<int> UpdateTimeTicker(InternalFunctionContext functionContexts, CancellationToken cancellationToken = default)
    {
        if (!HasApplicableProps(functionContexts)) return 0;

        var ticker = await CompareAndSetAsync<TTimeTicker>(TimeTickerKey(functionContexts.TickerId), row =>
        {
            if (row != null) ApplyFunctionContextToTicker(row, functionContexts);
            return row;
        }).ConfigureAwait(false);
        if (ticker == null) return 0;

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
        foreach (var id in timeTickerIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ticker = await CompareAndSetAsync<TTimeTicker>(TimeTickerKey(id), row =>
            {
                if (row != null) ApplyFunctionContextToTicker(row, functionContext);
                return row;
            }).ConfigureAwait(false);
            if (ticker != null)
                await IndexManager.AddTimeTickerIndexesAsync(ticker).ConfigureAwait(false);
        }
    }

    public async Task<TimeTickerEntity[]> AcquireImmediateTimeTickersAsync(Guid[] ids, CancellationToken cancellationToken = default)
    {
        if (ids == null || ids.Length == 0) return [];

        var acquired = new List<TimeTickerEntity>();
        var won = new Dictionary<Guid, DateTime>(ids.Length);
        try
        {
            foreach (var id in ids)
            {
                // A cancelled caller stops the loop: a row won here is already InProgress and must be returned to be
                // dispatched, the rows not written yet stay Idle for the fallback.
                if (cancellationToken.IsCancellationRequested) break;

                // A chain child is handed out by id only when its parent's status lets it run (RunNowGateOpen); a finished
                // status never changes back, so the gate read here holds at the claim. A row that does not deserialize
                // still goes to the claim script, which rewrites it in a readable form.
                var stored = await Serializer.GetAsync<TTimeTicker>(TimeTickerKey(id)).ConfigureAwait(false);
                if (stored?.ParentId != null
                    && !RunNowGateOpen(stored, await LoadParentStatusesAsync([stored], CancellationToken.None).ConfigureAwait(false)))
                    continue;

                // Not cancelled by the caller's token: a cancel here must not turn the rows already won into a failure.
                var children = await LoadChildrenAsync(id, CancellationToken.None).ConfigureAwait(false);
                var ticker = await TryClaimInProgressAsync<TTimeTicker>(TimeTickerKey(id), id, TryReleaseTimeTickerClaim).ConfigureAwait(false);

                if (ticker == null) continue;
                if (stored == null && !await KeepClaimOfUnreadRowAsync(ticker, Array.Empty<Guid>()).ConfigureAwait(false)) continue;

                if (HoldsNestedChildren(ticker))
                {
                    children = await SplitNestedChainAsync(ticker, PutBackTimeTickerClaim(ticker)).ConfigureAwait(false);
                    if (children == null) continue;
                }

                won[ticker.Id] = ticker.UpdatedAt;
                await TryUpdateIndexAfterClaim(ticker.Id, () => IndexManager.AddTimeTickerIndexesAsync(ticker)).ConfigureAwait(false);
                acquired.Add(ForQueue(ticker, children));
            }

            return acquired.ToArray();
        }
        catch (Exception failure) when (won.Count > 0)
        {
            // The caller gets no rows to run, so the rows won so far would stay InProgress until this node restarts.
            await ClaimPutBack.PutBackOrThrowAsync(won, TickerType.TimeTicker, failure, TryReleaseTimeTickerClaim).ConfigureAwait(false);
            throw;
        }
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

    // What an earlier run of that node left locked: only rows it locked before lockedBefore, so a row this run has claimed
    // since keeps its lock. Without this override the interface's default body would ignore lockedBefore and release every
    // row of the node, the running jobs of this run too.
    public Task ReleaseDeadNodeTimeTickerResources(string instanceIdentifier, DateTime lockedBefore, CancellationToken cancellationToken = default)
    {
        var cutoff = EarlierRunCutoff(lockedBefore);
        return ReleaseEarlierRunRowsAsync<TTimeTicker>(TimeTickerIdsKey, TimeTickerKey, row => row.Id,
            row => IsLeftByEarlierRun(row.LockHolder, row.LockedAt, row.Status, instanceIdentifier, cutoff),
            row =>
            {
                row.LockHolder = null;
                row.LockedAt = null;
                row.Status = TickerStatus.Idle;
                row.UpdatedAt = Clock.UtcNow;
            },
            IndexManager.AddTimeTickerIndexesAsync, cancellationToken);
    }

    // The index entries of a row are written before the row itself, as in TryReleaseClaimAsync: the fallback and the loop
    // find rows through the pending index, so a row set Idle whose index write then failed would be a row nothing looks
    // at, and a later pass would no longer see it as held. Written first, a failure leaves the row held and the next pass
    // does both again; an entry for a row that turns out to be claimed by this run is harmless, Acquire.lua rejects it.
    // The row is then written through the compare-and-set, which checks the rule again on what the key holds at that
    // moment, so a claim made since the read is kept.
    // One row that fails does not keep the others held: it is logged, the pass goes on, and the pass fails at its end so
    // the caller tries again. A store that cannot be reached fails the pass at once.
    private async Task ReleaseEarlierRunRowsAsync<T>(string idsKey, Func<Guid, string> keyOf, Func<T, Guid> idOf,
        Func<T, bool> isLeftByEarlierRun, Action<T> asIdle, Func<T, Task> indexAsIdle, CancellationToken cancellationToken) where T : class
    {
        var ids = ParseGuidSet(await Db.SetMembersAsync(idsKey).ConfigureAwait(false));
        var failed = 0;
        Exception firstFailure = null;

        foreach (var page in ids.Chunk(FallbackPage))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rows = await Serializer.LoadByIdsAsync<T>(page, keyOf, cancellationToken).ConfigureAwait(false);
            foreach (var row in rows.Where(isLeftByEarlierRun))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var id = idOf(row);
                try
                {
                    asIdle(row);
                    await indexAsIdle(row).ConfigureAwait(false);

                    await CompareAndSetAsync<T>(keyOf(id), current =>
                    {
                        if (current == null || !isLeftByEarlierRun(current))
                            return null;

                        asIdle(current);
                        return current;
                    }).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or JsonException or NotSupportedException)
                {
                    // The row's own trouble (it kept changing, it does not serialize), not the store's.
                    failed++;
                    firstFailure ??= ex;
                    _logger.LogWarning(ex, "Row {Id}, left locked by an earlier run of this node, could not be released.", id);
                }
            }
        }

        if (firstFailure != null)
            throw new InvalidOperationException($"{failed} row(s) an earlier run of this node left locked could not be released; the first failure is attached.", firstFailure);
    }

    // A fallback or run-now claim stores its claim marker as LockedAt, and a marker is cut to the millisecond. Cut the
    // start of the run the same way, or a claim made in the millisecond the run started would count as an earlier run's.
    private static DateTime EarlierRunCutoff(DateTime runStartedAt)
        => new(runStartedAt.Ticks - runStartedAt.Ticks % TimeSpan.TicksPerMillisecond, runStartedAt.Kind);

    // The rows RecoverDeadNode.lua releases (held by the node; Idle, Queued or InProgress), narrowed to those locked before
    // the cutoff. A row without LockedAt is left alone: nothing says it is older than this run.
    private static bool IsLeftByEarlierRun(string lockHolder, DateTime? lockedAt, TickerStatus status, string node, DateTime cutoff)
        => lockHolder == node
           && status is TickerStatus.Idle or TickerStatus.Queued or TickerStatus.InProgress
           && lockedAt is { } at && at < cutoff;
    #endregion

    #region Core_Cron_Ticker_Methods
    public async Task MigrateDefinedCronTickers((string Function, string Expression)[] cronTickers, CancellationToken cancellationToken = default)
    {
        var now = Clock.UtcNow;

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
        // A function can have several crons (a user's next to the seeded one, or a seeded copy from two nodes starting
        // together): grouped, not one per function, which threw and stopped the host on every start.
        var existingByFunction = existingList
            .Where(c => !orphanedSet.Contains(c.Id))
            .GroupBy(c => c.Function, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<TCronTicker>)g.ToList(), StringComparer.Ordinal);

        foreach (var (function, expression) in cronTickers)
        {
            if (existingByFunction.TryGetValue(function, out var rows))
            {
                // Copies of the seeded cron, left by nodes that started together before the seeded id was fixed.
                var copies = SeededCronTickers.Copies(rows);
                if (copies.Count > 0)
                {
                    foreach (var copy in copies)
                    {
                        await IndexManager.RemoveCronIndexesAsync(copy.Id).ConfigureAwait(false);
                        await IndexManager.RemoveCronOccurrencesByParentAsync(copy.Id).ConfigureAwait(false);
                        await Db.KeyDeleteAsync(CronKey(copy.Id)).ConfigureAwait(false);
                    }

                    rows = rows.Except(copies).ToList();
                    _logger.LogWarning(SeededCronTickers.CopiesRemovedWarning, copies.Count, function);
                }

                // Only the rows the seed owns take the expression in code; a user's cron of the same function keeps its own.
                foreach (var cron in SeededCronTickers.Owned(rows))
                {
                    if (!string.Equals(cron.Expression, expression, StringComparison.Ordinal))
                    {
                        cron.Expression = expression;
                        cron.UpdatedAt = now;
                        await Serializer.SetAsync(CronKey(cron.Id), cron).ConfigureAwait(false);
                    }
                }

                if (SeededCronTickers.IsAmbiguous(rows))
                    _logger.LogWarning(SeededCronTickers.AmbiguousWarning, function, expression, rows.Count);
            }
            else
            {
                var entity = new TCronTicker
                {
                    Id = SeededCronTickers.IdFor(function),
                    Function = function,
                    Expression = expression,
                    InitIdentifier = SeededCronTickers.InitIdentifierFor(function),
                    CreatedAt = now,
                    UpdatedAt = now,
                    Request = []
                };

                // Written only when the key is free: it can be held by a cron of another function, which must not be overwritten.
                if (!await Serializer.TryCreateAsync(CronKey(entity.Id), entity).ConfigureAwait(false))
                {
                    var holder = await Serializer.GetAsync<TCronTicker>(CronKey(entity.Id)).ConfigureAwait(false);
                    if (holder != null && !string.Equals(holder.Function, function, StringComparison.Ordinal))
                    {
                        // A seeded cron whose function was changed keeps its id: this cron is seeded under a random id, as before.
                        entity.Id = Guid.NewGuid();
                        await Serializer.SetAsync(CronKey(entity.Id), entity).ConfigureAwait(false);
                    }
                }

                // Also for a row another node created: a node that stopped between the row and its index would leave a
                // cron no node sees and, with the id fixed, no later start could add.
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

            var acquired = await TryClaimInProgressAsync<CronTickerOccurrenceEntity<TCronTicker>>(
                CronOccurrenceKey(id), id, TryReleaseCronOccurrenceClaim).ConfigureAwait(false);

            if (acquired == null)
            {
                await DropEndedPendingEntryAsync(id).ConfigureAwait(false);
                continue;
            }

            await TryUpdateIndexAfterClaim(acquired.Id, () => IndexManager.AddCronOccurrenceIndexesAsync(acquired)).ConfigureAwait(false);
            yield return acquired;
        }
    }

    // A due pending entry the fallback could not claim, whose occurrence has ended (an index write that never landed after
    // its run): dropped, as DropEndedPendingEntriesAsync does for time tickers, or every pass would try to claim it (#57).
    // An occurrence that is still Idle, Queued or InProgress keeps its entry: it is someone's to write.
    private async Task DropEndedPendingEntryAsync(Guid id)
    {
        var row = await Serializer.GetAsync<CronTickerOccurrenceEntity<TCronTicker>>(CronOccurrenceKey(id)).ConfigureAwait(false);
        if (row is { Status: not (TickerStatus.Idle or TickerStatus.Queued or TickerStatus.InProgress) })
            await TryUpdateIndexAfterClaim(id, () => IndexManager.AddCronOccurrenceIndexesAsync(row)).ConfigureAwait(false);
    }

    public async Task<bool> TryMarkCronOccurrenceInProgress(Guid id, DateTime claimMarker, CancellationToken cancellationToken = default)
    {
        var (claimed, row) = await TryMarkInProgressAsync<CronTickerOccurrenceEntity<TCronTicker>>(CronOccurrenceKey(id), claimMarker).ConfigureAwait(false);
        if (!claimed) return false;

        if (row != null)
            await TryUpdateIndexAfterClaim(id, () => IndexManager.AddCronOccurrenceIndexesAsync(row)).ConfigureAwait(false);
        return true;
    }

    public Task<bool> TryReleaseCronOccurrenceClaim(Guid id, DateTime claimMarker, CancellationToken cancellationToken = default)
        => TryReleaseClaimAsync<CronTickerOccurrenceEntity<TCronTicker>>(
            CronOccurrenceKey(id),
            row => row.Status == TickerStatus.InProgress && row.LockHolder == LockHolder && row.UpdatedAt == claimMarker,
            row => { row.Status = TickerStatus.Idle; row.LockHolder = null; row.LockedAt = null; row.UpdatedAt = Clock.UtcNow; },
            row => IndexManager.AddCronOccurrenceIndexesAsync(row),
            row => row.Status);

    public async Task UpdateCronTickerOccurrence(InternalFunctionContext functionContext, CancellationToken cancellationToken = default)
    {
        if (!HasApplicableProps(functionContext)) return;

        var occurrence = await CompareAndSetAsync<CronTickerOccurrenceEntity<TCronTicker>>(CronOccurrenceKey(functionContext.TickerId), row =>
        {
            if (row != null) ApplyFunctionContextToCronOccurrence(row, functionContext);
            return row;
        }).ConfigureAwait(false);
        if (occurrence == null) return;

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
        foreach (var id in cronOccurrenceIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var occurrence = await CompareAndSetAsync<CronTickerOccurrenceEntity<TCronTicker>>(CronOccurrenceKey(id), row =>
            {
                if (row != null) ApplyFunctionContextToCronOccurrence(row, functionContext);
                return row;
            }).ConfigureAwait(false);
            if (occurrence != null)
                await IndexManager.AddCronOccurrenceIndexesAsync(occurrence).ConfigureAwait(false);
        }
    }

    #region IFencedRunWriter
    // The fence of IFencedRunWriter: the row this node holds with the run's marker, compared as DateTime, and with
    // onlyWhileRunning still InProgress. Checked here in C# on the row that was read; the compare-and-set then sets it only
    // while the key still holds that text.
    private bool HoldsRun(string lockHolder, DateTime updatedAt, TickerStatus status, DateTime runMarker, bool onlyWhileRunning)
        => lockHolder == LockHolder && updatedAt == runMarker && (!onlyWhileRunning || status == TickerStatus.InProgress);

    public async Task<int> UpdateTimeTickerFencedAsync(InternalFunctionContext context, DateTime runMarker, bool onlyWhileRunning, CancellationToken cancellationToken = default)
    {
        var ticker = await CompareAndSetAsync<TTimeTicker>(TimeTickerKey(context.TickerId), row =>
        {
            if (row == null || !HoldsRun(row.LockHolder, row.UpdatedAt, row.Status, runMarker, onlyWhileRunning)) return null;
            ApplyFunctionContextToTicker(row, context, runMarker);
            return row;
        }).ConfigureAwait(false);
        if (ticker == null) return 0;

        await UpdateIndexAfterFencedWriteAsync(ticker.Id, () => IndexManager.AddTimeTickerIndexesAddOnlyAsync(ticker)).ConfigureAwait(false);
        return 1;
    }

    public async Task<int> UpdateCronOccurrenceFencedAsync(InternalFunctionContext context, DateTime runMarker, bool onlyWhileRunning, CancellationToken cancellationToken = default)
    {
        var occurrence = await CompareAndSetAsync<CronTickerOccurrenceEntity<TCronTicker>>(CronOccurrenceKey(context.TickerId), row =>
        {
            if (row == null || !HoldsRun(row.LockHolder, row.UpdatedAt, row.Status, runMarker, onlyWhileRunning)) return null;
            ApplyFunctionContextToCronOccurrence(row, context, runMarker);
            return row;
        }).ConfigureAwait(false);
        if (occurrence == null) return 0;

        await UpdateIndexAfterFencedWriteAsync(occurrence.Id, () => IndexManager.AddCronOccurrenceIndexesAddOnlyAsync(occurrence)).ConfigureAwait(false);
        return 1;
    }

    public async Task<int> StartInlineChildAsync(Guid timeTickerId, DateTime runMarker, CancellationToken cancellationToken = default)
    {
        var ticker = await ClaimWaitingChildAsync(timeTickerId, runMarker, Clock.UtcNow).ConfigureAwait(false);
        if (ticker == null) return 0;

        await UpdateIndexAfterFencedWriteAsync(ticker.Id, () => IndexManager.AddTimeTickerIndexesAddOnlyAsync(ticker)).ConfigureAwait(false);
        return 1;
    }

    // The field logic of UpdateCronTickerOccurrence, only on an occurrence that has not started: a running one keeps its run.
    public async Task<int> UpdateNotStartedCronOccurrenceAsync(InternalFunctionContext context, CancellationToken cancellationToken = default)
    {
        if (!HasApplicableProps(context)) return 0;

        var occurrence = await CompareAndSetAsync<CronTickerOccurrenceEntity<TCronTicker>>(CronOccurrenceKey(context.TickerId), row =>
        {
            if (row == null || row.Status is not (TickerStatus.Idle or TickerStatus.Queued)) return null;
            ApplyFunctionContextToCronOccurrence(row, context);
            return row;
        }).ConfigureAwait(false);
        if (occurrence == null) return 0;

        await IndexManager.AddCronOccurrenceIndexesAsync(occurrence).ConfigureAwait(false);
        return 1;
    }

    // The row is written: an index failure must not report the run's write as lost (the run would take its stored outcome
    // for a lost one). The index of a running or ended row has nothing to add but its id, which the row already has.
    private async Task UpdateIndexAfterFencedWriteAsync(Guid id, Func<Task> updateIndex)
    {
        try
        {
            if (AfterFencedWriteHook != null)
                await AfterFencedWriteHook(id).ConfigureAwait(false);
            await updateIndex().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Row {Id} was written by its run but its index entries could not be updated.", id);
        }
    }
    #endregion

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

    // See ReleaseDeadNodeTimeTickerResources with lockedBefore.
    public Task ReleaseDeadNodeOccurrenceResources(string instanceIdentifier, DateTime lockedBefore, CancellationToken cancellationToken = default)
    {
        var cutoff = EarlierRunCutoff(lockedBefore);
        return ReleaseEarlierRunRowsAsync<CronTickerOccurrenceEntity<TCronTicker>>(CronOccurrenceIdsKey, CronOccurrenceKey, row => row.Id,
            row => IsLeftByEarlierRun(row.LockHolder, row.LockedAt, row.Status, instanceIdentifier, cutoff),
            row =>
            {
                row.LockHolder = null;
                row.LockedAt = null;
                row.Status = TickerStatus.Idle;
                row.UpdatedAt = Clock.UtcNow;
            },
            IndexManager.AddCronOccurrenceIndexesAsync, cancellationToken);
    }
    #endregion
}
