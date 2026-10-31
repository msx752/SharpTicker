using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

        public bool IsEnabled => true;

        public SharpTickerDispatcher(
            ISharpTickerTaskScheduler taskScheduler,
            ITickerExecutionTaskHandler taskHandler,
            ITickerFunctionConcurrencyGate concurrencyGate,
            IHostApplicationLifetime lifetime = null,
            ILogger<SharpTickerDispatcher> logger = null)
        {
            _lifetime = lifetime;
            _logger = logger;
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

            // The scheduler skips work whose token is cancelled, so these jobs will not start. Their tickers are already marked
            // InProgress and stay so. That is left as it is, but it is not silent.
            if (jobToken.IsCancellationRequested)
            {
                _logger?.LogWarning(
                    "The application is stopping, so {JobCount} dispatched job(s) will not start: {Jobs}. Their tickers were already marked InProgress " +
                    "and stay InProgress until this node starts again or a dead node release frees them.",
                    contexts.Length,
                    string.Join(", ", contexts.Select(c => $"{c.FunctionName} ({c.TickerId})")));
            }

            foreach (var context in contexts)
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
