using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Exceptions;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Models;

namespace SharpTicker.BackgroundServices;

internal class SharpTickerFallbackBackgroundService :  BackgroundService
{
    private int _started;
    private readonly IInternalTickerManager _internalTickerManager;
    private readonly ISharpTickerTaskScheduler _sharpTickerTaskScheduler;
    private readonly ClaimedWorkQueue _work;
    private readonly ILogger<SharpTickerFallbackBackgroundService> _logger;
    private readonly TimeSpan _fallbackJobPeriod;
    private bool _failing;

    public SharpTickerFallbackBackgroundService(IInternalTickerManager internalTickerManager, SchedulerOptionsBuilder schedulerOptions,
        ITickerExecutionTaskHandler tickerExecutionTaskHandler, ISharpTickerTaskScheduler sharpTickerTaskScheduler,
        ITickerFunctionConcurrencyGate concurrencyGate, ILogger<SharpTickerFallbackBackgroundService> logger = null,
        ClaimedWorkQueue claimedWork = null)
    {
        _internalTickerManager = internalTickerManager;
        _fallbackJobPeriod = schedulerOptions.FallbackIntervalChecker;
        _sharpTickerTaskScheduler = sharpTickerTaskScheduler;
        _logger = logger;
        _work = claimedWork ?? new ClaimedWorkQueue(internalTickerManager, sharpTickerTaskScheduler, tickerExecutionTaskHandler, concurrencyGate, logger);
    }

    public override Task StartAsync(CancellationToken ct)
    {
        return Interlocked.CompareExchange(ref _started, 1, 0) != 0
            ? Task.CompletedTask : base.StartAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // If the scheduler is frozen or disposed (e.g., manual start mode or shutdown),
                // skip queuing fallback work to avoid throwing and stopping the host.
                if (_sharpTickerTaskScheduler.IsFrozen || _sharpTickerTaskScheduler.IsDisposed)
                {
                    await Task.Delay(_fallbackJobPeriod, stoppingToken);
                    continue;
                }

                var functions = await _internalTickerManager.RunTimedOutTickers(stoppingToken);
                RecordRecovered();

                if (functions.Length != 0)
                {
                    foreach (var function in functions)
                    {
                        CacheFunctionReferences(function);
                        await HandOverAsync(function, stoppingToken);
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(10), stoppingToken);
                }
                else
                {
                    await Task.Delay(_fallbackJobPeriod, stoppingToken);
                }
            }
            catch (ClaimsInterruptedException interrupted)
            {
                // The store failed after some rows were claimed: they are InProgress for this node and are handed over (or
                // put back, when the application is stopping). The failure is then reported like any other.
                foreach (var function in interrupted.Claimed)
                {
                    CacheFunctionReferences(function);
                    await HandOverAsync(function, stoppingToken);
                }

                if (stoppingToken.IsCancellationRequested)
                    break;

                RecordFailure(interrupted.InnerException);
                await Task.Delay(_fallbackJobPeriod, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host is shutting down – exit gracefully.
                break;
            }
            catch (Exception ex)
            {
                // An exception must not leave ExecuteAsync: under the default BackgroundServiceExceptionBehavior it stops the
                // host. It is logged once when the fallback starts failing (the attempts after it at Debug) so an outage of the
                // safety net is not silent, and the fallback tries again after its period.
                RecordFailure(ex);
                await Task.Delay(_fallbackJobPeriod, stoppingToken);
            }
        }
    }

    // Every row RunTimedOutTickers returned is already InProgress for this node. A row that is not handed to a worker,
    // because the scheduler froze or the application is stopping, is put back, and so is one the scheduler refuses.
    private async Task HandOverAsync(InternalFunctionContext function, CancellationToken stoppingToken)
    {
        var entry = _work.Record(function, function.ClaimMarker ?? default, owner: this);

        if (stoppingToken.IsCancellationRequested || !_work.SchedulerTakesWork)
        {
            await _work.PutBackAsync(entry, "the scheduler stopped taking work before its worker got it");
            return;
        }

        // Waits until the scheduler accepted the job, so a full scheduler keeps the fallback from claiming more rows than
        // this node can run.
        await _work.QueueWork(entry, isDue: true, stoppingToken);
    }

    private static void CacheFunctionReferences(InternalFunctionContext function)
    {
        if (TickerFunctionProvider.TickerFunctions.TryGetValue(function.FunctionName, out var tickerItem))
        {
            function.CachedDelegate = tickerItem.Delegate;
            function.CachedPriority = tickerItem.Priority;
            function.CachedMaxConcurrency = tickerItem.MaxConcurrency;
        }

        foreach (var child in function.TimeTickerChildren)
        {
            if (TickerFunctionProvider.TickerFunctions.TryGetValue(child.FunctionName, out var childItem))
            {
                child.CachedDelegate = childItem.Delegate;
                child.CachedPriority = childItem.Priority;
                child.CachedMaxConcurrency = childItem.MaxConcurrency;
            }

            foreach (var grandChild in child.TimeTickerChildren)
            {
                if (TickerFunctionProvider.TickerFunctions.TryGetValue(grandChild.FunctionName, out var grandChildItem))
                {
                    grandChild.CachedDelegate = grandChildItem.Delegate;
                    grandChild.CachedPriority = grandChildItem.Priority;
                    grandChild.CachedMaxConcurrency = grandChildItem.MaxConcurrency;
                }
            }
        }
    }

    private void RecordFailure(Exception ex)
    {
        if (_failing)
        {
            _logger?.LogDebug(ex, "The fallback failed again; it tries again in {Period}.", _fallbackJobPeriod);
            return;
        }

        _failing = true;
        _logger?.LogError(ex, "The fallback failed. Overdue jobs are not picked up until it recovers; it tries again every {Period}.", _fallbackJobPeriod);
    }

    private void RecordRecovered()
    {
        if (!_failing)
            return;

        _failing = false;
        _logger?.LogInformation("The fallback recovered and picks up overdue jobs again.");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _started, 0);
        await base.StopAsync(cancellationToken);
        // The loop has ended: a job it handed over that has not started (one that waits for its concurrency slot, which the
        // stop ends) is put back instead of staying InProgress. Only its own: the scheduler stops later and handles the rest.
        await _work.PutBackUnstartedAsync(owner: this, cancellationToken: cancellationToken);
    }
}
