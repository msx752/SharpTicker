using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpTicker.Exceptions;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Exceptions;
using SharpTicker.Utilities.Base;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Instrumentation;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Models;

namespace SharpTicker;

internal class TickerExecutionTaskHandler : ITickerExecutionTaskHandler, ILateOutcomeFollowUp
{
    // Cached lowercase TickerType names to avoid per-call ToString + ToLowerInvariant allocations
    private static readonly Dictionary<TickerType, string> TickerTypeNamesLower =
        Enum.GetValues<TickerType>().ToDictionary(t => t, t => t.ToString().ToLowerInvariant());

    private readonly IServiceProvider _serviceProvider;
    private readonly ITickerClock _clock;
    private readonly ISharpTickerInstrumentation _sharpTickerInstrumentation;
    private readonly IInternalTickerManager _internalTickerManager;

    public TickerExecutionTaskHandler(IServiceProvider serviceProvider, ITickerClock clock, ISharpTickerInstrumentation sharpTickerInstrumentation, IInternalTickerManager internalTickerManager)
    {
        _serviceProvider = serviceProvider;
        _clock = clock;
        _sharpTickerInstrumentation = sharpTickerInstrumentation;
        _internalTickerManager = internalTickerManager;
    }

    public Task ExecuteTaskAsync(InternalFunctionContext context, bool isDue, CancellationToken cancellationToken = default)
        => ExecuteTaskAsync(context, isDue, isChild: false, cancellationToken);

    private async Task ExecuteTaskAsync(InternalFunctionContext context, bool isDue, bool isChild, CancellationToken cancellationToken)
    {
        if (context.Type == TickerType.CronTickerOccurrence)
        {
            await RunContextFunctionAsync(context, isDue, cancellationToken);
            return;
        }

        // A chain child without a time of its own runs inside its parent's run and was never claimed: its row is recorded
        // InProgress here, before it starts and before its InProgress delayed children are handed off (the store lets them
        // run once this row is InProgress).
        if (isChild)
            await RecordChildInProgressAsync(context, cancellationToken);

        var childCount = context.TimeTickerChildren.Count;
        var childrenToRunAfter = new InternalFunctionContext[childCount];
        var tasksToRunNow = new Task[childCount + 1];

        var childrenToRunAfterCount = 0;
        var tasksToRunNowCount = 0;

        var hasChildren = context.TimeTickerChildren.Count > 0;
        List<InternalFunctionContext> delayedChildren = null;

        // Add parent task
        var parentRun = RunContextFunctionAsync(context, isDue, cancellationToken, isChild);
        tasksToRunNow[tasksToRunNowCount++] = parentRun;

        if (hasChildren)
        {
            // Process children - separate InProgress from others
            for (var i = 0; i < context.TimeTickerChildren.Count; i++)
            {
                var child = context.TimeTickerChildren[i];

                // A delayed child is a row of its own: the store hands it out once this parent has ended and its time
                // has come. Running it here as well would run it twice.
                if (child.IsDelayed)
                {
                    (delayedChildren ??= []).Add(child);
                    continue;
                }

                if (child.CachedDelegate != null)
                {
                    if (child.RunCondition == RunCondition.InProgress)
                        tasksToRunNow[tasksToRunNowCount++] = SafeRecursiveExecution(child, isDue, cancellationToken);
                    else
                    {
                        childrenToRunAfter[childrenToRunAfterCount++] = child;
                    }
                }
            }
        }
        
        // An InProgress delayed child may run once its parent has started, which it has: its row was claimed InProgress
        // before it was handed here.
        if (delayedChildren != null)
            await HandOffDelayedChildrenAsync(delayedChildren.Where(c => c.RunCondition == RunCondition.InProgress), cancellationToken);

        // Wait for concurrent tasks (parent + InProgress children)
        await Task.WhenAll(tasksToRunNow.AsSpan(0, tasksToRunNowCount).ToArray());

        // The children follow the parent's outcome as the store has it. When the store does not have it, the row is not
        // finished there: a child run or skipped now would act on an outcome that may never be stored, or that a later run
        // of the parent replaces. So none is run or skipped (the lost write is logged where it failed).
        if (!await parentRun)
            return;

        // The parent's outcome is written, so the store now hands out the delayed children it lets run. The InProgress ones
        // again too: the claim at the start finds a closed gate when the parent was not recorded InProgress, and a row
        // already claimed is simply not claimed twice. Handed off before anything below that can take long or throw, so a
        // child that is due is not held back by its siblings.
        if (delayedChildren != null && ChainRunGate.IsFinal(context.Status))
            await HandOffDelayedChildrenAsync(delayedChildren.Where(c => ShouldRunChild(c, context.Status)), cancellationToken);

        // Process deferred children after parent completion
        if (childrenToRunAfterCount > 0 || delayedChildren != null)
        {
            var childrenToSkip = new List<InternalFunctionContext>(30); // Pre-sized for performance
            var childrenToRunAfterTask = new Task[childrenToRunAfterCount];

            var taskCount = 0;

            for (var i = 0; i < childrenToRunAfterCount; i++)
            {
                var child = childrenToRunAfter[i];

                if (child.CachedDelegate != null)
                {
                    if (ShouldRunChild(child, context.Status))
                    {
                        childrenToRunAfterTask[taskCount++] = SafeRecursiveExecution(child, isDue, cancellationToken);
                    }
                    else
                    {
                        SkipWithDescendants(child, context, childrenToSkip);
                    }
                }
            }

            // A delayed child the parent's outcome lets run is left to the store; one it does not let run never will, as
            // a finished status does not change, so it is skipped now with what it carries.
            if (delayedChildren != null)
            {
                foreach (var child in delayedChildren)
                {
                    if (!ShouldRunChild(child, context.Status))
                        SkipWithDescendants(child, context, childrenToSkip);
                }
            }

            // Bulk update skipped children
            if (childrenToSkip.Count > 0)
                await _internalTickerManager.UpdateSkipTimeTickersWithUnifiedContextAsync(
                    childrenToSkip.ToArray(), cancellationToken);

            // Wait for deferred tasks
            if (taskCount > 0)
                await Task.WhenAll(childrenToRunAfterTask.AsSpan(0, taskCount).ToArray());
        }
    }

