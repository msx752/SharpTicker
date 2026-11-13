using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SharpTicker.EntityFrameworkCore.Infrastructure;

/// <summary>
/// What a starting node reads to report the jobs that are InProgress under another node identifier. With this store
/// alone such a job is released only when a node with the same identifier starts; nothing tells a node that is gone from
/// one that is running.
/// </summary>
internal interface IJobsOfOtherNodes
{
    /// <summary>
    /// The node identifiers other than <paramref name="nodeIdentifier"/> that hold jobs which are InProgress, time jobs
    /// and cron occurrences together, each with how many.
    /// </summary>
    Task<IReadOnlyList<(string NodeIdentifier, int Jobs)>> CountInProgressOfOtherNodesAsync(string nodeIdentifier, CancellationToken cancellationToken = default);
}
