using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Models;

namespace SharpTicker.BackgroundServices;

/// <summary>
/// Hands the jobs of won claims to the workers and puts back the claims whose job never starts. The scheduler loop, the
/// fallback and run-now all mark a row InProgress before its job runs; a job that then does not start (the application
/// stops while it waits for its concurrency slot, the scheduler refuses it, the claim call failed after its write landed)
/// would leave the row InProgress with no worker. Its claim is put back with a fenced write instead, and the fallback runs
/// the job once. One instance is shared, so a put-back that failed is tried again by the scheduler loop whoever claimed.
/// </summary>
internal sealed class ClaimedWorkQueue
{
    private readonly IInternalTickerManager _internalTickerManager;
    private readonly ISharpTickerTaskScheduler _taskScheduler;
    private readonly ITickerExecutionTaskHandler _taskHandler;
    private readonly ITickerFunctionConcurrencyGate _concurrencyGate;
    private readonly ILogger _logger;

    public ClaimedWorkQueue(
        IInternalTickerManager internalTickerManager,
        ISharpTickerTaskScheduler taskScheduler,
        ITickerExecutionTaskHandler taskHandler,
        ITickerFunctionConcurrencyGate concurrencyGate,
        ILogger logger = null)
    {
        _internalTickerManager = internalTickerManager ?? throw new ArgumentNullException(nameof(internalTickerManager));
        _taskScheduler = taskScheduler ?? throw new ArgumentNullException(nameof(taskScheduler));
        _taskHandler = taskHandler ?? throw new ArgumentNullException(nameof(taskHandler));
        _concurrencyGate = concurrencyGate ?? throw new ArgumentNullException(nameof(concurrencyGate));
        _logger = logger;
    }

    /// <summary>The claims won and not started yet. Each one ends as a started job or as a row put back.</summary>
    internal ClaimLedger Claims { get; } = new();

    /// <summary>Whether the scheduler takes work: a claim won while it does not is put back, not queued.</summary>
    internal bool SchedulerTakesWork => !_taskScheduler.IsFrozen && !_taskScheduler.IsDisposed;

    internal ClaimLedger.Entry Record(InternalFunctionContext function, DateTime claimMarker, bool claimInFlight = false, object owner = null)
        => Claims.Record(function, claimMarker, claimInFlight, owner);

    /// <summary>
    /// Queues the job of a won claim. The job counts as started only once it holds its function's concurrency slot; a stop
    /// that ends the wait for the slot leaves the claim pending, and the stop puts it back. A refusal by the scheduler puts
    /// the claim back. The returned task completes when the scheduler has accepted the job, or when a refused claim has been
    /// put back; it never faults. Awaiting it lets a full scheduler hold the caller back before it claims more.
    /// </summary>
    internal Task QueueWork(ClaimLedger.Entry entry, bool isDue, CancellationToken jobToken)
    {
        var function = entry.Function;
        var semaphore = _concurrencyGate.GetSemaphoreOrNull(function.FunctionName, function.CachedMaxConcurrency);

        ValueTask queued;
        try
        {
            queued = _taskScheduler.QueueAsync(async ct =>
            {
                if (semaphore != null)
                    await semaphore.WaitAsync(ct).ConfigureAwait(false);

                try
                {
                    // A claim that is being put back, or whose put-back was tried, does not start.
                    if (!entry.TryStart())
                        return;

                    await _taskHandler.ExecuteTaskAsync(function, isDue, ct).ConfigureAwait(false);
                }
                finally
                {
                    semaphore?.Release();
                }
            }, function.CachedPriority, jobToken);
        }
        catch (Exception ex)
        {
            queued = new ValueTask(Task.FromException(ex));
        }

        return ObserveQueueing(entry, queued);
    }

    // QueueAsync is async, so a refusal (the scheduler froze or was disposed after the claim) comes back in the task and not as
    // a throw. The row is already InProgress and has no worker, so it is put back.
    private Task ObserveQueueing(ClaimLedger.Entry entry, ValueTask queued)
        => queued.IsCompletedSuccessfully ? Task.CompletedTask : ObserveQueueingAsync(entry, queued);