    // A delayed child whose time has come is claimed and started now, instead of waiting for the next fallback pass; one due
    // later wakes the scheduler loop, which did not plan it while it waited for its parent. Never before its own time: the
    // claim does not look at the time. Best effort: the fallback still runs what this misses, so nothing here fails the run.
    // After an outcome stored late (lateParent) the children can also be inline ones, which have no time of their own and
    // are claimed at once. The fallback does not run those: one that was not claimed is named in a warning.
    private async Task HandOffDelayedChildrenAsync(IEnumerable<InternalFunctionContext> children, CancellationToken cancellationToken,
        InternalFunctionContext lateParent = null)
    {
        var now = _clock.UtcNow;
        List<Guid> due = null;
        List<InternalFunctionContext> inline = null;
        DateTime? nextTime = null;
        foreach (var child in children)
        {
            if (!child.IsDelayed)
            {
                (inline ??= []).Add(child);
                (due ??= []).Add(child.TickerId);
            }
            else if (child.ExecutionTime <= now)
                (due ??= []).Add(child.TickerId);
            else if (nextTime == null || child.ExecutionTime < nextTime)
                nextTime = child.ExecutionTime;
        }

        if (nextTime != null)
        {
            try
            {
                HostScheduler?.RestartIfNeeded(nextTime);
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "Waking the scheduler for a delayed chain child due at {ExecutionTime} failed; the fallback runs it.", nextTime);
            }
        }

        // Without a running dispatcher (background services off) a claimed row would never start.
        var dispatcher = due == null ? null : Dispatcher;
        if (dispatcher is not { IsEnabled: true })
        {
            WarnOfInlineChildrenNotClaimed(inline, [], lateParent);
            return;
        }

        InternalFunctionContext[] claimed;
        try
        {
            claimed = await _internalTickerManager.AcquireDelayedChildrenAsync(due.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            Logger?.LogDebug(ex, "Claiming delayed chain children was cancelled; the fallback runs them.");
            claimed = [];
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "Claiming delayed chain children {TickerIds} failed; the fallback runs them.", string.Join(", ", due));
            claimed = [];
        }

