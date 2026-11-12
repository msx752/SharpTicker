using System;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Utilities.Interfaces.Managers
{
    internal interface IInternalTickerManager
    {
        Task<(TimeSpan TimeRemaining, InternalFunctionContext[] Functions)> GetNextTickers(CancellationToken cancellationToken = default);
        Task ReleaseAcquiredResources(InternalFunctionContext[] context, CancellationToken cancellationToken = default);
        // Fenced Queued -> InProgress claim writing claimMarker; sets Status on a win. The caller chose the marker, so a claim
        // whose call failed after its write landed can still be put back with TryReleaseClaim.
        Task<bool> TryClaimDueTicker(InternalFunctionContext function, DateTime claimMarker, CancellationToken cancellationToken = default);
        // Puts back a claim whose job never started; sets Status to Idle when this call did it.
        Task<bool> TryReleaseClaim(InternalFunctionContext function, DateTime claimMarker, CancellationToken cancellationToken = default);
        // The next claim marker of this node, from the clock the store shares, so it never equals another claim's marker.
        DateTime NextClaimMarker();
        // How many due rows may be claimed at once on this store (1 when the store does not say).
        Task<int> GetMaxConcurrentClaims(CancellationToken cancellationToken = default);
        Task NotifyInProgress(InternalFunctionContext function);
        // Sends the function's current status to the dashboard.
        Task NotifyStatusChanged(InternalFunctionContext function);
        // Writes the function's changed fields to its row. True when the store has them: a time ticker store reports the rows
        // it wrote, so 0 rows (the row was deleted while the job ran) is false. A cron occurrence store reports nothing, so
        // there a write that did not throw counts as stored.
        // A run with a RunMarker on a fencing store writes only to the row it still holds (IFencedRunWriter); 0 rows is false.
        Task<bool> UpdateTickerAsync(InternalFunctionContext context, CancellationToken cancellationToken = default);
        // Whether the store fences the writes of a run (IFencedRunWriter). A claimed job carries a RunMarker whatever the store,
        // so only this says whether its writes are fenced; an unfenced write is by Id and could land on a later run.
        bool FencesRuns { get; }
        // Writes again a lost outcome (LostOutcomeRetryList), fenced only: throws InvalidOperationException when the store does
        // not fence or the context has no RunMarker. True when stored, false when the fence did not hold (0 rows). A failing
        // dashboard notification after a stored write is logged, not thrown, so a stored outcome never looks lost.
        Task<bool> StoreLostOutcomeAsync(InternalFunctionContext context, CancellationToken cancellationToken = default);
        // The stop's write of a job still running (Cancelled, lock released). With a RunMarker on a fencing store it lands only
        // while the row is still this run and still InProgress; otherwise by Id, as before. True when written.
        Task<bool> RecordStoppedRunAsync(InternalFunctionContext context, CancellationToken cancellationToken = default);
        // Starts an inline chain child: its row goes to InProgress. On a fencing store this is a claim (the row must still wait
        // for its start) that takes this node's lock and a run marker, set on the child; false when the claim was refused, and
        // the child must then not run here. On another store the write is by Id and the result is always true. Throws when the
        // store fails; a claim that may have landed before it failed is put back first.
        Task<bool> RecordChildStartedAsync(InternalFunctionContext child, CancellationToken cancellationToken = default);
        Task<T> GetRequestAsync<T>(Guid tickerId, TickerType type, CancellationToken cancellationToken = default);
        Task<T> GetRequestAsync<T>(Guid tickerId, TickerType type, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default);
        Task<InternalFunctionContext[]> RunTimedOutTickers(CancellationToken cancellationToken = default);
        Task MigrateDefinedCronTickers((string, string)[] cronExpressions, CancellationToken cancellationToken = default);
        Task DeleteTicker(Guid tickerId, TickerType type, CancellationToken cancellationToken = default);
        Task ReleaseDeadNodeResources(string instanceIdentifier, CancellationToken cancellationToken = default);

        // What an earlier run of this node left locked: only rows locked before runStartedAt, so the rows this run has
        // claimed since keep their lock.
        Task ReleaseEarlierRunResources(string nodeIdentifier, DateTime runStartedAt, CancellationToken cancellationToken = default);
        Task UpdateSkipTimeTickersWithUnifiedContextAsync(InternalFunctionContext[] context, CancellationToken cancellationToken = default);
        // Claims delayed chain children whose parent has ended (the store's gate decides) to run them now. Returns the rows
        // this call won, ready to dispatch; a failed claim is logged and returns none, the fallback then runs them.
        Task<InternalFunctionContext[]> AcquireDelayedChildrenAsync(Guid[] ids, CancellationToken cancellationToken = default);
        Task<int> SkipStaleCronOccurrencesAsync(TimeSpan staleThreshold, CancellationToken cancellationToken = default);
    }
}
