using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;

namespace SharpTicker.BackgroundServices;

/// <summary>
/// Writes again the outcomes whose final write threw (<see cref="LostOutcomeRetryList"/>), for the scheduler loop and its stop.
/// Each write is fenced by the run's marker (<see cref="IInternalTickerManager.StoreLostOutcomeAsync"/>): it lands only while the
/// row is still this run's, so it never replaces another run. A stored outcome is followed up for its delayed chain children
/// (<see cref="ILateOutcomeFollowUp"/>); a row that is no longer this run's is dropped. A write that fails again stays listed.
/// Starts no job.
/// </summary>
internal sealed class LostOutcomeDrain
{
    private const int MaxJobsInLog = 10;

    private readonly LostOutcomeRetryList _list;
    private readonly IInternalTickerManager _manager;
    private readonly ILateOutcomeFollowUp _followUp;
    private readonly ILogger _logger;
    private readonly ITickerClock _clock;

    // The reminder while outcomes stay unstored; only the loop's pass uses these.
    private DateTime _nextReminder = DateTime.MinValue;

    public LostOutcomeDrain(LostOutcomeRetryList list, IInternalTickerManager manager, ILateOutcomeFollowUp followUp = null,
        ILogger<LostOutcomeDrain> logger = null, ITickerClock clock = null)
    {
        _list = list ?? throw new ArgumentNullException(nameof(list));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _followUp = followUp;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>How long one write again may wait for the store. Not linked to any other token. Tests shorten it.</summary>
    internal TimeSpan OutcomeAttemptLimit { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long outcomes may stay unstored before a reminder is logged, and the time between reminders: at Error until the
    /// oldest has waited twice this long, at Critical after that.
    /// </summary>
    internal static readonly TimeSpan ReminderInterval = TimeSpan.FromMinutes(5);

    /// <summary>Whether outcomes wait to be written; the loop keeps its sleep short while they do.</summary>
    internal bool HasEntries => !_list.IsEmpty;

    private DateTime Now => _clock?.UtcNow ?? DateTime.UtcNow;

    /// <summary>Calls <paramref name="wake"/> whenever an outcome is listed; returns the unsubscribe.</summary>
    internal Action WakeOnLostOutcomes(Action wake)
    {
        _list.Added += wake;
        return () => _list.Added -= wake;
    }

    /// <summary>The scheduler starts again (the dashboard stopped and started it): outcomes are listed again.</summary>
    internal void Open() => _list.Open();

    private enum Attempt
    {
        Stored,
        NotStored,
        // The store answered with an error: the next outcome may still be stored (this one may be refused for its own content).
        Failed,
        // The attempt used up its limit: the store is out of reach, and waiting for it once per outcome would hold the caller.
        TimedOut
    }

    /// <summary>The epoch of the list, for a stop to take when it begins (see <see cref="StoreAtStopAsync"/>).</summary>
    internal long Epoch => _list.Epoch;

    /// <summary>
    /// One pass of the loop: each listed outcome, oldest first, is written once. An attempt that runs out of its limit ends the
    /// pass: the store is out of reach, and waiting for it once per outcome would hold the loop that long. A write the store
    /// refuses does not: an outcome it keeps refusing must not hold back the others.
    /// </summary>
    internal async Task RetryAsync()
    {
        var entries = _list.Snapshot();
        if (entries.Count == 0)
        {
            _nextReminder = DateTime.MinValue;
            return;
        }

        var failed = false;
        foreach (var entry in entries)
        {
            var (attempt, _) = await TryStoreAsync(entry).ConfigureAwait(false);
            if (attempt == Attempt.TimedOut)
            {
                failed = true;
                break;
            }

            if (attempt == Attempt.Failed)
            {
                failed = true;
                continue;
            }

            await SettleAsync(entry, attempt, claimChildren: true).ConfigureAwait(false);
        }

        if (failed)
            RemindIfDue();
    }

    /// <summary>
    /// The stop's last attempt. <paramref name="epoch"/> is the list's epoch when the stop began: when a start came in between,
    /// the running scheduler writes the outcomes and the stop does nothing. Otherwise the list is closed (an outcome lost after
    /// this is not listed) and each listed outcome is written once, each attempt within <see cref="OutcomeAttemptLimit"/> and
    /// not bound to the stop's token, which may be used up already. An attempt that runs out of its limit ends the attempts; one
    /// the store refuses does not. A stored outcome claims no child: the stopped scheduler would refuse it. Each one not stored
    /// is logged at Error and stays listed, for a start of the scheduler in this process.
    /// </summary>
    internal async Task StoreAtStopAsync(long epoch)
    {
        var entries = _list.Close(epoch);
        Exception storeFailure = null;
        var outOfReach = false;

        foreach (var entry in entries)
        {
            if (!outOfReach)
            {
                var (attempt, failure) = await TryStoreAsync(entry).ConfigureAwait(false);
                if (attempt is Attempt.Stored or Attempt.NotStored)
                {
                    await SettleAsync(entry, attempt, claimChildren: false).ConfigureAwait(false);
                    continue;
                }

                outOfReach = attempt == Attempt.TimedOut;
                storeFailure = failure;
            }

            _logger?.LogError(storeFailure,
                "The {Status} outcome of {FunctionName} ({TickerId}) could not be stored before the scheduler stopped. If the scheduler starts again in this process it writes it again; otherwise its row stays InProgress until the store releases the rows of this node, and the job can then run again.",
                entry.Context.Status, entry.Context.FunctionName, entry.Context.TickerId);
        }
    }

    private async Task<(Attempt, Exception)> TryStoreAsync(LostOutcome entry)
    {
        using var limit = new CancellationTokenSource(OutcomeAttemptLimit);
        try
        {
            var stored = await _manager.StoreLostOutcomeAsync(entry.Context, limit.Token).ConfigureAwait(false);
            return (stored ? Attempt.Stored : Attempt.NotStored, null);
        }
        catch (Exception ex)
        {
            // A write cancelled by the limit may have landed; the next one, on the same marker, then finds it stored.
            var attempts = entry.RecordFailedAttempt();
            _logger?.LogDebug(ex, "Writing the {Status} outcome of {FunctionName} ({TickerId}) again failed, attempt {Attempts}.",
                entry.Context.Status, entry.Context.FunctionName, entry.Context.TickerId, attempts);
            return (limit.IsCancellationRequested ? Attempt.TimedOut : Attempt.Failed, ex);
        }
    }

    // Only the caller that removes the entry reports it and acts on it: the loop and the stop can write the same one at once,
    // and both writes report it stored (a fenced write sent again lands again).
    private async Task SettleAsync(LostOutcome entry, Attempt attempt, bool claimChildren)
    {
        if (!_list.TryRemove(entry))
            return;

        var context = entry.Context;
        if (attempt == Attempt.NotStored)
        {
            _logger?.LogWarning(
                "The {Status} outcome of {FunctionName} ({TickerId}) was not stored: its row is no longer this run's (it was released, taken by another run, or ended meanwhile), so the job may run again. Its chain children are neither run nor skipped.",
                context.Status, context.FunctionName, context.TickerId);
            return;
        }

        _logger?.LogInformation("The {Status} outcome of {FunctionName} ({TickerId}) was stored late, after {Attempts} failed attempt(s) to write it again.",
            context.Status, context.FunctionName, context.TickerId, entry.FailedAttempts);

        if (_followUp == null)
            return;

        try
        {
            await _followUp.AfterLateStoreAsync(context, claimChildren, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "The delayed chain children of {FunctionName} ({TickerId}) could not be followed up after its outcome was stored late; the fallback runs those the outcome lets run.",
                context.FunctionName, context.TickerId);
        }
    }

    // While outcomes stay unstored their rows stay InProgress, and the attempts are written at Debug: once the oldest has
    // waited ReminderInterval, a reminder names them every interval: at Error while the oldest has waited less than twice the
    // interval, at Critical after that.
    private void RemindIfDue()
    {
        var entries = _list.Snapshot();
        if (entries.Count == 0)
            return;

        var now = Now;
        var waiting = now - entries.Min(e => e.LostAt);
        if (waiting < ReminderInterval || now < _nextReminder)
            return;

        _nextReminder = now + ReminderInterval;
        var level = waiting < ReminderInterval * 2 ? LogLevel.Error : LogLevel.Critical;
        var listed = string.Join(", ", entries.Take(MaxJobsInLog).Select(e => $"{e.Context.FunctionName} ({e.Context.TickerId})"));
        if (entries.Count > MaxJobsInLog)
            listed += $" and {entries.Count - MaxJobsInLog} more";

        _logger?.Log(level,
            "{Count} job outcome(s) could still not be stored; the oldest was lost {Duration} ago. Their rows stay InProgress, and the scheduler keeps writing them again: {Jobs}.",
            entries.Count, TimeSpan.FromSeconds(Math.Round(waiting.TotalSeconds)), listed);
    }
}
