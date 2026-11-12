using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Infrastructure;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Models;

namespace SharpTicker.MongoDB.Infrastructure
{
    internal sealed class TickerMongoPersistenceProvider<TTimeTicker, TCronTicker> :
        ITickerPersistenceProvider<TTimeTicker, TCronTicker>, IClaimFanOut, IChildSkipWriter, IFencedRunWriter
        where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
        where TCronTicker : CronTickerEntity, new()
    {
        private readonly ITickerMongoContext<TTimeTicker, TCronTicker> _context;
        private readonly ITickerClock _clock;
        private readonly string _lockHolder;
        private readonly ILogger _logger;
        private readonly Lazy<ChainGateStages> _chainGate;

        private static readonly Func<TTimeTicker, TimeTickerEntity> ProjectTimeTicker
            = MappingExtensions.ForQueueTimeTickers<TTimeTicker>().Compile();

        public TickerMongoPersistenceProvider(
            ITickerMongoContext<TTimeTicker, TCronTicker> context,
            ITickerClock clock,
            SchedulerOptionsBuilder optionsBuilder,
            ILogger logger = null)
        {
            _context = context;
            _clock = clock;
            _lockHolder = optionsBuilder.NodeIdentifier;
            _logger = logger ?? NullLogger.Instance;
            // Built on first use: the element names come from the class map, registered before the first query.
            _chainGate = new Lazy<ChainGateStages>(() => ChainGateStages.For(context.TimeTickers.CollectionNamespace.CollectionName));
        }

        // ===================================================================
        // Time Ticker — core scheduler methods
        // ===================================================================

        public async IAsyncEnumerable<TimeTickerEntity> QueueTimeTickers(
            TimeTickerEntity[] timeTickers,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var now = _clock.UtcNow;
            var coll = _context.TimeTickers;
            var fb = Builders<TTimeTicker>.Filter;

            foreach (var ticker in timeTickers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var filter = fb.And(
                    fb.Eq(x => x.Id, ticker.Id),
                    fb.Eq(x => x.UpdatedAt, ticker.UpdatedAt),
                    MongoUpdateBuilders.CanAcquireTimeTicker<TTimeTicker>(_lockHolder));
                var update = Builders<TTimeTicker>.Update
                    .Set(x => x.LockHolder, _lockHolder)
                    .Set(x => x.LockedAt, now)
                    .Set(x => x.UpdatedAt, now)
                    .Set(x => x.Status, TickerStatus.Queued);

                var result = await coll.UpdateOneAsync(filter, update, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (result.ModifiedCount <= 0)
                    continue;

                ticker.UpdatedAt = now;
                ticker.LockHolder = _lockHolder;
                ticker.LockedAt = now;
                ticker.Status = TickerStatus.Queued;
                yield return ticker;
            }
        }

        public async IAsyncEnumerable<TimeTickerEntity> QueueTimedOutTimeTickers(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var now = _clock.UtcNow;
            var fallbackThreshold = now.AddSeconds(-1);
            var coll = _context.TimeTickers;
            var fb = Builders<TTimeTicker>.Filter;

            var candidatesFilter = fb.And(
                fb.Ne(x => x.ExecutionTime, null),
                fb.In(x => x.Status, new[] { TickerStatus.Idle, TickerStatus.Queued }),
                fb.Lte(x => x.ExecutionTime, fallbackThreshold));

            var overdue = await coll.Find(candidatesFilter).ToListAsync(cancellationToken).ConfigureAwait(false);

            // A delayed child (a chain child with an execution time of its own) is claimed only once its parent's status
            // lets it run; one whose parent ended without a match is skipped; one whose parent has not ended waits.
            var parentStatuses = await LoadParentStatuses(overdue.Select(c => c.ParentId), cancellationToken).ConfigureAwait(false);
            var outcomeDoesNotMatch = overdue
                .Where(c => ParentEndedWithoutMatch(c.ParentId, c.RunCondition, parentStatuses))
                .Select(c => c.Id)
                .ToArray();
            if (outcomeDoesNotMatch.Length > 0)
            {
                // A failure here must not stop this pass from taking the overdue jobs: the rows it would skip can never be
                // claimed, and the next pass tries again.
                try
                {
                    await SkipDelayedChildrenAsync(outcomeDoesNotMatch, now, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Skipping delayed chain children whose parent's outcome does not match failed; the next fallback pass tries again.");
                }
            }

            var candidates = overdue.Where(c => ChainGateOpen(c.ParentId, c.RunCondition, parentStatuses)).ToList();
            var byParent = await LoadChildrenLookup(candidates.Select(c => c.Id).ToArray(), cancellationToken).ConfigureAwait(false);

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The status check keeps a row another node already moved to InProgress out of reach, even when
                // that node's clock is behind and wrote an earlier UpdatedAt.
                var filter = fb.And(
                    fb.Eq(x => x.Id, candidate.Id),
                    fb.Lte(x => x.UpdatedAt, candidate.UpdatedAt),
                    fb.In(x => x.Status, new[] { TickerStatus.Idle, TickerStatus.Queued }));

                // Every InProgress write of this node carries its own claim marker (see ClaimMarkers).
                var claimMarker = _clock.NextClaimMarker();
                var update = Builders<TTimeTicker>.Update
                    .Set(x => x.LockHolder, _lockHolder)
                    .Set(x => x.LockedAt, now)
                    .Set(x => x.UpdatedAt, claimMarker)
                    .Set(x => x.Status, TickerStatus.InProgress);

                UpdateResult result;
                try
                {
                    result = await coll.UpdateOneAsync(filter, update, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    await ClaimPutBack.PutBackOrThrowAsync(candidate.Id, claimMarker, TickerType.TimeTicker, failure, TryReleaseTimeTickerClaim).ConfigureAwait(false);
                    throw;
                }

                if (result.ModifiedCount <= 0)
                    continue;

                // The row was read before the claim; it is handed back with the marker the claim wrote, for a put-back.
                candidate.UpdatedAt = claimMarker;
                yield return BuildQueuedEntity(candidate, byParent);
            }
        }

        public async Task ReleaseAcquiredTimeTickers(Guid[] timeTickerIds, CancellationToken cancellationToken = default)
        {
            var now = _clock.UtcNow;
            var coll = _context.TimeTickers;
            var fb = Builders<TTimeTicker>.Filter;

            var canAcquire = MongoUpdateBuilders.CanAcquireTimeTicker<TTimeTicker>(_lockHolder);
            var filter = timeTickerIds.Length == 0
                ? canAcquire
                : fb.And(fb.In(x => x.Id, timeTickerIds), canAcquire);

            var update = Builders<TTimeTicker>.Update
                .Set(x => x.LockHolder, (string)null)
                .Set(x => x.LockedAt, (DateTime?)null)
                .Set(x => x.Status, TickerStatus.Idle)
                .Set(x => x.UpdatedAt, now);

            await coll.UpdateManyAsync(filter, update, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public async Task<TimeTickerEntity[]> GetEarliestTimeTickers(CancellationToken cancellationToken = default)
        {
            var now = _clock.UtcNow;
            var oneSecondAgo = now.AddSeconds(-1);
            var coll = _context.TimeTickers;
            var fb = Builders<TTimeTicker>.Filter;

            var baseFilter = fb.And(
                fb.Ne(x => x.ExecutionTime, null),
                fb.Gte(x => x.ExecutionTime, oneSecondAgo),
                MongoUpdateBuilders.CanAcquireTimeTicker<TTimeTicker>(_lockHolder));

            var gate = _chainGate.Value;
            var byTime = Builders<TTimeTicker>.Sort.Ascending(x => x.ExecutionTime);

            // The gate is part of the earliest-time query: a delayed child that still waits must not hide the rows due
            // after it.
            var earliest = await coll
                .Aggregate()
                .Match(baseFilter)
                .Sort(byTime)
                .AppendStage<BsonDocument>(gate.LookupParent)
                .Match(gate.Open)
                .Limit(1)
                .Project(gate.ExecutionTimeOnly)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (earliest == null || !earliest.TryGetValue(gate.ExecutionTimeElement, out var earliestTime) || earliestTime.IsBsonNull)
                return Array.Empty<TimeTickerEntity>();

            var min = earliestTime.ToUniversalTime();
            var minSecond = new DateTime(min.Year, min.Month, min.Day, min.Hour, min.Minute, min.Second, DateTimeKind.Utc);
            var maxExecutionTime = minSecond.AddSeconds(1);

            var windowFilter = fb.And(
                baseFilter,
                fb.Gte(x => x.ExecutionTime, minSecond),
                fb.Lt(x => x.ExecutionTime, maxExecutionTime));

            var rows = await coll
                .Aggregate()
                .Match(windowFilter)
                .Sort(byTime)
                .AppendStage<BsonDocument>(gate.LookupParent)
                .Match(gate.Open)
                .AppendStage<TTimeTicker>(gate.DropParent)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var byParent = await LoadChildrenLookup(rows.Select(r => r.Id).ToArray(), cancellationToken).ConfigureAwait(false);
            return rows.Select(r => BuildQueuedEntity(r, byParent)).ToArray();
        }

        // Each claim is a single-document conditional update.
        public ValueTask<int> GetMaxConcurrentClaimsAsync(CancellationToken cancellationToken = default) => new(ClaimFanOut.Default);

        public async Task<bool> TryMarkTimeTickerInProgress(Guid id, DateTime claimMarker, CancellationToken cancellationToken = default)
        {
            // Fenced claim: only a row still Queued by this node moves on, so a fallback that took it first wins.
            // The status changes on every match, so ModifiedCount equals the matched count.
            var result = await _context.TimeTickers
                .UpdateOneAsync(
                    Builders<TTimeTicker>.Filter.And(
                        Builders<TTimeTicker>.Filter.Eq(x => x.Id, id),
                        MongoUpdateBuilders.QueuedByTimeTicker<TTimeTicker>(_lockHolder)),
                    Builders<TTimeTicker>.Update
                        .Set(x => x.Status, TickerStatus.InProgress)
                        .Set(x => x.UpdatedAt, claimMarker),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return result.ModifiedCount > 0;
        }

        public async Task<int> UpdateTimeTicker(InternalFunctionContext functionContext, CancellationToken cancellationToken = default)
        {
            var update = MongoUpdateBuilders.BuildTimeTickerUpdate<TTimeTicker>(functionContext, _clock.UtcNow);
            var result = await _context.TimeTickers
                .UpdateOneAsync(
                    Builders<TTimeTicker>.Filter.Eq(x => x.Id, functionContext.TickerId),
                    update,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return (int)result.ModifiedCount;
        }

        public async Task<byte[]> GetTimeTickerRequest(Guid id, CancellationToken cancellationToken)
        {
            var ticker = await _context.TimeTickers
                .Find(Builders<TTimeTicker>.Filter.Eq(x => x.Id, id))
                .Project(x => x.Request)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            return ticker;
        }

        public async Task UpdateTimeTickersWithUnifiedContext(Guid[] timeTickerIds, InternalFunctionContext functionContext, CancellationToken cancellationToken = default)
        {
            if (timeTickerIds.Length == 0) return;
            var update = MongoUpdateBuilders.BuildTimeTickerUpdate<TTimeTicker>(functionContext, _clock.UtcNow);
            await _context.TimeTickers
                .UpdateManyAsync(
                    Builders<TTimeTicker>.Filter.In(x => x.Id, timeTickerIds),
                    update,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        // IChildSkipWriter: only a row that has not started is skipped, so a delayed child this store already handed out
        // keeps the status of its run.
        public async Task SkipNotStartedTimeTickersAsync(Guid[] timeTickerIds, InternalFunctionContext skip, CancellationToken cancellationToken = default)
        {
            if (timeTickerIds.Length == 0) return;
            var fb = Builders<TTimeTicker>.Filter;
            await _context.TimeTickers
                .UpdateManyAsync(
                    fb.And(fb.In(x => x.Id, timeTickerIds), fb.In(x => x.Status, new[] { TickerStatus.Idle, TickerStatus.Queued })),
                    MongoUpdateBuilders.BuildTimeTickerUpdate<TTimeTicker>(skip, _clock.UtcNow),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<bool> TryReleaseTimeTickerClaim(Guid id, DateTime claimMarker, CancellationToken cancellationToken = default)
        {
            var fb = Builders<TTimeTicker>.Filter;
            // The marker is unique per node and kept to the millisecond, so a row this node's fallback runs is never matched.
            var result = await _context.TimeTickers
                .UpdateOneAsync(
                    fb.And(
                        fb.Eq(x => x.Id, id),
                        fb.Eq(x => x.Status, TickerStatus.InProgress),
                        fb.Eq(x => x.LockHolder, _lockHolder),
                        fb.Eq(x => x.UpdatedAt, claimMarker)),
                    Builders<TTimeTicker>.Update
                        .Set(x => x.Status, TickerStatus.Idle)
                        .Set(x => x.LockHolder, (string)null)
                        .Set(x => x.LockedAt, (DateTime?)null)
                        .Set(x => x.UpdatedAt, _clock.UtcNow),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return result.ModifiedCount > 0;
        }

        public async Task<TimeTickerEntity[]> AcquireImmediateTimeTickersAsync(Guid[] ids, CancellationToken cancellationToken = default)
        {
            if (ids == null || ids.Length == 0) return Array.Empty<TimeTickerEntity>();

            var now = _clock.UtcNow;
            var coll = _context.TimeTickers;
            var fb = Builders<TTimeTicker>.Filter;

            // One marker for this call: markers must differ between claims of one row, not between rows.
            var claimMarker = _clock.NextClaimMarker();
            var update = Builders<TTimeTicker>.Update
                .Set(x => x.LockHolder, _lockHolder)
                .Set(x => x.LockedAt, now)
                .Set(x => x.Status, TickerStatus.InProgress)
                .Set(x => x.UpdatedAt, claimMarker);

            // A delayed child is claimable only once its parent's status lets it run. Read first, then claim by id: the gate
            // does not close again once open, as a finished parent status does not change. A cancelled caller is answered
            // by the loop below (it stops before claiming), not by a throw here.
            var requested = await coll
                .Find(fb.In(x => x.Id, ids))
                .Project(x => new { x.Id, x.ParentId, x.RunCondition, x.ExecutionTime })
                .ToListAsync(CancellationToken.None).ConfigureAwait(false);
            var parentStatuses = await LoadParentStatuses(requested.Select(r => r.ParentId), CancellationToken.None).ConfigureAwait(false);
            var gateOpen = requested
                .Where(r => RunNowGateOpen(r.ParentId, r.RunCondition, r.ExecutionTime.HasValue, parentStatuses))
                .Select(r => r.Id)
                .ToHashSet();
            if (gateOpen.Count == 0) return Array.Empty<TimeTickerEntity>();

            var won = new Dictionary<Guid, DateTime>(ids.Length);
            try
            {
                // One conditional write per row, so the rows this call won are known exactly. A batch update followed by
                // a read of "InProgress for this node" would also return a row this node's fallback had taken earlier.
                foreach (var id in ids)
                {
                    // A cancelled caller stops the loop but not the writes: a row won here is already InProgress and must
                    // be returned to be dispatched, the rows not written yet stay Idle for the fallback.
                    if (cancellationToken.IsCancellationRequested) break;

                    if (!gateOpen.Contains(id)) continue;

                    UpdateResult result;
                    try
                    {
                        result = await coll.UpdateOneAsync(
                            fb.And(fb.Eq(x => x.Id, id), MongoUpdateBuilders.CanAcquireTimeTicker<TTimeTicker>(_lockHolder)),
                            update,
                            cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // The write may have landed before its reply was lost: put it back with the rows already won.
                        won[id] = claimMarker;
                        throw;
                    }

                    if (result.ModifiedCount > 0) won[id] = claimMarker;
                }

                if (won.Count == 0) return Array.Empty<TimeTickerEntity>();

                var rows = await coll
                    .Find(fb.And(fb.In(x => x.Id, won.Keys), fb.Eq(x => x.LockHolder, _lockHolder), fb.Eq(x => x.Status, TickerStatus.InProgress)))
                    .ToListAsync(CancellationToken.None).ConfigureAwait(false);
                var byParent = await LoadChildrenLookup(rows.Select(r => r.Id).ToArray(), CancellationToken.None).ConfigureAwait(false);
                return rows.Select(r => BuildQueuedEntity(r, byParent)).ToArray();
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
            var now = _clock.UtcNow;
            var coll = _context.TimeTickers;

            await coll.UpdateManyAsync(
                MongoUpdateBuilders.CanAcquireTimeTicker<TTimeTicker>(instanceIdentifier),
                Builders<TTimeTicker>.Update
                    .Set(x => x.LockHolder, (string)null)
                    .Set(x => x.LockedAt, (DateTime?)null)
                    .Set(x => x.Status, TickerStatus.Idle)
                    .Set(x => x.UpdatedAt, now),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var fb = Builders<TTimeTicker>.Filter;
            await coll.UpdateManyAsync(
                fb.And(fb.Eq(x => x.LockHolder, instanceIdentifier), fb.Eq(x => x.Status, TickerStatus.InProgress)),
                Builders<TTimeTicker>.Update
                    .Set(x => x.LockHolder, (string)null)
                    .Set(x => x.LockedAt, (DateTime?)null)
                    .Set(x => x.Status, TickerStatus.Idle)
                    .Set(x => x.UpdatedAt, now),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // ===================================================================
        // Cron Ticker — core methods
        // ===================================================================

        public async Task MigrateDefinedCronTickers((string Function, string Expression)[] cronTickers, CancellationToken cancellationToken = default)
        {
            var now = _clock.UtcNow;
            var cronSet = _context.CronTickers;
            var occSet = _context.CronTickerOccurrences;

            var registeredFunctions = TickerFunctionProvider.TickerFunctions.Keys.ToHashSet(StringComparer.Ordinal);

            // Orphan cleanup is intentionally narrowed to *seeded* crons (those with a non-empty
            // InitIdentifier set by the code-defined-cron migration). Dashboard- or API-created
            // crons have an empty InitIdentifier and are never deleted here. Mirrors the EF provider.
            var fb = Builders<TCronTicker>.Filter;
            var orphans = await cronSet
                .Find(fb.And(
                    fb.Ne(x => x.InitIdentifier, null),
                    fb.Ne(x => x.InitIdentifier, string.Empty)))
                .Project(x => new { x.Id, x.Function })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var orphanIds = orphans
                .Where(o => !registeredFunctions.Contains(o.Function))
                .Select(o => o.Id)
                .ToArray();

            if (orphanIds.Length > 0)
            {
                await occSet.DeleteManyAsync(
                    Builders<CronTickerOccurrenceEntity<TCronTicker>>.Filter.In(x => x.CronTickerId, orphanIds),
                    cancellationToken).ConfigureAwait(false);
                await cronSet.DeleteManyAsync(fb.In(x => x.Id, orphanIds), cancellationToken).ConfigureAwait(false);
            }

            var functions = cronTickers.Select(x => x.Function).ToArray();
            var existing = await cronSet
                .Find(fb.In(x => x.Function, functions))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var existingByFunction = existing
                .GroupBy(c => c.Function)
                .ToDictionary(g => g.Key, g => (IReadOnlyCollection<TCronTicker>)g.ToList());

            foreach (var (function, expression) in cronTickers)
            {
                if (existingByFunction.TryGetValue(function, out var rows))
                {
                    // Copies of the seeded cron, left by nodes that started together before the seeded id was fixed.
                    var copies = SeededCronTickers.Copies(rows);
                    if (copies.Count > 0)
                    {
                        var copyIds = copies.Select(c => c.Id).ToArray();
                        await occSet.DeleteManyAsync(
                            Builders<CronTickerOccurrenceEntity<TCronTicker>>.Filter.In(x => x.CronTickerId, copyIds),
                            cancellationToken).ConfigureAwait(false);
                        await cronSet.DeleteManyAsync(fb.In(x => x.Id, copyIds), cancellationToken).ConfigureAwait(false);

                        rows = rows.Except(copies).ToList();
                        _logger.LogWarning(SeededCronTickers.CopiesRemovedWarning, copies.Count, function);
                    }

                    // Only the rows the seed owns take the expression in code; a user's cron of the same function keeps its own.
                    foreach (var cron in SeededCronTickers.Owned(rows))
                    {
                        if (!string.Equals(cron.Expression, expression, StringComparison.Ordinal))
                        {
                            await cronSet.UpdateOneAsync(
                                fb.Eq(x => x.Id, cron.Id),
                                Builders<TCronTicker>.Update
                                    .Set(x => x.Expression, expression)
                                    .Set(x => x.UpdatedAt, now),
                                cancellationToken: cancellationToken).ConfigureAwait(false);
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
                        Request = Array.Empty<byte>()
                    };

                    try
                    {
                        await cronSet.InsertOneAsync(entity, cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
                    {
                        // The id is taken. By a cron of this function: another node that started together seeded it, and
                        // nothing is left to do. By a cron of another function (a seeded cron whose function was changed
                        // keeps its id): this cron is seeded under a random id, as before.
                        var seededByAnotherNode = await cronSet
                            .Find(fb.Eq(x => x.Function, function))
                            .AnyAsync(cancellationToken)
                            .ConfigureAwait(false);

                        if (!seededByAnotherNode)
                        {
                            entity.Id = Guid.NewGuid();
                            await cronSet.InsertOneAsync(entity, cancellationToken: cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }
        }

        public async Task<CronTickerEntity[]> GetAllCronTickerExpressions(CancellationToken cancellationToken)
        {
            var fb = Builders<TCronTicker>.Filter;
            var rows = await _context.CronTickers
                .Find(fb.Eq(x => x.IsEnabled, true))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var project = MappingExtensions.ForCronTickerExpressions<TCronTicker>().Compile();
            return rows.Select(project).ToArray();
        }

        // ===================================================================
        // Cron Occurrence — core methods
        // ===================================================================

        public async Task<CronTickerOccurrenceEntity<TCronTicker>> GetEarliestAvailableCronOccurrence(Guid[] ids, CancellationToken cancellationToken = default)
        {
            var now = _clock.UtcNow;
            var mainSchedulerThreshold = now.AddSeconds(-1);
            var fb = Builders<CronTickerOccurrenceEntity<TCronTicker>>.Filter;

            var filter = fb.And(
                fb.In(x => x.CronTickerId, ids),
                fb.Gte(x => x.ExecutionTime, mainSchedulerThreshold),
                MongoUpdateBuilders.CanAcquireCronOccurrence<TCronTicker>(_lockHolder));

            var occurrence = await _context.CronTickerOccurrences
                .Find(filter)
                .Sort(Builders<CronTickerOccurrenceEntity<TCronTicker>>.Sort.Ascending(x => x.ExecutionTime))
                .Limit(1)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (occurrence == null) return null;

            occurrence.CronTicker = await _context.CronTickers
                .Find(Builders<TCronTicker>.Filter.Eq(x => x.Id, occurrence.CronTickerId))
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            return occurrence;
        }

        public async IAsyncEnumerable<CronTickerOccurrenceEntity<TCronTicker>> QueueCronTickerOccurrences(
            (DateTime Key, InternalManagerContext[] Items) cronTickerOccurrences,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var now = _clock.UtcNow;
            var executionTime = cronTickerOccurrences.Key;
            var coll = _context.CronTickerOccurrences;
            var fb = Builders<CronTickerOccurrenceEntity<TCronTicker>>.Filter;

            foreach (var item in cronTickerOccurrences.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (item.NextCronOccurrence is null)
                {
                    // INSERT path. Unique index on (CronTickerId, ExecutionTime) is our dedup — a
                    // duplicate-key error here means another scheduler already claimed this slot,
                    // so skip silently (mirrors EF's Upsert.NoUpdate() returning 0).
                    var toAdd = new CronTickerOccurrenceEntity<TCronTicker>
                    {
                        Id = Guid.NewGuid(),
                        Status = TickerStatus.Queued,
                        LockHolder = _lockHolder,
                        ExecutionTime = executionTime,
                        CronTickerId = item.Id,
                        LockedAt = now,
                        CreatedAt = now,
                        UpdatedAt = now
                    };

                    bool inserted;
                    try
                    {
                        await coll.InsertOneAsync(toAdd, cancellationToken: cancellationToken).ConfigureAwait(false);
                        inserted = true;
                    }
                    catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
                    {
                        inserted = false;
                    }

                    if (!inserted) continue;

                    toAdd.CronTicker = new TCronTicker
                    {
                        Id = item.Id,
                        Function = item.FunctionName,
                        InitIdentifier = _lockHolder,
                        Expression = item.Expression,
                        Retries = item.Retries,
                        RetryIntervals = item.RetryIntervals
                    };
                    yield return toAdd;
                }
                else
                {
                    // UPDATE path — claim an existing occurrence row
                    var filter = fb.And(
                        fb.Eq(x => x.Id, item.NextCronOccurrence.Id),
                        fb.Eq(x => x.ExecutionTime, executionTime),
                        MongoUpdateBuilders.CanAcquireCronOccurrence<TCronTicker>(_lockHolder));

                    var update = Builders<CronTickerOccurrenceEntity<TCronTicker>>.Update
                        .Set(x => x.LockHolder, _lockHolder)
                        .Set(x => x.LockedAt, now)
                        .Set(x => x.UpdatedAt, now)
                        .Set(x => x.Status, TickerStatus.Queued);

                    var result = await coll.UpdateOneAsync(filter, update, cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (result.ModifiedCount <= 0) continue;

                    yield return new CronTickerOccurrenceEntity<TCronTicker>
                    {
                        Id = item.NextCronOccurrence.Id,
                        CronTickerId = item.Id,
                        ExecutionTime = executionTime,
                        Status = TickerStatus.Queued,
                        LockHolder = _lockHolder,
                        LockedAt = now,
                        UpdatedAt = now,
                        CreatedAt = item.NextCronOccurrence.CreatedAt,
                        CronTicker = new TCronTicker
                        {
                            Id = item.Id,
                            Function = item.FunctionName,
                            InitIdentifier = _lockHolder,
                            Expression = item.Expression,
                            Retries = item.Retries,
                            RetryIntervals = item.RetryIntervals
                        }
                    };
                }
            }
        }

        public async IAsyncEnumerable<CronTickerOccurrenceEntity<TCronTicker>> QueueTimedOutCronTickerOccurrences(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var now = _clock.UtcNow;
            var fallbackThreshold = now.AddSeconds(-1);
            var coll = _context.CronTickerOccurrences;
            var fb = Builders<CronTickerOccurrenceEntity<TCronTicker>>.Filter;

            var candidatesFilter = fb.And(
                fb.In(x => x.Status, new[] { TickerStatus.Idle, TickerStatus.Queued }),
                fb.Lte(x => x.ExecutionTime, fallbackThreshold));

            var candidates = await coll.Find(candidatesFilter).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0) yield break;

            var cronById = await LoadCronTickers(candidates.Select(c => c.CronTickerId).ToArray(), cancellationToken).ConfigureAwait(false);

            foreach (var occ in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var filter = fb.And(
                    fb.Eq(x => x.Id, occ.Id),
                    fb.Eq(x => x.UpdatedAt, occ.UpdatedAt),
                    fb.In(x => x.Status, new[] { TickerStatus.Idle, TickerStatus.Queued }));

                var claimMarker = _clock.NextClaimMarker();
                var update = Builders<CronTickerOccurrenceEntity<TCronTicker>>.Update
                    .Set(x => x.LockHolder, _lockHolder)
                    .Set(x => x.LockedAt, now)
                    .Set(x => x.UpdatedAt, claimMarker)
                    .Set(x => x.Status, TickerStatus.InProgress);

                UpdateResult result;
                try
                {
                    result = await coll.UpdateOneAsync(filter, update, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    await ClaimPutBack.PutBackOrThrowAsync(occ.Id, claimMarker, TickerType.CronTickerOccurrence, failure, TryReleaseCronOccurrenceClaim).ConfigureAwait(false);
                    throw;
                }

                if (result.ModifiedCount <= 0) continue;

                // The row was read before the claim; it is handed back with the marker the claim wrote, for a put-back.
                occ.UpdatedAt = claimMarker;

                if (cronById.TryGetValue(occ.CronTickerId, out var cron))
                {
                    occ.CronTicker = new TCronTicker
                    {
                        Id = cron.Id,
                        Function = cron.Function,
                        RetryIntervals = cron.RetryIntervals,
                        Retries = cron.Retries
                    };
                }
                yield return occ;
            }
        }

        public async Task<bool> TryMarkCronOccurrenceInProgress(Guid id, DateTime claimMarker, CancellationToken cancellationToken = default)
        {
            var result = await _context.CronTickerOccurrences
                .UpdateOneAsync(
                    Builders<CronTickerOccurrenceEntity<TCronTicker>>.Filter.And(
                        Builders<CronTickerOccurrenceEntity<TCronTicker>>.Filter.Eq(x => x.Id, id),
                        MongoUpdateBuilders.QueuedByCronOccurrence<TCronTicker>(_lockHolder)),
                    Builders<CronTickerOccurrenceEntity<TCronTicker>>.Update
                        .Set(x => x.Status, TickerStatus.InProgress)
                        .Set(x => x.UpdatedAt, claimMarker),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return result.ModifiedCount > 0;
        }

        public async Task<bool> TryReleaseCronOccurrenceClaim(Guid id, DateTime claimMarker, CancellationToken cancellationToken = default)
        {
            var fb = Builders<CronTickerOccurrenceEntity<TCronTicker>>.Filter;
            var result = await _context.CronTickerOccurrences
                .UpdateOneAsync(
                    fb.And(
                        fb.Eq(x => x.Id, id),
                        fb.Eq(x => x.Status, TickerStatus.InProgress),
                        fb.Eq(x => x.LockHolder, _lockHolder),
                        fb.Eq(x => x.UpdatedAt, claimMarker)),
                    Builders<CronTickerOccurrenceEntity<TCronTicker>>.Update
                        .Set(x => x.Status, TickerStatus.Idle)
                        .Set(x => x.LockHolder, (string)null)
                        .Set(x => x.LockedAt, (DateTime?)null)
                        .Set(x => x.UpdatedAt, _clock.UtcNow),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return result.ModifiedCount > 0;
        }

        public async Task UpdateCronTickerOccurrence(InternalFunctionContext functionContext, CancellationToken cancellationToken = default)
        {
            var update = MongoUpdateBuilders.BuildCronOccurrenceUpdate<TCronTicker>(functionContext, _clock.UtcNow);
            await _context.CronTickerOccurrences
                .UpdateOneAsync(
                    Builders<CronTickerOccurrenceEntity<TCronTicker>>.Filter.Eq(x => x.Id, functionContext.TickerId),
                    update,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        // ===================================================================
        // IFencedRunWriter — the writes of a run land only on the row it still holds
        // ===================================================================

        // The counts are MatchedCount, not ModifiedCount: a write that landed and is sent again writes the same values, which
        // Mongo reports as not modified, and it is still stored.

        // With onlyWhileRunning the row must also still be InProgress (the stop's Cancelled write).
        public async Task<int> UpdateTimeTickerFencedAsync(InternalFunctionContext context, DateTime runMarker, bool onlyWhileRunning, CancellationToken cancellationToken = default)
        {
            var fb = Builders<TTimeTicker>.Filter;
            var fence = fb.And(fb.Eq(x => x.Id, context.TickerId), fb.Eq(x => x.LockHolder, _lockHolder), fb.Eq(x => x.UpdatedAt, runMarker));
            if (onlyWhileRunning)
                fence = fb.And(fence, fb.Eq(x => x.Status, TickerStatus.InProgress));
            var result = await _context.TimeTickers
                .UpdateOneAsync(
                    fence,
                    MongoUpdateBuilders.BuildTimeTickerUpdate<TTimeTicker>(context, runMarker),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return (int)result.MatchedCount;
        }

        public async Task<int> UpdateCronOccurrenceFencedAsync(InternalFunctionContext context, DateTime runMarker, bool onlyWhileRunning, CancellationToken cancellationToken = default)
        {
            var fb = Builders<CronTickerOccurrenceEntity<TCronTicker>>.Filter;
            var fence = fb.And(fb.Eq(x => x.Id, context.TickerId), fb.Eq(x => x.LockHolder, _lockHolder), fb.Eq(x => x.UpdatedAt, runMarker));
            if (onlyWhileRunning)
                fence = fb.And(fence, fb.Eq(x => x.Status, TickerStatus.InProgress));
            var result = await _context.CronTickerOccurrences
                .UpdateOneAsync(
                    fence,
                    MongoUpdateBuilders.BuildCronOccurrenceUpdate<TCronTicker>(context, runMarker),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return (int)result.MatchedCount;
        }

        public async Task<int> StartInlineChildAsync(Guid timeTickerId, DateTime runMarker, CancellationToken cancellationToken = default)
        {
            // A claim: only a child that still waits for its start (see IFencedRunWriter.StartInlineChildAsync).
            var fb = Builders<TTimeTicker>.Filter;
            var result = await _context.TimeTickers
                .UpdateOneAsync(
                    fb.And(
                        fb.Eq(x => x.Id, timeTickerId),
                        fb.Eq(x => x.Status, TickerStatus.Idle),
                        fb.Eq(x => x.LockHolder, (string)null),
                        fb.Eq(x => x.ExecutionTime, (DateTime?)null)),
                    Builders<TTimeTicker>.Update
                        .Set(x => x.Status, TickerStatus.InProgress)
                        .Set(x => x.LockHolder, _lockHolder)
                        .Set(x => x.LockedAt, _clock.UtcNow)
                        .Set(x => x.UpdatedAt, runMarker),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return (int)result.MatchedCount;
        }

        public async Task<int> UpdateNotStartedCronOccurrenceAsync(InternalFunctionContext context, CancellationToken cancellationToken = default)
        {
            var fb = Builders<CronTickerOccurrenceEntity<TCronTicker>>.Filter;
            var result = await _context.CronTickerOccurrences
                .UpdateOneAsync(
                    fb.And(fb.Eq(x => x.Id, context.TickerId), fb.In(x => x.Status, new[] { TickerStatus.Idle, TickerStatus.Queued })),
                    MongoUpdateBuilders.BuildCronOccurrenceUpdate<TCronTicker>(context, _clock.UtcNow),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return (int)result.MatchedCount;
        }

        public async Task ReleaseAcquiredCronTickerOccurrences(Guid[] occurrenceIds, CancellationToken cancellationToken = default)
        {
            var now = _clock.UtcNow;
            var coll = _context.CronTickerOccurrences;
            var fb = Builders<CronTickerOccurrenceEntity<TCronTicker>>.Filter;

            var canAcquire = MongoUpdateBuilders.CanAcquireCronOccurrence<TCronTicker>(_lockHolder);
            var filter = occurrenceIds.Length == 0
                ? canAcquire
                : fb.And(fb.In(x => x.Id, occurrenceIds), canAcquire);

            var update = Builders<CronTickerOccurrenceEntity<TCronTicker>>.Update
                .Set(x => x.LockHolder, (string)null)
                .Set(x => x.LockedAt, (DateTime?)null)
                .Set(x => x.Status, TickerStatus.Idle)
                .Set(x => x.UpdatedAt, now);

            await coll.UpdateManyAsync(filter, update, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public async Task<byte[]> GetCronTickerOccurrenceRequest(Guid tickerId, CancellationToken cancellationToken = default)
        {
            var occ = await _context.CronTickerOccurrences
                .Find(Builders<CronTickerOccurrenceEntity<TCronTicker>>.Filter.Eq(x => x.Id, tickerId))
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (occ == null) return null;

            var cron = await _context.CronTickers
                .Find(Builders<TCronTicker>.Filter.Eq(x => x.Id, occ.CronTickerId))
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            return cron?.Request;
        }

        public async Task UpdateCronTickerOccurrencesWithUnifiedContext(Guid[] cronOccurrenceIds, InternalFunctionContext functionContext, CancellationToken cancellationToken = default)
        {
            if (cronOccurrenceIds.Length == 0) return;
            var update = MongoUpdateBuilders.BuildCronOccurrenceUpdate<TCronTicker>(functionContext, _clock.UtcNow);
            await _context.CronTickerOccurrences
                .UpdateManyAsync(
                    Builders<CronTickerOccurrenceEntity<TCronTicker>>.Filter.In(x => x.Id, cronOccurrenceIds),
                    update,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task ReleaseDeadNodeOccurrenceResources(string instanceIdentifier, CancellationToken cancellationToken = default)
        {
            var now = _clock.UtcNow;
            var coll = _context.CronTickerOccurrences;

            await coll.UpdateManyAsync(
                MongoUpdateBuilders.CanAcquireCronOccurrence<TCronTicker>(instanceIdentifier),
                Builders<CronTickerOccurrenceEntity<TCronTicker>>.Update
                    .Set(x => x.LockHolder, (string)null)
                    .Set(x => x.LockedAt, (DateTime?)null)
                    .Set(x => x.Status, TickerStatus.Idle)
                    .Set(x => x.UpdatedAt, now),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var fb = Builders<CronTickerOccurrenceEntity<TCronTicker>>.Filter;
            await coll.UpdateManyAsync(
                fb.And(fb.Eq(x => x.LockHolder, instanceIdentifier), fb.Eq(x => x.Status, TickerStatus.InProgress)),
                Builders<CronTickerOccurrenceEntity<TCronTicker>>.Update
                    .Set(x => x.LockHolder, (string)null)
                    .Set(x => x.LockedAt, (DateTime?)null)
                    .Set(x => x.Status, TickerStatus.Idle)
                    .Set(x => x.UpdatedAt, now),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public async Task<int> SkipStaleCronOccurrencesAsync(TimeSpan staleThreshold, CancellationToken cancellationToken = default)
        {
            if (staleThreshold <= TimeSpan.Zero) return 0;
            var now = _clock.UtcNow;
            var cutoff = now - staleThreshold;
            var fb = Builders<CronTickerOccurrenceEntity<TCronTicker>>.Filter;

            var filter = fb.And(
                fb.In(x => x.Status, new[] { TickerStatus.Idle, TickerStatus.Queued }),
                fb.Lt(x => x.ExecutionTime, cutoff));

            var update = Builders<CronTickerOccurrenceEntity<TCronTicker>>.Update
                .Set(x => x.Status, TickerStatus.Skipped)
                .Set(x => x.SkippedReason, "Missed: occurrence was pending when the application restarted")
                .Set(x => x.UpdatedAt, now);

            var result = await _context.CronTickerOccurrences.UpdateManyAsync(filter, update, cancellationToken: cancellationToken).ConfigureAwait(false);
            return (int)result.ModifiedCount;
        }

        // ===================================================================
        // Shared / dashboard methods
        // ===================================================================

        public async Task<TTimeTicker> GetTimeTickerById(Guid id, CancellationToken cancellationToken = default)
        {
            var row = await _context.TimeTickers
                .Find(Builders<TTimeTicker>.Filter.Eq(x => x.Id, id))
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (row == null) return null;
            row.Children = await LoadChildrenRecursive<TTimeTicker>(row.Id, cancellationToken).ConfigureAwait(false);
            return row;
        }

        public async Task<TTimeTicker[]> GetTimeTickers(Expression<Func<TTimeTicker, bool>> predicate, CancellationToken cancellationToken = default)
        {
            var fb = Builders<TTimeTicker>.Filter;
            var filter = fb.And(fb.Eq(x => x.ParentId, (Guid?)null), predicate is null ? fb.Empty : fb.Where(predicate));
            var rows = await _context.TimeTickers
                .Find(filter)
                .Sort(Builders<TTimeTicker>.Sort.Descending(x => x.ExecutionTime))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var row in rows)
                row.Children = await LoadChildrenRecursive<TTimeTicker>(row.Id, cancellationToken).ConfigureAwait(false);

            return rows.ToArray();
        }

        public async Task<PaginationResult<TTimeTicker>> GetTimeTickersPaginated(Expression<Func<TTimeTicker, bool>> predicate, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
        {
            var fb = Builders<TTimeTicker>.Filter;
            var filter = fb.And(fb.Eq(x => x.ParentId, (Guid?)null), predicate is null ? fb.Empty : fb.Where(predicate));
            var total = await _context.TimeTickers.CountDocumentsAsync(filter, cancellationToken: cancellationToken).ConfigureAwait(false);
            var rows = await _context.TimeTickers
                .Find(filter)
                .Sort(Builders<TTimeTicker>.Sort.Descending(x => x.ExecutionTime))
                .Skip((pageNumber - 1) * pageSize)
                .Limit(pageSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var row in rows)
                row.Children = await LoadChildrenRecursive<TTimeTicker>(row.Id, cancellationToken).ConfigureAwait(false);

            return new PaginationResult<TTimeTicker>(rows, (int)total, pageNumber, pageSize);
        }

        public async Task<int> AddTimeTickers(TTimeTicker[] tickers, CancellationToken cancellationToken = default)
        {
            var count = 0;
            foreach (var t in tickers) count += await InsertWithChildren(t, null, cancellationToken).ConfigureAwait(false);
            return count;
        }

        private async Task<int> InsertWithChildren(TTimeTicker ticker, Guid? parentId, CancellationToken ct)
        {
            if (parentId.HasValue) ticker.ParentId = parentId.Value;
            await _context.TimeTickers.InsertOneAsync(ticker, cancellationToken: ct).ConfigureAwait(false);
            var count = 1;
            if (ticker.Children != null)
            {
                foreach (var child in ticker.Children)
                    if (child is TTimeTicker c) count += await InsertWithChildren(c, ticker.Id, ct).ConfigureAwait(false);
            }
            return count;
        }

        public async Task<int> UpdateTimeTickers(TTimeTicker[] tickers, CancellationToken cancellationToken = default)
        {
            var count = 0;
            foreach (var t in tickers) count += await ReplaceWithChildren(t, null, cancellationToken).ConfigureAwait(false);
            return count;
        }

        private async Task<int> ReplaceWithChildren(TTimeTicker ticker, Guid? parentId, CancellationToken ct)
        {
            if (parentId.HasValue) ticker.ParentId = parentId.Value;
            var count = await ReplaceKeepingRunAsync(ticker, ct).ConfigureAwait(false);
            if (ticker.Children != null)
            {
                foreach (var child in ticker.Children)
                    if (child is TTimeTicker c) count += await ReplaceWithChildren(c, ticker.Id, ct).ConfigureAwait(false);
            }
            return count;
        }

        // Bounds the rounds of an edit whose row keeps changing between its read and its write.
        private const int MaxEditAttempts = 8;

        /// <summary>
        /// Writes one row of a full-entity update (UpdateAsync, the dashboard edit) and keeps the run of a running row
        /// (<see cref="RunStateOnEdit"/>), atomically per row:
        /// <list type="number">
        /// <item>A row that is InProgress takes only the edit fields: its status, lock, run marker and the fields the run writes
        /// stay, so the run's fenced writes still land and the job is not run again.</item>
        /// <item>Any other row is replaced. A body that still shows a run (read while the job ran, sent after it ended) does not
        /// bring the run back: the stored status and lock are kept, by a replace that matches the values read, or Idle with no
        /// lock when there is no row.</item>
        /// <item>A row that became InProgress in between fails the upsert with a duplicate key; that is tried once more, then
        /// thrown.</item>
        /// </list>
        /// The replace upserts, as it did before: a row deleted in the meantime is created again (pre-existing behaviour).
        /// </summary>
        private async Task<int> ReplaceKeepingRunAsync(TTimeTicker ticker, CancellationToken ct)
        {
            var coll = _context.TimeTickers;
            var fb = Builders<TTimeTicker>.Filter;
            var byId = fb.Eq(x => x.Id, ticker.Id);

            // A stored chain child keeps its link (RunStateOnEdit.KeepChainLink). Read before the write: an update never
            // changes a stored ParentId, so it is the same when the write lands. The caller's entity is left as it was sent.
            var link = await coll.Find(byId)
                .Project(x => new { x.ParentId, x.RunCondition })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (link?.ParentId != null && (ticker.ParentId != link.ParentId || ticker.RunCondition == null))
            {
                ticker = MongoUpdateBuilders.CopyOf(ticker, coll.DocumentSerializer);
                RunStateOnEdit.KeepChainLink(ticker, link.ParentId, link.RunCondition);
            }

            // A row that runs or has ended keeps its run (RunStateOnEdit): an ended job written back as not started would run
            // again. Only a row that has not started (Idle, Queued) is replaced.
            TickerStatus[] notStartedStatuses = [TickerStatus.Idle, TickerStatus.Queued];
            var notRunning = fb.And(byId, fb.In(x => x.Status, notStartedStatuses));
            var duplicateKeySeen = false;

            for (var attempt = 0; attempt < MaxEditAttempts; attempt++)
            {
                var edit = await coll.UpdateOneAsync(
                    fb.And(byId, fb.Nin(x => x.Status, notStartedStatuses)),
                    MongoUpdateBuilders.BuildEditOfRunningTimeTicker(ticker, coll.DocumentSerializer),
                    cancellationToken: ct).ConfigureAwait(false);
                if (edit.MatchedCount > 0)
                    return (int)edit.ModifiedCount;

                var body = ticker;
                var filter = notRunning;
                var upsert = true;
                if (ticker.Status == TickerStatus.InProgress)
                {
                    var stored = await coll.Find(notRunning)
                        .Project(x => new { x.Status, x.LockHolder, x.LockedAt })
                        .FirstOrDefaultAsync(ct).ConfigureAwait(false);

                    body = MongoUpdateBuilders.CopyOf(ticker, coll.DocumentSerializer);
                    if (stored is null)
                    {
                        body.Status = TickerStatus.Idle;
                        body.LockHolder = null;
                        body.LockedAt = null;
                    }
                    else
                    {
                        body.Status = stored.Status;
                        body.LockHolder = stored.LockHolder;
                        body.LockedAt = stored.LockedAt;
                        // Only the row as read: one that changed after the read is read again.
                        filter = fb.And(byId,
                            fb.Eq(x => x.Status, stored.Status),
                            fb.Eq(x => x.LockHolder, stored.LockHolder),
                            fb.Eq(x => x.LockedAt, stored.LockedAt));
                        upsert = false;
                    }
                }

                try
                {
                    var replaced = await coll.ReplaceOneAsync(filter, body, new ReplaceOptions { IsUpsert = upsert }, ct).ConfigureAwait(false);
                    if (replaced.MatchedCount > 0 || replaced.UpsertedId != null)
                        return (int)replaced.ModifiedCount + (replaced.UpsertedId is null ? 0 : 1);
                }
                catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey && !duplicateKeySeen)
                {
                    // The row became InProgress after the first step: the next round writes the edit fields only.
                    duplicateKeySeen = true;
                }
            }

            throw new InvalidOperationException($"Time ticker {ticker.Id} kept changing while it was being updated; it was not updated.");
        }

        public async Task<int> RemoveTimeTickers(Guid[] tickerIds, CancellationToken cancellationToken = default)
        {
            var count = 0;
            foreach (var id in tickerIds)
            {
                var allIds = await CollectDescendantIds(id, cancellationToken).ConfigureAwait(false);
                allIds.Add(id);
                var result = await _context.TimeTickers.DeleteManyAsync(
                    Builders<TTimeTicker>.Filter.In(x => x.Id, allIds),
                    cancellationToken).ConfigureAwait(false);
                count += (int)result.DeletedCount;
            }
            return count;
        }

        public async Task<TCronTicker> GetCronTickerById(Guid id, CancellationToken cancellationToken)
            => await _context.CronTickers
                .Find(Builders<TCronTicker>.Filter.Eq(x => x.Id, id))
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

        public async Task<TCronTicker[]> GetCronTickers(Expression<Func<TCronTicker, bool>> predicate, CancellationToken cancellationToken)
        {
            var filter = predicate is null ? Builders<TCronTicker>.Filter.Empty : Builders<TCronTicker>.Filter.Where(predicate);
            var rows = await _context.CronTickers
                .Find(filter)
                .Sort(Builders<TCronTicker>.Sort.Descending(x => x.CreatedAt))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return rows.ToArray();
        }

        public async Task<PaginationResult<TCronTicker>> GetCronTickersPaginated(Expression<Func<TCronTicker, bool>> predicate, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
        {
            var filter = predicate is null ? Builders<TCronTicker>.Filter.Empty : Builders<TCronTicker>.Filter.Where(predicate);
            var total = await _context.CronTickers.CountDocumentsAsync(filter, cancellationToken: cancellationToken).ConfigureAwait(false);
            var rows = await _context.CronTickers
                .Find(filter)
                .Sort(Builders<TCronTicker>.Sort.Descending(x => x.CreatedAt))
                .Skip((pageNumber - 1) * pageSize)
                .Limit(pageSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return new PaginationResult<TCronTicker>(rows, (int)total, pageNumber, pageSize);
        }

        public async Task<int> InsertCronTickers(TCronTicker[] tickers, CancellationToken cancellationToken)
        {
            if (tickers.Length == 0) return 0;
            await _context.CronTickers.InsertManyAsync(tickers, cancellationToken: cancellationToken).ConfigureAwait(false);
            return tickers.Length;
        }

        public async Task<int> UpdateCronTickers(TCronTicker[] cronTicker, CancellationToken cancellationToken)
        {
            var count = 0;
            foreach (var t in cronTicker)
            {
                var result = await _context.CronTickers.ReplaceOneAsync(
                    Builders<TCronTicker>.Filter.Eq(x => x.Id, t.Id),
                    t,
                    new ReplaceOptions { IsUpsert = false },
                    cancellationToken).ConfigureAwait(false);
                count += (int)result.ModifiedCount;
            }
            return count;
        }

        public async Task<int> RemoveCronTickers(Guid[] cronTickerIds, CancellationToken cancellationToken)
        {
            var result = await _context.CronTickers.DeleteManyAsync(
                Builders<TCronTicker>.Filter.In(x => x.Id, cronTickerIds),
                cancellationToken).ConfigureAwait(false);
            return (int)result.DeletedCount;
        }

        public async Task<CronTickerOccurrenceEntity<TCronTicker>[]> GetAllCronTickerOccurrences(Expression<Func<CronTickerOccurrenceEntity<TCronTicker>, bool>> predicate, CancellationToken cancellationToken = default)
        {
            var filter = predicate is null
                ? Builders<CronTickerOccurrenceEntity<TCronTicker>>.Filter.Empty
                : Builders<CronTickerOccurrenceEntity<TCronTicker>>.Filter.Where(predicate);
            var rows = await _context.CronTickerOccurrences
                .Find(filter)
                .Sort(Builders<CronTickerOccurrenceEntity<TCronTicker>>.Sort.Descending(x => x.CreatedAt))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return rows.ToArray();
        }

        public async Task<PaginationResult<CronTickerOccurrenceEntity<TCronTicker>>> GetAllCronTickerOccurrencesPaginated(Expression<Func<CronTickerOccurrenceEntity<TCronTicker>, bool>> predicate, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
        {
            var filter = predicate is null
                ? Builders<CronTickerOccurrenceEntity<TCronTicker>>.Filter.Empty
                : Builders<CronTickerOccurrenceEntity<TCronTicker>>.Filter.Where(predicate);
            var total = await _context.CronTickerOccurrences.CountDocumentsAsync(filter, cancellationToken: cancellationToken).ConfigureAwait(false);
            var rows = await _context.CronTickerOccurrences
                .Find(filter)
                .Sort(Builders<CronTickerOccurrenceEntity<TCronTicker>>.Sort.Descending(x => x.CreatedAt))
                .Skip((pageNumber - 1) * pageSize)
                .Limit(pageSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return new PaginationResult<CronTickerOccurrenceEntity<TCronTicker>>(rows, (int)total, pageNumber, pageSize);
        }

        public async Task<int> InsertCronTickerOccurrences(CronTickerOccurrenceEntity<TCronTicker>[] cronTickerOccurrences, CancellationToken cancellationToken)
        {
            var count = 0;
            foreach (var occ in cronTickerOccurrences)
            {
                try
                {
                    await _context.CronTickerOccurrences.InsertOneAsync(occ, cancellationToken: cancellationToken).ConfigureAwait(false);
                    count++;
                }
                catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
                {
                    // unique-index violation: another writer claimed the same (CronTickerId, ExecutionTime) slot
                }
            }
            return count;
        }

        public async Task<int> RemoveCronTickerOccurrences(Guid[] cronTickerOccurrences, CancellationToken cancellationToken)
        {
            var result = await _context.CronTickerOccurrences.DeleteManyAsync(
                Builders<CronTickerOccurrenceEntity<TCronTicker>>.Filter.In(x => x.Id, cronTickerOccurrences),
                cancellationToken).ConfigureAwait(false);
            return (int)result.DeletedCount;
        }

        public async Task<CronTickerOccurrenceEntity<TCronTicker>[]> AcquireImmediateCronOccurrencesAsync(Guid[] occurrenceIds, CancellationToken cancellationToken = default)
        {
            if (occurrenceIds == null || occurrenceIds.Length == 0)
                return Array.Empty<CronTickerOccurrenceEntity<TCronTicker>>();

            var now = _clock.UtcNow;
            var coll = _context.CronTickerOccurrences;
            var fb = Builders<CronTickerOccurrenceEntity<TCronTicker>>.Filter;

            var won = new Dictionary<Guid, DateTime>(occurrenceIds.Length);
            try
            {
                // One conditional write per row, so the rows this call won are known exactly. A batch update followed by
                // a read of "InProgress for this node" also returned a row this node's fallback was already running.
                foreach (var id in occurrenceIds)
                {
                    // A cancelled caller stops the loop but not the writes: a row won here is already InProgress and must
                    // be returned to be dispatched, the rows not written yet stay Idle for the fallback.
                    if (cancellationToken.IsCancellationRequested) break;

                    var claimMarker = _clock.NextClaimMarker();
                    var update = Builders<CronTickerOccurrenceEntity<TCronTicker>>.Update
                        .Set(x => x.LockHolder, _lockHolder)
                        .Set(x => x.LockedAt, now)
                        .Set(x => x.Status, TickerStatus.InProgress)
                        .Set(x => x.UpdatedAt, claimMarker);

                    UpdateResult result;
                    try
                    {
                        result = await coll.UpdateOneAsync(
                            fb.And(fb.Eq(x => x.Id, id), MongoUpdateBuilders.CanAcquireCronOccurrence<TCronTicker>(_lockHolder)),
                            update,
                            cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // The write may have landed before its reply was lost: put it back with the rows already won.
                        won[id] = claimMarker;
                        throw;
                    }

                    if (result.ModifiedCount > 0) won[id] = claimMarker;
                }

                if (won.Count == 0) return Array.Empty<CronTickerOccurrenceEntity<TCronTicker>>();

                var rows = await coll
                    .Find(fb.And(fb.In(x => x.Id, won.Keys), fb.Eq(x => x.LockHolder, _lockHolder), fb.Eq(x => x.Status, TickerStatus.InProgress)))
                    .ToListAsync(CancellationToken.None).ConfigureAwait(false);

                // The caller runs each row from its cron (function, retries, intervals), which an occurrence document
                // does not hold; it is loaded as the fallback does. A failure here puts the won rows back below.
                var cronById = await LoadCronTickers(rows.Select(r => r.CronTickerId).Distinct().ToArray(), CancellationToken.None).ConfigureAwait(false);

                // Each row is handed back with the marker its claim wrote, for a put-back.
                foreach (var row in rows)
                {
                    row.UpdatedAt = won[row.Id];
                    if (cronById.TryGetValue(row.CronTickerId, out var cron))
                    {
                        row.CronTicker = new TCronTicker
                        {
                            Id = cron.Id,
                            Function = cron.Function,
                            RetryIntervals = cron.RetryIntervals,
                            Retries = cron.Retries
                        };
                    }
                }
                return rows.ToArray();
            }
            catch (Exception failure) when (won.Count > 0)
            {
                // The caller gets no rows to run, so the rows won so far would stay InProgress until this node restarts.
                await ClaimPutBack.PutBackOrThrowAsync(won, TickerType.CronTickerOccurrence, failure, TryReleaseCronOccurrenceClaim).ConfigureAwait(false);
                throw;
            }
        }

        // ===================================================================
        // Queryable hooks — implemented in MongoTickerQueryable (Task 6)
        // ===================================================================

        public ITickerQueryable<TTimeTicker> TimeTickersQuery()
            => new MongoTickerQueryable<TTimeTicker>(
                _context.TimeTickers,
                async (entity, relations, ct) =>
                {
                    if (relations.Any(r => r == TickerRelation.Children || r == TickerRelation.ChildrenDeep))
                        entity.Children = await LoadChildrenRecursive<TTimeTicker>(entity.Id, ct).ConfigureAwait(false);
                });

        public ITickerQueryable<TCronTicker> CronTickersQuery()
            => new MongoTickerQueryable<TCronTicker>(_context.CronTickers, null);

        public ITickerQueryable<CronTickerOccurrenceEntity<TCronTicker>> CronTickerOccurrencesQuery()
            => new MongoTickerQueryable<CronTickerOccurrenceEntity<TCronTicker>>(
                _context.CronTickerOccurrences,
                async (occ, relations, ct) =>
                {
                    if (relations.Any(r => r == TickerRelation.CronTicker))
                    {
                        occ.CronTicker = await _context.CronTickers
                            .Find(Builders<TCronTicker>.Filter.Eq(x => x.Id, occ.CronTickerId))
                            .FirstOrDefaultAsync(ct)
                            .ConfigureAwait(false);
                    }
                });

        // ===================================================================
        // Private helpers
        // ===================================================================

        // The dispatched payload holds the two levels below each row that the execution handler runs (children and
        // grandchildren), as on the other stores. Mongo keeps each descendant as a document of its own, so this is one
        // query per level. A delayed child (one with an execution time of its own) is carried with its time: the handler
        // never runs it inline, and skips it only while it has not started (IChildSkipWriter).
        private async Task<Dictionary<Guid, List<TTimeTicker>>> LoadChildrenLookup(Guid[] parentIds, CancellationToken ct)
        {
            var byParent = new Dictionary<Guid, List<TTimeTicker>>();
            if (parentIds.Length == 0) return byParent;

            var children = await LoadChildren(parentIds, ct).ConfigureAwait(false);
            var grandchildren = children.Count == 0
                ? children
                : await LoadChildren(children.Select(c => c.Id).ToArray(), ct).ConfigureAwait(false);

            foreach (var row in children.Concat(grandchildren))
            {
                if (!byParent.TryGetValue(row.ParentId!.Value, out var list))
                    byParent[row.ParentId.Value] = list = new List<TTimeTicker>();
                list.Add(row);
            }
            return byParent;
        }

        // The payload never carries a descendant's request (the handler reads it when the descendant runs), so it is not
        // read here.
        private Task<List<TTimeTicker>> LoadChildren(Guid[] parentIds, CancellationToken ct)
            => _context.TimeTickers
                .Find(Builders<TTimeTicker>.Filter.In(x => x.ParentId, parentIds.Select(p => (Guid?)p)))
                .Project<TTimeTicker>(Builders<TTimeTicker>.Projection.Exclude(x => x.Request))
                .ToListAsync(ct);

        // The status of each parent of the given rows, for the chain gate. A parent that is not found is left out, so its
        // children wait.
        private async Task<Dictionary<Guid, TickerStatus>> LoadParentStatuses(IEnumerable<Guid?> parentIds, CancellationToken ct)
        {
            var ids = parentIds.Where(p => p.HasValue).Select(p => p.Value).Distinct().ToArray();
            if (ids.Length == 0) return new Dictionary<Guid, TickerStatus>();
            var parents = await _context.TimeTickers
                .Find(Builders<TTimeTicker>.Filter.In(x => x.Id, ids))
                .Project(x => new { x.Id, x.Status })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            return parents.ToDictionary(p => p.Id, p => p.Status);
        }

        // A row may be handed out when it has no parent, or when its parent's status lets it run (ChainRunGate).
        private static bool ChainGateOpen(Guid? parentId, RunCondition? condition, Dictionary<Guid, TickerStatus> parentStatuses)
            => parentId == null
               || (parentStatuses.TryGetValue(parentId.Value, out var status) && ChainRunGate.Allows(condition, status));

        // The gate of a claim by id (ChainRunGate.AllowsRunNow): an InProgress child without a time of its own is left out.
        // It starts with its parent and is never handed out on its own.
        private static bool RunNowGateOpen(Guid? parentId, RunCondition? condition, bool hasOwnTime, Dictionary<Guid, TickerStatus> parentStatuses)
            => parentId == null
               || (parentStatuses.TryGetValue(parentId.Value, out var status) && ChainRunGate.AllowsRunNow(condition, status, hasOwnTime));

        // A delayed child whose parent ended with an outcome it does not run on never will, as a finished status does not
        // change.
        private static bool ParentEndedWithoutMatch(Guid? parentId, RunCondition? condition, Dictionary<Guid, TickerStatus> parentStatuses)
            => parentId != null
               && parentStatuses.TryGetValue(parentId.Value, out var status)
               && ChainRunGate.IsFinal(status)
               && !ChainRunGate.Allows(condition, status);

        // The parent's run skips a child its outcome does not let run; this covers a node that stopped before doing so. Its
        // own delayed children follow on a later pass. Only a row that has not started is written.
        private Task SkipDelayedChildrenAsync(Guid[] ids, DateTime now, CancellationToken ct)
        {
            var fb = Builders<TTimeTicker>.Filter;
            return _context.TimeTickers.UpdateManyAsync(
                fb.And(fb.In(x => x.Id, ids), fb.In(x => x.Status, new[] { TickerStatus.Idle, TickerStatus.Queued })),
                Builders<TTimeTicker>.Update
                    .Set(x => x.Status, TickerStatus.Skipped)
                    .Set(x => x.ExecutedAt, now)
                    .Set(x => x.SkippedReason, ChainRunGate.NotMatchedReason)
                    .Set(x => x.LockHolder, (string)null)
                    .Set(x => x.LockedAt, (DateTime?)null)
                    .Set(x => x.UpdatedAt, now),
                cancellationToken: ct);
        }

        /// <summary>
        /// The aggregation stages of the chain gate in the window query: each row is joined to its parent, and only a row
        /// without a parent, or one whose parent's status lets it run, goes on. The condition is built from
        /// <see cref="ChainRunGate"/>, so it holds the same table; element names come from the class map.
        /// </summary>
        private sealed class ChainGateStages
        {
            private const string ParentField = "__chainParent";

            public BsonDocument LookupParent { get; private set; }
            public BsonDocument Open { get; private set; }
            public BsonDocument DropParent { get; private set; }
            public BsonDocument ExecutionTimeOnly { get; private set; }
            public string ExecutionTimeElement { get; private set; }

            public static ChainGateStages For(string collectionName)
            {
                var classMap = BsonClassMap.LookupClassMap(typeof(TTimeTicker));
                BsonMemberMap MemberOf(string member)
                    => classMap.AllMemberMaps.FirstOrDefault(m => m.MemberName == member)
                       ?? throw new InvalidOperationException(
                           $"The MongoDB class map of {typeof(TTimeTicker).FullName} does not map {member}, which SharpTicker needs to run chains.");

                var parentIdMember = MemberOf(nameof(TimeTickerEntity.ParentId));
                var runConditionMember = MemberOf(nameof(TimeTickerEntity.RunCondition));
                var statusMember = MemberOf(nameof(TimeTickerEntity.Status));
                var parentId = parentIdMember.ElementName;
                var runCondition = runConditionMember.ElementName;
                var status = statusMember.ElementName;
                var executionTime = MemberOf(nameof(TimeTickerEntity.ExecutionTime)).ElementName;

                // Values are written by the members' own serializers, so a class map that stores the enums differently
                // (as strings, say) is matched as the typed filters match it.
                var open = new BsonArray { new BsonDocument(parentId, BsonNull.Value) };
                foreach (var condition in Enum.GetValues(typeof(RunCondition)).Cast<RunCondition?>().Append(null))
                {
                    var statuses = ChainRunGate.ParentStatusesFor(condition);
                    if (statuses.Length == 0) continue;
                    open.Add(new BsonDocument
                    {
                        { runCondition, Serialize(runConditionMember, condition) },
                        { ParentField + "." + status, new BsonDocument("$in", new BsonArray(statuses.Select(s => Serialize(statusMember, s)))) }
                    });
                }

                return new ChainGateStages
                {
                    // Only the parent's status is read: the parent is matched on _id (indexed) and nothing else is copied.
                    LookupParent = new BsonDocument("$lookup", new BsonDocument
                    {
                        { "from", collectionName },
                        { "let", new BsonDocument("parentId", "$" + parentId) },
                        { "pipeline", new BsonArray
                            {
                                new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { "$_id", "$$parentId" }))),
                                new BsonDocument("$project", new BsonDocument { { "_id", 0 }, { status, 1 } })
                            }
                        },
                        { "as", ParentField }
                    }),
                    Open = new BsonDocument("$or", open),
                    DropParent = new BsonDocument("$project", new BsonDocument(ParentField, 0)),
                    ExecutionTimeOnly = new BsonDocument { { "_id", 0 }, { executionTime, 1 } },
                    ExecutionTimeElement = executionTime
                };
            }

            private static BsonValue Serialize(BsonMemberMap member, object value)
            {
                var document = new BsonDocument();
                using (var writer = new BsonDocumentWriter(document))
                {
                    writer.WriteStartDocument();
                    writer.WriteName("v");
                    member.GetSerializer().Serialize(BsonSerializationContext.CreateRoot(writer), value);
                    writer.WriteEndDocument();
                }
                return document["v"];
            }
        }

        private TimeTickerEntity BuildQueuedEntity(TTimeTicker ticker, Dictionary<Guid, List<TTimeTicker>> byParent)
        {
            // Attach the loaded children and grandchildren, then run the shared projection, which keeps two levels.
            ticker.Children = ChildrenOf(ticker.Id, byParent);
            foreach (var child in ticker.Children)
                child.Children = ChildrenOf(child.Id, byParent);
            return ProjectTimeTicker(ticker);
        }

        private static List<TTimeTicker> ChildrenOf(Guid id, Dictionary<Guid, List<TTimeTicker>> byParent)
            => byParent.TryGetValue(id, out var children) ? children : new List<TTimeTicker>();

        private async Task<Dictionary<Guid, TCronTicker>> LoadCronTickers(Guid[] ids, CancellationToken ct)
        {
            if (ids.Length == 0) return new Dictionary<Guid, TCronTicker>();
            var rows = await _context.CronTickers
                .Find(Builders<TCronTicker>.Filter.In(x => x.Id, ids))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            return rows.ToDictionary(r => r.Id);
        }

        private async Task<List<T>> LoadChildrenRecursive<T>(Guid parentId, CancellationToken ct) where T : TTimeTicker
        {
            var rows = await _context.TimeTickers
                .Find(Builders<TTimeTicker>.Filter.Eq(x => x.ParentId, (Guid?)parentId))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var result = new List<T>(rows.Count);
            foreach (var row in rows)
            {
                row.Children = (await LoadChildrenRecursive<TTimeTicker>(row.Id, ct).ConfigureAwait(false)).Cast<TTimeTicker>().ToList();
                result.Add((T)row);
            }
            return result;
        }

        private async Task<HashSet<Guid>> CollectDescendantIds(Guid parentId, CancellationToken ct)
        {
            var collected = new HashSet<Guid>();
            var frontier = new Queue<Guid>();
            frontier.Enqueue(parentId);
            while (frontier.Count > 0)
            {
                var id = frontier.Dequeue();
                var childIds = await _context.TimeTickers
                    .Find(Builders<TTimeTicker>.Filter.Eq(x => x.ParentId, (Guid?)id))
                    .Project(x => x.Id)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                foreach (var c in childIds)
                {
                    if (collected.Add(c)) frontier.Enqueue(c);
                }
            }
            return collected;
        }
    }
}