        WarnOfInlineChildrenNotClaimed(inline, claimed, lateParent);

        if (claimed.Length == 0)
            return;

        try
        {
            await dispatcher.DispatchAsync(claimed, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The claimed-work queue does not fail a dispatch, so this is not expected; but the rows are InProgress for this
            // node now, and the fallback does not take them.
            Logger?.LogError(ex, "Delayed chain children {TickerIds} were claimed but could not be dispatched. They stay InProgress until the store releases the rows of this node.",
                string.Join(", ", claimed.Select(c => c.TickerId)));
        }
    }

    // An inline child that was not claimed after its parent's outcome was stored late: another claim holds its row (it was
    // given a time meanwhile, or it already ran), or the claim failed. Which of the two is not known here, and the fallback
    // does not take a row without a time, so the warning says what to check.
    private void WarnOfInlineChildrenNotClaimed(List<InternalFunctionContext> inline, InternalFunctionContext[] claimed, InternalFunctionContext lateParent)
    {
        if (inline == null)
            return;

        foreach (var child in inline)
        {
            if (Array.Exists(claimed, c => c.TickerId == child.TickerId))
                continue;

            Logger?.LogWarning(
                "Chain child {FunctionName} ({TickerId}) of {ParentFunctionName} ({ParentId}) was not claimed after its parent's outcome was stored late: its row was taken or changed meanwhile, or the claim failed. If its row is still Idle and has no execution time, nothing runs it; add a new job to run it.",
                child.FunctionName, child.TickerId, lateParent?.FunctionName, lateParent?.TickerId);
        }
    }

    // Resolved on first use: the dispatcher and the scheduler service depend on this handler.
    private ISharpTickerDispatcher Dispatcher => _dispatcher ??= _serviceProvider.GetService<ISharpTickerDispatcher>();
    private ISharpTickerHostScheduler HostScheduler => _hostScheduler ??= _serviceProvider.GetService<ISharpTickerHostScheduler>();
    private ILogger Logger => _logger ??= _serviceProvider.GetService<ILoggerFactory>()?.CreateLogger<TickerExecutionTaskHandler>();
    // Registered only when the background services run: without the scheduler loop nothing would write a listed outcome.
    private LostOutcomeRetryList LostOutcomes => _lostOutcomes ??= _serviceProvider.GetService<LostOutcomeRetryList>();
    private LostOutcomeRetryList _lostOutcomes;
    private ISharpTickerDispatcher _dispatcher;
    private ISharpTickerHostScheduler _hostScheduler;
    private ILogger _logger;

    private void SkipWithDescendants(InternalFunctionContext child, InternalFunctionContext parent, List<InternalFunctionContext> childrenToSkip)
    {
        _sharpTickerInstrumentation.LogJobSkipped(
            child.TickerId,
            child.FunctionName,
            $"Condition {child.RunCondition} not met (Parent status: {parent.Status})"
        );
        child.ParentId = parent.TickerId;
        childrenToSkip.Add(child);

        // Recursively gather all descendants to skip
        GatherDescendantsToSkip(child, childrenToSkip);
    }

    /// <summary>
    /// Whether an OperationCanceledException that came out of a job means the job was cancelled, as opposed to failing.
    /// </summary>
    private static bool IsCancellation(OperationCanceledException exception, CancellationTokenSource jobToken)
    {
        // The job was asked to stop, by the cancel button, RequestCancellation or the application stopping.
        if (jobToken.IsCancellationRequested)
            return true;

        // A cancelled task counts, except the one HttpClient throws when its own timeout runs out: the job was not cancelled,
        // the request timed out, and a timeout is a failure that the retries are there for.
        return exception is TaskCanceledException && exception.InnerException is not TimeoutException;
    }

