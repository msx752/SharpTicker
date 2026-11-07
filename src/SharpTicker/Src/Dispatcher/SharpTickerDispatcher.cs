using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpTicker.BackgroundServices;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Dispatcher
{
    internal class SharpTickerDispatcher : ISharpTickerDispatcher
    {
        private readonly ISharpTickerTaskScheduler _taskScheduler;
        private readonly ITickerExecutionTaskHandler _taskHandler;
        private readonly ITickerFunctionConcurrencyGate _concurrencyGate;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly ILogger<SharpTickerDispatcher> _logger;
        private readonly ClaimedWorkQueue _work;

        public bool IsEnabled => true;

        public SharpTickerDispatcher(
            ISharpTickerTaskScheduler taskScheduler,
            ITickerExecutionTaskHandler taskHandler,
            ITickerFunctionConcurrencyGate concurrencyGate,
            IHostApplicationLifetime lifetime = null,
            ILogger<SharpTickerDispatcher> logger = null,
            ClaimedWorkQueue claimedWork = null)
        {
            _lifetime = lifetime;
            _logger = logger;
            _work = claimedWork;
            _taskScheduler = taskScheduler ?? throw new ArgumentNullException(nameof(taskScheduler));
            _taskHandler = taskHandler ?? throw new ArgumentNullException(nameof(taskHandler));
            _concurrencyGate = concurrencyGate ?? throw new ArgumentNullException(nameof(concurrencyGate));
        }

        /// <summary>
        /// Queues the jobs to run now. The jobs outlive the call: the caller is usually a request that ends long before the
        /// job does, and the tickers are already stored and marked InProgress, so a job that was dropped or cancelled
        /// because the caller's token fired would stay InProgress. The jobs therefore run until the application stops,
        /// like the jobs the scheduler loop starts, and <paramref name="cancellationToken"/> does not cancel them.
        /// </summary>
        public async Task DispatchAsync(InternalFunctionContext[] contexts, CancellationToken cancellationToken = default)
        {
            if (contexts == null || contexts.Length == 0)
                return;

            var jobToken = _lifetime?.ApplicationStopping ?? CancellationToken.None;

            // A job whose claim marker is known goes through the claimed-work queue: it starts once it holds its slot, and a
            // job that cannot start (the application is stopping, the scheduler takes no work or refuses it, the stop ends its
            // wait for the slot) has its row put back, so the fallback runs it once. The call itself succeeds: the ticker is
            // stored and will run.
            var unclaimed = _work == null ? contexts : contexts.Where(c => c.ClaimMarker == null).ToArray();
            if (_work != null)
            {
                foreach (var context in contexts.Where(c => c.ClaimMarker != null))
                {
                    var entry = _work.Record(context, context.ClaimMarker!.Value);
                    // The scheduler would accept work with a cancelled token and never run it; a frozen or disposed
                    // scheduler refuses it, and the queue puts a refused claim back.
                    if (jobToken.IsCancellationRequested)
                    {
                        await _work.PutBackAsync(entry, "the application is stopping").ConfigureAwait(false);
                        continue;
                    }

                    // Returns once the scheduler accepted the job, like the unclaimed path below.
                    await _work.QueueWork(entry, isDue: false, jobToken).ConfigureAwait(false);
                }
            }

            if (unclaimed.Length == 0)
                return;

            // Without a claim marker the row cannot be put back. The scheduler skips work whose token is cancelled, so these
            // jobs will not start; their tickers were already marked InProgress and stay so. That is not silent.
            if (jobToken.IsCancellationRequested)
            {
                _logger?.LogWarning(
                    "The application is stopping, so {JobCount} dispatched job(s) will not start: {Jobs}. Their tickers were already marked InProgress " +
                    "and stay InProgress with no worker running them.",
                    unclaimed.Length,
                    string.Join(", ", unclaimed.Select(c => $"{c.FunctionName} ({c.TickerId})")));
            }

            foreach (var context in unclaimed)
            {
                var semaphore = _concurrencyGate.GetSemaphoreOrNull(context.FunctionName, context.CachedMaxConcurrency);

                await _taskScheduler.QueueAsync(
                    async ct =>
                    {
                        if (semaphore != null)
                            await semaphore.WaitAsync(ct).ConfigureAwait(false);

                        try
                        {
                            await _taskHandler.ExecuteTaskAsync(context, false, ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            semaphore?.Release();
                        }
                    },
                    context.CachedPriority,
                    jobToken).ConfigureAwait(false);
            }
        }
    }
}