    private async Task ObserveQueueingAsync(ClaimLedger.Entry entry, ValueTask queued)
    {
        try
        {
            await queued.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await PutBackAsync(entry, "the scheduler refused it", ex).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Puts back a claim whose job did not start: the fenced write moves the row back to Idle only while it is still this
    /// claim (InProgress, this node, this marker), and the fallback then runs the job once. A job that started meanwhile, or
    /// a row that moved on, is left alone. When the store cannot be reached the claim stays in the ledger and is tried again.
    /// </summary>
    internal async Task PutBackAsync(ClaimLedger.Entry entry, string reason, Exception cause = null, bool quietWhenNothingToPutBack = false,
        bool lastAttempt = false)
    {
        if (!entry.TryBeginRevert())
            return;

        var function = entry.Function;
        bool putBack;
        try
        {
            putBack = await _internalTickerManager.TryReleaseClaim(function, entry.ClaimMarker, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            entry.RevertFailed();
            if (lastAttempt)
            {
                // Nothing tries again after a stop: the row stays InProgress until its store releases this node's rows.
                _logger?.LogError(ex,
                    "Job {FunctionName} ({TickerId}) was claimed but did not start ({Reason}), and it could not be put back. " +
                    "Its row stays InProgress until the store releases the rows of this node.",
                    function.FunctionName, function.TickerId, reason);
                return;
            }

            // Tried again every PutBackRetryInterval while the store is out of reach: the first failure is the error, the
            // attempts after it are written at Debug, and the put-back that works says so.
            _logger?.Log(entry.FailedPutBacks == 1 ? LogLevel.Error : LogLevel.Debug, ex,
                "Job {FunctionName} ({TickerId}) was claimed but did not start ({Reason}), and it could not be put back. " +
                "Its row stays InProgress with no worker running it; the scheduler tries again.",
                function.FunctionName, function.TickerId, reason);
            return;
        }

        entry.RevertSucceeded();

        if (!putBack)
        {
            if (quietWhenNothingToPutBack)
                _logger?.LogDebug(cause, "Job {FunctionName} ({TickerId}) did not start ({Reason}); its row was not this claim, so there was nothing to put back.",
                    function.FunctionName, function.TickerId, reason);
            else
                _logger?.LogInformation(cause, "Job {FunctionName} ({TickerId}) did not start ({Reason}); its row had already moved on, so there was nothing to put back.",
                    function.FunctionName, function.TickerId, reason);
            return;
        }

        _logger?.LogWarning(cause, "Job {FunctionName} ({TickerId}) was claimed but did not start ({Reason}). It was put back and runs once the fallback picks it up.",
            function.FunctionName, function.TickerId, reason);

        try
        {
            await _internalTickerManager.NotifyStatusChanged(function).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Job {FunctionName} ({TickerId}) was put back but the dashboard could not be notified.",
                function.FunctionName, function.TickerId);
        }
    }

    internal async Task RetryFailedPutBacksAsync()
    {
        foreach (var entry in Claims.PendingPutBacks())
            await PutBackAsync(entry, "a put-back that failed earlier").ConfigureAwait(false);
    }

    /// <summary>
    /// After a stop, no worker starts these jobs any more: what is still pending (a job that waited for its slot, or one the
    /// stopped scheduler never ran) is put back. With <paramref name="owner"/>, only the claims that owner recorded: the
    /// fallback stops before the scheduler, which may still start the others, and the scheduler's stop puts back the rest.
    /// </summary>
    internal async Task PutBackUnstartedAsync(object owner = null)
    {
        foreach (var entry in Claims.Pending(owner))
            // The sweep of every claim is the scheduler's, which stops last: no one tries after it. While the scheduler still
            // runs, its loop retries what fails in the fallback's sweep.
            await PutBackAsync(entry, "the application stopped before its worker started it", lastAttempt: owner == null).ConfigureAwait(false);
    }
}
