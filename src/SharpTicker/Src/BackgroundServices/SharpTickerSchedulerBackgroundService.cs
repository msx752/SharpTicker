using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
        ITickerClock clock = null)
    {
        _logger = logger;
        _clock = clock;
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
                await _internalTickerManager.ReleaseAcquiredResources(_executionContext.Functions, stoppingToken);
                // Small delay to allow resources to be released
                await Task.Delay(100, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Application is shutting down - release resources and exit
                await _internalTickerManager.ReleaseAcquiredResources(_executionContext.Functions, CancellationToken.None);
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
        if (ex != null && _executionContext.NotifyCoreAction != null)
            _executionContext.NotifyCoreAction(ex.ToString(), CoreNotifyActionType.NotifyHostExceptionMessage);

        await _internalTickerManager.ReleaseAcquiredResources(null, CancellationToken.None);
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
    /// How long the stop waits for the running jobs. Shorter than the 30 seconds that the host and Kubernetes give by default,
    /// so there is time left to record the jobs that did not finish. The wait also ends when the host cancels the token it
    /// passes to <see cref="StopAsync"/>, which it does at its own shutdown timeout. Tests shorten it.
    /// </summary>
    internal TimeSpan ShutdownWaitLimit { get; set; } = TimeSpan.FromSeconds(20);

    private async Task WaitForRunningJobsAsync(CancellationToken hostToken)
    {
        var running = TickerCancellationTokenManager.GetRunningJobs(_taskHandler);
        if (running.Length == 0)
            return;

        _logger?.LogWarning("The application is stopping and {JobCount} job(s) are running. Waiting for them to finish: {Jobs}.",
            running.Length, DescribeJobs(running));

        var limit = ShutdownWaitLimit;
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
