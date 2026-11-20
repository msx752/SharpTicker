using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpTicker.EntityFrameworkCore.Infrastructure;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Interfaces;

namespace SharpTicker.EntityFrameworkCore.DependencyInjection;

/// <summary>
/// The lease on the jobs that are InProgress (#24). Every fifth of the lease this node renews the lock of the jobs it
/// holds in this run, then records the jobs of other node identifiers whose lock was not renewed for the whole lease as
/// Cancelled, so that they do not run again (#211; up to 1.6.1 they were released and the fallback ran them again). The
/// first pass comes one fifth of the lease after the start. Registered only when the background services run and the
/// lease is not zero.
/// <para>
/// It runs with Redis next to the store too. The heartbeat release frees the nodes Redis knows of, to Idle, so that their
/// jobs run again; this one also takes rows under names that never wrote a heartbeat there (rows from before Redis was
/// added, for one). A row one of them wrote is no longer InProgress, so it does not match the other.
/// </para>
/// </summary>
internal sealed class InProgressLeaseBackgroundService<TTimeTicker, TCronTicker> : BackgroundService
    where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
    where TCronTicker : CronTickerEntity, new()
{
    internal const string LogCategory = "SharpTicker.EntityFrameworkCore.InProgressLease";
    private const int MaxNodesInLog = 10;

    private readonly IInProgressLease _store;
    private readonly TickerExecutionContext _executionContext;
    private readonly TimeSpan _lease;
    private readonly TimeSpan _interval;
    private readonly ILogger _logger;

    public InProgressLeaseBackgroundService(ITickerPersistenceProvider<TTimeTicker, TCronTicker> store, SharpTickerEfCoreOptionBuilder<TTimeTicker, TCronTicker> options,
        TickerExecutionContext executionContext, ILoggerFactory loggerFactory = null)
    {
        _store = store as IInProgressLease;
        _executionContext = executionContext;
        _lease = options.InProgressLease;
        // SetInProgressLease keeps the lease from 1 second up to 30 days, so a fifth of it is a period the timer accepts.
        _interval = TimeSpan.FromTicks(_lease.Ticks / 5);
        _logger = loggerFactory?.CreateLogger(LogCategory) ?? NullLogger.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_store == null)
        {
            _logger.LogInformation("The persistence provider does not derive from the EF Core provider of SharpTicker, so jobs that are InProgress have no lease: " +
                                   "a job left InProgress is released only when an instance starts under the name that holds it.");
            return;
        }

        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await PassAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task PassAsync(CancellationToken stoppingToken)
    {
        // Set by the initializer before any job of this run is claimed; without it this host takes no jobs.
        if (_executionContext.RunStartedAt is not { } runStartedAt)
            return;

        try
        {
            await _store.RenewInProgressLeaseAsync(runStartedAt, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (stoppingToken.IsCancellationRequested)
        {
            StoppedDuringAPass(ex);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Renewing the lease of the jobs this node runs failed; it is tried again in {Interval}. If it fails for {Lease}, " +
                                   "other instances record these jobs as Cancelled.", _interval, _lease);
        }

        try
        {
            var (cancelled, nodes) = await _store.CancelExpiredInProgressOfOtherNodesAsync(_lease, stoppingToken).ConfigureAwait(false);
            if (cancelled == 0)
                return;

            var listed = nodes.OrderByDescending(x => x.Jobs).ThenBy(x => x.NodeIdentifier, StringComparer.Ordinal).Take(MaxNodesInLog)
                .Select(x => $"{x.NodeIdentifier} ({x.Jobs})");
            var names = string.Join(", ", listed) + (nodes.Count > MaxNodesInLog ? $" and {nodes.Count - MaxNodesInLog} more" : "");
            _logger.LogInformation("{Cancelled} job(s) that were InProgress under other node identifiers, with a lock not renewed for {Lease}, were recorded as Cancelled; they do not run again: {Nodes}.",
                cancelled, _lease, names);
        }
        catch (Exception ex) when (stoppingToken.IsCancellationRequested)
        {
            StoppedDuringAPass(ex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recording the jobs of other nodes whose lease ended as Cancelled failed; it is tried again in {Interval}.", _interval);
        }
    }

    // The host stops: what the store throws now (a disposed context pool, a cancelled command) says nothing about the
    // store, so the service ends as a stop, not as a failure.
    private void StoppedDuringAPass(Exception ex)
        => _logger.LogDebug(ex, "A lease pass ended because the host stops.");
}
