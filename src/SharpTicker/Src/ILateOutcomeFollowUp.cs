using System.Threading;
using System.Threading.Tasks;
using SharpTicker.Utilities.Models;

namespace SharpTicker;

/// <summary>
/// What follows an outcome that was stored late (<see cref="Utilities.LostOutcomeRetryList"/>): what the run would have done
/// right after a stored outcome for the delayed chain children of the row. Implemented by the execution handler, which owns
/// the chain rules.
/// </summary>
internal interface ILateOutcomeFollowUp
{
    /// <summary>
    /// Acts on the delayed chain children of <paramref name="outcome"/> (its <see cref="InternalFunctionContext.TimeTickerChildren"/>
    /// that have a time of their own): those the outcome does not let run are skipped with their descendants; with
    /// <paramref name="claimChildren"/>, those it lets run are handed off as after a stored outcome (claimed when due, the
    /// scheduler woken for later ones). Without it nothing is claimed: the scheduler is stopping, and the fallback runs them
    /// at the next start. Inline children are not run here. Throws when the store fails.
    /// </summary>
    Task AfterLateStoreAsync(InternalFunctionContext outcome, bool claimChildren, CancellationToken cancellationToken);
}
