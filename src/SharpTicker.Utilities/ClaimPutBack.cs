using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SharpTicker.Utilities;

/// <summary>
/// Puts back the rows a claim call had already won when the call fails before it can hand them to its caller.
/// </summary>
internal static class ClaimPutBack
{
    /// <summary>
    /// Releases every won row through the store's fenced put-back, so a row this node took again in between (another
    /// marker) is left alone. Returns when every release ran, so the caller rethrows <paramref name="failure"/>. When a
    /// release throws too, the row stays InProgress until this node's dead-node release at its next start, and an
    /// <see cref="AggregateException"/> with <paramref name="failure"/> first and the release errors after it is thrown,
    /// so that loss is not hidden.
    /// </summary>
    internal static async Task PutBackOrThrowAsync(
        IReadOnlyDictionary<Guid, DateTime> won,
        Exception failure,
        Func<Guid, DateTime, CancellationToken, Task<bool>> release)
    {
        List<Exception> releaseFailures = null;

        foreach (var claim in won)
        {
            try
            {
                await release(claim.Key, claim.Value, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception releaseFailure)
            {
                (releaseFailures ??= new List<Exception> { failure }).Add(releaseFailure);
            }
        }

        if (releaseFailures != null)
            throw new AggregateException(
                $"The claim call failed after it had won {won.Count} row(s), and {releaseFailures.Count - 1} of them could not be put back; they stay InProgress until this node starts again.",
                releaseFailures);
    }
}
