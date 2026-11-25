using System.Threading;
using System.Threading.Tasks;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Utilities.Interfaces
{
    /// <summary>
    /// Code of the application that runs when a job starts and when it ends. Registered once with
    /// <c>SetLifecycleHandler&lt;THandler&gt;()</c>, as a singleton; resolve scoped services through an
    /// <c>IServiceScopeFactory</c>. Implement only the methods you need: both return a completed task by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each call has its own timeout (<c>SetLifecycleHandlerTimeout</c>, 30 seconds by default) and its own token, which is
    /// cancelled when the timeout ends or the application stops. An exception or a timeout is logged at <c>Warning</c> and
    /// does not change the job.
    /// </para>
    /// <para>
    /// The callbacks run in the process that runs or records the job, from memory. When the process stops after a status is
    /// stored and before its callback ran, that callback is not made. A callback can run more than once for the same job,
    /// for example when a run is put back and runs again: use <see cref="TickerLifecycleContext.Id"/> to recognise a job.
    /// </para>
    /// </remarks>
    public interface ITickerLifecycleHandler
    {
        /// <summary>
        /// Called once per run, before the job's first attempt. The job waits for it, up to the timeout. A run that is put
        /// back before it ends, and runs again later, gets this call again.
        /// </summary>
        Task OnStartingAsync(TickerLifecycleContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>
        /// Called after a final status (<c>Done</c>, <c>DueDone</c>, <c>Failed</c>, <c>Cancelled</c> or <c>Skipped</c>)
        /// was stored for the job by this process; not between retries. The worker does not wait for it. Not reported: statuses
        /// that a store records in bulk for jobs of other instances (see the failure modes guide), and chain children that are
        /// skipped because their parent's outcome does not match their run condition.
        /// </summary>
        Task OnCompletedAsync(TickerLifecycleContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
