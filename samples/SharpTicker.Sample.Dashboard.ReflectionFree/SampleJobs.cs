using System.Text.Json.Serialization;
using SharpTicker.Utilities.Base;
using SharpTicker.Utilities.Interfaces;

namespace SharpTicker.Sample.Dashboard.ReflectionFree;

// Approach 1: Attribute-based (existing)
public class SampleJobs
{
    [TickerFunction("ReflectionFree_CronJob", "0 */5 * * * *")]
    public Task CronJobAsync(TickerFunctionContext context)
    {
        Console.WriteLine($"[{DateTime.UtcNow}] Cron job executed! Id={context.Id}");
        return Task.CompletedTask;
    }

    [TickerFunction("ReflectionFree_TimeJob")]
    public Task TimeJobAsync(TickerFunctionContext<OrderRequest> context)
    {
        Console.WriteLine($"[{DateTime.UtcNow}] Time job executed! Id={context.Id}");
        return Task.CompletedTask;
    }
}

// Approach 2: Interface-based (new) — registered via app.MapTicker<T>()
public class CleanupJob : ITickerFunction
{
    public Task ExecuteAsync(TickerFunctionContext context, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[{DateTime.UtcNow}] Cleanup executed! Id={context.Id}");
        return Task.CompletedTask;
    }
}

public record OrderRequest(string OrderId, decimal Amount);

// Reflection-free serialization: every request type must be listed in a source-generated context,
// and the context is passed to SharpTicker with WithJsonContext (see Program.cs). Without it,
// requests cannot be serialized and the dashboard has no example JSON to offer.
[JsonSerializable(typeof(OrderRequest))]
internal partial class SampleJsonContext : JsonSerializerContext;

public class ProcessOrderJob : ITickerFunction<OrderRequest>
{
    public Task ExecuteAsync(TickerFunctionContext<OrderRequest> context, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[{DateTime.UtcNow}] Processing order {context.Request?.OrderId} for ${context.Request?.Amount}");
        return Task.CompletedTask;
    }
}
