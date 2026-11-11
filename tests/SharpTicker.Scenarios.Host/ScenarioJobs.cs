using SharpTicker.DependencyInjection;
using SharpTicker.Utilities.Base;
using SharpTicker.Utilities.Enums;

namespace SharpTicker.Scenarios.Host;

/// <summary>The request of the jobs that wait: how long the job runs.</summary>
public sealed class WaitRequest
{
    public int Seconds { get; set; }
}

/// <summary>
/// The job functions of the scenario node. Every job writes a start line before its first await and an end line
/// with its outcome (Done, Failed or Cancelled), then rethrows any exception so the scheduler sees the real result.
/// </summary>
public static class ScenarioJobs
{
    public const string Record = "Scenario.Record";
    public const string Slow = "Scenario.Slow";
    public const string Stubborn = "Scenario.Stubborn";
    public const string OneAtATime = "Scenario.OneAtATime";
    public const string HttpTimeout = "Scenario.HttpTimeout";
    public const string Fail = "Scenario.Fail";
    public const string ChainStep = "Scenario.ChainStep";
    public const string High = "Scenario.High";
    public const string Normal = "Scenario.Normal";
    public const string Low = "Scenario.Low";
    public const string Blocker = "Scenario.Blocker";
    public const string Wave = "Scenario.Wave";
    public const string Seeded = "Scenario.Seeded";

    private static readonly HttpClient TimingOutClient = new() { Timeout = TimeSpan.FromSeconds(1) };

    /// <summary>A loopback port that accepts connections and never sends a byte; the connections stay open.</summary>
    private static readonly Lazy<int> SilentPort = new(() =>
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var held = new List<System.Net.Sockets.Socket>();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var socket = await listener.AcceptSocketAsync();
                lock (held)
                    held.Add(socket);
            }
        });
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    });

    public static void Register(IServiceCollection services)
    {
        services.MapTicker(Record, (context, ct) => Logged(context, ct, _ => Task.CompletedTask));

        services.MapTicker<WaitRequest>(Slow, (context, ct) =>
            Logged(context, ct, token => Task.Delay(TimeSpan.FromSeconds(context.Request?.Seconds ?? 0), token)));

        // Ignores its cancellation token: a stop cannot end it early, only wait for it.
        services.MapTicker<WaitRequest>(Stubborn, (context, ct) =>
            Logged(context, ct, _ => Task.Delay(TimeSpan.FromSeconds(context.Request?.Seconds ?? 0))));

        services.MapTicker<WaitRequest>(OneAtATime, (context, ct) =>
                Logged(context, ct, token => Task.Delay(TimeSpan.FromSeconds(context.Request?.Seconds ?? 0), token)))
            .WithMaxConcurrency(1);

        // A local port that accepts the connection and never answers: the 1 s HttpClient timeout fires inside the job, not the
        // job's own cancellation. Each timeout writes a SCENARIO_HTTP_TIMEOUT line, so the scenario can see the path was taken.
        services.MapTicker(HttpTimeout, (context, ct) => Logged(context, ct, async token =>
        {
            try
            {
                await TimingOutClient.GetAsync($"http://127.0.0.1:{SilentPort.Value}/", token);
            }
            catch (TaskCanceledException e) when (e.InnerException is TimeoutException)
            {
                Console.WriteLine($"SCENARIO_HTTP_TIMEOUT {context.Id}");
                throw;
            }
        }));

        services.MapTicker(Fail, (context, ct) =>
            Logged(context, ct, _ => throw new InvalidOperationException("Scenario.Fail always fails.")));

        services.MapTicker<WaitRequest>(ChainStep, (context, ct) =>
            Logged(context, ct, token => Task.Delay(TimeSpan.FromSeconds(context.Request?.Seconds ?? 0), token)));

        // Priority is per function. No request: nothing is awaited before the start line, so it shows the start thread.
        services.MapTicker(High, (context, ct) => Logged(context, ct, _ => Task.CompletedTask)).WithPriority(TickerTaskPriority.High);
        services.MapTicker(Normal, (context, ct) => Logged(context, ct, _ => Task.CompletedTask)).WithPriority(TickerTaskPriority.Normal);
        services.MapTicker(Low, (context, ct) => Logged(context, ct, _ => Task.CompletedTask)).WithPriority(TickerTaskPriority.Low);

        // Holds its worker for 8 s: Thread.Sleep, not an await, which would free the worker. No request: reading a request
        // is an await before the job's code, after which the code runs on the .NET pool.
        services.MapTicker(Blocker, (context, ct) => Logged(context, ct, _ => Sleep(TimeSpan.FromSeconds(8))));

        services.MapTicker(Wave, (context, ct) => Logged(context, ct, _ => Sleep(TimeSpan.FromMilliseconds(200))));

        // A cron defined in code, seeded into the store at every start: only when SCENARIO_SEEDED_CRON gives its expression,
        // so the other scenarios see no cron of their own.
        if (Environment.GetEnvironmentVariable("SCENARIO_SEEDED_CRON") is { Length: > 0 } seeded)
            services.MapTicker(Seeded, (context, ct) => Logged(context, ct, _ => Task.CompletedTask))
                .WithCron(SharpTicker.Utilities.CronExpression.Parse(seeded));
    }

    private static Task Sleep(TimeSpan duration)
    {
        Thread.Sleep(duration);
        return Task.CompletedTask;
    }

    private static async Task Logged(TickerFunctionContext context, CancellationToken ct, Func<CancellationToken, Task> body)
    {
        RunLog.Start(context);
        try
        {
            await body(ct);
            RunLog.End(context, "Done");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            RunLog.End(context, "Cancelled");
            throw;
        }
        catch
        {
            RunLog.End(context, "Failed");
            throw;
        }
    }
}
