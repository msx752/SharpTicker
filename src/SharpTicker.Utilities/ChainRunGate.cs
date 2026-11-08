using System;
using SharpTicker.Utilities.Enums;

namespace SharpTicker.Utilities
{
    /// <summary>
    /// When a chain child may run, from its <see cref="RunCondition"/> and its parent's status. The execution handler uses it
    /// for the children it runs after their parent, and every store uses it to decide whether a delayed child (a child with an
    /// execution time of its own, which is a row of its own) can be claimed.
    /// </summary>
    /// <remarks>
    /// A finished status (Done, DueDone, Failed, Cancelled, Skipped) is never changed back, so once a finished parent lets a
    /// child run it always will: a store can check the parent when it reads the row, without fencing the claim on it. The
    /// exception is an <see cref="RunCondition.InProgress"/> child, which may run once its parent has started; a parent put
    /// back after a claim goes from InProgress to Idle again, which only delays such a child.
    /// </remarks>
    internal static class ChainRunGate
    {
        /// <summary>The reason written to a child skipped because its parent's outcome did not match its condition.</summary>
        internal const string NotMatchedReason = "Rule RunCondition did not match!";

        private static readonly TickerStatus[] Succeeded = [TickerStatus.Done, TickerStatus.DueDone];
        private static readonly TickerStatus[] FailedOnly = [TickerStatus.Failed];
        private static readonly TickerStatus[] CancelledOnly = [TickerStatus.Cancelled];
        private static readonly TickerStatus[] FailedOrCancelled = [TickerStatus.Failed, TickerStatus.Cancelled];
        private static readonly TickerStatus[] AnyCompleted = [TickerStatus.Done, TickerStatus.DueDone, TickerStatus.Failed, TickerStatus.Cancelled];
        private static readonly TickerStatus[] Started = [TickerStatus.InProgress, TickerStatus.Done, TickerStatus.DueDone, TickerStatus.Failed, TickerStatus.Cancelled];

        /// <summary>
        /// The parent statuses that let a child with <paramref name="condition"/> run. A child stored without a condition
        /// runs on any completed status, as everywhere else. An unknown condition never runs.
        /// </summary>
        internal static TickerStatus[] ParentStatusesFor(RunCondition? condition) => (condition ?? RunCondition.OnAnyCompletedStatus) switch
        {
            RunCondition.OnSuccess => Succeeded,
            RunCondition.OnFailure => FailedOnly,
            RunCondition.OnCancelled => CancelledOnly,
            RunCondition.OnFailureOrCancelled => FailedOrCancelled,
            RunCondition.OnAnyCompletedStatus => AnyCompleted,
            RunCondition.InProgress => Started,
            _ => Array.Empty<TickerStatus>()
        };

        /// <summary>Whether a child with <paramref name="condition"/> may run while its parent has <paramref name="parentStatus"/>.</summary>
        internal static bool Allows(RunCondition? condition, TickerStatus parentStatus)
            => Array.IndexOf(ParentStatusesFor(condition), parentStatus) >= 0;

        /// <summary>Whether <paramref name="status"/> is final: a row with it never runs again.</summary>
        internal static bool IsFinal(TickerStatus status)
            => status is TickerStatus.Done or TickerStatus.DueDone or TickerStatus.Failed or TickerStatus.Cancelled or TickerStatus.Skipped;
    }
}
