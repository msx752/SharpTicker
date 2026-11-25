using System;
using SharpTicker.Utilities.Enums;

namespace SharpTicker.Utilities.Models
{
    /// <summary>
    /// The job that an <see cref="Interfaces.ITickerLifecycleHandler"/> call is about, as it was when the call was made.
    /// </summary>
    public sealed class TickerLifecycleContext
    {
        internal TickerLifecycleContext(Guid id, Guid? parentId, TickerType type, string functionName, TickerStatus status,
            int retryCount, TimeSpan elapsed, string exceptionMessage, string skippedReason)
        {
            Id = id;
            ParentId = parentId;
            Type = type;
            FunctionName = functionName;
            Status = status;
            RetryCount = retryCount;
            Elapsed = elapsed;
            ExceptionMessage = exceptionMessage;
            SkippedReason = skippedReason;
        }

        /// <summary>The id of the time ticker or of the cron occurrence.</summary>
        public Guid Id { get; }

        /// <summary>
        /// As <see cref="Base.TickerFunctionContext.ParentId"/>: for a cron occurrence, the id of its cron ticker; for a chain child,
        /// the id of its parent; <c>null</c> otherwise.
        /// </summary>
        public Guid? ParentId { get; }

        /// <summary>A time ticker or a cron occurrence.</summary>
        public TickerType Type { get; }

        /// <summary>The name the job's function is mapped under.</summary>
        public string FunctionName { get; }

        /// <summary><c>InProgress</c> in <c>OnStartingAsync</c>; the stored final status in <c>OnCompletedAsync</c>.</summary>
        public TickerStatus Status { get; }

        /// <summary>
        /// The retries the job has used, as the run recorded them; 0 for a job that the stop recorded as <c>Cancelled</c>
        /// while it was still running, and for a chain child that its parent's outcome skipped.
        /// </summary>
        public int RetryCount { get; }

        /// <summary>
        /// In <c>OnCompletedAsync</c>, the duration the run recorded with its outcome: from the first attempt to the end, retry
        /// waits included, for <c>Done</c>, <c>DueDone</c> and <c>Failed</c>; that of the last attempt for <c>Cancelled</c>
        /// and <c>Skipped</c>; zero for a job that the stop recorded as <c>Cancelled</c> while it was still running, and for a
        /// chain child that its parent's outcome skipped.
        /// </summary>
        public TimeSpan Elapsed { get; }

        /// <summary>
        /// The exception text stored in the job's <c>ExceptionMessage</c> for a <c>Failed</c> or <c>Cancelled</c> job;
        /// <c>null</c> otherwise.
        /// </summary>
        public string ExceptionMessage { get; }

        /// <summary>Why a <c>Skipped</c> job was skipped; <c>null</c> otherwise.</summary>
        public string SkippedReason { get; }
    }
}
