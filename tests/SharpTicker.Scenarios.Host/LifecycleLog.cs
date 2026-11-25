using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Scenarios.Host;

/// <summary>
/// The lifecycle handler of the scenario node (#1), registered with <c>SCENARIO_LIFECYCLE=1</c>. Each call writes one
/// <c>SCENARIO_RUN</c> line with the event <c>lifecycle-start</c> or <c>lifecycle-end</c>, the job's id and, for an end,
/// the status it was reported with. The run lines of the jobs themselves (<c>start</c>, <c>end</c>) are not changed.
/// </summary>
public sealed class LifecycleLog : ITickerLifecycleHandler
{
    public Task OnStartingAsync(TickerLifecycleContext context, CancellationToken cancellationToken)
    {
        Write(context, "lifecycle-start");
        return Task.CompletedTask;
    }

    public Task OnCompletedAsync(TickerLifecycleContext context, CancellationToken cancellationToken)
    {
        Write(context, "lifecycle-end");
        return Task.CompletedTask;
    }

    private static void Write(TickerLifecycleContext context, string evt)
    {
        var line = new Dictionary<string, object?>
        {
            ["seq"] = 0L,
            ["event"] = evt,
            ["tickerId"] = context.Id,
            ["function"] = context.FunctionName,
            ["node"] = RunLog.Node,
            ["attempt"] = context.RetryCount,
            ["at"] = DateTime.UtcNow.ToString("O"),
            ["status"] = context.Status.ToString(),
        };

        RunLog.WriteLine(line);
    }
}
