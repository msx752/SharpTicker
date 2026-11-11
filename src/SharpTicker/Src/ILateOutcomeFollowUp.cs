using System.Threading;
using System.Threading.Tasks;
using SharpTicker.Utilities.Models;

namespace SharpTicker;

/// <summary>
/// What follows an outcome that was stored late (<see cref="Utilities.LostOutcomeRetryList"/>): what the run would have done
/// right after a stored outcome for the chain children of the row. Implemented by the execution handler, which owns the
/// chain rules.
/// </summary>
internal interface ILateOutcomeFollowUp
{
    /// <summary>
    /// Acts on the chain children of <paramref name="outcome"/> (its <see cref="InternalFunctionContext.TimeTickerChildren"/>)
    /// that the run left for after its outcome: the delayed ones, and the inline ones that run after their parent. Those the
    /// outcome does not let run are skipped with their descendants. With <paramref name="claimChildren"/>, those it lets run
    /// are handed off through the store: a delayed child as after a stored outcome (claimed when due, the scheduler woken
    /// for a later one), an inline child claimed at once, to run as a claimed job. None is run by this call. Without
    /// <paramref name="claimChildren"/> nothing is claimed: the scheduler is stopping. The fallback runs the delayed ones at
    /// the next start; an inline one stays Idle and is named in an error. Throws when the store fails.
    /// </summary>
    Task AfterLateStoreAsync(InternalFunctionContext outcome, bool claimChildren, CancellationToken cancellationToken);
}
