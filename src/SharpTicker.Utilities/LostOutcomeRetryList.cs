using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Utilities;

/// <summary>
/// The outcomes of this node's runs whose final write threw. Such a row stays InProgress with this node's lock and the run's
/// marker, and nothing else writes it while the node lives; a later release would free it and the finished job would run
/// again. The scheduler loop writes these outcomes again, fenced by the run's marker, until the store takes them or the row
/// turns out to belong to another run. One entry per ticker id. One instance per service provider, registered only when the
/// background services run (nothing would write them otherwise).
/// </summary>
internal sealed class LostOutcomeRetryList
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, LostOutcome> _outcomes = new();
    private bool _open = true;
    private long _sequence;

    /// <summary>
    /// Raised after an outcome was listed. The scheduler loop may be asleep (up to a day when nothing is due), so it subscribes
    /// and wakes up to write it. A handler that throws is ignored: the caller is already handling a failure.
    /// </summary>
    internal event Action Added;

    internal bool IsEmpty
    {
        get
        {
            lock (_gate)
                return _outcomes.Count == 0;
        }
    }

    /// <summary>
    /// Lists the lost outcome of a run. A listed outcome of the same ticker is replaced: a second run of it on this node means
    /// the first run's row was released, so that run's fence cannot hold any more. Returns false, listing nothing, while the
    /// list is closed (the scheduler stopped: nothing would write it). The list keeps a copy of <paramref name="outcome"/>
    /// (<see cref="InternalFunctionContext.CopyForOutcomeRetry"/>).
    /// </summary>
    internal bool TryAdd(InternalFunctionContext outcome, bool isChild, DateTime lostAt)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.RunMarker is not { } runMarker)
            throw new ArgumentException("Only a fenced run's outcome is written again.", nameof(outcome));

        var copy = outcome.CopyForOutcomeRetry();
        lock (_gate)
        {
            if (!_open)
                return false;
            _outcomes[copy.TickerId] = new LostOutcome(copy, isChild, runMarker, ++_sequence, lostAt);
        }

        try
        {
            Added?.Invoke();
        }
        catch
        {
            // The outcome is listed; the loop takes it at its next pass even if it was not woken.
        }
        return true;
    }

    /// <summary>The listed outcomes, oldest first.</summary>
    internal IReadOnlyList<LostOutcome> Snapshot()
    {
        lock (_gate)
            return Ordered();
    }

    /// <summary>
    /// Removes this very entry, not a newer one of the same ticker. True for exactly one caller: the one that then reports the
    /// outcome and acts on it.
    /// </summary>
    internal bool TryRemove(LostOutcome entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            if (!_outcomes.TryGetValue(entry.Context.TickerId, out var listed) || !ReferenceEquals(listed, entry))
                return false;
            _outcomes.Remove(entry.Context.TickerId);
            return true;
        }
    }

    /// <summary>
    /// Closes the list as the scheduler stops and returns what is listed, in the same step: an outcome lost after this is not
    /// listed (its caller logs that it is not written again), and none slips in between. The entries stay listed until removed.
    /// </summary>
    internal IReadOnlyList<LostOutcome> Close()
    {
        lock (_gate)
        {
            _open = false;
            return Ordered();
        }
    }

    /// <summary>Opens the list again when the scheduler starts again (the dashboard stops and starts it).</summary>
    internal void Open()
    {
        lock (_gate)
            _open = true;
    }

    private List<LostOutcome> Ordered()
        => _outcomes.Values.OrderBy(o => o.Sequence).ToList();
}

/// <summary>A lost outcome waiting to be written again. Compared by reference: one entry is one loss.</summary>
internal sealed class LostOutcome
{
    private int _attempts;

    internal LostOutcome(InternalFunctionContext context, bool isChild, DateTime runMarker, long sequence, DateTime lostAt)
    {
        Context = context;
        IsChild = isChild;
        RunMarker = runMarker;
        Sequence = sequence;
        LostAt = lostAt;
    }

    /// <summary>The run's outcome, a copy (<see cref="InternalFunctionContext.CopyForOutcomeRetry"/>).</summary>
    internal InternalFunctionContext Context { get; }

    /// <summary>An inline chain child's outcome: its row was never claimed.</summary>
    internal bool IsChild { get; }

    internal DateTime RunMarker { get; }

    internal long Sequence { get; }

    /// <summary>When the final write failed.</summary>
    internal DateTime LostAt { get; }

    /// <summary>The writes tried again that failed too.</summary>
    internal int FailedAttempts => Volatile.Read(ref _attempts);

    internal int RecordFailedAttempt() => Interlocked.Increment(ref _attempts);
}
