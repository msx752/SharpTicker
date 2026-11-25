using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Utilities
{
    /// <summary>
    /// Calls the application's <see cref="ITickerLifecycleHandler"/> (#1). Registered only when one is set, so a run without
    /// a handler does nothing here.
    /// <list type="bullet">
    /// <item>A start is awaited by the run, up to the timeout.</item>
    /// <item>An end is started on its own and tracked; the run does not wait. The scheduler's stop waits for the tracked
    /// calls (<see cref="WaitForCompletedCallsAsync"/>).</item>
    /// <item>Each call has its own token, cancelled at its timeout and at the end of the stop's wait. What a call throws, and
    /// a call that runs past its timeout, is logged at Warning and goes no further.</item>
    /// </list>
    /// </summary>
    internal sealed class TickerLifecycleCallbacks
    {
        // A warning, once, when this many end calls run at the same time: a handler that is slower than the jobs.
        internal const int ManyRunningCallsThreshold = 1000;

        private readonly IServiceProvider _serviceProvider;
        private readonly Type _handlerType;
        private readonly TimeSpan _timeout;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<long, Task> _running = new();
        // Cancelled at the end of a stop's wait, and replaced: a scheduler that starts again gives its calls a new one.
        private CancellationTokenSource _stopping = new();
        private long _nextCall;
        private int _warnedOfManyCalls;

        public TickerLifecycleCallbacks(IServiceProvider serviceProvider, Type handlerType, TimeSpan timeout, ILogger<TickerLifecycleCallbacks> logger)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _handlerType = handlerType ?? throw new ArgumentNullException(nameof(handlerType));
            _timeout = timeout;
            _logger = logger;
        }

        internal int RunningCompletedCalls => _running.Count;

        /// <summary>The start of a run, before its first attempt. Returns when the handler returned, threw or timed out.</summary>
        public Task StartingAsync(InternalFunctionContext context)
            => CallAsync(Snapshot(context, TickerStatus.InProgress), "start", static (handler, c, ct) => handler.OnStartingAsync(c, ct));

        /// <summary>
        /// A final status of the job was stored by this process. A status that is not final is ignored. Does not wait for the
        /// handler.
        /// </summary>
        public void Completed(InternalFunctionContext context)
        {
            if (!IsFinal(context.Status))
                return;

            // The run's context changes after this returns (its fields are reset for the next write); the call gets a copy.
            var snapshot = Snapshot(context, context.Status);
            var id = Interlocked.Increment(ref _nextCall);
            // Listed before it starts, so a call that ends at once is not removed before it is listed.
            var start = new Task<Task>(() => CallAsync(snapshot, "end", static (handler, c, ct) => handler.OnCompletedAsync(c, ct)));
            var call = start.Unwrap();
            _running[id] = call;
            start.Start(TaskScheduler.Default);
            _ = call.ContinueWith(static (_, state) =>
            {
                var (running, key) = ((ConcurrentDictionary<long, Task>, long))state;
                running.TryRemove(key, out Task _);
            }, (_running, id), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            if (_running.Count >= ManyRunningCallsThreshold && Interlocked.Exchange(ref _warnedOfManyCalls, 1) == 0)
                _logger?.LogWarning(
                    "{Count} calls of the lifecycle handler {Handler} for ended jobs are running at the same time: the handler is slower than the jobs end.",
                    _running.Count, _handlerType.Name);
        }

        /// <summary>
        /// Waits for the end calls that are running, until <paramref name="cancellationToken"/> is cancelled. The calls still
        /// running then are cancelled, and their number is logged at Warning.
        /// </summary>
        public async Task WaitForCompletedCallsAsync(CancellationToken cancellationToken)
        {
            // Calls that start while the stop waits (a job that ends meanwhile) are waited for too.
            try
            {
                while (!_running.IsEmpty)
                    await Task.WhenAll(_running.Values).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }

            var left = _running.Count;
            Interlocked.Exchange(ref _stopping, new CancellationTokenSource()).Cancel();
            if (left > 0)
                _logger?.LogWarning(
                    "The application stopped while {Count} call(s) of the lifecycle handler {Handler} for ended jobs were still running. They were cancelled.",
                    left, _handlerType.Name);
        }

        private async Task CallAsync(TickerLifecycleContext context, string what,
            Func<ITickerLifecycleHandler, TickerLifecycleContext, CancellationToken, Task> call)
        {
            CancellationTokenSource timeout = null;
            Task task;
            try
            {
                timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                timeout.CancelAfter(_timeout);
                var handler = (ITickerLifecycleHandler)_serviceProvider.GetService(typeof(ITickerLifecycleHandler));
                if (handler == null)
                {
                    timeout.Dispose();
                    return;
                }

                // Off the caller's thread: a handler that blocks before its first await holds neither the job's worker nor
                // the timeout.
                var token = timeout.Token;
                task = Task.Run(() => call(handler, context, token) ?? Task.CompletedTask);
            }
            catch (Exception ex)
            {
                timeout?.Dispose();
                LogThrew(ex, context, what);
                return;
            }

            using var ownedTimeout = timeout;
            try
            {
                // A handler that does not watch its token is not waited for past the timeout.
                await task.WaitAsync(_timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The handler is left behind; its token is cancelled before the source is disposed.
                timeout.Cancel();
                ObserveLater(task);
                _logger?.LogWarning(
                    "The lifecycle handler {Handler} did not end within {Timeout} for the {What} of {FunctionName} ({TickerId}). The job goes on without it.",
                    _handlerType.Name, _timeout, what, context.FunctionName, context.Id);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                _logger?.LogWarning(
                    "The lifecycle handler {Handler} did not end within {Timeout} for the {What} of {FunctionName} ({TickerId}), or the application stopped. The job goes on without it.",
                    _handlerType.Name, _timeout, what, context.FunctionName, context.Id);
            }
            catch (Exception ex)
            {
                LogThrew(ex, context, what);
            }
        }

        private void LogThrew(Exception ex, TickerLifecycleContext context, string what)
            => _logger?.LogWarning(ex,
                "The lifecycle handler {Handler} threw for the {What} of {FunctionName} ({TickerId}). The job goes on as if it had returned.",
                _handlerType.Name, what, context.FunctionName, context.Id);

        // The handler's task is left behind at its timeout; what it throws later is still observed, not raised as unobserved.
        private static void ObserveLater(Task task)
            => _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        private static bool IsFinal(TickerStatus status)
            => status is TickerStatus.Done or TickerStatus.DueDone or TickerStatus.Failed or TickerStatus.Cancelled or TickerStatus.Skipped;

        private static TickerLifecycleContext Snapshot(InternalFunctionContext context, TickerStatus status)
            => new(context.TickerId, context.ParentId, context.Type, context.FunctionName, status, context.RetryCount,
                TimeSpan.FromMilliseconds(context.ElapsedTime),
                status is TickerStatus.Failed or TickerStatus.Cancelled ? context.ExceptionDetails : null,
                status == TickerStatus.Skipped ? context.ExceptionDetails : null);
    }
}
