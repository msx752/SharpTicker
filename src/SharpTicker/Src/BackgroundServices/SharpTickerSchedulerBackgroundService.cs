using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharpTicker.SharpTickerThreadPool;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Models;

namespace SharpTicker.BackgroundServices;

internal class SharpTickerSchedulerBackgroundService : BackgroundService, ISharpTickerHostScheduler
{
    private readonly RestartThrottleManager _restartThrottle;
    private readonly Action _stopWakingOnPutBacks;
    private readonly Action _stopWakingOnLostOutcomes;
    // Writes again the outcomes whose final write threw; null when the service was built without it (tests).
    private readonly LostOutcomeDrain _lostOutcomes;
    private readonly IInternalTickerManager _internalTickerManager;
    private readonly TickerExecutionContext _executionContext;
    private readonly TimeSpan _minPollingInterval;
    private SafeCancellationTokenSource _schedulerLoopCancellationTokenSource;
    private readonly ISharpTickerTaskScheduler  _taskScheduler;
    private readonly ITickerExecutionTaskHandler  _taskHandler;
    private readonly ITickerFunctionConcurrencyGate _concurrencyGate;
    private readonly ILogger<SharpTickerSchedulerBackgroundService> _logger;
    private readonly IOptions<HostOptions> _hostOptions;
    private readonly ITickerClock _clock;
    private int _started;
    public bool SkipFirstRun;
    public bool IsRunning => _started == 1;


    public SharpTickerSchedulerBackgroundService(
        TickerExecutionContext executionContext,
        ITickerExecutionTaskHandler taskHandler,
        ISharpTickerTaskScheduler taskScheduler,
        IInternalTickerManager  internalTickerManager,
        SchedulerOptionsBuilder schedulerOptions,
        ITickerFunctionConcurrencyGate concurrencyGate,
        ILogger<SharpTickerSchedulerBackgroundService> logger = null,
        ITickerClock clock = null,
        IOptions<HostOptions> hostOptions = null,
        ClaimedWorkQueue claimedWork = null,
        LostOutcomeDrain lostOutcomes = null)
    {
        _lostOutcomes = lostOutcomes;
        _logger = logger;
        _clock = clock;
        _hostOptions = hostOptions;
        _executionContext = executionContext;
        _taskHandler = taskHandler;
        _taskScheduler = taskScheduler;
        _internalTickerManager = internalTickerManager ?? throw new ArgumentNullException(nameof(internalTickerManager));
        _concurrencyGate = concurrencyGate;
        _minPollingInterval = ResolveMinPollingInterval(schedulerOptions);
        _work = claimedWork ?? new ClaimedWorkQueue(_internalTickerManager, taskScheduler, taskHandler, concurrencyGate, logger);
        _restartThrottle = new RestartThrottleManager(() => _schedulerLoopCancellationTokenSource?.Cancel());
        // A row whose put-back failed inside a claim call (the fallback, run-now) may reach the retry list while the loop
        // sleeps; it wakes the loop, which tries the put-back first thing.
        _stopWakingOnPutBacks = _work.WakeOnPutBacksToRetry(Restart);
        // An outcome whose final write threw is listed while the loop may sleep; it wakes the loop the same way.
        _stopWakingOnLostOutcomes = _lostOutcomes?.WakeOnLostOutcomes(Restart) ?? (() => { });
    }
    
