using System;
using System.Threading;
using System.Threading.Tasks;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Utilities.Interfaces;

/// <summary>
/// The writes of a running job, fenced by its run marker: the marker its claim (or, for an inline chain child, its start)
/// wrote to UpdatedAt. During the run every write keeps UpdatedAt at that marker, and a write lands only on the row that
/// still holds it: <c>Id ∧ LockHolder = this node ∧ UpdatedAt = marker</c>. A row that was released and claimed again, by
/// another node or by this one, carries another marker or lock, so a late write of the old run changes nothing. The marker is
/// unique per node, so a write that landed and is sent again lands on the same row with the same values.
/// Internal on purpose, like <see cref="IChildSkipWriter"/>: a provider that does not implement it keeps today's write by Id.
/// </summary>
internal interface IFencedRunWriter
{
    /// <summary>
    /// Writes the changed fields of a running time ticker. Returns the rows written, 0 when the fence does not hold. With
    /// <paramref name="onlyWhileRunning"/>, the row must also still be InProgress: the stop's Cancelled write, which must not
    /// replace an outcome the run stored with the same marker (an outcome write keeps the lock and the marker).
    /// </summary>
    Task<int> UpdateTimeTickerFencedAsync(InternalFunctionContext context, DateTime runMarker, bool onlyWhileRunning, CancellationToken cancellationToken = default);

    /// <summary>Writes the changed fields of a running cron occurrence, as <see cref="UpdateTimeTickerFencedAsync"/>.</summary>
    Task<int> UpdateCronOccurrenceFencedAsync(InternalFunctionContext context, DateTime runMarker, bool onlyWhileRunning, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts an inline chain child (one without a time of its own) for this node: InProgress, this node's lock and the run
    /// marker. A claim: the row is written only while it is Idle, unlocked and still without a time, so of several starts
    /// of the same child one wins. Returns 1 when this call started it, 0 when the row is missing or does not wait for its
    /// start (it runs or ran elsewhere, another node holds it, or it was given a time and is a delayed child now).
    /// </summary>
    Task<int> StartInlineChildAsync(Guid timeTickerId, DateTime runMarker, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the changed fields of a cron occurrence that has not started (Idle or Queued), for a cron edit. A running
    /// occurrence keeps its run untouched. Returns the rows written.
    /// </summary>
    Task<int> UpdateNotStartedCronOccurrenceAsync(InternalFunctionContext context, CancellationToken cancellationToken = default);
}
