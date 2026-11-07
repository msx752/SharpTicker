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

        // true: this call moved the row from Queued (held by this node) to InProgress; the caller may run the job.
        // Default bodies keep the unfenced behaviour for custom providers.
        async Task<bool> TryMarkTimeTickerInProgress(Guid id, CancellationToken cancellationToken = default)
        {
            await UpdateTimeTickersWithUnifiedContext([id], new InternalFunctionContext().SetProperty(x => x.Status, TickerStatus.InProgress), cancellationToken).ConfigureAwait(false);
            return true;
        }
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
        async Task<bool> TryMarkCronOccurrenceInProgress(Guid id, CancellationToken cancellationToken = default)
        {
            await UpdateCronTickerOccurrencesWithUnifiedContext([id], new InternalFunctionContext().SetProperty(x => x.Status, TickerStatus.InProgress), cancellationToken).ConfigureAwait(false);
            return true;
        }
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