    public override Task StartAsync(CancellationToken ct)
    {
        if (SkipFirstRun)
        {
            _taskScheduler.Freeze();
            SkipFirstRun = false;
            return Task.CompletedTask;
        }
        
        _taskScheduler.Resume();
        return Interlocked.CompareExchange(ref _started, 1, 0) != 0 
            ? Task.CompletedTask : base.StartAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            _schedulerLoopCancellationTokenSource = SafeCancellationTokenSource.CreateLinked(stoppingToken);

            try
            {
                await RunSharpTickerSchedulerAsync(stoppingToken, _schedulerLoopCancellationTokenSource.Token);
            }
            catch (OperationCanceledException) when (_schedulerLoopCancellationTokenSource.Token.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                // This is a restart request - release resources and continue loop
                await TryReleaseAcquiredResourcesAsync(_executionContext.Functions, stoppingToken);
                // Small delay to allow resources to be released
                await Task.Delay(100, stoppingToken);
            }
            catch (Exception ex) when (stoppingToken.IsCancellationRequested)
            {
                // The application is stopping. What the stop cut off ends in an OperationCanceledException, or in a store call
                // that fails because its connection was closed under it; either way it is part of a normal stop, not an error.
                if (ex is not OperationCanceledException)
                    _logger?.LogDebug(ex, "The scheduler loop was interrupted by the application stopping.");

                await TryReleaseAcquiredResourcesAsync(_executionContext.Functions, CancellationToken.None, stopping: true);
                break;
            }
            catch (Exception ex)
            {
                await ReleaseAllResourcesAsync(ex);
                // Continue running - don't exit the scheduler loop on exceptions
                // Add a small delay to prevent tight loop if errors persist
                await Task.Delay(1000, stoppingToken);
            }
            finally
            {
                _executionContext.SetFunctions(null);
                _schedulerLoopCancellationTokenSource?.Dispose();
                _schedulerLoopCancellationTokenSource = null;
            }
        }
    }

    private async Task RunSharpTickerSchedulerAsync(CancellationToken stoppingToken, CancellationToken cancellationToken)
    {
        while (!stoppingToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The put-backs of every claim of this node (loop, fallback, run-now) are tried again here.
            await _work.RetryFailedPutBacksAsync();

            // The outcomes whose final write threw are written again here, fenced: their rows stay InProgress until then.
            if (_lostOutcomes != null)
                await _lostOutcomes.RetryAsync();

            if (_executionContext.Functions.Length != 0)
            {

                var batch = _executionContext.Functions.OrderBy(x => x.CachedPriority).ToArray();
                await HandOverBatchAsync(batch, stoppingToken);

                _executionContext.SetFunctions(null);
            }
            
            var (timeRemaining, functions) =
                await _internalTickerManager.GetNextTickers(cancellationToken);

            RecordLoopRecovered();

            _executionContext.SetFunctions(functions);

            TimeSpan sleepDuration;
            if (timeRemaining == Timeout.InfiniteTimeSpan || timeRemaining > TimeSpan.FromDays(1))
            {
                sleepDuration = TimeSpan.FromDays(1);
                _executionContext.SetNextPlannedOccurrence(null);
                _executionContext.SetFunctions(null);
            }
            else
            {
                var minInterval = _minPollingInterval < TimeSpan.Zero
                    ? TimeSpan.Zero
                    : _minPollingInterval;

                sleepDuration = timeRemaining <= minInterval
                    ? minInterval
                    : timeRemaining;
                _executionContext.SetNextPlannedOccurrence(DateTime.UtcNow.Add(sleepDuration));
            }

            // A put-back or an outcome write that failed is tried again on the loop's next pass, and an idle loop could sleep
            // up to a day.
            if (sleepDuration > PutBackRetryInterval && (_work.HasPutBacksToRetry || (_lostOutcomes?.HasEntries ?? false)))
                sleepDuration = PutBackRetryInterval;

            var notify = _executionContext.NotifyCoreAction;
            if (notify != null)
            {
                notify(_executionContext.GetNextPlannedOccurrence(), CoreNotifyActionType.NotifyNextOccurence);
            }

            await Task.Delay(sleepDuration, cancellationToken);
        }
    }

    private enum ClaimOutcome
    {
        Lost,
        Won,
        Failed
    }

    // Hands won claims to the workers and puts back those whose job does not start; shared with the fallback and run-now.
    private readonly ClaimedWorkQueue _work;

    internal ClaimLedger Claims => _work.Claims;

    /// <summary>How long the loop sleeps at most while a put-back is waiting to be tried again. Tests shorten it.</summary>
    internal TimeSpan PutBackRetryInterval { get; set; } = TimeSpan.FromSeconds(5);

    private bool CanHandOver(CancellationToken stoppingToken)
        => !stoppingToken.IsCancellationRequested && !_taskScheduler.IsFrozen && !_taskScheduler.IsDisposed;

    // The batch is claimed first, as many rows at once as the store takes, and the won rows are then handed to the workers in
    // priority order. A job goes to a worker only when this node won the claim on its row: the fallback or a cancel may have
    // taken the row while the loop slept, and then the job is not ours to run. A won row that is not handed over, because the
    // scheduler stopped or froze meanwhile, is put back, and so is a row whose claim call failed, because its write may have
    // landed. A failed claim then ends the batch the way any store failure ends a pass of the loop.
    private async Task HandOverBatchAsync(InternalFunctionContext[] batch, CancellationToken stoppingToken)
    {
        // A row claimed after the scheduler stopped taking work would be InProgress with no worker to run it. Nothing is
        // claimed yet, the rows are still Queued and are released.
        if (!CanHandOver(stoppingToken))
        {
            await TryReleaseAcquiredResourcesAsync(batch, CancellationToken.None, stopping: stoppingToken.IsCancellationRequested);
            return;
        }

        var fanOut = Math.Max(1, await _internalTickerManager.GetMaxConcurrentClaims(stoppingToken).ConfigureAwait(false));
        var entries = new ClaimLedger.Entry[batch.Length];
        var outcomes = new ClaimOutcome[batch.Length];
        var failures = new Exception[batch.Length];

        // The claim follows the application's token, not the loop's: a restart request must not cut a hand-over short, it
        // takes effect at the next wait on the loop's token. The marker is chosen and recorded before the call, so a claim
        // whose call fails after its write landed is still found and put back. Until the call returns, a sweep by another
        // stopping service leaves the claim alone; the outcomes below decide it.
        await Parallel.ForEachAsync(Enumerable.Range(0, batch.Length), new ParallelOptions { MaxDegreeOfParallelism = fanOut }, async (i, _) =>
        {
            var entry = _work.Record(batch[i], _internalTickerManager.NextClaimMarker(), claimInFlight: true);
            entries[i] = entry;
            try
            {
                outcomes[i] = await _internalTickerManager.TryClaimDueTicker(batch[i], entry.ClaimMarker, stoppingToken).ConfigureAwait(false)
                    ? ClaimOutcome.Won
                    : ClaimOutcome.Lost;
            }
            catch (Exception ex)
            {
                outcomes[i] = ClaimOutcome.Failed;
                failures[i] = ex;
            }
        }).ConfigureAwait(false);

        Exception firstFailure = null;
        for (var i = 0; i < batch.Length; i++)
        {
            var function = batch[i];
            switch (outcomes[i])
            {
                case ClaimOutcome.Lost:
                    entries[i].Forget();
                    _logger?.LogInformation("Job {FunctionName} ({TickerId}) is no longer this node's to run (taken by another claim, cancelled or removed) and is not run by this loop.",
                        function.FunctionName, function.TickerId);
                    break;

                case ClaimOutcome.Failed:
                    firstFailure ??= failures[i];
                    entries[i].ClaimSettled();
                    await _work.PutBackAsync(entries[i], "its claim call failed", failures[i], quietWhenNothingToPutBack: true);
                    break;

                default:
                    entries[i].ClaimSettled();
                    if (!CanHandOver(stoppingToken))
                    {
                        await _work.PutBackAsync(entries[i], "the scheduler stopped taking work before its worker got it");
                        break;
                    }

                    await NotifyInProgressAsync(function);
                    // Not awaited, as before: the loop hands the whole batch over in priority order and is not held by a
                    // full scheduler. A refusal is still put back.
                    _ = _work.QueueWork(entries[i], isDue: false, stoppingToken);
                    break;
            }
        }

        if (firstFailure != null)
            ExceptionDispatchInfo.Capture(firstFailure).Throw();
    }

    private async Task NotifyInProgressAsync(InternalFunctionContext function)
    {
        // The notification is for the dashboard and does not decide the job.
        try
        {
            await _internalTickerManager.NotifyInProgress(function);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Job {FunctionName} ({TickerId}) is running but the dashboard could not be notified.",
                function.FunctionName, function.TickerId);
        }
    }

    private static TimeSpan ResolveMinPollingInterval(SchedulerOptionsBuilder schedulerOptions)
    {
        if (schedulerOptions == null)
            return TimeSpan.FromSeconds(1);

        var prop = schedulerOptions.GetType().GetProperty("MinPollingInterval");
        if (prop?.PropertyType == typeof(TimeSpan))
            return (TimeSpan)prop.GetValue(schedulerOptions);

        return TimeSpan.FromSeconds(1);
    }

    private async Task ReleaseAllResourcesAsync(Exception ex)
    {
        var report = RecordLoopFailure(ex);

        // The notification reaches whatever handlers were attached to the context, so it is guarded for the same reason. Its
        // failure only affects the dashboard, the scheduler goes on, so it is a warning.
        try
        {
            if (ex != null)
                _executionContext.NotifyCoreAction?.Invoke(ex.ToString(), CoreNotifyActionType.NotifyHostExceptionMessage);
        }
        catch (Exception notifyException)
        {
            if (report)
                _logger?.LogWarning(notifyException, "The scheduler could not report its failure to the dashboard.");
            else
                _logger?.LogDebug(notifyException, "The scheduler could not report its failure to the dashboard.");
        }

        await TryReleaseAcquiredResourcesAsync(null, CancellationToken.None, report: report);
    }

    // The loop tries again every second while it fails, so an outage of the store would write two errors a second for as long
    // as it lasts. It is logged by its changes instead: an error when the loop starts failing, a reminder with the attempts and
    // the time so far once a minute while it goes on, and one line when it recovers, so the log tells how long jobs were not
    // picked up. The attempts in between are written at Debug. A short outage heals by itself and is an error; one that lasts
    // longer than CriticalFailureDuration means the store stays out of reach and needs someone, so its reminders are critical.
    // Only the loop's own thread uses these fields.
    internal static readonly TimeSpan FailureReminderInterval = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan CriticalFailureDuration = TimeSpan.FromMinutes(5);
    private DateTime? _failingSince;
    private int _failedAttempts;
    private DateTime _nextFailureReminder;

    private DateTime Now => _clock?.UtcNow ?? DateTime.UtcNow;

    private static TimeSpan ToWholeSeconds(TimeSpan duration) => TimeSpan.FromSeconds(Math.Round(duration.TotalSeconds));

    /// <summary>Records a failure of the loop and logs it when the failure starts or a reminder is due. Returns whether it logged.</summary>
    private bool RecordLoopFailure(Exception ex)
    {
        var now = Now;
        _failedAttempts++;

        if (_failingSince == null)
        {
            _failingSince = now;
            _nextFailureReminder = now + FailureReminderInterval;
            _logger?.LogError(ex, "The scheduler loop failed. No job is picked up until it recovers; it tries again every second.");
            return true;
        }

        if (now < _nextFailureReminder)
        {
            _logger?.LogDebug(ex, "The scheduler loop failed again, attempt {Attempts}.", _failedAttempts);
            return false;
        }

        _nextFailureReminder = now + FailureReminderInterval;
        var failingFor = now - _failingSince.Value;
        _logger?.Log(failingFor >= CriticalFailureDuration ? LogLevel.Critical : LogLevel.Error, ex,
            "The scheduler loop is still failing: {Attempts} attempts in {Duration}. No job is picked up until it recovers.",
            _failedAttempts, ToWholeSeconds(failingFor));
        return true;
    }

    private void RecordLoopRecovered()
    {
        if (_failingSince == null)
            return;

        _logger?.LogInformation("The scheduler loop recovered after {Attempts} failed attempts in {Duration}.",
            _failedAttempts, ToWholeSeconds(Now - _failingSince.Value));
        _failingSince = null;
        _failedAttempts = 0;
    }

    // Releasing what the loop held talks to the store, so it fails for the same reason a poll fails when the store cannot be
    // reached. It runs inside the loop's error handling, where an exception would leave ExecuteAsync, and a background
    // service that throws stops the whole host under the default HostOptions.BackgroundServiceExceptionBehavior (StopHost).
    // A short database outage then ended the application. A failed release is logged and the loop goes on: the locks it
    // could not release are this node's own and are acquired again by its next poll.
    // When the application is stopping, a failed release is a warning: the stop itself goes on normally, and the locks stay
    // with this node until it acquires them again.
    // While the loop is failing, a failed release is part of the same outage and is logged only with the loop's own reports.
    private async Task TryReleaseAcquiredResourcesAsync(InternalFunctionContext[] resources, CancellationToken cancellationToken, bool stopping = false, bool report = true)
    {
        try
        {
            await _internalTickerManager.ReleaseAcquiredResources(resources, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (stopping)
        {
            _logger?.LogWarning(ex, "The scheduler could not release the jobs it had acquired while the application was stopping.");
        }
        catch (Exception ex) when (!report)
        {
            _logger?.LogDebug(ex, "The scheduler could not release the jobs it had acquired.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "The scheduler could not release the jobs it had acquired.");
        }
    }

    public void RestartIfNeeded(DateTime? dateTime)
    {
        if (!dateTime.HasValue)
            return;
        
        var now = DateTime.UtcNow;
        var nextPlannedOccurrence = _executionContext.GetNextPlannedOccurrence();
        
        // Restart if:
        // 1. No tasks are currently planned, OR
        // 2. The new task should execute at least 500ms earlier than the currently planned task, OR
        // 3. The new task is already due/overdue (ExecutionTime <= now)
        if (nextPlannedOccurrence == null)
        {
            _restartThrottle.RequestRestart();
            return;
        }

        var newTime = dateTime.Value;
        var threshold = TimeSpan.FromMilliseconds(500);
        var diff = nextPlannedOccurrence.Value - newTime;

        if (newTime <= now || diff > threshold)
            _restartThrottle.RequestRestart();
    }

    public void Restart()
    {
        _restartThrottle.RequestRestart();
    }
    
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _taskScheduler.Freeze();
        Interlocked.Exchange(ref _started, 0);
        await base.StopAsync(cancellationToken);
        await WaitForRunningJobsAsync(cancellationToken);
        // After the stop no worker starts a job of this node any more: what is still pending is put back, within the time
        // the host gives the stop.
        await _work.PutBackUnstartedAsync(cancellationToken: cancellationToken);
    }

    // The jobs run on the scheduler's workers, not inside this service, so stopping the service did not wait for them. The
    // host then disposed its services under them: a job that honored the cancellation could not write that it was cancelled
    // and stayed InProgress. The wait is bounded, because a job that ignores its token cannot be stopped and whatever ends the
    // process, an orchestrator that runs out of patience for one, will not wait for it either. What is still running at the end
    // is recorded as cancelled, with its lock released. The host waits for this method even after its own shutdown timeout has
    // passed and cancelled the token, so recording the outcome then still happens before it disposes its services.
    private const int MaxJobsInLog = 10;

    /// <summary>
    /// Replaces the wait limit that follows the host's shutdown timeout. Tests use it to keep a stop short.
    /// </summary>
    internal TimeSpan? ShutdownWaitLimit { get; set; }

    // The host's shutdown timeout is the setting an application already manages for how long a stop may take, so the wait follows it:
    // a little less, so there is time left to record the jobs that did not finish. A timeout that is zero or infinite means the host
    // sets no limit, and the default of the host, 30 seconds, is used. The wait also ends when the host cancels the token it passes
    // to StopAsync, which it does at that timeout.
    internal static TimeSpan WaitLimitFor(TimeSpan hostShutdownTimeout)
    {
        var timeout = hostShutdownTimeout <= TimeSpan.Zero || hostShutdownTimeout == Timeout.InfiniteTimeSpan
            ? TimeSpan.FromSeconds(30)
            : hostShutdownTimeout;

        var margin = TimeSpan.FromTicks(timeout.Ticks / 5);
        if (margin < TimeSpan.FromMilliseconds(200)) margin = TimeSpan.FromMilliseconds(200);
        if (margin > TimeSpan.FromSeconds(5)) margin = TimeSpan.FromSeconds(5);

        var limit = timeout - margin;
        return limit > TimeSpan.Zero ? limit : TimeSpan.Zero;
    }

    private async Task WaitForRunningJobsAsync(CancellationToken hostToken)
    {
        var running = TickerCancellationTokenManager.GetRunningJobs(_taskHandler);
        if (running.Length == 0)
            return;

        _logger?.LogWarning("The application is stopping and {JobCount} job(s) are running. Waiting for them to finish: {Jobs}.",
            running.Length, DescribeJobs(running));

        var limit = ShutdownWaitLimit ?? WaitLimitFor(_hostOptions?.Value.ShutdownTimeout ?? TimeSpan.FromSeconds(30));
        var waited = Stopwatch.StartNew();
        while (running.Length > 0 && waited.Elapsed < limit && !hostToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), hostToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            running = TickerCancellationTokenManager.GetRunningJobs(_taskHandler);
        }

        if (running.Length == 0)
            return;

        var now = _clock?.UtcNow ?? DateTime.UtcNow;
        foreach (var job in running)
        {
            var context = new InternalFunctionContext
            {
                TickerId = job.TickerId,
                FunctionName = job.FunctionName,
                Type = job.Type,
                ParentId = job.ParentId == Guid.Empty ? null : job.ParentId
            };
            context.SetProperty(x => x.Status, TickerStatus.Cancelled)
                .SetProperty(x => x.ExecutedAt, now)
                .SetProperty(x => x.ExceptionDetails, "The application stopped before the job finished.")
                .SetProperty(x => x.ReleaseLock, true);

            try
            {
                await _internalTickerManager.UpdateTickerAsync(context, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Could not record job {FunctionName} ({TickerId}) as Cancelled when the application stopped.", job.FunctionName, job.TickerId);
            }
        }

        _logger?.LogWarning("{JobCount} job(s) had not finished when the wait for them ended and are recorded as Cancelled: {Jobs}. " +
                            "A job that ignores its cancellation token keeps running until the process ends.",
            running.Length, DescribeJobs(running));
    }

    private static string DescribeJobs(RunningJob[] jobs)
    {
        var listed = string.Join(", ", jobs.Take(MaxJobsInLog).Select(j => $"{j.FunctionName} ({j.TickerId})"));
        return jobs.Length > MaxJobsInLog ? $"{listed} and {jobs.Length - MaxJobsInLog} more" : listed;
    }

    public override void Dispose()
    {
        _stopWakingOnPutBacks();
        _stopWakingOnLostOutcomes();
        _restartThrottle.Dispose();
        base.Dispose();
    }
}
