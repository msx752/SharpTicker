using System;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;

namespace SharpTicker.Utilities;

/// <summary>
/// What a full-entity update of a time ticker (an edit through <c>UpdateAsync</c> or the dashboard) writes over the state
/// of a run. A running row keeps its run: its status, lock, run marker (UpdatedAt) and the fields the run writes, or the
/// run's fenced writes would no longer match and its outcome would be lost, leaving the row InProgress or running the job
/// again. A body that still shows a run the store no longer has (read while the job ran, sent after it ended) does not bring
/// that run back. The stores apply this to every row an update writes, the root and its chain rows.
/// <para>
/// The link of a chain child is the store's too (<see cref="KeepChainLink{TTicker}"/>): an update does not take a stored
/// child out of its chain or move it to another parent. To do that, delete the child and add a new job.
/// </para>
/// </summary>
internal static class RunStateOnEdit
{
    /// <summary>Changes <paramref name="body"/>, the row to be written, against <paramref name="stored"/>, the row in the store.</summary>
    internal static void Apply<TTicker>(TTicker body, TTicker stored) where TTicker : TimeTickerEntity<TTicker>
    {
        KeepChainLink(body, stored.ParentId, stored.RunCondition);

        if (stored.Status == TickerStatus.InProgress)
        {
            KeepRunOf(body, stored);
            body.UpdatedAt = stored.UpdatedAt;
            return;
        }

        // A job that has ended keeps its outcome: a body read before it ran (a dialog opened before, saved after) would write
        // it back as not started, and it would run again. To run it again, add a new job.
        if (ChainRunGate.IsFinal(stored.Status))
        {
            KeepRunOf(body, stored);
            return;
        }

        if (body.Status == TickerStatus.InProgress)
        {
            body.Status = stored.Status;
            body.LockHolder = stored.LockHolder;
            body.LockedAt = stored.LockedAt;
        }
    }

    /// <summary>
    /// A stored chain child keeps its parent, and its run condition when <paramref name="body"/> has none: the dashboard's
    /// edit form sends neither, and a child written without them would leave its chain and run as a job of its own.
    /// </summary>
    internal static void KeepChainLink<TTicker>(TTicker body, Guid? storedParentId, RunCondition? storedRunCondition)
        where TTicker : TimeTickerEntity<TTicker>
    {
        if (storedParentId == null)
            return;

        body.ParentId = storedParentId;
        body.RunCondition ??= storedRunCondition;
    }

    /// <summary>The run fields of a row as the caller sent it, so an update that is tried again starts from them.</summary>
    internal readonly record struct RunFields(TickerStatus Status, string LockHolder, DateTime? LockedAt, DateTime UpdatedAt,
        int RetryCount, DateTime? ExecutedAt, long ElapsedTime, string ExceptionMessage, string SkippedReason);

    internal static RunFields Snapshot<TTicker>(TTicker body) where TTicker : TimeTickerEntity<TTicker>
        => new(body.Status, body.LockHolder, body.LockedAt, body.UpdatedAt, body.RetryCount, body.ExecutedAt, body.ElapsedTime,
            body.ExceptionMessage, body.SkippedReason);

    internal static void Restore<TTicker>(TTicker body, RunFields sent) where TTicker : TimeTickerEntity<TTicker>
    {
        body.Status = sent.Status;
        body.LockHolder = sent.LockHolder;
        body.LockedAt = sent.LockedAt;
        body.UpdatedAt = sent.UpdatedAt;
        body.RetryCount = sent.RetryCount;
        body.ExecutedAt = sent.ExecutedAt;
        body.ElapsedTime = sent.ElapsedTime;
        body.ExceptionMessage = sent.ExceptionMessage;
        body.SkippedReason = sent.SkippedReason;
    }

    /// <summary>Copies the run state of <paramref name="stored"/> onto <paramref name="body"/>, all but the run marker.</summary>
    private static void KeepRunOf<TTicker>(TTicker body, TTicker stored) where TTicker : TimeTickerEntity<TTicker>
    {
        body.Status = stored.Status;
        body.LockHolder = stored.LockHolder;
        body.LockedAt = stored.LockedAt;
        body.RetryCount = stored.RetryCount;
        body.ExecutedAt = stored.ExecutedAt;
        body.ElapsedTime = stored.ElapsedTime;
        body.ExceptionMessage = stored.ExceptionMessage;
        body.SkippedReason = stored.SkippedReason;
    }
}