    // Returns whether the store has the job's final outcome.
    private async Task<bool> RunContextFunctionAsync(InternalFunctionContext context, bool isDue, CancellationToken cancellationToken, bool isChild = false)
    {
        var typeName = TickerTypeNamesLower.GetValueOrDefault(context.Type, context.Type.ToString().ToLowerInvariant());

        using var jobActivity = _sharpTickerInstrumentation.StartJobActivity($"sharpticker.job.execute.{typeName}", context);

        jobActivity?.SetTag("sharpticker.job.is_due", isDue);
        jobActivity?.SetTag("sharpticker.job.is_child", isChild);

        _sharpTickerInstrumentation.LogJobEnqueued(typeName, context.FunctionName, context.TickerId, "ExecutionTaskHandler");
        
        context.SetProperty(x => x.Status, TickerStatus.InProgress);

        // A claimed job's writes are fenced by the marker its claim wrote. An inline chain child got its marker when its start
        // was recorded (RecordChildInProgressAsync), or none when that failed.
        if (!isChild)
            context.RunMarker = context.ClaimMarker;

        var stopWatch = new Stopwatch();
        // Total wall-clock from first attempt start through the final outcome,
        // including retry wait intervals. Persisted as ElapsedTime so the
        // dashboard's Duration column reflects the user's lived time, not just
        // the last attempt's CPU time.
        var totalStopWatch = Stopwatch.StartNew();
        var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // IMPORTANT: Register the ticker FIRST, before creating the SkipIfAlreadyRunningAction callback
        // This ensures the current occurrence is properly tracked when the callback checks for siblings
        TickerCancellationTokenManager.AddTickerCancellationToken(cancellationTokenSource, context, isDue, this);

        // The job leaves the registry of running jobs on every path, a failed outcome write included: a job left listed
        // would hold a stop, which then records it as Cancelled, and make every later occurrence of its cron ticker skip
        // itself as already running. RemoveTickerCancellationToken also disposes the token source.
        try
        {
            var tickerFunctionContext = new TickerFunctionContext
            {
                FunctionName = context.FunctionName,
                Id = context.TickerId,
                ParentId = context.ParentId,
                Type = context.Type,
                IsDue = isDue,
                ScheduledFor = context.ExecutionTime,
                // Forward retry config so delegates that defer execution elsewhere
                // can pass it along.
                Retries = context.Retries,
                RetryIntervals = context.RetryIntervals,
                RequestCancelOperationAction = () => cancellationTokenSource.Cancel(),
                CronOccurrenceOperations = new CronOccurrenceOperations
                {
                    SkipIfAlreadyRunningAction = () =>
                    {
                        if (context.Type == TickerType.TimeTicker)
                            return;

                        // Check for other running occurrences of the same parent (excluding self)
                        // Since we're already registered, we need to exclude ourselves from the check
                        var isRunning = context.ParentId.HasValue &&
                                        TickerCancellationTokenManager.IsParentRunningExcludingSelf(context.ParentId.Value, context.TickerId);

                        if (isRunning)
                            throw new TerminateExecutionException("Another CronOccurrence is already running!");
                    },
                }
            };

            Exception lastException = null;
            var success = false;

            for (var attempt = context.RetryCount; attempt <= context.Retries; attempt++)
            {
                tickerFunctionContext.RetryCount = attempt;

                // Update activity with current attempt information
                jobActivity?.SetTag("sharpticker.job.current_attempt", attempt + 1);

                try
                {
                    if (await WaitForRetry(context, cancellationToken, attempt, cancellationTokenSource)) break;

                    stopWatch.Restart();

                    if (context.CachedDelegate is null)
                    {
                        throw new InvalidOperationException(
                            $"Ticker function '{context.FunctionName}' was not found in the registered functions. " +
                            "Ensure the function is properly decorated with [TickerFunction] attribute and the containing class is registered.");
                    }

                    // Create service scope - will be disposed automatically via await using
                    await using var scope = _serviceProvider.CreateAsyncScope();
                    tickerFunctionContext.SetServiceScope(scope);
                    await context.CachedDelegate(cancellationTokenSource.Token, scope.ServiceProvider, tickerFunctionContext);

                    success = true;
                    context.RetryCount = attempt;
                    break;
                }
                // A cancelled task counts, and so does any OperationCanceledException when this job's own token was cancelled,
                // for example by the cancel button or by the application stopping. A plain OperationCanceledException that
                // nobody asked for is an ordinary failure, and so is the TaskCanceledException that HttpClient throws when its own
                // timeout runs out (see IsCancellation).
                catch (OperationCanceledException ex) when (IsCancellation(ex, cancellationTokenSource))
                {
                    context.SetProperty(x => x.Status, TickerStatus.Cancelled)
                        .SetProperty(x => x.ExecutedAt, _clock.UtcNow)
                        .SetProperty(x => x.ElapsedTime, stopWatch.ElapsedMilliseconds)
                        .SetProperty(x => x.ExceptionDetails, SerializeException(ex));

                    // Add cancellation tags to activity
                    jobActivity?.SetTag("sharpticker.job.final_status", context.Status.ToString());
                    jobActivity?.SetTag("sharpticker.job.cancellation_reason", "Task was cancelled");

                    // Log job cancelled
                    _sharpTickerInstrumentation.LogJobCancelled(context.TickerId, context.FunctionName, "Task was cancelled");

                    if (_serviceProvider.GetService(typeof(ITickerExceptionHandler)) is ITickerExceptionHandler handler)
                        await handler.HandleCanceledExceptionAsync(ex, context.TickerId, context.Type);

                    return await StoreOutcomeAsync(context, isChild);
                }
                catch (TerminateExecutionException ex)
                {
                    context.SetProperty(x => x.Status, ex.Status)
                        .SetProperty(x => x.ExecutedAt, _clock.UtcNow)
                        .SetProperty(x => x.ElapsedTime, stopWatch.ElapsedMilliseconds);

                    if (ex.InnerException != null)
                    {
                        context.SetProperty(x => x.ExceptionDetails, ex.InnerException.Message);
                        jobActivity?.SetTag("sharpticker.job.skip_reason", ex.InnerException.Message);
                    }
                    else
                    {
                        context.SetProperty(x => x.ExceptionDetails, ex.Message);
                        jobActivity?.SetTag("sharpticker.job.skip_reason", ex.Message);
                    }

                    // Add skip tags to activity
                    jobActivity?.SetTag("sharpticker.job.final_status", context.Status.ToString());

                    // Log job skipped
                    _sharpTickerInstrumentation.LogJobSkipped(context.TickerId, context.FunctionName, ex.Message);

                    return await StoreOutcomeAsync(context, isChild);
                }
                catch (Exception ex)
                {
                    lastException = ex;

                    context.SetProperty(x => x.ExceptionDetails, SerializeException(ex));

                    if (_serviceProvider.GetService(typeof(ITickerExceptionHandler)) is ITickerExceptionHandler handler)
                        await handler.HandleExceptionAsync(ex, context.TickerId, context.Type);

                    // Per-attempt failure log so retries are visible in trace/logs.
                    // The terminal failure (last attempt that exhausts retries) is logged
                    // by the post-loop block below as Error — guard with `attempt < Retries`
                    // so non-terminal attempts log as Warning instead and the final attempt
                    // isn't double-logged.
                    if (attempt < context.Retries)
                        _sharpTickerInstrumentation.LogJobAttemptFailed(
                            context.TickerId, context.FunctionName, attempt, context.Retries, stopWatch.ElapsedMilliseconds, ex);

                    await StoreAttemptAsync(context, cancellationToken);
                }
            }

            stopWatch.Stop();
            totalStopWatch.Stop();

            // Persist the *total* time (first attempt start → final outcome, includes
            // retry waits). The per-attempt stopWatch is still used for instrumentation
            // ("attempt X failed in 200ms") but doesn't reflect user-lived duration.
            context.SetProperty(x => x.ElapsedTime, totalStopWatch.ElapsedMilliseconds)
                .SetProperty(x => x.ExecutedAt, _clock.UtcNow);

            if (success)
            {
                context.SetProperty(x => x.Status, isDue ? TickerStatus.DueDone : TickerStatus.Done);

                // Add success tags to activity
                jobActivity?.SetTag("sharpticker.job.final_status", context.Status.ToString());
                jobActivity?.SetTag("sharpticker.job.final_retry_count", context.RetryCount);

                // Log job completed successfully
                _sharpTickerInstrumentation.LogJobCompleted(context.TickerId, context.FunctionName, totalStopWatch.ElapsedMilliseconds, true);

                return await StoreOutcomeAsync(context, isChild);
            }

            if (lastException != null)
            {
                context.SetProperty(x => x.Status, TickerStatus.Failed)
                    .SetProperty(x => x.ExceptionDetails, SerializeException(lastException));

                // Add failure tags to activity
                jobActivity?.SetTag("sharpticker.job.final_status", context.Status.ToString());
                jobActivity?.SetTag("sharpticker.job.final_retry_count", context.RetryCount);
                jobActivity?.SetTag("sharpticker.job.error_type", lastException.GetType().Name);

                // Log job failed
                _sharpTickerInstrumentation.LogJobFailed(context.TickerId, context.FunctionName, lastException, context.RetryCount);
                _sharpTickerInstrumentation.LogJobCompleted(context.TickerId, context.FunctionName, totalStopWatch.ElapsedMilliseconds, false);

                return await StoreOutcomeAsync(context, isChild);
            }

            // No attempt ran (the retry count was already past the retries), so there is no outcome to store.
            Logger?.LogWarning(
                "{FunctionName} ({TickerId}) was not run: its retry count {RetryCount} is already past its {Retries} retries. No outcome is stored, and any chain children are neither run nor skipped.",
                context.FunctionName, context.TickerId, context.RetryCount, context.Retries);
            return false;
        }
        finally
        {
            TickerCancellationTokenManager.RemoveTickerCancellationToken(context.TickerId);
        }
    }

