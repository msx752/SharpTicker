using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SharpTicker.EntityFrameworkCore.Infrastructure;

/// <summary>
/// The lease on the rows that are InProgress (#24). With this store alone nothing tells a node that is gone from one that
/// is running, so the lock of a running job is renewed by the node that holds it, and a lock that was not renewed for the
/// lease is taken for the lock of a node that is gone. Internal, like <see cref="IJobsOfOtherNodes"/>: a custom persistence
/// provider does not have to implement it, and then has no lease.
/// </summary>
internal interface IInProgressLease
{
    /// <summary>
    /// Sets <c>LockedAt</c> to now on the rows, time jobs and cron occurrences, that this node holds, that are InProgress and
    /// that were locked at or after <paramref name="runStartedAt"/>. <c>UpdatedAt</c>, which holds the run's claim marker, is
    /// not written. Rows an earlier run of this node left are not renewed, so the startup release still finds them, and a
    /// row another node released, cancelled or claimed no longer matches. Returns how many rows were renewed.
    /// </summary>
    Task<int> RenewInProgressLeaseAsync(DateTime runStartedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the rows, time jobs and cron occurrences, that are InProgress under another node identifier and whose
    /// <c>LockedAt</c> is older than <paramref name="lease"/> as Cancelled, as a graceful stop records a run that did not
    /// finish (#211): Cancelled, <c>ExecutedAt</c> now, a reason in <c>ExceptionMessage</c>, no lock holder, no
    /// <c>LockedAt</c>, <c>UpdatedAt</c> now. Such a row does not run again; the chain children waiting for it follow their
    /// run condition, and a cron plans its next occurrences as usual. Up to 1.6.1 the rows were released to Idle and ran
    /// again. The rows of this node are not touched. Returns how many rows were cancelled, and the node identifiers that
    /// held the rows found for it, each with how many.
    /// </summary>
    Task<(int Cancelled, IReadOnlyList<(string NodeIdentifier, int Jobs)> Nodes)> CancelExpiredInProgressOfOtherNodesAsync(TimeSpan lease, CancellationToken cancellationToken = default);
}
