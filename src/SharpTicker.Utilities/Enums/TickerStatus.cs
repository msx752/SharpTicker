namespace SharpTicker.Utilities.Enums
{
    /// <summary>
    /// The status of a time ticker or of a cron occurrence. Stores keep it as its number, shown after each value.
    /// </summary>
    public enum TickerStatus
    {
        /// <summary>0: stored and waiting for its time; no instance holds it.</summary>
        Idle,
        /// <summary>1: claimed by an instance, which holds it, and not started yet.</summary>
        Queued,
        /// <summary>2: running on the instance that holds it.</summary>
        InProgress,
        /// <summary>3: the function completed without an exception.</summary>
        Done,
        /// <summary>
        /// 4: the function completed without an exception, and the run was started by the fallback that looks for
        /// missed jobs rather than at its time. A <c>RunCondition</c> treats it like <see cref="Done"/>.
        /// </summary>
        DueDone,
        /// <summary>5: the function threw an exception on its last attempt, after its retries if it has any.</summary>
        Failed,
        /// <summary>6: the run was cancelled or stopped before it completed; it is not run again by itself.</summary>
        Cancelled,
        /// <summary>
        /// 7: not run, for example a chain child whose run condition did not match its parent's outcome, or a cron
        /// occurrence that was missed. <c>SkippedReason</c> holds the reason where the store records one.
        /// </summary>
        Skipped
    }
}