    // The final outcome write. It never throws: the job has ended either way, and the caller still has to leave the registry
    // of running jobs and decide on the chain children. The write has no cancellation: the token that cancelled the job would
    // cancel the write too, and the job would stay InProgress instead of ending as cancelled. Returns whether the store has
    // the outcome.
    private async Task<bool> StoreOutcomeAsync(InternalFunctionContext context, bool isChild)
    {
        try
        {
            if (await _internalTickerManager.UpdateTickerAsync(context, CancellationToken.None))
                return true;

            Logger?.LogWarning(
                "The {Status} outcome of {FunctionName} ({TickerId}) was not stored: no stored row matched, as the row was deleted while the job ran, or released and taken by another run. Any chain children are neither run nor skipped.",
                context.Status, context.FunctionName, context.TickerId);
            return false;
        }
        catch (Exception ex)
        {
            // Listed before this returns, so before the run leaves the registry of running jobs: a stop that finds no job
            // running finds this outcome in the list.
            switch (ListLostOutcome(context, isChild))
            {
                case LostOutcomeListing.Listed when isChild:
                    Logger?.LogError(ex,
                        "The {Status} outcome of chain child {FunctionName} ({TickerId}) could not be stored. The scheduler writes it again while it runs; if it stops first, the row stays InProgress until the store releases the rows of this node. Its chain children follow once it is written; those without a time of their own only when that happens before the scheduler stops.",
                        context.Status, context.FunctionName, context.TickerId);
                    break;
                case LostOutcomeListing.Listed:
                    Logger?.LogError(ex,
                        "The {Status} outcome of {FunctionName} ({TickerId}) could not be stored. The scheduler writes it again while it runs; if it stops first, the row stays InProgress until the store releases the rows of this node, and the job can then run again. Its chain children follow once it is written; those without a time of their own only when that happens before the scheduler stops.",
                        context.Status, context.FunctionName, context.TickerId);
                    break;
                // An inline chain child that was not fenced was never claimed by this node's lock, so nothing releases it.
                case var listing when isChild:
                    Logger?.LogError(ex,
                        "The {Status} outcome of chain child {FunctionName} ({TickerId}) could not be stored. Its row stays InProgress. Any chain children of it are neither run nor skipped.{Stopping}",
                        context.Status, context.FunctionName, context.TickerId, StoppingNote(listing));
                    break;
                case var listing:
                    Logger?.LogError(ex,
                        "The {Status} outcome of {FunctionName} ({TickerId}) could not be stored. The row stays InProgress until the store releases the rows of this node, and the job can then run again. Any chain children are neither run nor skipped.{Stopping}",
                        context.Status, context.FunctionName, context.TickerId, StoppingNote(listing));
                    break;
            }
            return false;
        }
    }

