using Microsoft.EntityFrameworkCore;

using System;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Models;
using System.Collections.Generic;

namespace SharpTicker.EntityFrameworkCore.Infrastructure
{
    internal class TickerEfCorePersistenceProvider<TDbContext, TTimeTicker, TCronTicker> :
        BasePersistenceProvider<TDbContext, TTimeTicker, TCronTicker>,
        ITickerPersistenceProvider<TTimeTicker, TCronTicker>
        where TDbContext : DbContext
        where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
        where TCronTicker : CronTickerEntity, new()
    {
        public TickerEfCorePersistenceProvider(IServiceProvider serviceProvider, ITickerClock clock, SchedulerOptionsBuilder optionsBuilder, ISharpTickerRedisContext  redisContext)
            :  base(serviceProvider, clock, optionsBuilder, redisContext) { }
        
        #region Queryable

        public ITickerQueryable<TTimeTicker> TimeTickersQuery()
        {
            return new EfTickerQueryable<TDbContext, TTimeTicker>(
                _serviceProvider,
                (query, relations) =>
                {
                    foreach (var relation in relations)
                    {
                        query = relation switch
                        {
                            TickerRelation.Children => query
                                .Include(x => x.Children),
                            TickerRelation.ChildrenDeep => query
                                .Include(x => x.Children)
                                .ThenInclude(x => x.Children)
                                .AsSingleQuery(),
                            _ => query
                        };
                    }
                    return query;
                });
        }

        public ITickerQueryable<TCronTicker> CronTickersQuery()
        {
            return new EfTickerQueryable<TDbContext, TCronTicker>(_serviceProvider);
        }

        public ITickerQueryable<CronTickerOccurrenceEntity<TCronTicker>> CronTickerOccurrencesQuery()
        {
            return new EfTickerQueryable<TDbContext, CronTickerOccurrenceEntity<TCronTicker>>(
                _serviceProvider,
                (query, relations) =>
                {
                    foreach (var relation in relations)
                    {
                        if (relation == TickerRelation.CronTicker)
                            query = query.Include(x => x.CronTicker);
                    }
                    return query;
                });
        }

        #endregion

        #region Time_Ticker_Implementations

        public async Task<TTimeTicker> GetTimeTickerById(Guid id, CancellationToken cancellationToken = default)
        {
            using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var dbContext = session.Context;
            return await dbContext.Set<TTimeTicker>()
                .AsNoTracking()
                .Include(x => x.Children)
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<TTimeTicker[]> GetTimeTickers(Expression<Func<TTimeTicker, bool>> predicate, CancellationToken cancellationToken)
        {
            using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var dbContext = session.Context;

            var baseQuery = dbContext.Set<TTimeTicker>()
                .Include(x => x.Children)
                .ThenInclude(x => x.Children)
                .AsSingleQuery()
                .AsNoTracking();
            
            if (predicate != null)
                baseQuery = baseQuery.Where(predicate);
            
            return await baseQuery
                .Where(x => x.ParentId == null)
                .OrderByDescending(x => x.ExecutionTime)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        
        public async Task<PaginationResult<TTimeTicker>> GetTimeTickersPaginated(
            Expression<Func<TTimeTicker, bool>> predicate, 
            int pageNumber, 
            int pageSize, 
            CancellationToken cancellationToken)
        {
            using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var dbContext = session.Context;

            var baseQuery = dbContext.Set<TTimeTicker>()
                .Include(x => x.Children)
                .ThenInclude(x => x.Children)
                .AsSingleQuery()
                .AsNoTracking();
            
            if (predicate != null)
                baseQuery = baseQuery.Where(predicate);
            
            baseQuery = baseQuery
                .Where(x => x.ParentId == null)
                .OrderByDescending(x => x.ExecutionTime);
            
            return await baseQuery.ToPaginatedListAsync(pageNumber, pageSize, cancellationToken).ConfigureAwait(false);
        }

        public async Task<int> AddTimeTickers(TTimeTicker[] tickers, CancellationToken cancellationToken)
        {
            using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var dbContext = session.Context;

            await dbContext.Set<TTimeTicker>()
                .AddRangeAsync(tickers, cancellationToken);
            
            return await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<int> UpdateTimeTickers(TTimeTicker[] timeTickers, CancellationToken cancellationToken = default)
        {
            using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var dbContext = session.Context;

            dbContext.Set<TTimeTicker>().UpdateRange(timeTickers);
             
            return await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<int> RemoveTimeTickers(Guid[] timeTickerIds, CancellationToken cancellationToken)
        {
            using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var dbContext = session.Context;

            // Load the entities to be deleted (including children for cascade delete)
            var idList = timeTickerIds.ToList();
            var tickersToDelete = await dbContext.Set<TTimeTicker>()
                .Include(x => x.Children)
                .ThenInclude(x => x.Children) // Include grandchildren if needed
                .AsSingleQuery()
                .Where(x => idList.Contains(x.Id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            
            // The model does not cascade a delete to the children (DeleteBehavior.NoAction), so the children and grandchildren
            // are removed here with their parent. Left behind they would have no parent and no execution time, and would
            // never run. A set by reference, because a ticker asked for can also be the child of another one asked for.
            var withDescendants = new HashSet<TTimeTicker>(ReferenceEqualityComparer.Instance);
            foreach (var ticker in tickersToDelete)
            {
                withDescendants.Add(ticker);
                foreach (var child in ticker.Children ?? [])
                {
                    withDescendants.Add(child);
                    foreach (var grandchild in child.Children ?? [])
                        withDescendants.Add(grandchild);
                }
            }

            dbContext.Set<TTimeTicker>().RemoveRange(withDescendants);

            return await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        #endregion

        #region Cron_Ticker_Implementations

        public async Task<TCronTicker> GetCronTickerById(Guid id, CancellationToken cancellationToken)
        {
            using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var dbContext = session.Context;
            return await dbContext.Set<TCronTicker>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false);;
        }

        public async Task<TCronTicker[]> GetCronTickers(Expression<Func<TCronTicker, bool>> predicate,
            CancellationToken cancellationToken)
        {
            using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var dbContext = session.Context;

            var baseQuery = dbContext.Set<TCronTicker>()
                .AsNoTracking();
            
            if (predicate != null)
                baseQuery = baseQuery.Where(predicate);
            
            return await baseQuery
                .OrderByDescending(x => x.CreatedAt)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        
        public async Task<PaginationResult<TCronTicker>> GetCronTickersPaginated(
            Expression<Func<TCronTicker, bool>> predicate, 
            int pageNumber, 
            int pageSize, 
            CancellationToken cancellationToken)
        {
            using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var dbContext = session.Context;

            var baseQuery = dbContext.Set<TCronTicker>()
                .AsNoTracking();
            
            if (predicate != null)
                baseQuery = baseQuery.Where(predicate);
            
            baseQuery = baseQuery.OrderByDescending(x => x.CreatedAt);
            
            return await baseQuery.ToPaginatedListAsync(pageNumber, pageSize, cancellationToken).ConfigureAwait(false);
        }

        public async Task<int> InsertCronTickers(TCronTicker[] tickers, CancellationToken cancellationToken)
        {
            using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var dbContext = session.Context;

            await dbContext.Set<TCronTicker>().AddRangeAsync(tickers, cancellationToken).ConfigureAwait(false);
            
            var result = await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            
            if(RedisContext.HasRedisConnection)
                await RedisContext.DistributedCache.RemoveAsync("cron:expressions", cancellationToken).ConfigureAwait(false);
            
            return result;
        }

        public async Task<int> UpdateCronTickers(TCronTicker[] cronTickers, CancellationToken cancellationToken = default)
        {
            using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var dbContext = session.Context;

            dbContext.Set<TCronTicker>().UpdateRange(cronTickers);

            var result =  await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            
            if(RedisContext.HasRedisConnection)
                await RedisContext.DistributedCache.RemoveAsync("cron:expressions", cancellationToken).ConfigureAwait(false);
            
            return result;
        }

        public async Task<int> RemoveCronTickers(Guid[] cronTickerIds, CancellationToken cancellationToken)
        {
            using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var dbContext = session.Context;
            var idList = cronTickerIds.ToList();
            var result = await dbContext.Set<TCronTicker>().Where(x => idList.Contains(x.Id))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            
            if(RedisContext.HasRedisConnection)
                await RedisContext.DistributedCache.RemoveAsync("cron:expressions", cancellationToken).ConfigureAwait(false);
            
            return result;
        }

        #endregion

        #region Cron_TickerOccurrence_Implementations
        public async Task<CronTickerOccurrenceEntity<TCronTicker>[]> GetAllCronTickerOccurrences(Expression<Func<CronTickerOccurrenceEntity<TCronTicker>, bool>> predicate, CancellationToken cancellationToken = default)
        {
            using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var dbContext = session.Context;
            var cronTickerOccurrenceContext = dbContext.Set<CronTickerOccurrenceEntity<TCronTicker>>()
                .AsNoTracking();

            var query = predicate == null
                ? cronTickerOccurrenceContext.Include(x => x.CronTicker)
                : cronTickerOccurrenceContext.Include(x => x.CronTicker).Where(predicate);
            
            return await query.OrderByDescending(x => x.ExecutionTime).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        
        public async Task<PaginationResult<CronTickerOccurrenceEntity<TCronTicker>>> GetAllCronTickerOccurrencesPaginated(
            Expression<Func<CronTickerOccurrenceEntity<TCronTicker>, bool>> predicate, 
            int pageNumber, 
            int pageSize, 
            CancellationToken cancellationToken)
        {
            using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var dbContext = session.Context;

            var baseQuery = dbContext.Set<CronTickerOccurrenceEntity<TCronTicker>>()
                .Include(x => x.CronTicker)
                .AsNoTracking();

            if (predicate != null)
                baseQuery = baseQuery.Where(predicate);
            
            baseQuery = baseQuery.OrderByDescending(x => x.ExecutionTime);
            
            return await baseQuery.ToPaginatedListAsync(pageNumber, pageSize, cancellationToken).ConfigureAwait(false);
        }

        public async Task<int> InsertCronTickerOccurrences(CronTickerOccurrenceEntity<TCronTicker>[] cronTickerOccurrences, CancellationToken cancellationToken = default)
        {
            using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var dbContext = session.Context;

            await dbContext.Set<CronTickerOccurrenceEntity<TCronTicker>>().AddRangeAsync(cronTickerOccurrences, cancellationToken).ConfigureAwait(false);

            return await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<int> RemoveCronTickerOccurrences(Guid[] cronTickerOccurrences, CancellationToken cancellationToken = default)
        {
            using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var dbContext = session.Context;
            var idList = cronTickerOccurrences.ToList();
            return await dbContext.Set<CronTickerOccurrenceEntity<TCronTicker>>()
                .Where(x => idList.Contains(x.Id))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<CronTickerOccurrenceEntity<TCronTicker>[]> AcquireImmediateCronOccurrencesAsync(Guid[] occurrenceIds, CancellationToken cancellationToken = default)
        {
            if (occurrenceIds == null || occurrenceIds.Length == 0)
                return Array.Empty<CronTickerOccurrenceEntity<TCronTicker>>();

            using var session = await CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var dbContext = session.Context;
            var now = _clock.UtcNow;
            var set = dbContext.Set<CronTickerOccurrenceEntity<TCronTicker>>();
            var won = new Dictionary<Guid, DateTime>(occurrenceIds.Length);

            try
            {
                // One conditional write per row, so the rows this call won are known exactly. A batch update followed by a
                // read of "InProgress for this node" also returned a row this node's fallback was already running, and a
                // read by the marker would miss rows on a column that keeps less than milliseconds.
                foreach (var id in occurrenceIds)
                {
                    // A cancelled caller stops the loop but not the writes: a row won here is already InProgress and must
                    // be returned to be dispatched, the rows not written yet stay Idle for the fallback.
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    var claimMarker = _clock.NextClaimMarker();
                    var affected = await set
                        .Where(x => x.Id == id)
                        .WhereCanAcquire(_lockHolder)
                        .ExecuteUpdateAsync(setter => setter
                            .SetProperty(x => x.LockHolder, _lockHolder)
                            .SetProperty(x => x.LockedAt, now)
                            .SetProperty(x => x.Status, TickerStatus.InProgress)
                            .SetProperty(x => x.UpdatedAt, claimMarker), CancellationToken.None)
                        .ConfigureAwait(false);

                    if (affected > 0)
                        won[id] = claimMarker;
                }

                if (won.Count == 0)
                    return Array.Empty<CronTickerOccurrenceEntity<TCronTicker>>();

                // Return the occurrences this call claimed, with CronTicker populated, each with the marker its claim wrote.
                var wonIds = won.Keys.ToList();
                var acquired = await set
                    .AsNoTracking()
                    .Where(x => wonIds.Contains(x.Id) && x.LockHolder == _lockHolder && x.Status == TickerStatus.InProgress)
                    .Include(x => x.CronTicker)
                    .ToArrayAsync(CancellationToken.None)
                    .ConfigureAwait(false);

                foreach (var occurrence in acquired)
                    occurrence.UpdatedAt = won[occurrence.Id];

                return acquired;
            }
            catch (Exception failure) when (won.Count > 0)
            {
                // The caller gets no rows to run, so the rows won so far would stay InProgress until this node restarts.
                await ClaimPutBack.PutBackOrThrowAsync(won, failure, TryReleaseCronOccurrenceClaim).ConfigureAwait(false);
                throw;
            }
        }

        #endregion
    }
}
