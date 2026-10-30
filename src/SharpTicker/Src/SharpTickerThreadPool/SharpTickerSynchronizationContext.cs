using System.Threading;

namespace SharpTicker.SharpTickerThreadPool;

/// <summary>
/// Custom SynchronizationContext that keeps async continuations on SharpTicker worker threads.
/// Ensures true concurrency control by preventing execution from switching to ThreadPool.
/// Uses flexible thread assignment for optimal performance and load balancing.
/// </summary>
internal sealed class SharpTickerSynchronizationContext : SynchronizationContext
{
    private readonly SharpTickerTaskScheduler _scheduler;

    public SharpTickerSynchronizationContext(SharpTickerTaskScheduler scheduler)
    {
        _scheduler = scheduler;
    }

    /// <summary>
    /// Posts async continuations to SharpTicker workers with circular dependency protection.
    /// Uses direct execution if called from within a SharpTicker worker to prevent deadlocks.
    /// </summary>
    public override void Post(SendOrPostCallback d, object state)
    {
        // Check if we're already on a SharpTicker worker thread using ThreadStatic flag
        if (SharpTickerTaskScheduler.IsSharpTickerWorkerThread)
        {
            // We're on a SharpTicker worker - execute directly to avoid circular queueing
            try 
            { 
                d(state); 
            } 
            catch 
            { 
                /* swallow continuation exceptions */ 
            }
        }
        else
        {
            // We're not on a SharpTicker worker - safe to queue the continuation
            _scheduler.PostContinuation(d, state);
        }
    }

    /// <summary>
    /// Sends synchronous operations (not typically used in async scenarios).
    /// </summary>
    public override void Send(SendOrPostCallback d, object state)
    {
        // For synchronous operations, execute directly
        d(state);
    }
}