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

    internal Entry Record(InternalFunctionContext function, DateTime claimMarker)
    {
        var entry = new Entry(this, function, claimMarker);
        _entries.TryAdd(entry, 0);
        return entry;
    }

    /// <summary>The claims that are neither started nor being put back right now.</summary>
    internal IReadOnlyList<Entry> Pending() => _entries.Keys.Where(x => x.IsPending).ToArray();

    internal sealed class Entry
    {
        private const int PendingState = 0;
        private const int StartedState = 1;
        private const int RevertingState = 2;
        private const int GoneState = 3;

        private readonly ClaimLedger _ledger;
        private int _state = PendingState;

        internal Entry(ClaimLedger ledger, InternalFunctionContext function, DateTime claimMarker)
        {
            _ledger = ledger;
            Function = function;
            ClaimMarker = claimMarker;
        }

        internal InternalFunctionContext Function { get; }

        internal DateTime ClaimMarker { get; }

        internal bool IsPending => Volatile.Read(ref _state) == PendingState;

        /// <summary>The worker is about to run the job. False: the claim is being put back, the job must not run.</summary>
        internal bool TryStart() => Leave(PendingState, StartedState);

        /// <summary>The caller may put the claim back. False: the job started (or the claim is already handled).</summary>
        internal bool TryBeginRevert() => Interlocked.CompareExchange(ref _state, RevertingState, PendingState) == PendingState;

        internal void RevertSucceeded() => Leave(RevertingState, GoneState);

        /// <summary>The put-back did not reach the store; the row is still this node's, so the claim is pending again.</summary>
        internal void RevertFailed() => Interlocked.CompareExchange(ref _state, PendingState, RevertingState);

        /// <summary>The claim was lost (another claim took the row first): nothing to start, nothing to put back.</summary>
        internal void Forget() => Leave(PendingState, GoneState);

        private bool Leave(int from, int to)
        {
            if (Interlocked.CompareExchange(ref _state, to, from) != from)
                return false;

            _ledger._entries.TryRemove(this, out _);
            return true;
        }
    }
}