    private enum LostOutcomeListing
    {
        // The scheduler writes it again.
        Listed,
        // The scheduler is stopping: its list takes nothing more.
        Closed,
        // Not written again: no scheduler (background services off), or the run is not fenced (no marker, or a store that
        // does not fence), where a write by Id could land on a later run of the row.
        NotRetried
    }

    private LostOutcomeListing ListLostOutcome(InternalFunctionContext context, bool isChild)
    {
        if (LostOutcomes is not { } list || context.RunMarker is null || !_internalTickerManager.FencesRuns)
            return LostOutcomeListing.NotRetried;
        return list.TryAdd(context, isChild, _clock.UtcNow) ? LostOutcomeListing.Listed : LostOutcomeListing.Closed;
    }

    private static string StoppingNote(LostOutcomeListing listing)
        => listing == LostOutcomeListing.Closed ? " The scheduler is stopping, so it is not written again." : string.Empty;

    // A child without a time of its own that the parent's run starts after the parent has ended: it has a function, and it is
    // not an InProgress child, which started with the parent.
    private static bool RunsInlineAfterItsParent(InternalFunctionContext child)
        => !child.IsDelayed && child.CachedDelegate != null && child.RunCondition != RunCondition.InProgress;

    public async Task AfterLateStoreAsync(InternalFunctionContext outcome, bool claimChildren, CancellationToken cancellationToken)
    {
        // The run returned before these children when its outcome could not be stored (ExecuteTaskAsync), so nothing has
        // run or skipped them. The inline ones are not started from here, in the scheduler loop: the store hands them out
        // as it does the delayed ones (a claim by id), and each then runs as a claimed job.
        var children = outcome.TimeTickerChildren?.Where(c => c.IsDelayed || RunsInlineAfterItsParent(c)).ToList();
        if (children is not { Count: > 0 })
            return;

        // As after an outcome stored at once (ExecuteTaskAsync): the store now hands out the children the outcome lets run,
        // and those it does not let run never will, so they are skipped with what they carry.
        if (ChainRunGate.IsFinal(outcome.Status))
        {
            var allowed = children.Where(c => ShouldRunChild(c, outcome.Status));
            if (claimChildren)
                await HandOffDelayedChildrenAsync(allowed, cancellationToken, outcome).ConfigureAwait(false);
            else
            {
                // The scheduler is stopping. A delayed child is run by the fallback at the next start; nothing claims a row
                // without a time, and writing it Skipped would record an outcome it did not have.
                foreach (var child in allowed.Where(c => !c.IsDelayed))
                    Logger?.LogError(
                        "Chain child {FunctionName} ({TickerId}) of {ParentFunctionName} ({ParentId}) was not started: its parent's {Status} outcome was stored only while the scheduler stopped. Its row stays Idle and nothing runs it; add a new job to run it.",
                        child.FunctionName, child.TickerId, outcome.FunctionName, outcome.TickerId, outcome.Status);
            }
        }

        var childrenToSkip = new List<InternalFunctionContext>();
        foreach (var child in children)
        {
            if (!ShouldRunChild(child, outcome.Status))
                SkipWithDescendants(child, outcome, childrenToSkip);
        }

        if (childrenToSkip.Count > 0)
            await _internalTickerManager.UpdateSkipTimeTickersWithUnifiedContextAsync(childrenToSkip.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    // A write during the run (a failed attempt, the number of the next retry) keeps the dashboard current; the final outcome
    // write still follows. So it never throws, and a failure here neither fails the job nor counts as a failed attempt. That
    // holds for a cancelled write too: the run goes on to its next step, which sees the cancellation and ends the job as
    // cancelled, so an outcome is still written. The changed fields are kept when the write failed, so the next write carries
    // them.
    private async Task StoreAttemptAsync(InternalFunctionContext context, CancellationToken cancellationToken)
    {
        try
        {
            await _internalTickerManager.UpdateTickerAsync(context, cancellationToken);
            context.ResetUpdateProps();
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            Logger?.LogDebug(ex, "Recording attempt {Attempt} of {FunctionName} ({TickerId}) was cancelled.",
                context.RetryCount, context.FunctionName, context.TickerId);
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex,
                "Recording attempt {Attempt} of {FunctionName} ({TickerId}) failed; the job goes on, and its final outcome is written as usual.",
                context.RetryCount, context.FunctionName, context.TickerId);
        }
    }

    private async Task<bool> WaitForRetry(InternalFunctionContext context, CancellationToken cancellationToken,
        int attempt, CancellationTokenSource cancellationTokenSource)
    {
        if (attempt == 0)
            return false;

        if (attempt > context.Retries)
            return true;

        context.SetProperty(x => x.RetryCount, attempt);

        await StoreAttemptAsync(context, cancellationToken);

        var retryInterval = (context.RetryIntervals?.Length > 0)
            ? (attempt - 1 < context.RetryIntervals.Length
                ? context.RetryIntervals[attempt - 1]
                : context.RetryIntervals[^1])
            : 30;

        // Announce the upcoming retry. attempt is the 1-based retry number that's
        // about to run, matching the dashboard's "N/M" Retries column ("retries done / max").
        _sharpTickerInstrumentation.LogJobRetryScheduled(
            context.TickerId, context.FunctionName, attempt, context.Retries, retryInterval);

        await Task.Delay(TimeSpan.FromSeconds(retryInterval), cancellationTokenSource.Token);

        return false;
    }

    private static Exception GetRootException(Exception ex)
    {
        while (ex.InnerException != null)
            ex = ex.InnerException;
        return ex;
    }

    private static string SerializeException(Exception ex)
    {
        var rootException = GetRootException(ex);
        var stackTrace = new StackTrace(rootException, true);
        var frame = stackTrace.GetFrame(0);

        return JsonSerializer.Serialize(new ExceptionDetailClassForSerialization
        {
            Message = ex.Message,
            StackTrace = frame?.ToString() ?? rootException.StackTrace
        }, SharpTickerInternalJsonContext.Default.ExceptionDetailClassForSerialization);
    }

    // The same rule the stores apply to a delayed child, so a child runs on the same parent outcomes either way. A child
    // that is not delayed and runs while its parent is InProgress was started with the parent and never comes here.
    private static bool ShouldRunChild(InternalFunctionContext childContext, TickerStatus parentStatus)
        => ChainRunGate.Allows(childContext.RunCondition, parentStatus);

    private static void GatherDescendantsToSkip(InternalFunctionContext parent, List<InternalFunctionContext> skipList)
    {
        if (parent.TimeTickerChildren == null || parent.TimeTickerChildren.Count == 0)
            return;

        foreach (var child in parent.TimeTickerChildren)
        {
            skipList.Add(child);
            
            // Recursively gather grandchildren
            GatherDescendantsToSkip(child, skipList);
        }
    }

    // The status is for the dashboard and the chain gate; a failed write must not stop the child, which nothing else would
    // ever run, nor the rest of the chain. Its final status is written as usual.
    private async Task RecordChildInProgressAsync(InternalFunctionContext child, CancellationToken cancellationToken)
    {
        try
        {
            await _internalTickerManager.RecordChildStartedAsync(child, cancellationToken);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            Logger?.LogDebug(ex, "Recording chain child {FunctionName} ({TickerId}) as InProgress was cancelled.", child.FunctionName, child.TickerId);
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "Chain child {FunctionName} ({TickerId}) could not be recorded as InProgress; it runs anyway, and its writes are by Id, not fenced.",
                child.FunctionName, child.TickerId);
        }
        finally
        {
            child.ResetUpdateProps();
        }
    }

    private Task SafeRecursiveExecution(InternalFunctionContext context, bool isDue, CancellationToken cancellationToken = default)
    {
        try
        {
            return ExecuteTaskAsync(context, isDue, isChild: true, cancellationToken);
        }
        catch
        {
            // ignored
        }

        return Task.CompletedTask;
    }
}
