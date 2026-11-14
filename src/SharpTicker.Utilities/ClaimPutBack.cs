using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SharpTicker.Utilities.Enums;

namespace SharpTicker.Utilities;

/// <summary>
/// Puts back the rows a claim call had already won when the call fails before it can hand them to its caller.
/// </summary>
internal static class ClaimPutBack
{
    /// <summary>
    /// For one claim whose own write failed: the write may have landed before the call failed (its reply was lost, or a stop
    /// cancelled it), so it is put back like a won row. When it did not land, the fenced release finds nothing to change.
    /// </summary>
    internal static Task PutBackOrThrowAsync(Guid id, DateTime claimMarker, TickerType type, Exception failure,
        Func<Guid, DateTime, CancellationToken, Task<bool>> release)
        => PutBackOrThrowAsync(new Dictionary<Guid, DateTime> { [id] = claimMarker }, type, failure, release);

    /// <summary>
    /// Releases every won row through the store's fenced put-back, so a row this node took again in between (another
    /// marker) is left alone. Returns when every release ran, so the caller rethrows <paramref name="failure"/>. When a
    /// release throws too, a <see cref="ClaimPutBackFailedException"/> with <paramref name="failure"/> first and the release
    /// errors after it is thrown, so that loss is not hidden. It lists the rows that could not be put back, so the caller
    /// hands them to <see cref="ClaimPutBackRetryList"/> and they are put back once the store answers.
    /// </summary>
    internal static async Task PutBackOrThrowAsync(
        IReadOnlyDictionary<Guid, DateTime> won,
        TickerType type,
        Exception failure,
        Func<Guid, DateTime, CancellationToken, Task<bool>> release)
    {
        List<Exception> releaseFailures = null;
        List<NotPutBackClaim> notPutBack = null;

        foreach (var claim in won)
        {
            try
            {
                await release(claim.Key, claim.Value, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception releaseFailure)
            {
                (releaseFailures ??= new List<Exception> { failure }).Add(releaseFailure);
                (notPutBack ??= new List<NotPutBackClaim>()).Add(new NotPutBackClaim(claim.Key, claim.Value, type));
            }
        }

        if (releaseFailures != null)
            throw new ClaimPutBackFailedException(won.Count, releaseFailures, notPutBack);
    }
}

/// <summary>
/// A row that is still InProgress for this node under <see cref="ClaimMarker"/> and has to be put back: a claimed row
/// whose put-back failed, or, with <see cref="StoppedFunction"/>, the row of a run of that function this node stopped
/// without an outcome (see <see cref="NodeFence"/>).
/// </summary>
internal readonly record struct NotPutBackClaim(Guid Id, DateTime ClaimMarker, TickerType Type, string StoppedFunction = null);

/// <summary>
/// A claim call failed after it had claimed rows, and some of them could not be put back. The first inner exception is the
/// failure of the claim call, the others are the failed put-backs. <see cref="NotPutBack"/> lists those rows, which stay
/// InProgress for this node until a later put-back reaches the store.
/// </summary>
internal sealed class ClaimPutBackFailedException(int claimed, IEnumerable<Exception> failures, IReadOnlyList<NotPutBackClaim> notPutBack = null)
    : AggregateException(
        $"The claim call failed after it had claimed {claimed} row(s), and some of them could not be put back; they stay InProgress until a put-back reaches the store.",
        failures)
{
    internal IReadOnlyList<NotPutBackClaim> NotPutBack { get; } = notPutBack ?? Array.Empty<NotPutBackClaim>();

    /// <summary>
    /// Every <see cref="ClaimPutBackFailedException"/> in <paramref name="exception"/>: itself, or wrapped as the inner exception
    /// of another (a <c>ClaimsInterruptedException</c>, an execution strategy) or of an <see cref="AggregateException"/>.
    /// </summary>
    internal static IEnumerable<ClaimPutBackFailedException> FindIn(Exception exception)
    {
        if (exception == null)
            yield break;

        if (exception is ClaimPutBackFailedException found)
        {
            yield return found;
            yield break;
        }

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            foreach (var nested in FindIn(inner))
                yield return nested;
            yield break;
        }

        foreach (var nested in FindIn(exception.InnerException))
            yield return nested;
    }
}
