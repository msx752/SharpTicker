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

## When a job starts and ends

To run code of your own when a job starts and when it ends, register one lifecycle handler:

```csharp
public sealed class JobEndLog(ILogger<JobEndLog> logger) : ITickerLifecycleHandler
{
    public Task OnCompletedAsync(TickerLifecycleContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("{Function} ({Id}) ended as {Status} after {Elapsed}",
            context.FunctionName, context.Id, context.Status, context.Elapsed);
        return Task.CompletedTask;
    }
}

builder.Services.AddSharpTicker(options => options.SetLifecycleHandler<JobEndLog>());
```

Both methods return a completed task by default, so implement only the ones you need.

- **`OnStartingAsync`** is called once per run, before its first attempt. The run waits for it, up to the timeout. A run that is put back before it ends, and runs again later, gets the call again.
- **`OnCompletedAsync`** is called after a final status (`Done`, `DueDone`, `Failed`, `Cancelled` or `Skipped`) is stored for the job, not between retries. It is made by the instance that stored the status, and the worker that ran the job does not wait for it.
- A chain child that is skipped because its parent's outcome does not match its run condition gets `OnCompletedAsync` with `Skipped` from the instance that ran the parent, and no `OnStartingAsync`.
- Each call has its own timeout, 30 seconds by default (`SetLifecycleHandlerTimeout`), and its own token, which is cancelled when the timeout ends or when the application's stop has finished waiting for the calls. A call that throws or runs past its timeout is logged at `Warning`, and it does not change the job.
- When the application stops, the scheduler waits for the `OnCompletedAsync` calls that are still running, as the last step of its stop and with what is left of the host's shutdown timeout. The calls still running when that time is up are cancelled and counted in a `Warning`.
- The handler is a singleton. Resolve scoped services, such as a `DbContext`, through `IServiceScopeFactory`.
- When no handler is registered, no call is made.

`TickerLifecycleContext` describes the job as it was when the call was made:

| Member | Description |
|---|---|
| `Id` | Id of the time ticker or cron occurrence. |
| `ParentId` | As in `TickerFunctionContext`: for a cron run, the id of its cron ticker; for a chained child, the id of the parent. |
| `Type` | `TickerType.TimeTicker` or `TickerType.CronTickerOccurrence`. |
| `FunctionName` | The function name. |
| `Status` | `InProgress` in `OnStartingAsync`; the stored final status in `OnCompletedAsync`. |
| `RetryCount` | The retries the run used. |
| `Elapsed` | The duration stored with the outcome: from the first attempt to the end, retry waits included, for `Done`, `DueDone` and `Failed`; that of the last attempt for `Cancelled` and `Skipped`. |
| `ExceptionMessage` | The exception text stored for a `Failed` or `Cancelled` job; `null` otherwise. |
| `SkippedReason` | Why a `Skipped` job was skipped; `null` otherwise. |

`RetryCount` and `Elapsed` are zero for a job that the stop recorded as `Cancelled` while it was still running, and for a chain child that its parent's outcome skipped.

The calls are made from memory, in the process that runs or records the job, so some ends are not reported:

- A process that stops after it stored a status, and before it made the call, does not make it.
- When the write of a job's outcome fails, the scheduler writes it again later. If that write finds the outcome already stored, or the application stops before it, the call is not made.
- A status that a store records in bulk for jobs of other instances is not reported: the EF Core lease recording expired jobs as `Cancelled`, the fallback skipping the children of a parent that ended on another instance, and the startup skip of cron occurrences that are too old.
- If the write that skips a parent's children fails or is cancelled partway, or the dashboard notification fails while they are reported, the children skipped before that point and not yet reported are not reported.
- With `DisableBackgroundServices()`, the instance runs no jobs, so it makes no calls.
- With a [custom persistence provider](storage.md#custom-persistence-providers) that does not derive from one of SharpTicker's stores, every child that the parent's outcome skips is reported, even one that had already run.

A call can also be made more than once for the same job, for example when a run is put back and runs again. Use `Id` to recognize a job.

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

- **`LongRunning`** starts the job at once on a thread of its own instead of a worker of the scheduler: it does not wait in the queue, does not count against `MaxConcurrency` and ignores priority order. Like any job, it keeps that thread only up to its first `await` of a task that has not completed yet. Use it for jobs that block a thread for a long time, so they do not hold a worker.
- **`High`, `Normal` and `Low`** decide which waiting job a free worker takes. When more jobs are ready than there are workers (see `MaxConcurrency`), `High` jobs start before `Normal` ones and `Normal` before `Low` ones; jobs of one priority start in the order they became ready. A `Low` job is not held back for ever: after ten higher priority jobs have started in a row while it waits, one `Low` job goes next. Priority does not pause or preempt jobs that are already running, and it does not change when a job becomes due. It applies to every job that goes through the worker pool, however it was started: by the scheduler, from the dashboard, or picked up again after a timeout.
