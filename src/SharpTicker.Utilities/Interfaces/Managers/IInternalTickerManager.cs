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
        Task UpdateTickerAsync(InternalFunctionContext context, CancellationToken cancellationToken = default);
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
