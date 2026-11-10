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
    /// <summary>Writes the changed fields of a running time ticker. Returns the rows written, 0 when the fence does not hold.</summary>
    Task<int> UpdateTimeTickerFencedAsync(InternalFunctionContext context, DateTime runMarker, CancellationToken cancellationToken = default);

    /// <summary>Writes the changed fields of a running cron occurrence. Returns the rows written, 0 when the fence does not hold.</summary>
    Task<int> UpdateCronOccurrenceFencedAsync(InternalFunctionContext context, DateTime runMarker, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records an inline chain child (one without a time of its own, never claimed) as started by this node: InProgress, this
    /// node's lock and the run marker. By Id, as the child's write was before: the last run that starts it holds it.
    /// Returns the rows written, 0 when the row does not exist.
    /// </summary>
    Task<int> StartInlineChildAsync(Guid timeTickerId, DateTime runMarker, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the changed fields of a cron occurrence that has not started (Idle or Queued), for a cron edit. A running
    /// occurrence keeps its run untouched. Returns the rows written.
    /// </summary>
    Task<int> UpdateNotStartedCronOccurrenceAsync(InternalFunctionContext context, CancellationToken cancellationToken = default);
}
