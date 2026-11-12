using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using SharpTicker.EntityFrameworkCore.DbContextFactory;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Infrastructure;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Models;

namespace SharpTicker.EntityFrameworkCore.Infrastructure;

internal abstract class BasePersistenceProvider<TDbContext, TTimeTicker, TCronTicker> : IClaimFanOut, IChildSkipWriter, IFencedRunWriter
    where TDbContext : DbContext
    where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
    where TCronTicker : CronTickerEntity, new()
{
    private int _maxConcurrentClaims;

    // SQLite allows a single writer, and a single-connection setup (an in-memory database) cannot run two commands at once,
    // so SQLite rows are claimed one at a time. The EF Core provider of a context does not change, so it is read once.
    public async ValueTask<int> GetMaxConcurrentClaimsAsync(CancellationToken cancellationToken = default)
    {
        var known = Volatile.Read(ref _maxConcurrentClaims);
        if (known > 0)
            return known;

        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var providerName = session.Context.Database.ProviderName ?? string.Empty;
        var fanOut = providerName.IndexOf("Sqlite", StringComparison.OrdinalIgnoreCase) >= 0 ? ClaimFanOut.Sequential : ClaimFanOut.Default;
        Volatile.Write(ref _maxConcurrentClaims, fanOut);
        return fanOut;
    }

    public BasePersistenceProvider(IServiceProvider serviceProvider, ITickerClock clock, SchedulerOptionsBuilder optionsBuilder, ISharpTickerRedisContext redisContext)
    {
        _clock = clock;
        RedisContext = redisContext;
        _serviceProvider = serviceProvider;
        _lockHolder = optionsBuilder.NodeIdentifier;
        _logger = (serviceProvider.GetService(typeof(ILoggerFactory)) as ILoggerFactory)?.CreateLogger("SharpTicker.EntityFrameworkCore")
                  ?? NullLogger.Instance;
    }

    private readonly ILogger _logger;
    protected readonly IServiceProvider _serviceProvider;
    protected readonly string _lockHolder;
    protected readonly ITickerClock _clock;
    protected readonly ISharpTickerRedisContext RedisContext;

    protected Task<DbContextLease<TDbContext>> CreateDbContextAsync(CancellationToken cancellationToken)
        => DbContextLease<TDbContext>.CreateAsync(_serviceProvider, cancellationToken);

    protected DbContextLease<TDbContext> CreateDbContext()
        => DbContextLease<TDbContext>.Create(_serviceProvider);

    #region IFencedRunWriter

    // The writes of a run land only on the row this node holds with the run's marker (and, with onlyWhileRunning, that is still
    // InProgress), and keep the marker in UpdatedAt.
    public async Task<int> UpdateTimeTickerFencedAsync(InternalFunctionContext context, DateTime runMarker, bool onlyWhileRunning, CancellationToken cancellationToken = default)
    {
        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = session.Context.Set<TTimeTicker>();
        var id = context.TickerId;

        var written = await rows
            .Where(x => x.Id == id && x.LockHolder == _lockHolder && x.UpdatedAt == runMarker
                        && (!onlyWhileRunning || x.Status == TickerStatus.InProgress))
            .ExecuteUpdateAsync(setter => setter.UpdateTimeTicker<TTimeTicker>(context, runMarker), cancellationToken).ConfigureAwait(false);

        // A provider that counts only the rows it changed (MySQL with UseAffectedRows) reports 0 for a write sent again with
        // the same values; the row that still matches the fence has the write. The same fence, status included: a Done row
        // with the marker was not written by a write that needed InProgress.
        return written > 0 || await rows.AnyAsync(x => x.Id == id && x.LockHolder == _lockHolder && x.UpdatedAt == runMarker
                                                        && (!onlyWhileRunning || x.Status == TickerStatus.InProgress), cancellationToken).ConfigureAwait(false)
            ? 1
            : 0;
    }

    public async Task<int> UpdateCronOccurrenceFencedAsync(InternalFunctionContext context, DateTime runMarker, bool onlyWhileRunning, CancellationToken cancellationToken = default)
    {
        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = session.Context.Set<CronTickerOccurrenceEntity<TCronTicker>>();
        var id = context.TickerId;

        var written = await rows
            .Where(x => x.Id == id && x.LockHolder == _lockHolder && x.UpdatedAt == runMarker
                        && (!onlyWhileRunning || x.Status == TickerStatus.InProgress))
            .ExecuteUpdateAsync(setter => setter.UpdateCronTickerOccurrence<TCronTicker>(context, runMarker), cancellationToken).ConfigureAwait(false);

        return written > 0 || await rows.AnyAsync(x => x.Id == id && x.LockHolder == _lockHolder && x.UpdatedAt == runMarker
                                                        && (!onlyWhileRunning || x.Status == TickerStatus.InProgress), cancellationToken).ConfigureAwait(false)
            ? 1
            : 0;
    }

    public async Task<int> StartInlineChildAsync(Guid timeTickerId, DateTime runMarker, CancellationToken cancellationToken = default)
    {
        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;

        return await session.Context.Set<TTimeTicker>()
            .Where(x => x.Id == timeTickerId)
            .ExecuteUpdateAsync(setter => setter
                .SetProperty(x => x.Status, TickerStatus.InProgress)
                .SetProperty(x => x.LockHolder, _lockHolder)
                .SetProperty(x => x.LockedAt, now)
                .SetProperty(x => x.UpdatedAt, runMarker), cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> UpdateNotStartedCronOccurrenceAsync(InternalFunctionContext context, CancellationToken cancellationToken = default)
    {
        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var id = context.TickerId;
        var now = _clock.UtcNow;

        return await session.Context.Set<CronTickerOccurrenceEntity<TCronTicker>>()
            .Where(x => x.Id == id && (x.Status == TickerStatus.Idle || x.Status == TickerStatus.Queued))
            .ExecuteUpdateAsync(setter => setter.UpdateCronTickerOccurrence<TCronTicker>(context, now), cancellationToken).ConfigureAwait(false);
    }

    #endregion

    #region Core_Time_Ticker_Methods
    public async IAsyncEnumerable<TimeTickerEntity> QueueTimeTickers(TimeTickerEntity[] timeTickers, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var dbContext = session.Context;
        var context = dbContext.Set<TTimeTicker>();
        var now = _clock.UtcNow;
        
        foreach (var timeTicker in timeTickers)
        {
            cancellationToken.ThrowIfCancellationRequested();
                
            var updatedTicker = await context
                .Where(x => x.Id == timeTicker.Id)
                .Where(x => x.UpdatedAt == timeTicker.UpdatedAt)
                .WhereCanAcquire(_lockHolder)
                .ExecuteUpdateAsync(prop => prop
                    .SetProperty(x => x.LockHolder, _lockHolder)
                    .SetProperty(x => x.LockedAt, now)
                    .SetProperty(x => x.UpdatedAt, now)
                    .SetProperty(x => x.Status, TickerStatus.Queued), cancellationToken);

            if (updatedTicker <= 0) 
                continue;
                
            timeTicker.UpdatedAt = now;
            timeTicker.LockHolder = _lockHolder;
            timeTicker.LockedAt = now;
            timeTicker.Status = TickerStatus.Queued;
                
            yield return timeTicker;
        }
    }

    public async IAsyncEnumerable<TimeTickerEntity> QueueTimedOutTimeTickers([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var dbContext = session.Context;
        var context = dbContext.Set<TTimeTicker>();
        var now = _clock.UtcNow;
        var fallbackThreshold = now.AddSeconds(-1);  // Fallback picks up tasks older than main 1-second window

        // A failure here must not stop this pass from taking the overdue jobs: the rows it would skip can never be claimed,
        // and the next pass tries again.
        try
        {
            await SkipDelayedChildrenWhoseParentDoesNotMatchAsync(context, fallbackThreshold, now, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Skipping delayed chain children whose parent's outcome does not match failed; the next fallback pass tries again.");
        }

        // A delayed child whose parent has not ended (with a status its condition accepts) waits.
        var timeTickersToUpdate =  await context
            .AsNoTracking()
            .Where(x => x.ExecutionTime != null)
            .Where(x => x.Status == TickerStatus.Idle || x.Status == TickerStatus.Queued)
            .Where(x => x.ExecutionTime <= fallbackThreshold)  // Only tasks older than 1 second
            .WhereChainGateOpen()
            .Select(MappingExtensions.ForQueueTimeTickers<TTimeTicker>())
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

        foreach (var timeTicker in timeTickersToUpdate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Every InProgress write of this node carries its own claim marker, so a claim of the loop is never mistaken
            // for this one when it is put back.
            var claimMarker = _clock.NextClaimMarker();
            int affected;
            try
            {
                affected = await context
                    .Where(x => x.Id == timeTicker.Id && x.UpdatedAt <= timeTicker.UpdatedAt)
                    .Where(x => x.Status == TickerStatus.Idle || x.Status == TickerStatus.Queued)
                    .ExecuteUpdateAsync(setter => setter
                        .SetProperty(x => x.LockHolder, _lockHolder)
                        .SetProperty(x => x.LockedAt, now)
                        .SetProperty(x => x.UpdatedAt, claimMarker)
                        .SetProperty(x => x.Status, TickerStatus.InProgress), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                await ClaimPutBack.PutBackOrThrowAsync(timeTicker.Id, claimMarker, TickerType.TimeTicker, failure, TryReleaseTimeTickerClaim).ConfigureAwait(false);
                throw;
            }

            if(affected <= 0)
                continue;

            // The row was read before the claim; it is handed back with the marker the claim wrote, for a put-back.
            timeTicker.UpdatedAt = claimMarker;
            yield return timeTicker;
        }
    }

    // A delayed child whose parent ended with an outcome it does not run on never will. The parent's run skips it; this
    // covers a node that stopped before doing so. Its own delayed children follow on a later pass.
    // Read first, then write by id: an UPDATE through the parent join does not translate on every provider (SQLite), and
    // such a row can never become claimable meanwhile, as its parent's finished status does not change.
    private static async Task SkipDelayedChildrenWhoseParentDoesNotMatchAsync(DbSet<TTimeTicker> context, DateTime fallbackThreshold, DateTime now, CancellationToken cancellationToken)
    {
        var outcomeDoesNotMatch = await context
            .AsNoTracking()
            .Where(x => x.ExecutionTime != null && x.ExecutionTime <= fallbackThreshold)
            .Where(x => x.Status == TickerStatus.Idle || x.Status == TickerStatus.Queued)
            .WhereParentEndedWithoutMatch()
            .Select(x => x.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (outcomeDoesNotMatch.Count == 0)
            return;

        await context
            .Where(x => outcomeDoesNotMatch.Contains(x.Id))
            .Where(x => x.Status == TickerStatus.Idle || x.Status == TickerStatus.Queued)
            .ExecuteUpdateAsync(setter => setter
                .SetProperty(x => x.Status, TickerStatus.Skipped)
                .SetProperty(x => x.ExecutedAt, now)
                .SetProperty(x => x.SkippedReason, ChainRunGate.NotMatchedReason)
                .SetProperty(x => x.LockHolder, _ => null)
                .SetProperty(x => x.LockedAt, _ => null)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken).ConfigureAwait(false);
    }

    public async Task ReleaseAcquiredTimeTickers(Guid[] timeTickerIds, CancellationToken cancellationToken)
    {
        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var dbContext = session.Context;
        var now = _clock.UtcNow;
            
        var idList = timeTickerIds.ToList();
        var baseQuery = idList.Count == 0
            ? dbContext.Set<TTimeTicker>()
            : dbContext.Set<TTimeTicker>().Where(x => idList.Contains(x.Id));
            
        await baseQuery
            .WhereCanAcquire(_lockHolder)
            .ExecuteUpdateAsync(setter => setter
                .SetProperty(x => x.LockHolder, _ => null)
                .SetProperty(x => x.LockedAt, _ => null)
                .SetProperty(x => x.Status, _ => TickerStatus.Idle)
                .SetProperty(x => x.UpdatedAt, _ => now), cancellationToken).ConfigureAwait(false);;
    }
        
    public async Task<bool> TryMarkTimeTickerInProgress(Guid id, DateTime claimMarker, CancellationToken cancellationToken = default)
    {
        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var dbContext = session.Context;

        // Fenced claim: only a row still Queued by this node moves on, so a fallback that took it first wins.
        var affected = await dbContext.Set<TTimeTicker>()
            .Where(x => x.Id == id)
            .WhereQueuedBy(_lockHolder)
            .ExecuteUpdateAsync(setter => setter
                .SetProperty(x => x.Status, TickerStatus.InProgress)
                .SetProperty(x => x.UpdatedAt, claimMarker), cancellationToken).ConfigureAwait(false);

        return affected > 0;
    }

    public async Task<bool> TryReleaseTimeTickerClaim(Guid id, DateTime claimMarker, CancellationToken cancellationToken = default)
    {
        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var dbContext = session.Context;
        var now = _clock.UtcNow;

        // The marker is unique per node, so a row this node's fallback took and runs (its own marker) is never matched.
        var affected = await dbContext.Set<TTimeTicker>()
            .Where(x => x.Id == id && x.Status == TickerStatus.InProgress && x.LockHolder == _lockHolder && x.UpdatedAt == claimMarker)
            .ExecuteUpdateAsync(setter => setter
                .SetProperty(x => x.Status, TickerStatus.Idle)
                .SetProperty(x => x.LockHolder, _ => null)
                .SetProperty(x => x.LockedAt, _ => null)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken).ConfigureAwait(false);

        return affected > 0;
    }

    public async Task<int> UpdateTimeTicker(InternalFunctionContext functionContexts, CancellationToken cancellationToken)
    {
        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var dbContext = session.Context;
        return await dbContext.Set<TTimeTicker>()
            .Where(x => x.Id == functionContexts.TickerId)
            .ExecuteUpdateAsync(setter => setter.UpdateTimeTicker<TTimeTicker>(functionContexts, _clock.UtcNow), cancellationToken).ConfigureAwait(false);
    }
        
    public async Task UpdateTimeTickersWithUnifiedContext(Guid[] timeTickerIds, InternalFunctionContext functionContext, CancellationToken cancellationToken = default)
    {
        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var dbContext = session.Context;
        var idList = timeTickerIds.ToList();
        await dbContext.Set<TTimeTicker>()
            .Where(x => idList.Contains(x.Id))
            .ExecuteUpdateAsync(setter => setter.UpdateTimeTicker<TTimeTicker>(functionContext, _clock.UtcNow), cancellationToken).ConfigureAwait(false);
    }

    // IChildSkipWriter: only a row that has not started is skipped, so a delayed child this store already handed out keeps
    // the status of its run.
    public async Task SkipNotStartedTimeTickersAsync(Guid[] timeTickerIds, InternalFunctionContext skip, CancellationToken cancellationToken = default)
    {
        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var dbContext = session.Context;
        var idList = timeTickerIds.ToList();
        await dbContext.Set<TTimeTicker>()
            .Where(x => idList.Contains(x.Id))
            .Where(x => x.Status == TickerStatus.Idle || x.Status == TickerStatus.Queued)
            .ExecuteUpdateAsync(setter => setter.UpdateTimeTicker<TTimeTicker>(skip, _clock.UtcNow), cancellationToken).ConfigureAwait(false);
    }
        
    public async Task<TimeTickerEntity[]> GetEarliestTimeTickers(CancellationToken cancellationToken)
    {
        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var dbContext = session.Context;
        var now = _clock.UtcNow;
    
        // Define the window: ignore anything older than 1 second ago
        var oneSecondAgo = now.AddSeconds(-1);
    
        var baseQuery = dbContext.Set<TTimeTicker>()
            .AsNoTracking()
            .Where(x => x.ExecutionTime != null)
            .Where(x => x.ExecutionTime >= oneSecondAgo)  // Ignore old tickers (fallback handles them)
            .WhereCanAcquire(_lockHolder)
            // Part of the earliest-time query: a delayed child that still waits must not hide the rows due after it.
            .WhereChainGateOpen();
    
        // Find the earliest ticker within our window
        var minExecutionTime = await baseQuery
            .OrderBy(x => x.ExecutionTime)
            .Select(x => x.ExecutionTime)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (minExecutionTime == null)
            return [];
    
        // Round the minimum execution time down to its second
        var minSecond = new DateTime(minExecutionTime.Value.Year, minExecutionTime.Value.Month,
            minExecutionTime.Value.Day, minExecutionTime.Value.Hour,
            minExecutionTime.Value.Minute, minExecutionTime.Value.Second,
            DateTimeKind.Utc);
    
        // Fetch all tickers within that complete second (this ensures we get all tickers in the same second)
        var maxExecutionTime = minSecond.AddSeconds(1);
    
        return await baseQuery
            .Where(x => x.ExecutionTime >= minSecond && x.ExecutionTime < maxExecutionTime)
            .OrderBy(x => x.ExecutionTime)
            .Select(MappingExtensions.ForQueueTimeTickers<TTimeTicker>())
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }
    
    public async Task<byte[]> GetTimeTickerRequest(Guid tickerId, CancellationToken cancellationToken = default)
    {
        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var dbContext = session.Context;
        return await dbContext.Set<TTimeTicker>()
            .AsNoTracking()
            .Where(x => x.Id == tickerId)
            .Select(x => x.Request)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }
    
    public Task ReleaseDeadNodeTimeTickerResources(string instanceIdentifier, CancellationToken cancellationToken = default)
        => ReleaseDeadNodeTimeTickerResourcesCore(instanceIdentifier, null, cancellationToken);

    public Task ReleaseDeadNodeTimeTickerResources(string instanceIdentifier, DateTime lockedBefore, CancellationToken cancellationToken = default)
        => ReleaseDeadNodeTimeTickerResourcesCore(instanceIdentifier, lockedBefore, cancellationToken);

    // With lockedBefore only the rows that node locked before that moment are released; every claim sets LockedAt, so a row
    // this run claimed since keeps its lock. Without it, the release takes every row of that node, as before.
    private async Task ReleaseDeadNodeTimeTickerResourcesCore(string instanceIdentifier, DateTime? lockedBefore, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var dbContext = session.Context;

        var waiting = dbContext.Set<TTimeTicker>().WhereCanAcquire(instanceIdentifier);
        if (lockedBefore is { } waitingBefore)
            waiting = waiting.Where(x => x.LockHolder == instanceIdentifier && x.LockedAt < waitingBefore);

        await waiting
            .ExecuteUpdateAsync(setter => setter
                .SetProperty(x => x.LockHolder, _ => null)
                .SetProperty(x => x.LockedAt, _ => null)
                .SetProperty(x => x.Status, TickerStatus.Idle)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);

        var running = dbContext.Set<TTimeTicker>()
            .Where(x => x.LockHolder == instanceIdentifier && x.Status == TickerStatus.InProgress);
        if (lockedBefore is { } runningBefore)
            running = running.Where(x => x.LockedAt < runningBefore);

        await running
            .ExecuteUpdateAsync(setter => setter
                .SetProperty(x => x.LockHolder, _ => null)
                .SetProperty(x => x.LockedAt, _ => null)
                .SetProperty(x => x.Status, TickerStatus.Idle)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
    }
    #endregion

    public async Task<TimeTickerEntity[]> AcquireImmediateTimeTickersAsync(Guid[] ids, CancellationToken cancellationToken = default)
    {
        if (ids == null || ids.Length == 0)
            return [];

        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var dbContext = session.Context;
        var now = _clock.UtcNow;
        var set = dbContext.Set<TTimeTicker>();
        var won = new Dictionary<Guid, DateTime>(ids.Length);

        // A delayed child is claimable only once its parent's status lets it run. Read first, then claim by id: an UPDATE
        // through the parent join does not translate on every provider (SQLite), and the gate does not close again once
        // open, as a finished parent status does not change.
        var idList = ids.ToList(); // a List, not the array: see the note on span overloads in TickerManager
        var gateOpen = await set
            .AsNoTracking()
            .Where(x => idList.Contains(x.Id))
            .WhereRunNowGateOpen()
            .Select(x => x.Id)
            // A cancelled caller is answered by the loop below (it stops before claiming), not by a throw here.
            .ToListAsync(CancellationToken.None).ConfigureAwait(false);
        if (gateOpen.Count == 0)
            return [];

        try
        {
            // One conditional write per row, so the rows this call won are known exactly. A batch update followed by a
            // read of "InProgress for this node" would also return a row this node's fallback had taken earlier.
            foreach (var id in ids)
            {
                // A cancelled caller stops the loop but not the writes: a row won here is already InProgress and must be
                // returned to be dispatched, the rows not written yet stay Idle for the fallback.
                if (cancellationToken.IsCancellationRequested)
                    break;

                if (!gateOpen.Contains(id))
                    continue;

                var claimMarker = _clock.NextClaimMarker();
                int affected;
                try
                {
                    affected = await set
                        .Where(x => x.Id == id)
                        .WhereCanAcquire(_lockHolder)
                        .ExecuteUpdateAsync(setter => setter
                            .SetProperty(x => x.LockHolder, _lockHolder)
                            .SetProperty(x => x.LockedAt, now)
                            .SetProperty(x => x.Status, TickerStatus.InProgress)
                            .SetProperty(x => x.UpdatedAt, claimMarker), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // The write may have landed before its reply was lost: put it back with the rows already won.
                    won[id] = claimMarker;
                    throw;
                }

                if (affected > 0)
                    won[id] = claimMarker;
            }

            if (won.Count == 0)
                return [];

            // Return the acquired tickers for immediate execution, with children, each with the marker its claim wrote.
            var wonIds = won.Keys.ToList();
            var acquired = await set
                .AsNoTracking()
                .Where(x => wonIds.Contains(x.Id) && x.LockHolder == _lockHolder && x.Status == TickerStatus.InProgress)
                .Select(MappingExtensions.ForQueueTimeTickers<TTimeTicker>())
                .ToArrayAsync(CancellationToken.None)
                .ConfigureAwait(false);

            foreach (var ticker in acquired)
                ticker.UpdatedAt = won[ticker.Id];

            return acquired;
        }
        catch (Exception failure) when (won.Count > 0)
        {
            // The caller gets no rows to run, so the rows won so far would stay InProgress until this node restarts.
            await ClaimPutBack.PutBackOrThrowAsync(won, TickerType.TimeTicker, failure, TryReleaseTimeTickerClaim).ConfigureAwait(false);
            throw;
        }
    }
        
    #region Core_Cron_Ticker_Methods
    public async Task MigrateDefinedCronTickers((string Function, string Expression)[] cronTickers, CancellationToken cancellationToken = default)
    {
        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var dbContext = session.Context;
        var now = _clock.UtcNow;

        var cronSet = dbContext.Set<TCronTicker>();

        // Build the complete list of registered function names to detect orphaned tickers.
        // This covers functions whose InitIdentifier was cleared by a dashboard edit.
        // Use List<string> instead of HashSet<string> for broader EF Core provider compatibility
        // (some providers like Devart MySQL don't assign type mappings to HashSet parameters).
        var allRegisteredFunctions = TickerFunctionProvider.TickerFunctions.Keys.ToList();

        // Orphan cleanup is intentionally narrowed to *seeded* crons (those that
        // carry an InitIdentifier from the code-defined-cron migration), so
        // dashboard- or API-created crons are never deleted on restart.
        //
        // Skipping non-seeded crons here is safe: they were never tied to a
        // code definition in the first place, so "the code definition went
        // away" doesn't apply to them.
        var orphanedCron = await cronSet
            .Where(c => !string.IsNullOrEmpty(c.InitIdentifier)
                        && !allRegisteredFunctions.Contains(c.Function))
            .Select(c => c.Id)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        var orphanedCronList = orphanedCron.ToList();
        if (orphanedCronList.Count > 0)
        {
            // Delete related occurrences first (if any), then the cron tickers
            await dbContext.Set<CronTickerOccurrenceEntity<TCronTicker>>()
                .Where(o => orphanedCronList.Contains(o.CronTickerId))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            await cronSet
                .Where(c => orphanedCronList.Contains(c.Id))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            await SeedDefinedCronTickersAsync(dbContext, cronTickers, now, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Another node that started at the same time inserted a seeded cron first: its row has the same id (the id of
            // the function name), so this insert met the key. Read again and add only what is still missing. A second
            // failure is not that race and is thrown.
            dbContext.ChangeTracker.Clear();
            await SeedDefinedCronTickersAsync(dbContext, cronTickers, now, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SeedDefinedCronTickersAsync(TDbContext dbContext, (string Function, string Expression)[] cronTickers, DateTime now,
        CancellationToken cancellationToken)
    {
        var functions = cronTickers.Select(x => x.Function).ToList();
        var cronSet = dbContext.Set<TCronTicker>();

        // Load existing (remaining) cron tickers for the current function set
        var existing = await cronSet
            .Where(c => functions.Contains(c.Function))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var existingByFunction = existing
            .GroupBy(c => c.Function)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<TCronTicker>)g.ToList());

        // The id of a function name can be held by a cron of another function (a seeded cron whose function was changed on
        // the dashboard). The cron is then seeded under another id; that one start has no guard against a second node.
        var seedIds = cronTickers.Where(x => !existingByFunction.ContainsKey(x.Function)).Select(x => SeededCronTickers.IdFor(x.Function)).ToList();
        var takenIds = seedIds.Count == 0
            ? []
            : await cronSet.Where(c => seedIds.Contains(c.Id)).Select(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var (function, expression) in cronTickers)
        {
            if (existingByFunction.TryGetValue(function, out var rows))
            {
                // Only the rows the seed owns take the expression in code; a user's cron of the same function keeps its own.
                foreach (var cron in SeededCronTickers.Owned(rows))
                {
                    if (!string.Equals(cron.Expression, expression, StringComparison.Ordinal))
                    {
                        cron.Expression = expression;
                        cron.UpdatedAt = now;
                    }
                }

                if (SeededCronTickers.IsAmbiguous(rows))
                    _logger.LogWarning(SeededCronTickers.AmbiguousWarning, function, expression, rows.Count);
            }
            else
            {
                // Insert new seeded cron ticker
                var seedId = SeededCronTickers.IdFor(function);
                var entity = new TCronTicker
                {
                    Id = takenIds.Contains(seedId) ? Guid.NewGuid() : seedId,
                    Function = function,
                    Expression = expression,
                    InitIdentifier = SeededCronTickers.InitIdentifierFor(function),
                    CreatedAt = now,
                    UpdatedAt = now,
                    Request = Array.Empty<byte>()
                };
                await cronSet.AddAsync(entity, cancellationToken).ConfigureAwait(false);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
        
    public async Task<CronTickerEntity[]> GetAllCronTickerExpressions(CancellationToken cancellationToken = default)
    {
        var result = await RedisContext.GetOrSetArrayAsync(
            cacheKey: "cron:expressions",
            factory: async (ct) =>
            {
                using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                var dbContext = session.Context;
                return await dbContext.Set<TCronTicker>()
                    .AsNoTracking()
                    .Where(x => x.IsEnabled)
                    .Select(MappingExtensions.ForCronTickerExpressions<CronTickerEntity>())
                    .ToArrayAsync(ct)
                    .ConfigureAwait(false);
            },
            expiration: TimeSpan.FromMinutes(10),
            cancellationToken: cancellationToken);

        if (result != null)
            return result;

        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var dbContext = session.Context;
        return await dbContext.Set<TCronTicker>()
            .AsNoTracking()
            .Where(x => x.IsEnabled)
            .Select(MappingExtensions.ForCronTickerExpressions<CronTickerEntity>())
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }
    #endregion

    #region Core_Cron_TickerOccurrence_Methods
    public async Task UpdateCronTickerOccurrence(InternalFunctionContext functionContext, CancellationToken cancellationToken)
    {
        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var dbContext = session.Context;
        await dbContext.Set<CronTickerOccurrenceEntity<TCronTicker>>()
            .Where(x => x.Id == functionContext.TickerId)
            .ExecuteUpdateAsync(setter => setter.UpdateCronTickerOccurrence<TCronTicker>(functionContext, _clock.UtcNow), cancellationToken)
            .ConfigureAwait(false);
    }
    
    public async IAsyncEnumerable<CronTickerOccurrenceEntity<TCronTicker>> QueueTimedOutCronTickerOccurrences([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var fallbackThreshold = now.AddSeconds(-1);  // Fallback picks up tasks older than main 1-second window

        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var dbContext = session.Context;
        var context = dbContext.Set<CronTickerOccurrenceEntity<TCronTicker>>();
            
        var cronTickersToUpdate = await context
            .AsNoTracking()
            .Include(x => x.CronTicker)
            .Where(x => x.Status == TickerStatus.Idle || x.Status == TickerStatus.Queued)
            .Where(x => x.ExecutionTime <= fallbackThreshold)  // Only tasks older than 1 second
            .Select(MappingExtensions.ForQueueCronTickerOccurrence<CronTickerOccurrenceEntity<TCronTicker>, TCronTicker>())
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

        foreach (var cronTickerOccurrence in cronTickersToUpdate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var claimMarker = _clock.NextClaimMarker();
            int affected;
            try
            {
                affected = await context
                    .Where(x => x.Id == cronTickerOccurrence.Id && x.UpdatedAt == cronTickerOccurrence.UpdatedAt)
                    .Where(x => x.Status == TickerStatus.Idle || x.Status == TickerStatus.Queued)
                    .ExecuteUpdateAsync(setter => setter
                        .SetProperty(x => x.LockHolder, _lockHolder)
                        .SetProperty(x => x.LockedAt, now)
                        .SetProperty(x => x.UpdatedAt, claimMarker)
                        .SetProperty(x => x.Status, TickerStatus.InProgress), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                await ClaimPutBack.PutBackOrThrowAsync(cronTickerOccurrence.Id, claimMarker, TickerType.CronTickerOccurrence, failure, TryReleaseCronOccurrenceClaim).ConfigureAwait(false);
                throw;
            }

            if(affected <= 0)
                continue;

            // The row was read before the claim; it is handed back with the marker the claim wrote, for a put-back.
            cronTickerOccurrence.UpdatedAt = claimMarker;
            yield return cronTickerOccurrence;
        }
    }
    
    public async Task<bool> TryMarkCronOccurrenceInProgress(Guid id, DateTime claimMarker, CancellationToken cancellationToken = default)
    {
        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var dbContext = session.Context;

        var affected = await dbContext.Set<CronTickerOccurrenceEntity<TCronTicker>>()
            .Where(x => x.Id == id)
            .WhereQueuedBy(_lockHolder)
            .ExecuteUpdateAsync(setter => setter
                .SetProperty(x => x.Status, TickerStatus.InProgress)
                .SetProperty(x => x.UpdatedAt, claimMarker), cancellationToken).ConfigureAwait(false);

        return affected > 0;
    }

    public async Task<bool> TryReleaseCronOccurrenceClaim(Guid id, DateTime claimMarker, CancellationToken cancellationToken = default)
    {
        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var dbContext = session.Context;
        var now = _clock.UtcNow;

        var affected = await dbContext.Set<CronTickerOccurrenceEntity<TCronTicker>>()
            .Where(x => x.Id == id && x.Status == TickerStatus.InProgress && x.LockHolder == _lockHolder && x.UpdatedAt == claimMarker)
            .ExecuteUpdateAsync(setter => setter
                .SetProperty(x => x.Status, TickerStatus.Idle)
                .SetProperty(x => x.LockHolder, _ => null)
                .SetProperty(x => x.LockedAt, _ => null)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken).ConfigureAwait(false);

        return affected > 0;
    }

    public Task ReleaseDeadNodeOccurrenceResources(string instanceIdentifier, CancellationToken cancellationToken = default)
        => ReleaseDeadNodeOccurrenceResourcesCore(instanceIdentifier, null, cancellationToken);

    public Task ReleaseDeadNodeOccurrenceResources(string instanceIdentifier, DateTime lockedBefore, CancellationToken cancellationToken = default)
        => ReleaseDeadNodeOccurrenceResourcesCore(instanceIdentifier, lockedBefore, cancellationToken);

    // Same rule as ReleaseDeadNodeTimeTickerResourcesCore.
    private async Task ReleaseDeadNodeOccurrenceResourcesCore(string instanceIdentifier, DateTime? lockedBefore, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var dbContext = session.Context;

        var waiting = dbContext.Set<CronTickerOccurrenceEntity<TCronTicker>>().WhereCanAcquire(instanceIdentifier);
        if (lockedBefore is { } waitingBefore)
            waiting = waiting.Where(x => x.LockHolder == instanceIdentifier && x.LockedAt < waitingBefore);

        await waiting
            .ExecuteUpdateAsync(setter => setter
                .SetProperty(x => x.LockHolder, _ => null)
                .SetProperty(x => x.LockedAt, _ => null)
                .SetProperty(x => x.Status, TickerStatus.Idle)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);

        var running = dbContext.Set<CronTickerOccurrenceEntity<TCronTicker>>()
            .Where(x => x.LockHolder == instanceIdentifier && x.Status == TickerStatus.InProgress);
        if (lockedBefore is { } runningBefore)
            running = running.Where(x => x.LockedAt < runningBefore);

        await running
            .ExecuteUpdateAsync(setter => setter
                .SetProperty(x => x.LockHolder, _ => null)
                .SetProperty(x => x.LockedAt, _ => null)
                .SetProperty(x => x.Status, TickerStatus.Idle)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
    }
    
    public async Task ReleaseAcquiredCronTickerOccurrences(Guid[] occurrenceIds, CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var dbContext = session.Context;

        var idList = occurrenceIds.ToList();
        var baseQuery = idList.Count == 0
            ? dbContext.Set<CronTickerOccurrenceEntity<TCronTicker>>()
            : dbContext.Set<CronTickerOccurrenceEntity<TCronTicker>>().Where(x => idList.Contains(x.Id));
           
        await baseQuery
            .WhereCanAcquire(_lockHolder)
            .ExecuteUpdateAsync(setter => setter
                .SetProperty(x => x.LockHolder, _ => null)
                .SetProperty(x => x.LockedAt, _ => null)
                .SetProperty(x => x.Status, TickerStatus.Idle)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
    }
    
    public async IAsyncEnumerable<CronTickerOccurrenceEntity<TCronTicker>> QueueCronTickerOccurrences((DateTime Key, InternalManagerContext[] Items) cronTickerOccurrences, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var executionTime = cronTickerOccurrences.Key;

        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var dbContext = session.Context;
        var context = dbContext.Set<CronTickerOccurrenceEntity<TCronTicker>>();
        
        foreach (var item in cronTickerOccurrences.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (item.NextCronOccurrence is null)
            {
                var itemToAdd = new CronTickerOccurrenceEntity<TCronTicker>
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
                
                var affectAdded = await context.Upsert(itemToAdd)
                    .On(x => new { x.ExecutionTime, x.CronTickerId })
                    .NoUpdate()
                    .RunAsync(cancellationToken).ConfigureAwait(false);;

                if (affectAdded <= 0)
                    continue;
                
                itemToAdd.CronTicker = new TCronTicker
                {
                    Id = item.Id,
                    Function = item.FunctionName,
                    InitIdentifier = _lockHolder,
                    Expression = item.Expression,
                    Retries = item.Retries,
                    RetryIntervals = item.RetryIntervals
                };
                yield return itemToAdd;
            }
            else
            {
                var affectedUpdate = await context
                    .Where(x => x.Id == item.NextCronOccurrence.Id)
                    .Where(x => x.ExecutionTime == executionTime)
                    .WhereCanAcquire(_lockHolder)
                    .ExecuteUpdateAsync(prop => prop
                            .SetProperty(y => y.LockHolder, _lockHolder)
                            .SetProperty(y => y.LockedAt, now)
                            .SetProperty(y => y.UpdatedAt, now)
                            .SetProperty(y => y.Status, TickerStatus.Queued),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (affectedUpdate <= 0)
                    continue;
                
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
    
    public async Task<CronTickerOccurrenceEntity<TCronTicker>> GetEarliestAvailableCronOccurrence(Guid[] ids, CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var mainSchedulerThreshold = now.AddSeconds(-1);
        var idList = ids.ToList();
        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var dbContext = session.Context;
        return await dbContext.Set<CronTickerOccurrenceEntity<TCronTicker>>()
            .AsNoTracking()
            .Include(x => x.CronTicker)
            .Where(x => idList.Contains(x.CronTickerId))
            .Where(x => x.ExecutionTime >= mainSchedulerThreshold)  // Only items within the 1-second main scheduler window
            .WhereCanAcquire(_lockHolder)
            .OrderBy(x => x.ExecutionTime)
            .Select(MappingExtensions.ForLatestQueuedCronTickerOccurrence<CronTickerOccurrenceEntity<TCronTicker>, TCronTicker>())
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }
    
    public async Task<byte[]> GetCronTickerOccurrenceRequest(Guid tickerId, CancellationToken cancellationToken = default)
    {
        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var dbContext = session.Context;
        return await dbContext.Set<CronTickerOccurrenceEntity<TCronTicker>>()
            .AsNoTracking()
            .Include(x => x.CronTicker)
            .Where(x => x.Id == tickerId)
            .Select(x => x.CronTicker.Request)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }
    
    public async Task UpdateCronTickerOccurrencesWithUnifiedContext(Guid[] cronOccurrenceIds, InternalFunctionContext functionContext,
        CancellationToken cancellationToken = default)
    {
        var idList = cronOccurrenceIds.ToList();
        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var dbContext = session.Context;
        await dbContext.Set<CronTickerOccurrenceEntity<TCronTicker>>()
            .Where(x => idList.Contains(x.Id))
            .ExecuteUpdateAsync(setter => setter.UpdateCronTickerOccurrence<TCronTicker>(functionContext, _clock.UtcNow), cancellationToken)
            .ConfigureAwait(false);
    }
    
    public async Task<int> SkipStaleCronOccurrencesAsync(TimeSpan staleThreshold, CancellationToken cancellationToken = default)
    {
        if (staleThreshold <= TimeSpan.Zero)
            return 0;

        var now = _clock.UtcNow;
        var cutoff = now - staleThreshold;

        using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var dbContext = session.Context;

        return await dbContext.Set<CronTickerOccurrenceEntity<TCronTicker>>()
            .Where(x => x.Status == TickerStatus.Idle || x.Status == TickerStatus.Queued)
            .Where(x => x.ExecutionTime < cutoff)
            .ExecuteUpdateAsync(setter => setter
                .SetProperty(x => x.Status, TickerStatus.Skipped)
                .SetProperty(x => x.SkippedReason, "Missed: occurrence was pending when the application restarted")
                .SetProperty(x => x.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
    }

    #endregion
}
