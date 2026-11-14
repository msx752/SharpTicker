using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Enums;
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
    private readonly ClaimPutBackRetryList _putBackRetry;
    private readonly NodeFence _fence;

    public ClaimedWorkQueue(
        IInternalTickerManager internalTickerManager,
        ISharpTickerTaskScheduler taskScheduler,
        ITickerExecutionTaskHandler taskHandler,
        ITickerFunctionConcurrencyGate concurrencyGate,
        ILogger logger = null,
        ClaimPutBackRetryList putBackRetry = null,
        NodeFence fence = null)
    {
        _putBackRetry = putBackRetry;
        _fence = fence;
        _internalTickerManager = internalTickerManager ?? throw new ArgumentNullException(nameof(internalTickerManager));
        _taskScheduler = taskScheduler ?? throw new ArgumentNullException(nameof(taskScheduler));
        _taskHandler = taskHandler ?? throw new ArgumentNullException(nameof(taskHandler));
        _concurrencyGate = concurrencyGate ?? throw new ArgumentNullException(nameof(concurrencyGate));
        _logger = logger;
    }

    /// <summary>The claims won and not started yet. Each one ends as a started job or as a row put back.</summary>
    internal ClaimLedger Claims { get; } = new();

    /// <summary>
    /// Whether the node's fence is raised (see <see cref="NodeFence"/>): the loop and the fallback then claim nothing, so a
    /// row is not claimed and put back over and over while the store answers and the fence is still up.
    /// </summary>
    internal bool StartsNoJob => _fence is { IsRaised: true };

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
                    // The wait can get the slot after the stop began: the job that held it ended on the stop and freed it
                    // before the stop reached this wait. The job does not start then; its claim stays pending and the
                    // stop's put-back moves the row back to Idle.
                    if (ct.IsCancellationRequested)
                    {
                        _logger?.LogDebug("Job {FunctionName} ({TickerId}) got its slot as the application was stopping; it does not start and the stop puts its claim back.",
                            function.FunctionName, function.TickerId);
                        return;
                    }

                    // While the node's fence is raised no job starts: the other nodes may release this node's rows, and a
                    // job started now would run while they start it again. The loop and the fallback claim nothing then;
                    // this is for a claim that was already won (it waited for its slot, or came from run-now). It is put
                    // back, for at most PutBackAttemptLimit so the worker and the slot are not held for as long as the
                    // store stays silent; a put-back that did not land stays in the ledger for the loop to try again.
                    if (StartsNoJob)
                    {
                        using var limit = new CancellationTokenSource(PutBackAttemptLimit);
                        await PutBackAsync(entry, NotStartedWhileFenced, cancellationToken: limit.Token).ConfigureAwait(false);
                        return;
                    }

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

    private const string NotStartedWhileFenced = "this node starts no job while it cannot show the other nodes that it is alive";
    private const string StoppedWhileFenced = "it could not show the other nodes that it is alive";

    /// <summary>
    /// Puts back a claim whose job did not start: the fenced write moves the row back to Idle only while it is still this
    /// claim (InProgress, this node, this marker), and the fallback then runs the job once. A job that started meanwhile, or
    /// a row that moved on, is left alone. When the store cannot be reached, or <paramref name="cancellationToken"/> ends the
    /// wait for it, the claim stays in the ledger and is tried again. Returns false only in that case.
    /// </summary>
    internal async Task<bool> PutBackAsync(ClaimLedger.Entry entry, string reason, Exception cause = null, bool quietWhenNothingToPutBack = false,
        bool lastAttempt = false, CancellationToken cancellationToken = default)
    {
        if (!entry.TryBeginRevert())
            return true;

        var function = entry.Function;
        bool putBack;
        try
        {
            putBack = await _internalTickerManager.TryReleaseClaim(function, entry.ClaimMarker, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            entry.RevertFailed();
            if (entry.StoppedRun)
            {
                // The run's own log entry said that the row is put back once the store answers; a stop ends the tries.
                _logger?.Log(lastAttempt ? LogLevel.Error : LogLevel.Debug, ex,
                    "Job {FunctionName} ({TickerId}) was stopped by this node ({Reason}), and its row could not be put back{Then}.",
                    function.FunctionName, function.TickerId, reason,
                    lastAttempt ? ". It stays InProgress until the store releases the rows of this node" : "; the scheduler tries again");
                return false;
            }

            if (lastAttempt)
            {
                // Nothing tries again after a stop: the row stays InProgress until its store releases this node's rows.
                _logger?.LogError(ex,
                    "Job {FunctionName} ({TickerId}) was claimed but did not start ({Reason}), and it could not be put back. " +
                    "Its row stays InProgress until the store releases the rows of this node.",
                    function.FunctionName, function.TickerId, reason);
                return false;
            }

            // Tried again every PutBackRetryInterval while the store is out of reach: the first failure is the error, the
            // attempts after it are written at Debug, and the put-back that works says so.
            _logger?.Log(entry.FailedPutBacks == 1 ? LogLevel.Error : LogLevel.Debug, ex,
                "Job {FunctionName} ({TickerId}) was claimed but did not start ({Reason}), and it could not be put back. " +
                "Its row stays InProgress with no worker running it; the scheduler tries again.",
                function.FunctionName, function.TickerId, reason);
            return false;
        }

        entry.RevertSucceeded();

        if (!putBack)
        {
            if (entry.StoppedRun)
                _logger?.LogInformation("Job {FunctionName} ({TickerId}) was stopped by this node ({Reason}). Its row had already moved on (released by another node, or ended), so there was nothing to put back.",
                    function.FunctionName, function.TickerId, reason);
            else if (quietWhenNothingToPutBack)
                _logger?.LogDebug(cause, "Job {FunctionName} ({TickerId}) did not start ({Reason}); its row was not this claim, so there was nothing to put back.",
                    function.FunctionName, function.TickerId, reason);
            else
                _logger?.LogInformation(cause, "Job {FunctionName} ({TickerId}) did not start ({Reason}); its row had already moved on, so there was nothing to put back.",
                    function.FunctionName, function.TickerId, reason);
            return true;
        }

        // The run's own log entry was the warning; the put-back that follows it is information.
        if (entry.StoppedRun)
            _logger?.LogInformation("Job {FunctionName} ({TickerId}) was stopped by this node ({Reason}). Its row was put back and the job runs again once the fallback picks it up.",
                function.FunctionName, function.TickerId, reason);
        else
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

        return true;
    }

    /// <summary>
    /// Whether a put-back waits to be tried again: one that failed here, or one that failed inside a claim call and is still
    /// in <see cref="ClaimPutBackRetryList"/>. The loop keeps its sleep short while there is one.
    /// </summary>
    /// <summary>Calls <paramref name="wake"/> whenever a claim call adds rows whose put-back failed; returns the unsubscribe.</summary>
    internal Action WakeOnPutBacksToRetry(Action wake)
    {
        if (_putBackRetry == null)
            return () => { };
        _putBackRetry.Added += wake;
        return () => _putBackRetry.Added -= wake;
    }

    internal bool HasPutBacksToRetry => !(_putBackRetry?.IsEmpty ?? true) || Claims.PendingPutBacks().Count > 0;

    // The rows whose put-back failed inside a claim call become claims of the ledger that can never start, so the loop tries
    // their put-back again like any other that failed. Only the id, the type and the marker are known: the put-back needs no
    // more, and the logs name the job by its id. The row of a run this node stopped (see NodeFence) comes the same way,
    // with the name of its function.
    private void AdoptFailedPutBacks()
    {
        if (_putBackRetry == null)
            return;

        foreach (var claim in _putBackRetry.TakeAll())
        {
            var function = new InternalFunctionContext
            {
                TickerId = claim.Id,
                Type = claim.Type,
                FunctionName = claim.StoppedFunction ?? "(not read: its claim call failed)",
                ClaimMarker = claim.ClaimMarker,
                Status = TickerStatus.InProgress
            };
            Claims.RecordFailedPutBack(function, claim.ClaimMarker, stoppedRun: claim.StoppedFunction != null);
        }
    }

    /// <summary>How long one retried put-back may wait for the store before the loop goes on.</summary>
    internal TimeSpan PutBackAttemptLimit { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Tries the failed put-backs again, each for at most <see cref="PutBackAttemptLimit"/>: those that failed here and those
    /// that failed inside a claim call (<see cref="ClaimPutBackRetryList"/>). The first that fails again ends the round: the
    /// store is still out of reach, and waiting for it once per row would hold the loop that long. The rest are tried on the
    /// next pass.
    /// </summary>
    internal async Task RetryFailedPutBacksAsync()
    {
        AdoptFailedPutBacks();
        foreach (var entry in Claims.PendingPutBacks())
        {
            using var limit = new CancellationTokenSource(PutBackAttemptLimit);
            if (!await PutBackAsync(entry, entry.StoppedRun ? StoppedWhileFenced : "a put-back that failed earlier", cancellationToken: limit.Token).ConfigureAwait(false))
                return;
        }
    }

    /// <summary>
    /// After a stop, no worker starts these jobs any more: what is still pending (a job that waited for its slot, or one the
    /// stopped scheduler never ran) is put back. With <paramref name="owner"/>, only the claims that owner recorded: the
    /// fallback stops before the scheduler, which may still start the others, and the scheduler's stop puts back the rest.
    /// <paramref name="cancellationToken"/> is the stop's: once the host's time for the stop is used up, the put-backs that
    /// have not answered give up and are logged, instead of each waiting for the store's own timeout.
    /// </summary>
    internal async Task PutBackUnstartedAsync(object owner = null, CancellationToken cancellationToken = default)
    {
        // The scheduler's sweep also takes the rows whose put-back failed inside a claim call: no one tries after it.
        if (owner == null)
            AdoptFailedPutBacks();
        foreach (var entry in Claims.Pending(owner))
            // The sweep of every claim is the scheduler's, which stops last: no one tries after it. While the scheduler still
            // runs, its loop retries what fails in the fallback's sweep.
            await PutBackAsync(entry, entry.StoppedRun ? StoppedWhileFenced : "the application stopped before its worker started it", lastAttempt: owner == null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
