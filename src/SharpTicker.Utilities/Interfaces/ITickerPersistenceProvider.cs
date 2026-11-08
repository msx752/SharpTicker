using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Utilities.Interfaces
{
    public interface ITickerPersistenceProvider<TTimeTicker, TCronTicker>
    where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
    where TCronTicker : CronTickerEntity, new()
    {
        #region Time_Ticker_Core_Methods
        IAsyncEnumerable<TimeTickerEntity> QueueTimeTickers(TimeTickerEntity[] timeTickers, CancellationToken cancellationToken = default);
        IAsyncEnumerable<TimeTickerEntity> QueueTimedOutTimeTickers(CancellationToken cancellationToken = default);
        Task ReleaseAcquiredTimeTickers(Guid[] timeTickerIds, CancellationToken cancellationToken = default);
        Task<TimeTickerEntity[]> GetEarliestTimeTickers(CancellationToken cancellationToken = default);
        Task<int> UpdateTimeTicker(InternalFunctionContext functionContext, CancellationToken cancellationToken = default);
        Task<byte[]> GetTimeTickerRequest(Guid id, CancellationToken cancellationToken);
        Task UpdateTimeTickersWithUnifiedContext(Guid[] timeTickerIds, InternalFunctionContext functionContext, CancellationToken cancellationToken = default);
        Task<TimeTickerEntity[]> AcquireImmediateTimeTickersAsync(Guid[] ids, CancellationToken cancellationToken = default);

        // true: this call moved the row from Queued (held by this node) to InProgress and wrote claimMarker as its UpdatedAt;
        // the caller may run the job. The marker comes from the node's clock (ClaimMarkers) and is unique per node, so the
        // claim can be found again and put back if the job never starts, even when this call failed after the write landed.
        // Default bodies keep the unfenced behaviour for custom providers and do not write the marker.
        async Task<bool> TryMarkTimeTickerInProgress(Guid id, DateTime claimMarker, CancellationToken cancellationToken = default)
        {
            await UpdateTimeTickersWithUnifiedContext([id], new InternalFunctionContext().SetProperty(x => x.Status, TickerStatus.InProgress), cancellationToken).ConfigureAwait(false);
            return true;
        }

        // Puts back a claim this node made whose job never started: the row goes back to Idle with its lock cleared, only
        // while it is still InProgress, held by this node and carries claimMarker as its UpdatedAt. Anything else (the
        // fallback or another node took the row, the job ran, the row is gone) is left alone and the call returns false.
        // The default body writes nothing and returns false, so a custom provider keeps today's behaviour: such a row stays
        // InProgress and the scheduler logs it.
        Task<bool> TryReleaseTimeTickerClaim(Guid id, DateTime claimMarker, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
        #endregion
        
        #region Cron_Ticker_Core_Methods
        Task MigrateDefinedCronTickers((string Function, string Expression)[] cronTickers, CancellationToken cancellationToken = default);
        Task<CronTickerEntity[]> GetAllCronTickerExpressions(CancellationToken cancellationToken);
        Task ReleaseDeadNodeTimeTickerResources(string instanceIdentifier, CancellationToken cancellationToken = default);

        // Releases only what the node locked before lockedBefore. A node that releases what an earlier run of itself left
        // behind passes the moment this run started, so rows it has claimed since are kept and not run a second time.
        // The default body keeps the unfenced release for custom providers.
        Task ReleaseDeadNodeTimeTickerResources(string instanceIdentifier, DateTime lockedBefore, CancellationToken cancellationToken = default)
            => ReleaseDeadNodeTimeTickerResources(instanceIdentifier, cancellationToken);
        #endregion
        
        #region Cron_TickerOccurrence_Core_Methods
        Task<CronTickerOccurrenceEntity<TCronTicker>> GetEarliestAvailableCronOccurrence(Guid[] ids, CancellationToken cancellationToken = default);
        IAsyncEnumerable<CronTickerOccurrenceEntity<TCronTicker>> QueueCronTickerOccurrences((DateTime Key, InternalManagerContext[] Items) cronTickerOccurrences, CancellationToken cancellationToken = default);
        IAsyncEnumerable<CronTickerOccurrenceEntity<TCronTicker>> QueueTimedOutCronTickerOccurrences(CancellationToken cancellationToken = default);
        Task UpdateCronTickerOccurrence(InternalFunctionContext functionContext, CancellationToken cancellationToken = default);
        Task ReleaseAcquiredCronTickerOccurrences(Guid[] occurrenceIds, CancellationToken cancellationToken = default);
        Task<byte[]> GetCronTickerOccurrenceRequest(Guid tickerId, CancellationToken cancellationToken = default);
        Task UpdateCronTickerOccurrencesWithUnifiedContext(Guid[] timeTickerIds, InternalFunctionContext functionContext, CancellationToken cancellationToken = default);
        Task ReleaseDeadNodeOccurrenceResources(string instanceIdentifier, CancellationToken cancellationToken = default);

        // Same contract as the time ticker overload with lockedBefore.
        Task ReleaseDeadNodeOccurrenceResources(string instanceIdentifier, DateTime lockedBefore, CancellationToken cancellationToken = default)
            => ReleaseDeadNodeOccurrenceResources(instanceIdentifier, cancellationToken);
        Task<int> SkipStaleCronOccurrencesAsync(TimeSpan staleThreshold, CancellationToken cancellationToken = default) => Task.FromResult(0);

        // Same contract as TryMarkTimeTickerInProgress, for a cron occurrence row.
        async Task<bool> TryMarkCronOccurrenceInProgress(Guid id, DateTime claimMarker, CancellationToken cancellationToken = default)
        {
            await UpdateCronTickerOccurrencesWithUnifiedContext([id], new InternalFunctionContext().SetProperty(x => x.Status, TickerStatus.InProgress), cancellationToken).ConfigureAwait(false);
            return true;
        }

        // Same contract as TryReleaseTimeTickerClaim, for a cron occurrence row.
        Task<bool> TryReleaseCronOccurrenceClaim(Guid id, DateTime claimMarker, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
        #endregion
        
        #region Queryable
        ITickerQueryable<TTimeTicker> TimeTickersQuery();
        ITickerQueryable<TCronTicker> CronTickersQuery();
        ITickerQueryable<CronTickerOccurrenceEntity<TCronTicker>> CronTickerOccurrencesQuery();
        #endregion

        #region Time_Ticker_Shared_Methods
        Task<TTimeTicker> GetTimeTickerById(Guid id, CancellationToken cancellationToken = default);
        Task<TTimeTicker[]> GetTimeTickers(Expression<Func<TTimeTicker, bool>> predicate, CancellationToken cancellationToken = default);
        Task<PaginationResult<TTimeTicker>> GetTimeTickersPaginated(Expression<Func<TTimeTicker, bool>> predicate, int pageNumber, int pageSize, CancellationToken cancellationToken = default);
        Task<int> AddTimeTickers(TTimeTicker[] tickers, CancellationToken cancellationToken = default);
        Task<int> UpdateTimeTickers(TTimeTicker[] tickers, CancellationToken cancellationToken = default);
        Task<int> RemoveTimeTickers(Guid[] tickerIds, CancellationToken cancellationToken = default);
        #endregion

        #region Cron_Ticker_Shared_Methods
        Task<TCronTicker> GetCronTickerById(Guid id, CancellationToken cancellationToken);
        Task<TCronTicker[]> GetCronTickers(Expression<Func<TCronTicker, bool>> predicate, CancellationToken cancellationToken);
        Task<PaginationResult<TCronTicker>> GetCronTickersPaginated(Expression<Func<TCronTicker, bool>> predicate, int pageNumber, int pageSize, CancellationToken cancellationToken = default);
        Task<int> InsertCronTickers(TCronTicker[] tickers, CancellationToken cancellationToken);
        Task<int> UpdateCronTickers(TCronTicker[] cronTicker, CancellationToken cancellationToken);
        Task<int> RemoveCronTickers(Guid[] cronTickerIds, CancellationToken cancellationToken);
        #endregion
        
        #region Cron_TickerOccurrence_Shared_Methods
        Task<CronTickerOccurrenceEntity<TCronTicker>[]> GetAllCronTickerOccurrences(Expression<Func<CronTickerOccurrenceEntity<TCronTicker>, bool>> predicate, CancellationToken cancellationToken = default);
        Task<PaginationResult<CronTickerOccurrenceEntity<TCronTicker>>> GetAllCronTickerOccurrencesPaginated(Expression<Func<CronTickerOccurrenceEntity<TCronTicker>, bool>> predicate, int pageNumber, int pageSize, CancellationToken cancellationToken = default);
        Task<int> InsertCronTickerOccurrences(CronTickerOccurrenceEntity<TCronTicker>[] cronTickerOccurrences, CancellationToken cancellationToken);
        Task<int> RemoveCronTickerOccurrences(Guid[] cronTickerOccurrences, CancellationToken cancellationToken);
        Task<CronTickerOccurrenceEntity<TCronTicker>[]> AcquireImmediateCronOccurrencesAsync(Guid[] occurrenceIds, CancellationToken cancellationToken = default);
        #endregion
    }
}
