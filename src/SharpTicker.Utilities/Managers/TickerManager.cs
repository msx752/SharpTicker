using NCrontab;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Exceptions;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Utilities.Managers
{
    internal class
        TickerManager<TTimeTicker, TCronTicker> :
        ICronTickerManager<TCronTicker>,
        ITimeTickerManager<TTimeTicker>
        where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
        where TCronTicker : CronTickerEntity, new()
    {
        private readonly ITickerPersistenceProvider<TTimeTicker, TCronTicker> _persistenceProvider;
        private readonly ISharpTickerHostScheduler _sharpTickerHostScheduler;
        private readonly ITickerClock _clock;
        private readonly ISharpTickerNotificationHubSender _notificationHubSender;
        private readonly ISharpTickerDispatcher _dispatcher;
        private readonly TickerExecutionContext _executionContext;
        public TickerManager(
            ITickerPersistenceProvider<TTimeTicker, TCronTicker> persistenceProvider,
            ISharpTickerHostScheduler sharpTickerHostScheduler,
            ITickerClock clock,
            ISharpTickerNotificationHubSender notificationHubSender,
            TickerExecutionContext executionContext,
            ISharpTickerDispatcher dispatcher
            )
        {
            _persistenceProvider = persistenceProvider;
            _sharpTickerHostScheduler = sharpTickerHostScheduler ?? throw new ArgumentNullException(nameof(sharpTickerHostScheduler));
            _clock = clock;
            _notificationHubSender = notificationHubSender;
            _executionContext = executionContext ?? throw new ArgumentNullException(nameof(executionContext));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        Task<TickerResult<TCronTicker>> ICronTickerManager<TCronTicker>.AddAsync(TCronTicker entity, CancellationToken cancellationToken)
            => AddCronTickerAsync(entity, cancellationToken);

        Task<TickerResult<TTimeTicker>> ITimeTickerManager<TTimeTicker>.AddAsync(TTimeTicker entity, CancellationToken cancellationToken)
            => AddTimeTickerAsync(entity, cancellationToken);

        Task<TickerResult<TCronTicker>> ICronTickerManager<TCronTicker>.UpdateAsync(TCronTicker cronTicker, CancellationToken cancellationToken)
            => UpdateCronTickerAsync(cronTicker, cancellationToken);

        Task<TickerResult<TTimeTicker>> ITimeTickerManager<TTimeTicker>.UpdateAsync(TTimeTicker timeTicker, CancellationToken cancellationToken)
            => UpdateTimeTickerAsync(timeTicker, cancellationToken);

        Task<TickerResult<TCronTicker>> ICronTickerManager<TCronTicker>.DeleteAsync(Guid id, CancellationToken cancellationToken)
            => DeleteCronTickerAsync(id, cancellationToken);

        Task<TickerResult<TTimeTicker>> ITimeTickerManager<TTimeTicker>.DeleteAsync(Guid id, CancellationToken cancellationToken)
            => DeleteTimeTickerAsync(id, cancellationToken);

        Task<TickerResult<List<TTimeTicker>>> ITimeTickerManager<TTimeTicker>.AddBatchAsync(List<TTimeTicker> entities, CancellationToken cancellationToken)
            => AddTimeTickersBatchAsync(entities, cancellationToken);

        Task<TickerResult<List<TTimeTicker>>> ITimeTickerManager<TTimeTicker>.UpdateBatchAsync(List<TTimeTicker> timeTickers, CancellationToken cancellationToken)
            => UpdateTimeTickersBatchAsync(timeTickers, cancellationToken);

        Task<TickerResult<TTimeTicker>> ITimeTickerManager<TTimeTicker>.DeleteBatchAsync(List<Guid> ids, CancellationToken cancellationToken)
            => DeleteTimeTickersBatchAsync(ids, cancellationToken);

        Task<TickerResult<TTimeTicker>> ITimeTickerManager<TTimeTicker>.AddAsync<TFunction>(DateTime? executionTime, CancellationToken cancellationToken)
        {
            var functionName = TickerFunctionProvider.GetFunctionName<TFunction>();
            var entity = new TTimeTicker
            {
                Function = functionName,
                ExecutionTime = executionTime
            };
            return AddTimeTickerAsync(entity, cancellationToken);
        }

        Task<TickerResult<TTimeTicker>> ITimeTickerManager<TTimeTicker>.AddAsync<TFunction, TRequest>(DateTime? executionTime, TRequest request, CancellationToken cancellationToken)
        {
            var functionName = TickerFunctionProvider.GetFunctionName<TFunction>();
            var entity = new TTimeTicker
            {
                Function = functionName,
                ExecutionTime = executionTime,
                Request = TickerHelper.CreateTickerRequest(request)
            };
            return AddTimeTickerAsync(entity, cancellationToken);
        }

        Task<TickerResult<List<TCronTicker>>> ICronTickerManager<TCronTicker>.AddBatchAsync(List<TCronTicker> entities, CancellationToken cancellationToken)
            => AddCronTickersBatchAsync(entities, cancellationToken);

        Task<TickerResult<List<TCronTicker>>> ICronTickerManager<TCronTicker>.UpdateBatchAsync(List<TCronTicker> cronTickers, CancellationToken cancellationToken)
            => UpdateCronTickersBatchAsync(cronTickers, cancellationToken);

        Task<TickerResult<TCronTicker>> ICronTickerManager<TCronTicker>.DeleteBatchAsync(List<Guid> ids, CancellationToken cancellationToken)
            => DeleteCronTickersBatchAsync(ids, cancellationToken);

        private async Task<TickerResult<TTimeTicker>> AddTimeTickerAsync(TTimeTicker entity, CancellationToken cancellationToken)
        {
            if (entity.Id == Guid.Empty)
                entity.Id = Guid.NewGuid();

            if (TickerFunctionProvider.TickerFunctions.All(x => x.Key != entity?.Function))
                return new TickerResult<TTimeTicker>(
                    new TickerValidatorException($"Cannot find TickerFunction with name {entity?.Function}"));
            
            entity.ExecutionTime = entity.ExecutionTime == null
                ? _clock.UtcNow
                : ConvertToUtcIfNeeded(entity.ExecutionTime.Value);

            entity.CreatedAt = _clock.UtcNow;
            entity.UpdatedAt = _clock.UtcNow;
            
            try
            {
                var now = _clock.UtcNow;
                var executionTime = entity.ExecutionTime!.Value;

                // Persist first
                await _persistenceProvider.AddTimeTickers([entity], cancellationToken: cancellationToken);

                // Notify the dashboard BEFORE the dispatch path — for tickers
                // scheduled at "now" the dispatcher acquires + runs immediately
                // and execution often finishes before this method returns. If
                // the SignalR push happens after dispatch, the dashboard never
                // sees the row in its Idle/Queued/InProgress states; it just
                // appears already Done. Sending the scope.changed event up
                // front guarantees the list refetches the moment the row is
                // committed to the DB.
                await _notificationHubSender.AddTimeTickerNotifyAsync(entity.Id).ConfigureAwait(false);

                // Only try to dispatch immediately if dispatcher is enabled (background services running)
                if (_dispatcher.IsEnabled && executionTime <= now.AddSeconds(1))
                {
                    // Acquire and mark InProgress in one provider call
                    var acquired = await _persistenceProvider
                        .AcquireImmediateTimeTickersAsync([entity.Id], cancellationToken)
                        .ConfigureAwait(false);

                    if (acquired.Length > 0)
                    {
                        var contexts = BuildImmediateContextsFromNonGeneric(acquired);
                        CacheFunctionReferences(contexts.AsSpan());
                        await _dispatcher.DispatchAsync(contexts, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    _sharpTickerHostScheduler.RestartIfNeeded(executionTime);
                }

                return new TickerResult<TTimeTicker>(entity);
            }
            catch (Exception e)
            {
                return new TickerResult<TTimeTicker>(e);
            }
        }

        private async Task<TickerResult<TCronTicker>> AddCronTickerAsync(TCronTicker entity, CancellationToken cancellationToken)
        {
           
            if (entity.Id == Guid.Empty)
                entity.Id = Guid.NewGuid();

            if (TickerFunctionProvider.TickerFunctions.All(x => x.Key != entity?.Function))
                return new TickerResult<TCronTicker>(
                    new TickerValidatorException($"Cannot find TickerFunction with name {entity?.Function}"));

            if (CronScheduleCache.GetNextOccurrenceOrDefault(entity.Expression, _clock.UtcNow) is not { } nextOccurrence)
                return new TickerResult<TCronTicker>(
                    new TickerValidatorException($"Cannot parse expression {entity.Expression}"));

            if (CronRangeCheck.HasBackwardsRange(entity.Expression))
                return new TickerResult<TCronTicker>(
                    new TickerValidatorException($"The cron expression {entity.Expression} has a range that runs backwards, which is not supported"));

            
            entity.CreatedAt = _clock.UtcNow;
            entity.UpdatedAt = _clock.UtcNow;
            
            try
            {
                await _persistenceProvider.InsertCronTickers([entity], cancellationToken: cancellationToken);

                _sharpTickerHostScheduler.RestartIfNeeded(nextOccurrence);

                await _notificationHubSender.AddCronTickerNotifyAsync(entity);

                return new TickerResult<TCronTicker>(entity);
            }
            catch (Exception e)
            {
                return new TickerResult<TCronTicker>(e);
            }
        }

        private async Task<TickerResult<TTimeTicker>> UpdateTimeTickerAsync(TTimeTicker timeTicker, CancellationToken cancellationToken)
        {
            if (timeTicker is null)
                return new TickerResult<TTimeTicker>(
                    new TickerValidatorException($"Ticker must not be null!"));
            
            if(timeTicker.ExecutionTime == null)
                return new TickerResult<TTimeTicker>(
                    new TickerValidatorException($"Ticker ExecutionTime must not be null!"));
            
            timeTicker.UpdatedAt = _clock.UtcNow;
            timeTicker.ExecutionTime = ConvertToUtcIfNeeded(timeTicker.ExecutionTime.Value);
            
            try {
                if (await _persistenceProvider.GetTimeTickerById(timeTicker.Id, cancellationToken).ConfigureAwait(false) is null)
                    return new TickerResult<TTimeTicker>(
                        new TickerValidatorException($"Cannot find time ticker with id {timeTicker.Id}"));

                var affectedRows = await _persistenceProvider.UpdateTimeTickers([timeTicker], cancellationToken: cancellationToken).ConfigureAwait(false);
                
                if (_executionContext.Functions.Any(x => x.TickerId == timeTicker.Id))
                    _sharpTickerHostScheduler.Restart();
                else
                    _sharpTickerHostScheduler.RestartIfNeeded(timeTicker.ExecutionTime);
                
                return new TickerResult<TTimeTicker>(timeTicker, affectedRows);
            }
            catch (Exception e)
            {
                return new TickerResult<TTimeTicker>(e);
            }
        }

        private async Task<TickerResult<TCronTicker>> UpdateCronTickerAsync(TCronTicker cronTicker, CancellationToken cancellationToken = default)
        {
            if (cronTicker is null)
                return new TickerResult<TCronTicker>(new Exception($"Cron ticker must not be null!"));

            if (TickerFunctionProvider.TickerFunctions.All(x => x.Key != cronTicker?.Function))
                return new TickerResult<TCronTicker>(
                    new TickerValidatorException($"Cannot find TickerFunction with name {cronTicker.Function}"));


            if (CronScheduleCache.GetNextOccurrenceOrDefault(cronTicker.Expression, _clock.UtcNow) is not { } nextOccurrence)
                return new TickerResult<TCronTicker>(
                    new TickerValidatorException($"Cannot parse expression {cronTicker.Expression}"));

            if (CronRangeCheck.HasBackwardsRange(cronTicker.Expression))
                return new TickerResult<TCronTicker>(
                    new TickerValidatorException($"The cron expression {cronTicker.Expression} has a range that runs backwards, which is not supported"));
            
            try
            {
                if (await _persistenceProvider.GetCronTickerById(cronTicker.Id, cancellationToken).ConfigureAwait(false) is null)
                    return new TickerResult<TCronTicker>(
                        new TickerValidatorException($"Cannot find cron ticker with id {cronTicker.Id}"));

                cronTicker.UpdatedAt = _clock.UtcNow;

                var affectedRows = await _persistenceProvider.UpdateCronTickers([cronTicker], cancellationToken: cancellationToken);

                if (_executionContext.Functions.FirstOrDefault(x => x.ParentId == cronTicker.Id) is { } internalFunction)
                {
                    internalFunction.ResetUpdateProps()
                        .SetProperty(x => x.ExecutionTime, nextOccurrence);
                    
                    await _persistenceProvider.UpdateCronTickerOccurrence(internalFunction, cancellationToken: cancellationToken).ConfigureAwait(false);
                    
                    _sharpTickerHostScheduler.Restart();
                }
                
                _sharpTickerHostScheduler.RestartIfNeeded(nextOccurrence);

                return new TickerResult<TCronTicker>(cronTicker, affectedRows);
            }
            catch (Exception e)
            {
                return new TickerResult<TCronTicker>(e);
            }
        }

        private async Task<TickerResult<TCronTicker>> DeleteCronTickerAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var affectedRows = await _persistenceProvider.RemoveCronTickers([id], cancellationToken: cancellationToken);
            
            if(affectedRows > 0 && _executionContext.Functions.Any(x => x.ParentId == id))
                _sharpTickerHostScheduler.Restart();

            return new TickerResult<TCronTicker>(affectedRows);
        }


        private async Task<TickerResult<TTimeTicker>> DeleteTimeTickerAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var affectedRows = await _persistenceProvider.RemoveTimeTickers([id], cancellationToken: cancellationToken);

            if(affectedRows > 0 && _executionContext.Functions.Any(x => x.TickerId == id))
                _sharpTickerHostScheduler.Restart();

            // The dashboard list refreshes on this notification; without it a deleted row stays on screen until a reload
            if (affectedRows > 0)
                await _notificationHubSender.RemoveTimeTickerNotifyAsync(id).ConfigureAwait(false);

            return new TickerResult<TTimeTicker>(affectedRows);
        }
        
        private static DateTime ConvertToUtcIfNeeded(DateTime dateTime)
        {
            // If DateTime.Kind is Unspecified, assume it's in system timezone
            return dateTime.Kind switch
            {
                DateTimeKind.Utc => dateTime,
                DateTimeKind.Local => dateTime.ToUniversalTime(),
                DateTimeKind.Unspecified => TimeZoneInfo.ConvertTimeToUtc(dateTime, CronScheduleCache.TimeZoneInfo),
                _ => dateTime
            };
        }

        // Batch operations implementation
        private static void CacheFunctionReferences(Span<InternalFunctionContext> functions)
        {
            for (var i = 0; i < functions.Length; i++)
            {
                ref var context = ref functions[i];
                if (TickerFunctionProvider.TickerFunctions.TryGetValue(context.FunctionName, out var tickerItem))
                {
                    context.CachedDelegate = tickerItem.Delegate;
                    context.CachedPriority = tickerItem.Priority;
                    context.CachedMaxConcurrency = tickerItem.MaxConcurrency;
                }

                if (context.TimeTickerChildren is { Count: > 0 })
                {
                    var childrenSpan = CollectionsMarshal.AsSpan(context.TimeTickerChildren);
                    CacheFunctionReferences(childrenSpan);
                }
            }
        }

        private static InternalFunctionContext[] BuildImmediateContextsFromNonGeneric(IEnumerable<TimeTickerEntity> tickers)
        {
            return tickers.Select(BuildContextFromNonGeneric).ToArray();
        }

        private static InternalFunctionContext BuildContextFromNonGeneric(TimeTickerEntity ticker)
        {
            return new InternalFunctionContext
            {
                FunctionName = ticker.Function,
                TickerId = ticker.Id,
                Type = Enums.TickerType.TimeTicker,
                Retries = ticker.Retries,
                RetryIntervals = ticker.RetryIntervals,
                ParentId = ticker.ParentId,
                ExecutionTime = ticker.ExecutionTime ?? DateTime.UtcNow,
                RunCondition = ticker.RunCondition ?? Enums.RunCondition.OnAnyCompletedStatus,
                TimeTickerChildren = ticker.Children.Select(BuildContextFromNonGeneric).ToList()
            };
        }

        private async Task<TickerResult<List<TTimeTicker>>> AddTimeTickersBatchAsync(List<TTimeTicker> entities, CancellationToken cancellationToken = default)
        {
            if (entities == null || entities.Count == 0)
                return new TickerResult<List<TTimeTicker>>(entities ?? new List<TTimeTicker>());

            var tickerFunctionsHashSet = new HashSet<string>(TickerFunctionProvider.TickerFunctions.Keys);
            var immediateTickers = new List<Guid>();
            var now = _clock.UtcNow;
            DateTime earliestForNonImmediate = default;
            foreach (var entity in entities)
            {
                if (entity is null)
                    return new TickerResult<List<TTimeTicker>>(new TickerValidatorException("Ticker must not be null!"));

                if (entity.Id == Guid.Empty)
                    entity.Id = Guid.NewGuid();

                if (!tickerFunctionsHashSet.Contains(entity.Function))
                    return new TickerResult<List<TTimeTicker>>(
                        new TickerValidatorException($"Cannot find TickerFunction with name {entity?.Function}"));

                entity.ExecutionTime ??= now;
                entity.ExecutionTime = ConvertToUtcIfNeeded(entity.ExecutionTime.Value);

                // Align with single AddTimeTickerAsync: initialize timestamps
                entity.CreatedAt = now;
                entity.UpdatedAt = now;

                if (entity.ExecutionTime.Value <= now.AddSeconds(1))
                    immediateTickers.Add(entity.Id);
                else if (earliestForNonImmediate == default || entity.ExecutionTime <= earliestForNonImmediate)
                    earliestForNonImmediate = entity.ExecutionTime.Value;
            }
            
            try
            {
                await _persistenceProvider.AddTimeTickers(entities.ToArray(), cancellationToken: cancellationToken);

                await _notificationHubSender.AddTimeTickersBatchNotifyAsync().ConfigureAwait(false);

                // Only try to dispatch immediately if dispatcher is enabled (background services running)
                if (_dispatcher.IsEnabled && immediateTickers.Count > 0)
                {
                    var acquired = await _persistenceProvider
                        .AcquireImmediateTimeTickersAsync(immediateTickers.ToArray(), cancellationToken)
                        .ConfigureAwait(false);

                    if (acquired.Length > 0)
                    {
                        var contexts = BuildImmediateContextsFromNonGeneric(acquired);
                        CacheFunctionReferences(contexts.AsSpan());
                        await _dispatcher.DispatchAsync(contexts, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (earliestForNonImmediate != default)
                {
                    _sharpTickerHostScheduler.RestartIfNeeded(earliestForNonImmediate);
                }

                return new TickerResult<List<TTimeTicker>>(entities);
            }
            catch (Exception e)
            {
                return new TickerResult<List<TTimeTicker>>(e);
            }
        }

        private async Task<TickerResult<List<TCronTicker>>> AddCronTickersBatchAsync(List<TCronTicker> entities, CancellationToken cancellationToken = default)
        {
            if (entities == null || entities.Count == 0)
                return new TickerResult<List<TCronTicker>>(entities ?? new List<TCronTicker>());

            var validEntities = new List<TCronTicker>();
            var errors = new List<Exception>();
            var nextOccurrences = new List<DateTime>();
            
            foreach (var entity in entities)
            {
                if (entity is null)
                {
                    errors.Add(new TickerValidatorException("Ticker must not be null!"));
                    continue;
                }

                if (entity.Id == Guid.Empty)
                    entity.Id = Guid.NewGuid();

                if (TickerFunctionProvider.TickerFunctions.All(x => x.Key != entity?.Function))
                {
                    errors.Add(new TickerValidatorException($"Cannot find TickerFunction with name {entity?.Function}"));
                    continue;
                }

                if (CronScheduleCache.GetNextOccurrenceOrDefault(entity.Expression, _clock.UtcNow) is not { } nextOccurrence)
                {
                    errors.Add(new TickerValidatorException($"Cannot parse expression {entity.Expression}"));
                    continue;
                }

                if (CronRangeCheck.HasBackwardsRange(entity.Expression))
                {
                    errors.Add(new TickerValidatorException($"The cron expression {entity.Expression} has a range that runs backwards, which is not supported"));
                    continue;
                }

                entity.CreatedAt = _clock.UtcNow;
                entity.UpdatedAt = _clock.UtcNow;
                
                validEntities.Add(entity);
                nextOccurrences.Add(nextOccurrence);
            }
            
            if (errors.Any())
                return new TickerResult<List<TCronTicker>>(errors.First());
            
            try
            {
                await _persistenceProvider.InsertCronTickers(validEntities.ToArray(), cancellationToken: cancellationToken);

                if (validEntities.Count != 0)
                {
                    // Restart scheduler for earliest occurrence
                    var earliestOccurrence = nextOccurrences.Min();
                    _sharpTickerHostScheduler.RestartIfNeeded(earliestOccurrence);

                    // Send notifications for all
                    foreach (var entity in validEntities)
                    {
                        await _notificationHubSender.AddCronTickerNotifyAsync(entity);
                    }
                }

                return new TickerResult<List<TCronTicker>>(validEntities);
            }
            catch (Exception e)
            {
                return new TickerResult<List<TCronTicker>>(e);
            }
        }

        private async Task<TickerResult<List<TTimeTicker>>> UpdateTimeTickersBatchAsync(List<TTimeTicker> timeTickers, CancellationToken cancellationToken = default)
        {
            if (timeTickers == null || timeTickers.Count == 0)
                return new TickerResult<List<TTimeTicker>>(timeTickers ?? new List<TTimeTicker>());

            var validTickers = new List<TTimeTicker>();
            var errors = new List<Exception>();
            var needsRestart = false;
            
            foreach (var timeTicker in timeTickers)
            {
                if (timeTicker is null)
                {
                    errors.Add(new TickerValidatorException("Ticker must not be null!"));
                    continue;
                }
                
                if (timeTicker.ExecutionTime == null)
                {
                    errors.Add(new TickerValidatorException("Ticker ExecutionTime must not be null!"));
                    continue;
                }
                
                timeTicker.UpdatedAt = _clock.UtcNow;
                timeTicker.ExecutionTime = ConvertToUtcIfNeeded(timeTicker.ExecutionTime.Value);
                
                if (_executionContext.Functions.Any(x => x.TickerId == timeTicker.Id))
                    needsRestart = true;
                    
                validTickers.Add(timeTicker);
            }
            
            if (errors.Any())
                return new TickerResult<List<TTimeTicker>>(errors.First());
            
            try 
            {
                var ids = validTickers.Select(x => x.Id).Distinct().ToList(); // a List, not an array: with C# 14 Contains on an array binds to the span overload, which EF Core on net8 cannot translate
                var existing = (await _persistenceProvider.GetTimeTickers(x => ids.Contains(x.Id), cancellationToken).ConfigureAwait(false))
                    .Select(x => x.Id).ToHashSet();
                if (ids.Where(id => !existing.Contains(id)).Cast<Guid?>().FirstOrDefault() is { } missing)
                    return new TickerResult<List<TTimeTicker>>(
                        new TickerValidatorException($"Cannot find time ticker with id {missing}"));

                var affectedRows = await _persistenceProvider.UpdateTimeTickers(validTickers.ToArray(), cancellationToken: cancellationToken).ConfigureAwait(false);
                
                if (needsRestart)
                    _sharpTickerHostScheduler.Restart();
                else if (validTickers.Any())
                {
                    var earliestExecution = validTickers.Min(t => t.ExecutionTime);
                    _sharpTickerHostScheduler.RestartIfNeeded(earliestExecution);
                }
                
                return new TickerResult<List<TTimeTicker>>(validTickers, affectedRows);
            }
            catch (Exception e)
            {
                return new TickerResult<List<TTimeTicker>>(e);
            }
        }

        private async Task<TickerResult<List<TCronTicker>>> UpdateCronTickersBatchAsync(List<TCronTicker> cronTickers, CancellationToken cancellationToken = default)
        {
            if (cronTickers == null || cronTickers.Count == 0)
                return new TickerResult<List<TCronTicker>>(cronTickers ?? new List<TCronTicker>());

            var validTickers = new List<TCronTicker>();
            var errors = new List<Exception>();
            var nextOccurrences = new List<DateTime>();
            var needsRestart = false;
            var internalFunctionsToUpdate = new List<InternalFunctionContext>();
            
            foreach (var cronTicker in cronTickers)
            {
                if (cronTicker is null)
                {
                    errors.Add(new TickerValidatorException("Cron ticker must not be null!"));
                    continue;
                }

                if (TickerFunctionProvider.TickerFunctions.All(x => x.Key != cronTicker?.Function))
                {
                    errors.Add(new TickerValidatorException($"Cannot find TickerFunction with name {cronTicker.Function}"));
                    continue;
                }

                if (CronScheduleCache.GetNextOccurrenceOrDefault(cronTicker.Expression, _clock.UtcNow) is not { } nextOccurrence)
                {
                    errors.Add(new TickerValidatorException($"Cannot parse expression {cronTicker.Expression}"));
                    continue;
                }

                if (CronRangeCheck.HasBackwardsRange(cronTicker.Expression))
                {
                    errors.Add(new TickerValidatorException($"The cron expression {cronTicker.Expression} has a range that runs backwards, which is not supported"));
                    continue;
                }
                
                cronTicker.UpdatedAt = _clock.UtcNow;
                
                if (_executionContext.Functions.FirstOrDefault(x => x.ParentId == cronTicker.Id) is { } internalFunction)
                {
                    internalFunction.ResetUpdateProps()
                        .SetProperty(x => x.ExecutionTime, nextOccurrence);
                    internalFunctionsToUpdate.Add(internalFunction);
                    needsRestart = true;
                }
                
                validTickers.Add(cronTicker);
                nextOccurrences.Add(nextOccurrence);
            }
            
            if (errors.Any())
                return new TickerResult<List<TCronTicker>>(errors.First());
            
            try
            {
                var ids = validTickers.Select(x => x.Id).Distinct().ToList(); // a List, not an array: with C# 14 Contains on an array binds to the span overload, which EF Core on net8 cannot translate
                var existing = (await _persistenceProvider.GetCronTickers(x => ids.Contains(x.Id), cancellationToken).ConfigureAwait(false))
                    .Select(x => x.Id).ToHashSet();
                if (ids.Where(id => !existing.Contains(id)).Cast<Guid?>().FirstOrDefault() is { } missing)
                    return new TickerResult<List<TCronTicker>>(
                        new TickerValidatorException($"Cannot find cron ticker with id {missing}"));

                var affectedRows = await _persistenceProvider.UpdateCronTickers(validTickers.ToArray(), cancellationToken: cancellationToken);

                // Update internal functions for those that need it
                foreach (var internalFunction in internalFunctionsToUpdate)
                {
                    await _persistenceProvider.UpdateCronTickerOccurrence(internalFunction, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                
                if (needsRestart)
                    _sharpTickerHostScheduler.Restart();
                
                else if (nextOccurrences.Any())
                {
                    var earliestOccurrence = nextOccurrences.Min();
                    _sharpTickerHostScheduler.RestartIfNeeded(earliestOccurrence);
                }

                return new TickerResult<List<TCronTicker>>(validTickers, affectedRows);
            }
            catch (Exception e)
            {
                return new TickerResult<List<TCronTicker>>(e);
            }
        }

        private async Task<TickerResult<TTimeTicker>> DeleteTimeTickersBatchAsync(List<Guid> ids, CancellationToken cancellationToken = default)
        {
            if (ids == null || ids.Count == 0)
                return new TickerResult<TTimeTicker>(0);

            var affectedRows = await _persistenceProvider.RemoveTimeTickers(ids.ToArray(), cancellationToken: cancellationToken);
            
            if (affectedRows > 0 && _executionContext.Functions.Any(x => ids.Contains(x.TickerId)))
                _sharpTickerHostScheduler.Restart();

            return new TickerResult<TTimeTicker>(affectedRows);
        }

        private async Task<TickerResult<TCronTicker>> DeleteCronTickersBatchAsync(List<Guid> ids, CancellationToken cancellationToken = default)
        {
            if (ids == null || ids.Count == 0)
                return new TickerResult<TCronTicker>(0);

            var affectedRows = await _persistenceProvider.RemoveCronTickers(ids.ToArray(), cancellationToken: cancellationToken);
            
            if (affectedRows > 0 && _executionContext.Functions.Any(x => ids.Contains(x.ParentId ?? Guid.Empty)))
                _sharpTickerHostScheduler.Restart();

            return new TickerResult<TCronTicker>(affectedRows);
        }
    }
}
