using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using SharpTicker.Utilities.Entities;
using Microsoft.Extensions.Logging;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Exceptions;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Utilities.Managers
{
    internal class InternalTickerManager<TTimeTicker, TCronTicker> : IInternalTickerManager
        where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
        where TCronTicker : CronTickerEntity, new()
    {
        private readonly ITickerPersistenceProvider<TTimeTicker, TCronTicker> _persistenceProvider;
        private readonly ITickerClock _clock;
        private readonly ISharpTickerNotificationHubSender _notificationHubSender;
        private readonly ILogger _logger;

        public InternalTickerManager(
            ITickerPersistenceProvider<TTimeTicker, TCronTicker> persistenceProvider,
            ITickerClock clock,
            ISharpTickerNotificationHubSender notificationHubSender)
            : this(persistenceProvider, clock, notificationHubSender, null)
        {
        }

        public InternalTickerManager(
            ITickerPersistenceProvider<TTimeTicker, TCronTicker> persistenceProvider,
            ITickerClock clock,
            ISharpTickerNotificationHubSender notificationHubSender,
            ILogger<InternalTickerManager<TTimeTicker, TCronTicker>> logger)
        {
            _persistenceProvider = persistenceProvider;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _notificationHubSender = notificationHubSender;
            _logger = logger;
        }
        
        public async Task<(TimeSpan TimeRemaining, InternalFunctionContext[] Functions)> GetNextTickers(CancellationToken cancellationToken = default)
        {
            var now = _clock.UtcNow;

            var minCronGroupTask = GetEarliestCronTickerGroupAsync(cancellationToken);
            var minTimeTickersTask = _persistenceProvider.GetEarliestTimeTickers(cancellationToken);

            await Task.WhenAll(minCronGroupTask, minTimeTickersTask).ConfigureAwait(false);

            var minCronGroup = await minCronGroupTask.ConfigureAwait(false);
            var minTimeTickers = await minTimeTickersTask.ConfigureAwait(false);

            var cronTime = minCronGroup?.Key;
            var timeTickerTime = minTimeTickers.Length > 0
                ? minTimeTickers[0].ExecutionTime
                : null;

            if (cronTime is null && timeTickerTime is null)
                return (Timeout.InfiniteTimeSpan, []);

            TimeSpan timeRemaining;
            bool includeCron = false;
            bool includeTimeTickers = false;

            if (cronTime is null)
            {
                includeTimeTickers = true;
                timeRemaining = SafeRemaining(timeTickerTime!.Value, now);
            }
            else if (timeTickerTime is null)
            {
                includeCron = true;
                timeRemaining = SafeRemaining(cronTime.Value, now);
            }
            else
            {
                var cronSecond = new DateTime(cronTime.Value.Year, cronTime.Value.Month, cronTime.Value.Day,
                    cronTime.Value.Hour, cronTime.Value.Minute, cronTime.Value.Second);
                var timeSecond = new DateTime(timeTickerTime.Value.Year, timeTickerTime.Value.Month, timeTickerTime.Value.Day,
                    timeTickerTime.Value.Hour, timeTickerTime.Value.Minute, timeTickerTime.Value.Second);

                if (cronSecond == timeSecond)
                {
                    includeCron = true;
                    includeTimeTickers = true;
                    var earliest = cronTime < timeTickerTime ? cronTime.Value : timeTickerTime.Value;
                    timeRemaining = SafeRemaining(earliest, now);
                }
                else if (cronTime < timeTickerTime)
                {
                    includeCron = true;
                    timeRemaining = SafeRemaining(cronTime.Value, now);
                }
                else
                {
                    includeTimeTickers = true;
                    timeRemaining = SafeRemaining(timeTickerTime.Value, now);
                }
            }

            if (!includeCron && !includeTimeTickers)
                return (Timeout.InfiniteTimeSpan, []);

            InternalFunctionContext[] cronFunctions = [];
            InternalFunctionContext[] timeFunctions = [];

            if (includeCron && minCronGroup is not null)
                cronFunctions = await QueueNextCronTickersAsync(minCronGroup.Value, cancellationToken).ConfigureAwait(false);

            if (includeTimeTickers && minTimeTickers.Length > 0)
                timeFunctions = await QueueNextTimeTickersAsync(minTimeTickers, cancellationToken).ConfigureAwait(false);

            if (cronFunctions.Length == 0 && timeFunctions.Length == 0)
                return (timeRemaining, []);

            if (cronFunctions.Length == 0)
                return (timeRemaining, timeFunctions);

            if (timeFunctions.Length == 0)
                return (timeRemaining, cronFunctions);

            var merged = new InternalFunctionContext[cronFunctions.Length + timeFunctions.Length];
            cronFunctions.AsSpan().CopyTo(merged.AsSpan(0, cronFunctions.Length));
            timeFunctions.AsSpan().CopyTo(merged.AsSpan(cronFunctions.Length, timeFunctions.Length));

            return (timeRemaining, merged);
        }

        private static TimeSpan SafeRemaining(DateTime target, DateTime now)
        {
            var remaining = target - now;
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }

        private async Task<InternalFunctionContext[]> QueueNextTimeTickersAsync(TimeTickerEntity[] minTimeTickers, CancellationToken cancellationToken = default)
        {
            var results = new List<InternalFunctionContext>();
            
            await foreach(var updatedTimeTicker in _persistenceProvider.QueueTimeTickers(minTimeTickers, cancellationToken))
            {
                results.Add(new InternalFunctionContext
                {
                    FunctionName = updatedTimeTicker.Function,
                    TickerId = updatedTimeTicker.Id,
                    Type = TickerType.TimeTicker,
                    Retries = updatedTimeTicker.Retries,
                    RetryIntervals = updatedTimeTicker.RetryIntervals,
                    ParentId = updatedTimeTicker.ParentId,
                    ExecutionTime = updatedTimeTicker.ExecutionTime ?? _clock.UtcNow,
                    TimeTickerChildren = updatedTimeTicker.Children.Select(ch => new InternalFunctionContext
                    {
                        FunctionName = ch.Function,
                        TickerId = ch.Id,
                        Type = TickerType.TimeTicker,
                        Retries = ch.Retries,
                        RetryIntervals = ch.RetryIntervals,
                        ParentId = ch.ParentId,
                        RunCondition = ch.RunCondition ?? RunCondition.OnAnyCompletedStatus,
                        ExecutionTime = ch.ExecutionTime ?? default,
                        IsDelayed = ch.ExecutionTime.HasValue,
                        TimeTickerChildren = ch.Children.Select(gch => new InternalFunctionContext
                        {
                            FunctionName = gch.Function,
                            TickerId = gch.Id,
                            Type = TickerType.TimeTicker,
                            Retries = gch.Retries,
                            RetryIntervals = gch.RetryIntervals,
                            ParentId = gch.ParentId,
                            RunCondition = gch.RunCondition ?? RunCondition.OnAnyCompletedStatus,
                            ExecutionTime = gch.ExecutionTime ?? default,
                            IsDelayed = gch.ExecutionTime.HasValue
                        }).ToList()
                    }).ToList()
                });

                await _notificationHubSender.UpdateTimeTickerNotifyAsync(updatedTimeTicker);
            }

            return results.ToArray();
        }

        private async Task<InternalFunctionContext[]> QueueNextCronTickersAsync((DateTime Key, InternalManagerContext[] Items) minCronTicker, CancellationToken cancellationToken = default)
        {
            var results = new List<InternalFunctionContext>();
            
            await foreach (var occurrence in _persistenceProvider.QueueCronTickerOccurrences(minCronTicker, cancellationToken).ConfigureAwait(false))
            {
                results.Add(new InternalFunctionContext
                {
                    ParentId = occurrence.CronTickerId,
                    FunctionName = occurrence.CronTicker.Function,
                    TickerId = occurrence.Id,
                    Type = TickerType.CronTickerOccurrence,
                    Retries = occurrence.CronTicker.Retries,
                    RetryIntervals = occurrence.CronTicker.RetryIntervals,
                    ExecutionTime = occurrence.ExecutionTime
                });
                
                if (occurrence.CreatedAt == occurrence.UpdatedAt && _notificationHubSender != null)
                    await _notificationHubSender.AddCronOccurrenceAsync(occurrence.CronTickerId, occurrence).ConfigureAwait(false);
                else if(_notificationHubSender != null)
                    await _notificationHubSender.UpdateCronOccurrenceAsync(occurrence.CronTickerId, occurrence).ConfigureAwait(false);
            }
            
            return results.ToArray();
        }
        
        private async Task<(DateTime Key, InternalManagerContext[] Items)?> GetEarliestCronTickerGroupAsync(CancellationToken cancellationToken = default)
        {
            var now = _clock.UtcNow;

            var cronTickers = await _persistenceProvider
                .GetAllCronTickerExpressions(cancellationToken)
                .ConfigureAwait(false);

            var cronTickerIds = cronTickers.Select(x => x.Id).ToArray();

            var earliestAvailableCronOccurrence = await _persistenceProvider
                .GetEarliestAvailableCronOccurrence(cronTickerIds, cancellationToken)
                .ConfigureAwait(false);

            return EarliestCronTickerGroup(cronTickers, now, earliestAvailableCronOccurrence);
        }

        private static (DateTime Next, InternalManagerContext[] Items)? EarliestCronTickerGroup(CronTickerEntity[] cronTickers, DateTime now, CronTickerOccurrenceEntity<TCronTicker> earliestStored)
        {
            DateTime? min = null;
            InternalManagerContext first = null;
            List<InternalManagerContext> ties = null;

            foreach (var cronTicker in cronTickers)
            {
                var next = CronScheduleCache.GetNextOccurrenceOrDefault(cronTicker.Expression, now);
                if (next is null) continue;
                
                if(earliestStored != null && earliestStored.ExecutionTime == next && cronTicker.Id == earliestStored.CronTickerId)
                    continue;
                
                var n = next.Value;
                if (min is null || n < min)
                {
                    min = n;
                    first = new InternalManagerContext(cronTicker.Id)
                    {
                        FunctionName = cronTicker.Function,
                        Expression = cronTicker.Expression,
                        Retries = cronTicker.Retries,
                        RetryIntervals = cronTicker.RetryIntervals,
                    };
                    
                    ties = null;
                }
                else if (n == min)
                {
                    ties ??= new List<InternalManagerContext>(2) { first };
                    ties.Add(new InternalManagerContext(cronTicker.Id)
                    {
                        FunctionName = cronTicker.Function,
                        Expression = cronTicker.Expression,
                        Retries = cronTicker.Retries,
                        RetryIntervals = cronTicker.RetryIntervals,
                    });
                }
            }

            // If we have a stored occurrence, compare/merge
            if (earliestStored is not null)
            {
                var storedTime = earliestStored.ExecutionTime;
                var storedItem = new InternalManagerContext(earliestStored.CronTickerId)
                {
                    FunctionName = earliestStored.CronTicker.Function,
                    Expression = earliestStored.CronTicker.Expression,
                    Retries = earliestStored.CronTicker.Retries,
                    RetryIntervals = earliestStored.CronTicker.RetryIntervals,
                    NextCronOccurrence = new NextCronOccurrence(earliestStored.Id, earliestStored.CreatedAt)
                };

                // If no in-memory occurrences or stored is earlier, return stored only
                if (min is null || storedTime < min.Value)
                    return (storedTime, [storedItem]);

                // If stored time equals the earliest in-memory time, aggregate them
                if (storedTime == min.Value)
                {
                    if (ties is null)
                        return (min.Value, [first, storedItem]);

                    ties.Add(storedItem);
                    return (min.Value, ties.ToArray());
                }

                // Stored is later than min, return in-memory winners only
                var winners = ties is null ? [first] : ties.ToArray();
                return (min.Value, winners);
            }

            // No stored occurrence - return in-memory winners or null if none
            if (min is null)
                return null;

            var finalWinners = ties is null ? [first] : ties.ToArray();
            return (min.Value, finalWinners);
        }

        // The row is handed to a worker only when this call moved it from Queued (held by this node) to InProgress.
        // A node whose loop woke late finds the row taken by the fallback, or cancelled, and gets false.
        public async Task<bool> TryClaimDueTicker(InternalFunctionContext function, DateTime claimMarker, CancellationToken cancellationToken = default)
        {
            var won = function.Type == TickerType.TimeTicker
                ? await _persistenceProvider.TryMarkTimeTickerInProgress(function.TickerId, claimMarker, cancellationToken).ConfigureAwait(false)
                : await _persistenceProvider.TryMarkCronOccurrenceInProgress(function.TickerId, claimMarker, cancellationToken).ConfigureAwait(false);

            if (won)
                function.Status = TickerStatus.InProgress;

            return won;
        }

        public async Task<bool> TryReleaseClaim(InternalFunctionContext function, DateTime claimMarker, CancellationToken cancellationToken = default)
        {
            var released = function.Type == TickerType.TimeTicker
                ? await _persistenceProvider.TryReleaseTimeTickerClaim(function.TickerId, claimMarker, cancellationToken).ConfigureAwait(false)
                : await _persistenceProvider.TryReleaseCronOccurrenceClaim(function.TickerId, claimMarker, cancellationToken).ConfigureAwait(false);

            if (released)
                function.Status = TickerStatus.Idle;

            return released;
        }

        public DateTime NextClaimMarker() => _clock.NextClaimMarker();

        public async Task<int> GetMaxConcurrentClaims(CancellationToken cancellationToken = default)
            => _persistenceProvider is IClaimFanOut fanOut
                ? Math.Max(1, await fanOut.GetMaxConcurrentClaimsAsync(cancellationToken).ConfigureAwait(false))
                : ClaimFanOut.Sequential;

        public Task NotifyStatusChanged(InternalFunctionContext function) => NotifyInProgress(function);

        public async Task NotifyInProgress(InternalFunctionContext function)
        {
            if (function.Type == TickerType.TimeTicker)
                await _notificationHubSender.UpdateTimeTickerFromInternalFunctionContext<TTimeTicker>(function).ConfigureAwait(false);
            else
                await _notificationHubSender.UpdateCronOccurrenceFromInternalFunctionContext<TCronTicker>(function).ConfigureAwait(false);
        }

        public async Task ReleaseAcquiredResources(InternalFunctionContext[] resources, CancellationToken cancellationToken = default)
        {
            if (resources is null)
            {
                await Task.WhenAll(
                    _persistenceProvider.ReleaseAcquiredCronTickerOccurrences([], cancellationToken),
                    _persistenceProvider.ReleaseAcquiredTimeTickers([], cancellationToken)
                    );
                return;
            }
            
            var cronTickerIds = resources.Length == 0 
                ? [] 
                : resources.Where(x => x.Type == TickerType.CronTickerOccurrence).Select(x => x.TickerId).ToArray();
            
            if(cronTickerIds.Length != 0)
                await _persistenceProvider.ReleaseAcquiredCronTickerOccurrences(cronTickerIds, cancellationToken).ConfigureAwait(false);
            
            var timeTickerIds = resources.Length == 0
                ? []
                : resources.Where(x => x.Type == TickerType.TimeTicker).Select(x => x.TickerId).ToArray();
            
            if (timeTickerIds.Length != 0)
                await _persistenceProvider.ReleaseAcquiredTimeTickers(timeTickerIds, cancellationToken).ConfigureAwait(false);
        }
        
        public async Task UpdateTickerAsync(InternalFunctionContext functionContext, CancellationToken cancellationToken = default)
        {
            if (functionContext.Type == TickerType.CronTickerOccurrence)
            {
                await _persistenceProvider.UpdateCronTickerOccurrence(functionContext, cancellationToken).ConfigureAwait(false);
                await _notificationHubSender.UpdateCronOccurrenceFromInternalFunctionContext<TCronTicker>(functionContext).ConfigureAwait(false);
            }
            else
            {
                await _persistenceProvider.UpdateTimeTicker(functionContext, cancellationToken).ConfigureAwait(false);
                await _notificationHubSender.UpdateTimeTickerFromInternalFunctionContext<TTimeTicker>(functionContext).ConfigureAwait(false);
            }
        }
        
        public async Task UpdateSkipTimeTickersWithUnifiedContextAsync(InternalFunctionContext[] resources, CancellationToken cancellationToken = default)
        {
            var unifiedFunctionContext = new InternalFunctionContext()
                .SetProperty(x => x.Status, TickerStatus.Skipped)
                .SetProperty(x => x.ExecutedAt, _clock.UtcNow)
                .SetProperty(x => x.ExceptionDetails, ChainRunGate.NotMatchedReason);
            
            if (resources.Length != 0)
            {
                var ids = resources.Select(x => x.TickerId).ToArray();
                if (_persistenceProvider is IChildSkipWriter skipWriter)
                    await skipWriter.SkipNotStartedTimeTickersAsync(ids, unifiedFunctionContext, cancellationToken).ConfigureAwait(false);
                else
                    await _persistenceProvider.UpdateTimeTickersWithUnifiedContext(ids, unifiedFunctionContext, cancellationToken).ConfigureAwait(false);
            }
            
            foreach (var resource in resources)
            {
                resource.ExecutedAt = _clock.UtcNow;
                resource.Status = TickerStatus.Skipped;
                resource.ExceptionDetails = ChainRunGate.NotMatchedReason;
                if(resource.Type == TickerType.TimeTicker)
                    await _notificationHubSender.UpdateTimeTickerFromInternalFunctionContext<TTimeTicker>(resource).ConfigureAwait(false);
                else
                    await _notificationHubSender.UpdateCronOccurrenceFromInternalFunctionContext<TCronTicker>(resource).ConfigureAwait(false);
            }
        }

        public async Task<InternalFunctionContext[]> AcquireDelayedChildrenAsync(Guid[] ids, CancellationToken cancellationToken = default)
        {
            if (ids == null || ids.Length == 0) return [];

            TimeTickerEntity[] acquired;
            try
            {
                acquired = await _persistenceProvider.AcquireImmediateTimeTickersAsync(ids, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ClaimPutBackFailedException ex)
            {
                _logger?.LogError(ex, "Claiming delayed chain children {TickerIds} after their parent ended failed, and some of the rows it claimed could not be put back. " +
                    "Those stay InProgress until the store releases the rows of this node; the others run when the fallback picks them up.", string.Join(", ", ids));
                return [];
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Claiming delayed chain children {TickerIds} after their parent ended failed. They run when the fallback picks them up.",
                    string.Join(", ", ids));
                return [];
            }

            var contexts = ClaimedTimeTickerContexts.Build(acquired);
            for (var i = 0; i < contexts.Length; i++)
            {
                var row = acquired[i];
                await NotifyClaimedAsync(() => _notificationHubSender.UpdateTimeTickerNotifyAsync(row), contexts[i], afterParent: true).ConfigureAwait(false);
            }
            return contexts;
        }

        public async Task<T> GetRequestAsync<T>(Guid tickerId, TickerType type, CancellationToken cancellationToken = default)
        {
            var request = type == TickerType.CronTickerOccurrence
                ? await _persistenceProvider.GetCronTickerOccurrenceRequest(tickerId, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await _persistenceProvider.GetTimeTickerRequest(tickerId, cancellationToken: cancellationToken).ConfigureAwait(false);

            return request == null || request.Length == 0
                ? default
                : TickerHelper.ReadTickerRequest<T>(request);
        }

        public async Task<T> GetRequestAsync<T>(Guid tickerId, TickerType type, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
        {
            var request = type == TickerType.CronTickerOccurrence
                ? await _persistenceProvider.GetCronTickerOccurrenceRequest(tickerId, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await _persistenceProvider.GetTimeTickerRequest(tickerId, cancellationToken: cancellationToken).ConfigureAwait(false);

            return request == null || request.Length == 0
                ? default
                : TickerHelper.ReadTickerRequest(request, typeInfo);
        }

        // The stores claim the overdue rows one by one while they are enumerated, so a failure in the middle (a dropped
        // connection, a cancelled token) comes after some rows were claimed. Those rows are InProgress for this node; they are
        // handed back with the failure so the fallback runs them, instead of being dropped with the list.
        public async Task<InternalFunctionContext[]> RunTimedOutTickers(CancellationToken cancellationToken = default)
        {
            var results = new List<InternalFunctionContext>();

            try
            {
                await ClaimTimedOutTickersAsync(results, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (results.Count > 0)
            {
                throw new ClaimsInterruptedException(results.ToArray(), ex);
            }

            return results.ToArray();
        }

        // The notification is for the dashboard and does not decide the job: a hub that fails must not drop a claimed row or
        // end the pass.
        private async Task NotifyClaimedAsync(Func<Task> notify, InternalFunctionContext function, bool afterParent = false)
        {
            try
            {
                await notify().ConfigureAwait(false);
            }
            catch (Exception ex) when (afterParent)
            {
                _logger?.LogWarning(ex, "Delayed chain child {FunctionName} ({TickerId}) was claimed to run after its parent but the dashboard could not be notified.",
                    function.FunctionName, function.TickerId);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Job {FunctionName} ({TickerId}) was taken by the fallback but the dashboard could not be notified.",
                    function.FunctionName, function.TickerId);
            }
        }

        private async Task ClaimTimedOutTickersAsync(List<InternalFunctionContext> results, CancellationToken cancellationToken)
        {
            await foreach(var timedOutTimeTicker in _persistenceProvider.QueueTimedOutTimeTickers(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new InternalFunctionContext
                {
                    FunctionName = timedOutTimeTicker.Function,
                    TickerId = timedOutTimeTicker.Id,
                    Type = TickerType.TimeTicker,
                    Retries = timedOutTimeTicker.Retries,
                    RetryIntervals = timedOutTimeTicker.RetryIntervals,
                    ParentId = timedOutTimeTicker.ParentId,
                    ExecutionTime = timedOutTimeTicker.ExecutionTime ?? _clock.UtcNow,
                    ClaimMarker = timedOutTimeTicker.UpdatedAt,
                    TimeTickerChildren = timedOutTimeTicker.Children.Select(ch => new InternalFunctionContext
                    {
                        FunctionName = ch.Function,
                        TickerId = ch.Id,
                        Type = TickerType.TimeTicker,
                        Retries = ch.Retries,
                        RetryIntervals = ch.RetryIntervals,
                        ParentId = ch.ParentId,
                        RunCondition = ch.RunCondition ?? RunCondition.OnAnyCompletedStatus,
                        ExecutionTime = ch.ExecutionTime ?? default,
                        IsDelayed = ch.ExecutionTime.HasValue,
                        TimeTickerChildren = ch.Children.Select(gch => new InternalFunctionContext
                        {
                            FunctionName = gch.Function,
                            TickerId = gch.Id,
                            Type = TickerType.TimeTicker,
                            Retries = gch.Retries,
                            RetryIntervals = gch.RetryIntervals,
                            ParentId = gch.ParentId,
                            RunCondition = gch.RunCondition ?? RunCondition.OnAnyCompletedStatus,
                            ExecutionTime = gch.ExecutionTime ?? default,
                            IsDelayed = gch.ExecutionTime.HasValue
                        }).ToList()
                    }).ToList()
                });

                await NotifyClaimedAsync(() => _notificationHubSender.UpdateTimeTickerNotifyAsync(timedOutTimeTicker), results[^1]).ConfigureAwait(false);
            }

            await foreach (var timedOutCronTicker in _persistenceProvider.QueueTimedOutCronTickerOccurrences(cancellationToken).ConfigureAwait(false))
            {
                var functionContext = new InternalFunctionContext
                {
                    FunctionName = timedOutCronTicker.CronTicker.Function,
                    TickerId = timedOutCronTicker.Id,
                    Type = TickerType.CronTickerOccurrence,
                    Retries = timedOutCronTicker.CronTicker.Retries,
                    RetryIntervals = timedOutCronTicker.CronTicker.RetryIntervals,
                    ParentId = timedOutCronTicker.CronTickerId,
                    ExecutionTime = timedOutCronTicker.ExecutionTime,
                    ClaimMarker = timedOutCronTicker.UpdatedAt
                };
                
                results.Add(functionContext);
                await NotifyClaimedAsync(() => _notificationHubSender.UpdateCronOccurrenceFromInternalFunctionContext<TCronTicker>(functionContext), functionContext)
                    .ConfigureAwait(false);
            }
        }
        
        public async Task MigrateDefinedCronTickers((string, string)[] cronExpressions, CancellationToken cancellationToken = default)
            => await _persistenceProvider.MigrateDefinedCronTickers(cronExpressions, cancellationToken).ConfigureAwait(false);

        public async Task DeleteTicker(Guid tickerId, TickerType type, CancellationToken cancellationToken = default)
        {
            if (type == TickerType.CronTickerOccurrence)
                await _persistenceProvider.RemoveCronTickers([tickerId], cancellationToken).ConfigureAwait(false);
            else
                await _persistenceProvider.RemoveTimeTickers([tickerId], cancellationToken).ConfigureAwait(false);
        }

        public async Task<int> SkipStaleCronOccurrencesAsync(TimeSpan staleThreshold, CancellationToken cancellationToken = default)
            => await _persistenceProvider.SkipStaleCronOccurrencesAsync(staleThreshold, cancellationToken).ConfigureAwait(false);

        public Task ReleaseDeadNodeResources(string instanceIdentifier, CancellationToken cancellationToken = default)
            => ReleaseBothAsync(
                () => _persistenceProvider.ReleaseDeadNodeOccurrenceResources(instanceIdentifier, cancellationToken),
                () => _persistenceProvider.ReleaseDeadNodeTimeTickerResources(instanceIdentifier, cancellationToken));

        public Task ReleaseEarlierRunResources(string nodeIdentifier, DateTime runStartedAt, CancellationToken cancellationToken = default)
            => ReleaseBothAsync(
                () => _persistenceProvider.ReleaseDeadNodeOccurrenceResources(nodeIdentifier, runStartedAt, cancellationToken),
                () => _persistenceProvider.ReleaseDeadNodeTimeTickerResources(nodeIdentifier, runStartedAt, cancellationToken));

        private static async Task ReleaseBothAsync(Func<Task> releaseOccurrences, Func<Task> releaseTimeTickers)
        {
            // One after the other: a store behind a single connection, such as an in-memory SQLite database, cannot run two
            // operations at once, and these are two short statements. They do not depend on each other, so the second runs
            // even when the first fails, and the first failure is the one that reaches the caller.
            Exception failure = null;

            try
            {
                await releaseOccurrences().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            try
            {
                await releaseTimeTickers().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }

            if (failure != null)
                ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
