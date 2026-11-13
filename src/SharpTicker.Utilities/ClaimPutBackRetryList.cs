using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SharpTicker.Utilities;

/// <summary>
/// The rows of this node whose put-back failed inside a claim call (<see cref="ClaimPutBackFailedException"/>). They are
/// InProgress for this node with no worker, and only a later put-back frees them while the node lives. The callers of the
/// claim calls add them here; the scheduler's queue of claimed work takes them over and tries the put-back again until it
/// reaches the store. One instance per service provider, registered only when the background services run (nothing would
/// try again otherwise).
/// </summary>
internal sealed class ClaimPutBackRetryList
{
    private readonly ConcurrentQueue<NotPutBackClaim> _claims = new();

    internal bool IsEmpty => _claims.IsEmpty;

    /// <summary>
    /// Raised after rows were added. The scheduler loop may be asleep (up to a day when nothing is due), so it subscribes and
    /// wakes up to put them back. A handler that throws is ignored: the caller is already handling a failure.
    /// </summary>
    internal event Action Added;

    /// <summary>Adds the rows of every <see cref="ClaimPutBackFailedException"/> in <paramref name="failure"/>; returns how many.</summary>
    internal int AddFrom(Exception failure)
    {
        var added = 0;
        foreach (var found in ClaimPutBackFailedException.FindIn(failure))
        foreach (var claim in found.NotPutBack)
        {
            _claims.Enqueue(claim);
            added++;
        }

        if (added > 0)
            RaiseAdded();
        return added;
    }

    /// <summary>
    /// Adds the claim of a run this node stopped without an outcome (see <see cref="NodeFence"/>): the row is still
    /// InProgress for this node, and the put-back lets the job run again.
    /// </summary>
    internal void Add(NotPutBackClaim claim)
    {
        _claims.Enqueue(claim);
        RaiseAdded();
    }

    private void RaiseAdded()
    {
        try
        {
            Added?.Invoke();
        }
        catch
        {
            // The rows are listed; the loop takes them at its next pass even if it was not woken.
        }
    }

    /// <summary>What becomes of the rows that could not be put back, for the log entry of the failed claim call.</summary>
    internal static string WhatHappens(bool retried)
        => retried
            ? "Those are put back by the scheduler loop once the store answers"
            : "Those stay InProgress until the store releases the rows of this node";

    /// <summary>Removes and returns every row added so far.</summary>
    internal IReadOnlyList<NotPutBackClaim> TakeAll()
    {
        var taken = new List<NotPutBackClaim>();
        while (_claims.TryDequeue(out var claim))
            taken.Add(claim);
        return taken;
    }
}
