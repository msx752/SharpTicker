using System;
using System.Diagnostics;
using System.Linq;
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
        IOptions<HostOptions> hostOptions = null)
    {
        _logger = logger;
        _clock = clock;
        _hostOptions = hostOptions;
        _executionContext = executionContext;
        _taskHandler = taskHandler;
        _taskScheduler = taskScheduler;
        _internalTickerManager = internalTickerManager ?? throw new ArgumentNullException(nameof(internalTickerManager));
        _concurrencyGate = concurrencyGate;
        _minPollingInterval = ResolveMinPollingInterval(schedulerOptions);
        _restartThrottle = new RestartThrottleManager(() => _schedulerLoopCancellationTokenSource?.Cancel());
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
            if (_executionContext.Functions.Length != 0)
            {
                await _internalTickerManager.SetTickersInProgress(_executionContext.Functions, cancellationToken);

                foreach (var function in _executionContext.Functions.OrderBy(x => x.CachedPriority))
                {
                    var semaphore = _concurrencyGate.GetSemaphoreOrNull(function.FunctionName, function.CachedMaxConcurrency);

                    _ = _taskScheduler.QueueAsync(async ct =>
                    {
                        if (semaphore != null)
                            await semaphore.WaitAsync(ct).ConfigureAwait(false);

                        try
                        {
                            await _taskHandler.ExecuteTaskAsync(function, false, ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            semaphore?.Release();
                        }
                    }, function.CachedPriority, stoppingToken);
                }
                
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

            var notify = _executionContext.NotifyCoreAction;
            if (notify != null)
            {
                notify(_executionContext.GetNextPlannedOccurrence(), CoreNotifyActionType.NotifyNextOccurence);
            }

            await Task.Delay(sleepDuration, cancellationToken);
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
        _restartThrottle.Dispose();
        base.Dispose();
    }
}
