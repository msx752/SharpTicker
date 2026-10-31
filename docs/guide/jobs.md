# Defining jobs

A job is a function with a unique name. The scheduler stores only the name, so a job keeps working across restarts and on every node that has the same code.

There are two ways to register one: the `[TickerFunction]` attribute, found by the source generator, or code-first registration with `MapTicker`.

## The `[TickerFunction]` attribute

```csharp
using SharpTicker.Utilities.Base;
using SharpTicker.Utilities.Enums;

public class ReportJobs
{
    [TickerFunction("BuildDailyReport", "0 0 6 * * *", TickerTaskPriority.High, maxConcurrency: 1)]
    public async Task BuildDailyReport(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        await Task.Delay(1000, cancellationToken);
    }
}
```

Constructors:

```csharp
TickerFunctionAttribute(string functionName, string cronExpression = null,
    TickerTaskPriority taskPriority = TickerTaskPriority.Normal, int maxConcurrency = 0)

TickerFunctionAttribute(string functionName, TickerTaskPriority taskPriority, int maxConcurrency = 0)
```

| Argument | Meaning |
|---|---|
| `functionName` | Required and unique. This is the name you schedule by. |
| `cronExpression` | Optional. Makes the job recurring; see [Scheduling](scheduling.md#cron-jobs). A value such as `%Jobs:Cleanup:Cron%` is read from configuration at startup. |
| `taskPriority` | `High`, `Normal` (default), `Low` or `LongRunning`. See [Priority](#priority). |
| `maxConcurrency` | How many runs of this function may execute at once on one node. `0` (default) means no limit. |

### Supported method shapes

The source generator checks every job method and reports problems as compile errors (`ST001` to `ST011`):

- The class and the method are `public` or `internal`. The class is not abstract and not nested.
- The method is static or an instance method, synchronous or `async`.
- Parameters may only be `TickerFunctionContext`, `TickerFunctionContext<TRequest>` and `CancellationToken`, in any combination, or none. Other parameters, including `IServiceProvider`, are not allowed; use constructor injection instead.
- Function names are present and unique, and cron expressions are valid.

### Dependency injection

Instance jobs are created through dependency injection, so constructor parameters are resolved from the container:

```csharp
public class InvoiceJobs(IInvoiceService invoices, ILogger<InvoiceJobs> logger)
{
    [TickerFunction("SendInvoices")]
    public Task SendInvoices(CancellationToken cancellationToken) => invoices.SendDueAsync(cancellationToken);
}
```

If the class has several constructors, mark the one to use with `[SharpTickerConstructor]`. Otherwise the first one is used and the generator warns (`ST006`).

## Job context

`TickerFunctionContext` describes the current run:

| Member | Description |
|---|---|
| `Id` | Id of the ticker being run. |
| `ParentId` | For a cron run, the id of its cron ticker; for a chained child, the id of the parent. |
| `Type` | `TickerType.TimeTicker` or `TickerType.CronTickerOccurrence`. |
| `FunctionName` | The function name. |
| `ScheduledFor` | When the run was due, in UTC. |
| `IsDue` | `true` when the run was missed and picked up late by the fallback check (for example after downtime), `false` when the scheduler started it on time. |
| `RetryCount` | How many retries happened before this attempt. |
| `RequestCancellation()` | Cancels this run. |
| `CronOccurrenceOperations.SkipIfAlreadyRunning()` | For cron runs: skips this occurrence if the previous one is still running. |

### Cancellation

A run is cancelled by `RequestCancellation()`, by the cancel button in the dashboard, or by the application stopping. Pass the `CancellationToken` on to what the job awaits. When the job then throws `TaskCanceledException`, or `OperationCanceledException` (which is what `cancellationToken.ThrowIfCancellationRequested()` throws), the run is recorded as `Cancelled`, not `Failed`, and is not retried. An `OperationCanceledException` that nobody asked for, such as a timeout inside the job, is an ordinary failure and follows the retry rules. That includes the `TaskCanceledException` that `HttpClient` throws when its own timeout runs out: it carries a `TimeoutException` and the job's token was not cancelled, so the run is recorded as `Failed` and retried.

A job that runs at once, because it was added for now, is not tied to the request that added it: it keeps running after that request has ended and is cancelled only by the cases above.

When the application stops, the scheduler waits for the jobs that are running, for a little less than the host's shutdown timeout (`HostOptions.ShutdownTimeout`, 30 seconds by default), so a job that honors its token can record that it was cancelled before the host disposes its services. A job that is still running when the wait ends is recorded as `Cancelled` and its lock is released; it is not retried. A job that ignores its token keeps running until the process ends, and it delays the stop by the whole wait, so pass the token on to what the job awaits. If your jobs need longer to wind down, raise the shutdown timeout of the host, and on Kubernetes the termination grace period with it.

## Payloads

A job can take a typed request. Use `TickerFunctionContext<TRequest>` and read the `Request` field:

```csharp
public record ResizeImage(string Path, int Width);

public class ImageJobs
{
    [TickerFunction("ResizeImage")]
    public Task Resize(TickerFunctionContext<ResizeImage> context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        return Task.CompletedTask;
    }
}
```

When scheduling, serialize the request with `TickerHelper.CreateTickerRequest`:

```csharp
using SharpTicker.Utilities;

await tickers.AddAsync(new TimeTickerEntity
{
    Function = "ResizeImage",
    Request = TickerHelper.CreateTickerRequest(new ResizeImage("photos/1.jpg", 800))
});
```

Requests are stored as JSON. For trimmed or AOT applications, pass a source-generated `JsonTypeInfo<T>` to `CreateTickerRequest`, or register a context with `WithJsonContext(...)` on the options builder. `UseGZipCompression()` compresses stored requests; it is off by default.

The dashboard offers an example request for every job that takes one. It builds the example from the same serializer options, so in a trimmed or AOT application only request types that are listed in the registered context get an example; for the others the request field stays editable and you enter the JSON by hand.

## Code-first registration

`MapTicker` registers jobs without the attribute. It is useful for classes you do not own, generic setups and small inline handlers.

```csharp
using SharpTicker.DependencyInjection;

// A class implementing ITickerFunction; the name defaults to the class name
builder.Services.MapTicker<CleanupFunction>();

// A typed request
builder.Services.MapTicker<ResizeFunction, ResizeImage>();

// An inline handler; this form may also take IServiceProvider
builder.Services.MapTicker("Ping", (context, services, cancellationToken) => Task.CompletedTask);

// A group with shared settings; functions are named "Reports.<ClassName>"
builder.Services.MapTickerGroup("Reports", group => group
    .WithPriority(TickerTaskPriority.Low)
    .WithMaxConcurrency(2)
    .MapTicker<WeeklyReportFunction>());
```

`ITickerFunction` and `ITickerFunction<TRequest>` have one method, `ExecuteAsync(context, cancellationToken)`. With code-first registration you can also schedule by type: `ITimeTickerManager.AddAsync<TFunction>(executionTime)` and `AddAsync<TFunction, TRequest>(executionTime, request)`.

## Priority

`TickerTaskPriority` has four values:

- **`LongRunning`** runs the job on its own thread instead of the scheduler's worker pool. Use it for jobs that block a thread for a long time.
- **`High`, `Normal` and `Low`** order jobs that become due at the same moment: the scheduler hands higher priority jobs to the workers first. Priority does not pause or preempt jobs that are already running.

> [!NOTE]
> Today the order is applied when the main scheduler loop starts due jobs. Jobs started from the dashboard, run immediately or picked up again after a timeout are not ordered yet. This is tracked in [issue #4](https://github.com/msx752/SharpTicker/issues/4).
