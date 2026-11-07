using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SharpTicker.Utilities.Models;

namespace SharpTicker.BackgroundServices;

/// <summary>
/// The claims this node won (or whose claim call failed and may have landed) and whose job has not started. Each claim
/// ends exactly one way: its worker starts it, or it is put back with a fenced write so the fallback runs it once later.
/// The two are decided by one atomic transition, so a job that is being put back never starts and a started job is never
/// put back.
/// </summary>
internal sealed class ClaimLedger
{
    private readonly ConcurrentDictionary<Entry, byte> _entries = new();

    internal int Count => _entries.Count;

    /// <summary>
    /// Records a claim. <paramref name="claimInFlight"/>: the claim call has not returned yet, so the claim is not pending
    /// until <see cref="Entry.ClaimSettled"/> (or <see cref="Entry.Forget"/>); a sweep in the meantime leaves it to the
    /// caller, which then starts its job or puts it back.
    /// </summary>
    internal Entry Record(InternalFunctionContext function, DateTime claimMarker, bool claimInFlight = false)
    {
        var entry = new Entry(this, function, claimMarker, claimInFlight);
        _entries.TryAdd(entry, 0);
        return entry;
    }

    /// <summary>The claims that are neither started nor being put back right now.</summary>
    internal IReadOnlyList<Entry> Pending() => _entries.Keys.Where(x => x.IsPending).ToArray();

    /// <summary>The pending claims whose put-back failed and has to be tried again; a claim waiting for its worker is not one.</summary>
    internal IReadOnlyList<Entry> PendingPutBacks() => _entries.Keys.Where(x => x.IsPending && x.NeedsPutBack).ToArray();

    internal sealed class Entry
    {
        private const int PendingState = 0;
        private const int StartedState = 1;
        private const int RevertingState = 2;
        private const int GoneState = 3;
        // A put-back was tried and failed. It may still have landed, so the job must not start any more; only another
        // put-back can end the claim.
        private const int PutBackFailedState = 4;
        // The claim call has not returned: whether there is anything to start or put back is not known yet, and only the
        // caller that made the call decides it.
        private const int ClaimInFlightState = 5;

        private readonly ClaimLedger _ledger;
        private int _state;

        internal Entry(ClaimLedger ledger, InternalFunctionContext function, DateTime claimMarker, bool claimInFlight = false)
        {
            _ledger = ledger;
            Function = function;
            ClaimMarker = claimMarker;
            _state = claimInFlight ? ClaimInFlightState : PendingState;
        }

        /// <summary>The claim call returned (won, or failed after its write may have landed): the claim is now pending.</summary>
        internal void ClaimSettled() => Interlocked.CompareExchange(ref _state, PendingState, ClaimInFlightState);

        internal InternalFunctionContext Function { get; }

        internal DateTime ClaimMarker { get; }

        /// <summary>Neither started nor gone, and no put-back is under way right now.</summary>
        internal bool IsPending => Volatile.Read(ref _state) is PendingState or PutBackFailedState;

        /// <summary>A put-back of this claim was tried and failed; the loop tries it again.</summary>
        internal bool NeedsPutBack => Volatile.Read(ref _state) == PutBackFailedState;

        /// <summary>The worker is about to run the job. False: the claim is being put back, or a put-back was tried; the job must not run.</summary>
        internal bool TryStart() => Leave(PendingState, StartedState);

        /// <summary>The caller may put the claim back. False: the job started (or the claim is already handled).</summary>
        internal bool TryBeginRevert()
            => Interlocked.CompareExchange(ref _state, RevertingState, PendingState) == PendingState
               || Interlocked.CompareExchange(ref _state, RevertingState, PutBackFailedState) == PutBackFailedState;

        internal void RevertSucceeded() => Leave(RevertingState, GoneState);

        /// <summary>How many put-backs of this claim failed so far.</summary>
        internal int FailedPutBacks => Volatile.Read(ref _failedPutBacks);

        private int _failedPutBacks;

        /// <summary>
        /// The put-back did not reach the store, or its reply was lost. The claim stays in the ledger to be tried again, and
        /// it can no longer start: the write may have landed, and the fallback may then be running the job.
        /// </summary>
        internal void RevertFailed()
        {
            Interlocked.Increment(ref _failedPutBacks);
            Interlocked.CompareExchange(ref _state, PutBackFailedState, RevertingState);
        }

        /// <summary>The claim was lost (another claim took the row first): nothing to start, nothing to put back.</summary>
        internal void Forget()
        {
            if (!Leave(ClaimInFlightState, GoneState))
                Leave(PendingState, GoneState);
        }

        private bool Leave(int from, int to)
        {
            if (Interlocked.CompareExchange(ref _state, to, from) != from)
                return false;

            _ledger._entries.TryRemove(this, out _);
            return true;
        }
    }
}
