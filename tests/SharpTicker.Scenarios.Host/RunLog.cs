using System.Text.Json;
using SharpTicker.Utilities.Base;

namespace SharpTicker.Scenarios.Host;

/// <summary>
/// Writes one <c>SCENARIO_RUN {json}</c> line to stdout for every job start and end. The scenario tests read these
/// lines from the container log to count runs per ticker id and attempt.
/// </summary>
public static class RunLog
{
    public const string Prefix = "SCENARIO_RUN ";

    private static readonly object Gate = new();
    private static long _seq;

    public static string Node { get; set; } = Environment.MachineName;

    public static void Start(TickerFunctionContext context)
        => Write(context, "start", null);

    public static void End(TickerFunctionContext context, string status)
        => Write(context, "end", status);

    private static void Write(TickerFunctionContext context, string evt, string? status)
    {
        var line = new Dictionary<string, object?>
        {
            ["seq"] = 0L,
            ["event"] = evt,
            ["tickerId"] = context.Id,
            ["function"] = context.FunctionName,
            ["node"] = Node,
            ["attempt"] = context.RetryCount,
            ["at"] = DateTime.UtcNow.ToString("O"),
            ["scheduledFor"] = DateTime.SpecifyKind(context.ScheduledFor, DateTimeKind.Utc).ToString("O"),
            // Where the line was written: for a start line, the thread the job's synchronous start runs on.
            ["thread"] = Thread.CurrentThread.Name,
            ["pool"] = Thread.CurrentThread.IsThreadPoolThread,
        };
        if (status != null)
            line["status"] = status;

        // The sequence number is taken under the lock, so the lines appear in the log in seq order.
        lock (Gate)
        {
            line["seq"] = ++_seq;
            Console.Out.WriteLine(Prefix + JsonSerializer.Serialize(line));
            Console.Out.Flush();
        }
    }
}
