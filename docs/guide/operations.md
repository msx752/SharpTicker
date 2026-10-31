# Running in production

## Several instances

Instances that use the same shared store ([EF Core, MongoDB or Redis](storage.md)) work as one scheduler. When a job becomes due, an instance claims it with a conditional update on the stored record and marks itself as the lock holder, so each run happens once.

Give every instance a stable, unique name. The default is the machine name, which is not unique when several containers share a host name pattern or run on one machine:

```csharp
builder.Services.AddSharpTicker(options =>
{
    options.ConfigureScheduler(scheduler =>
    {
        scheduler.NodeIdentifier = Environment.GetEnvironmentVariable("POD_NAME") ?? Environment.MachineName;
    });
});
```

When an instance stops while holding jobs, they are released for the others: with EF Core when that node starts again, and with Redis when its heartbeat stops.

## Scheduler options

Set through `options.ConfigureScheduler(scheduler => ...)`:

| Option | Default | Meaning |
|---|---|---|
| `NodeIdentifier` | `Environment.MachineName` | Name of this instance in locks and in the dashboard. |
| `MaxConcurrency` | `Environment.ProcessorCount` | Number of worker threads that start jobs. |
| `IdleWorkerTimeOut` | 1 minute | How long an idle worker thread stays alive. |
| `MinPollingInterval` | 1 second | Shortest wait between two checks for due jobs. |
| `FallbackIntervalChecker` | 30 seconds | How often missed or timed-out jobs are looked for. |
| `SchedulerTimeZone` | `TimeZoneInfo.Local` | Time zone for cron expressions; see [Scheduling](scheduling.md#time-zone). |

To limit how many runs of one function execute at the same time on an instance, use `maxConcurrency` on `[TickerFunction]` or `WithMaxConcurrency` on code-first registrations.

## Start modes

```csharp
app.UseSharpTicker(SharpTickerStartMode.Manual);
```

- `Immediate` (default) starts processing when the host starts.
- `Manual` waits until you start it through `ISharpTickerHostScheduler.StartAsync()`. The same service has `StopAsync()`, `Restart()` and `IsRunning`.

`DisableBackgroundServices()` on the options builder turns processing off completely. Use it for instances that only schedule jobs, such as an API that adds jobs for separate worker instances.

## Handling failures

Register one handler for all failed and cancelled runs:

```csharp
public class AlertingExceptionHandler(ILogger<AlertingExceptionHandler> logger) : ITickerExceptionHandler
{
    public Task HandleExceptionAsync(Exception exception, Guid tickerId, TickerType tickerType)
    {
        logger.LogError(exception, "Job {TickerId} failed", tickerId);
        return Task.CompletedTask;
    }

    public Task HandleCanceledExceptionAsync(Exception exception, Guid tickerId, TickerType tickerType)
        => Task.CompletedTask;
}

builder.Services.AddSharpTicker(options => options.SetExceptionHandler<AlertingExceptionHandler>());
```

The handler is a singleton. Retries are configured per ticker; see [Scheduling](scheduling.md#retries).

### Dashboard failures

When the dashboard refuses an add or update, for example for an invalid cron expression, it logs a warning with the reason. A failure of the store is logged as an error with the exception. Both use the `SharpTicker.Dashboard.Endpoints.DashboardEndpoints` logger category. The browser gets the reason only for a refusal; for a failure of the store it gets a short message, so table and constraint names stay out of the page.

### Scheduler log entries

A job reports its own failure through the exception handler above. These entries are for what goes wrong around the jobs, and none of them is written when everything is fine:

| Category | Level | What happened |
|---|---|---|
| `SharpTicker.TaskScheduler` | Error | A job threw an exception that nothing handled, or the scheduler could not start a worker thread. The scheduler keeps running. |
| `SharpTicker.TaskScheduler` | Warning | The scheduler was disposed with queued jobs that had not started. The number is in the entry. |
| `SharpTicker.Dispatcher.SharpTickerDispatcher` | Warning | Jobs were dispatched while the application was stopping, so they will not start. The entry names each function and ticker id. Their tickers stay `InProgress`. |
| `SharpTicker.BackgroundServices.SharpTickerSchedulerBackgroundService` | Warning | The application is stopping with jobs running. The first entry says that the scheduler waits for them and names them (the first ten); the second says that some were still running when the wait ended (20 seconds, or the host's shutdown timeout if that comes first) and were recorded as `Cancelled`. |
| `SharpTicker.EntityFrameworkCore.StartupRelease` | Error | Releasing the jobs that a dead node or an earlier run of this node left locked failed when the application started. |

## Tracing

SharpTicker reports job execution through `System.Diagnostics.ActivitySource` with the source name **`SharpTicker`**.

- `options.EnableActivitySource()` in the core package turns the activities on.
- The `SharpTicker.Instrumentation.OpenTelemetry` package adds `options.AddOpenTelemetryInstrumentation()`, which also keeps writing log entries.

Add the source to your OpenTelemetry setup:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource("SharpTicker"));
```

Activities are named `sharpticker.job.execute.<type>`, `sharpticker.job.enqueued`, `.completed`, `.failed`, `.cancelled` and `.skipped`, plus `sharpticker.seeding.started` and `.completed`. Tags use the prefix `sharpticker.job.`, for example `sharpticker.job.function`, `sharpticker.job.id`, `sharpticker.job.priority` and `sharpticker.job.retry_count`.
