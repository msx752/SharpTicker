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

internal class TickerExecutionTaskHandler : ITickerExecutionTaskHandler
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
        tasksToRunNow[tasksToRunNowCount++] = RunContextFunctionAsync(context, isDue, cancellationToken, isChild);

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
    private async Task HandOffDelayedChildrenAsync(IEnumerable<InternalFunctionContext> children, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        List<Guid> due = null;
        DateTime? nextTime = null;
        foreach (var child in children)
        {
            if (child.ExecutionTime <= now)
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
            return;

        InternalFunctionContext[] claimed;
        try
        {
            claimed = await _internalTickerManager.AcquireDelayedChildrenAsync(due.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            Logger?.LogDebug(ex, "Claiming delayed chain children was cancelled; the fallback runs them.");
            return;
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "Claiming delayed chain children {TickerIds} failed; the fallback runs them.", string.Join(", ", due));
            return;
        }

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

    // Resolved on first use: the dispatcher and the scheduler service depend on this handler.
    private ISharpTickerDispatcher Dispatcher => _dispatcher ??= _serviceProvider.GetService<ISharpTickerDispatcher>();
    private ISharpTickerHostScheduler HostScheduler => _hostScheduler ??= _serviceProvider.GetService<ISharpTickerHostScheduler>();
    private ILogger Logger => _logger ??= _serviceProvider.GetService<ILoggerFactory>()?.CreateLogger<TickerExecutionTaskHandler>();
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

    private async Task RunContextFunctionAsync(InternalFunctionContext context, bool isDue, CancellationToken cancellationToken, bool isChild = false)
    {
        var typeName = TickerTypeNamesLower.GetValueOrDefault(context.Type, context.Type.ToString().ToLowerInvariant());

        using var jobActivity = _sharpTickerInstrumentation.StartJobActivity($"sharpticker.job.execute.{typeName}", context);

        jobActivity?.SetTag("sharpticker.job.is_due", isDue);
        jobActivity?.SetTag("sharpticker.job.is_child", isChild);

        _sharpTickerInstrumentation.LogJobEnqueued(typeName, context.FunctionName, context.TickerId, "ExecutionTaskHandler");
        
        context.SetProperty(x => x.Status, TickerStatus.InProgress);

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

                // The outcome is written with no cancellation: the token that cancelled the job would cancel the write
                // too, and the job would stay InProgress instead of ending as cancelled.
                await _internalTickerManager.UpdateTickerAsync(context, CancellationToken.None);
                
                // Clean up and exit early on cancellation
                TickerCancellationTokenManager.RemoveTickerCancellationToken(context.TickerId);
                return;
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

                await _internalTickerManager.UpdateTickerAsync(context, CancellationToken.None);

                // Clean up and exit early on termination
                TickerCancellationTokenManager.RemoveTickerCancellationToken(context.TickerId);
                return;
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

                await _internalTickerManager.UpdateTickerAsync(context, cancellationToken);

                context.ResetUpdateProps();
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

            await _internalTickerManager.UpdateTickerAsync(context, CancellationToken.None);
        }
        else if (lastException != null)
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

            await _internalTickerManager.UpdateTickerAsync(context, CancellationToken.None);
        }


        // Clean up: RemoveTickerCancellationToken handles disposal of the CTS
        TickerCancellationTokenManager.RemoveTickerCancellationToken(context.TickerId);
    }

    private async Task<bool> WaitForRetry(InternalFunctionContext context, CancellationToken cancellationToken,
        int attempt, CancellationTokenSource cancellationTokenSource)
    {
        if (attempt == 0)
            return false;

        if (attempt > context.Retries)
            return true;

        context.SetProperty(x => x.RetryCount, attempt);

        await _internalTickerManager.UpdateTickerAsync(context, cancellationToken);

        context.ResetUpdateProps();

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
        child.SetProperty(x => x.Status, TickerStatus.InProgress);
        try
        {
            await _internalTickerManager.UpdateTickerAsync(child, cancellationToken);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            Logger?.LogDebug(ex, "Recording chain child {FunctionName} ({TickerId}) as InProgress was cancelled.", child.FunctionName, child.TickerId);
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "Chain child {FunctionName} ({TickerId}) could not be recorded as InProgress; it runs anyway.",
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
