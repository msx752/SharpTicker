using System.Threading;
using System.Threading.Tasks;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Utilities.Temps;

/// <summary>
/// No-operation implementation of ISharpTickerDispatcher.
/// Used when background services are disabled (queue-only mode).
/// </summary>
internal class NoOpSharpTickerDispatcher : ISharpTickerDispatcher
{
    public bool IsEnabled => false;
    
    public Task DispatchAsync(InternalFunctionContext[] contexts, CancellationToken cancellationToken = default)
    {
        // No-op: dispatcher not available in queue-only mode
        return Task.CompletedTask;
    }

    public Task DispatchAsync(InternalFunctionContext context, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
