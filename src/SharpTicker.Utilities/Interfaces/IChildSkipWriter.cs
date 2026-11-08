using System;
using System.Threading;
using System.Threading.Tasks;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Utilities.Interfaces;

/// <summary>
/// Writes the skip of chain children whose parent's outcome does not let them run, to the rows that have not started
/// (Idle or Queued) only. A delayed child (one with an execution time of its own) is a row the store hands out by itself,
/// so it may already run or have run when its parent ends; it keeps the status of that run. Internal on purpose, like
/// <see cref="IClaimFanOut"/>: a provider that does not implement it gets the plain
/// <see cref="ITickerPersistenceProvider{TTimeTicker,TCronTicker}.UpdateTimeTickersWithUnifiedContext"/> write.
/// </summary>
internal interface IChildSkipWriter
{
    Task SkipNotStartedTimeTickersAsync(Guid[] timeTickerIds, InternalFunctionContext skip, CancellationToken cancellationToken = default);
}
