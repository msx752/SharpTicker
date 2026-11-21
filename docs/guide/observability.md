# Tracing and logs

## Tracing

SharpTicker reports job execution through `System.Diagnostics.ActivitySource` with the source name **`SharpTicker`**.

- `options.EnableActivitySource()` in the core package turns the activities on.
- The `SharpTicker.Instrumentation.OpenTelemetry` package adds `options.AddOpenTelemetryInstrumentation()`, which also keeps writing log entries.

Add the source to your OpenTelemetry setup:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource("SharpTicker"));
```

Activities are created only while a listener, such as the OpenTelemetry SDK, listens to the `SharpTicker` source. SharpTicker has no `Meter`; it reports traces and log entries, not metrics. Exporters (console, OTLP and others) come from their own OpenTelemetry packages.

### Activities

| Activity | When |
|---|---|
| `sharpticker.job.execute.timeticker`, `sharpticker.job.execute.crontickeroccurrence` | One per run of a time job or a cron occurrence, around all of its attempts. |
| `sharpticker.job.enqueued` | When the run starts. |
| `sharpticker.job.completed` | When the run ends with success or failure. Its status is `Ok` or `Error`. |
| `sharpticker.job.failed` | When the last attempt failed. Its status is `Error`. |
| `sharpticker.job.cancelled` | When the run was cancelled. Its status is `Error`. |
| `sharpticker.job.skipped` | When the job ended itself as skipped, or a chain child was skipped because its parent's outcome does not let it run. |
| `sharpticker.job_request_serialization.failed` | When the request of a job could not be read. |

`enqueued`, `completed`, `failed` and `cancelled` are started while the `execute` activity of the run is current, so they show as its children.

### Tags

On the `execute` activity:

| Tag | Value |
|---|---|
| `sharpticker.job.id` | The ticker id. |
| `sharpticker.job.type` | `TimeTicker` or `CronTickerOccurrence`. |
| `sharpticker.job.function` | The function name. |
| `sharpticker.job.priority` | `LongRunning`, `High`, `Normal` or `Low`. |
| `sharpticker.job.machine` | The `NodeIdentifier` of the instance. |
| `sharpticker.job.retries` | The number of retries the job allows. |
| `sharpticker.job.parent_id` | The parent's id, for a chain child. |
| `sharpticker.job.run_condition` | The run condition, for a chain child that is a time job. |
| `sharpticker.job.is_due` | Whether the run was picked up as overdue; such a run ends `DueDone` on success. |
| `sharpticker.job.is_child` | Whether the run is a chain child that runs inside its parent's run. |
| `sharpticker.job.current_attempt` | The attempt, from 1. |
| `sharpticker.job.final_status` | The status the run ended with, such as `Done`, `DueDone`, `Failed`, `Cancelled` or `Skipped`. |
| `sharpticker.job.final_retry_count` | The retry count at the end, on success or failure. |
| `sharpticker.job.error_type` | The exception type of the last attempt, on failure. |
| `sharpticker.job.cancellation_reason` | `Task was cancelled`, on cancellation. |
| `sharpticker.job.skip_reason` | The reason the job gave, when it ended itself as skipped. |
| `sharpticker.job.stopped_by_node_fence` | `true` when the run was stopped because its node could not write its heartbeat (see [When an instance stops](failure-modes.md#when-an-instance-stops)). |

On the other activities:

| Activity | Tags |
|---|---|
| `enqueued` | `sharpticker.job.id`, `sharpticker.job.type` (lower case, for example `timeticker`), `sharpticker.job.function`, `sharpticker.job.enqueued_from` |
| `completed` | `sharpticker.job.id`, `sharpticker.job.function`, `sharpticker.job.execution_time_ms` (the whole run, with the waits between attempts), `sharpticker.job.success` |
| `failed` | `sharpticker.job.id`, `sharpticker.job.function`, `sharpticker.job.retry_count`, `sharpticker.job.error_type`, `sharpticker.job.error_message`, `sharpticker.job.error_stack_trace` |
| `cancelled` | `sharpticker.job.id`, `sharpticker.job.function`, `sharpticker.job.cancellation_reason` |
| `skipped` | `sharpticker.job.id`, `sharpticker.job.function`, `sharpticker.job.skip_reason` |
| `job_request_serialization.failed` | `sharpticker.job.id`, `sharpticker.job.function`, `sharpticker.job.cancellation_reason` (the exception message) |

`sharpticker.job.error_message` and `sharpticker.job.error_stack_trace` carry the exception's message and stack trace as they are; keep that in mind when traces leave your systems.

## Job log entries

Every run writes these entries through `ILogger`, also without tracing, so they reach the logging provider of the application (Serilog, NLog or another). The category is `SharpTicker.Utilities.Instrumentation.LoggerInstrumentation`, `SharpTicker.Utilities.Instrumentation.ActivitySourceInstrumentation` with `EnableActivitySource()`, or `SharpTicker.Instrumentation.OpenTelemetry.OpenTelemetryInstrumentation` with `AddOpenTelemetryInstrumentation()`.

| Level | Message |
|---|---|
| Information | `SharpTicker Job enqueued: {JobType} - {Function} ({JobId}) from {EnqueuedFrom}`, when a run starts. |
| Error | `Attempt of {Function} failed in {ElapsedMs}ms — {Error}`, or `Retry {Attempt} of {MaxRetries} for {Function} failed in {ElapsedMs}ms — {Error}`, for an attempt that is followed by a retry. |
| Warning | `Retrying in {IntervalSeconds}s (retry {NextAttempt} of {MaxRetries})…` |
| Error | `SharpTicker Job failed: {Function} ({JobId}) - Retry {RetryCount} - {Error}`, when the last attempt failed. |
| Information | `SharpTicker Job completed: {Function} ({JobId}) in {ExecutionTime}ms - Success: {Success}`, when a run ends with success or failure. |
| Warning | `SharpTicker Job cancelled: {Function} ({JobId}) - {Reason}` |
| Information | `SharpTicker Job skipped: {Function} ({JobId}) - {Reason}` |
| Error | `Failed to deserialize request to {RequestType} - {TickerId} - {TickerType}: {Exception}` |

To keep only problems, set the level of that category to `Warning` in your logging configuration.

## Scheduler log entries

A job reports its own failure through the [exception handler](failure-modes.md#handling-failures). These entries are for what goes wrong around the jobs, and none of them is written when everything is fine:

| Category | Level | What happened |
|---|---|---|
| `SharpTicker.TaskScheduler` | Error | A job threw an exception that nothing handled, or the scheduler could not start a worker thread. The scheduler keeps running. |
| `SharpTicker.TaskScheduler` | Warning | The scheduler was disposed with queued jobs that had not started. The number is in the entry. |
| `SharpTicker.Dispatcher.SharpTickerDispatcher` | Warning | Jobs without a claim marker were dispatched while the application was stopping, so they will not start. The entry names each function and ticker id. Their tickers stay `InProgress`. Jobs that SharpTicker claimed itself carry a marker and are put back instead. |
| `SharpTicker.BackgroundServices.ClaimedWorkQueue` | Warning | A claimed job did not start (the reason is in the entry) and was put back; the fallback runs it. A second entry at the same level says when the dashboard could not be told. |
| `SharpTicker.BackgroundServices.ClaimedWorkQueue` | Error | A claimed job did not start and could not be put back, so its row stays `InProgress` for now. The scheduler tries again; the attempts after the first are written at `Debug`. The entry written while the application stops says that nothing tries again, and the row stays `InProgress` until its store releases this instance's rows. |
| `SharpTicker.Utilities.Managers.TickerManager` | Warning | Jobs added for "run now" were stored, but claiming them failed. The add returned success, and the jobs run when the fallback picks them up. At `Error`: some of the rows that had been claimed could not be put back either. They stay `InProgress` until the scheduler puts them back, which it tries again like any other put-back that failed; the fallback then runs them. With the background services disabled nothing tries again, and they stay `InProgress` until their store releases this instance's rows. |
| `SharpTicker.BackgroundServices.ClaimedWorkQueue` | Information | A claimed job did not start and its row was no longer this claim (another claim took it, or it was removed), so nothing was put back. A custom provider that keeps the default put-back writes this entry for every such job. |
| `SharpTicker.BackgroundServices.SharpTickerFallbackBackgroundService` | Error | The fallback failed, so overdue jobs are not picked up until it recovers. It tries again every `FallbackIntervalChecker`; the failures after the first are written at `Debug`, and the recovery at `Information`. When the entry says that some rows could not be put back, those rows stay `InProgress` until the scheduler puts them back, which it tries again like any other put-back that failed. |
| `SharpTicker.BackgroundServices.SharpTickerSchedulerBackgroundService` | Error | The scheduler loop failed, so no job is picked up until it recovers. It tries again every second; the failures after the first are written at `Debug`, and the recovery at `Information`. A restart of the loop (for example for a ticker added earlier than the planned one) is not logged as a failure, whatever exception the store's provider throws for the command it cancelled; that exception is written at `Debug`. A failure of the store that ends a pass at the same moment, while the loop reads its next plan, is written at `Debug` too; when it goes on, a later pass that ends without a restart reports it. A failure while the loop hands due jobs over is reported as a loop failure, as above. |
| `SharpTicker.Utilities.Managers.InternalTickerManager` | Warning | The fallback took a job but the dashboard could not be told. The job runs. |
| `SharpTicker.Utilities.Managers.InternalTickerManager` | Warning | At the startup of each instance, once per enabled cron ticker: its stored expression has no next occurrence (it does not parse, or it matches no date), so it does not run. The entry names the cron ticker, its function and the expression. Correct the expression on the dashboard or in code, or remove the cron ticker. Another entry at the same level says that the cron tickers could not be read for this check; the startup goes on. |
| `SharpTicker.BackgroundServices.SharpTickerSchedulerBackgroundService` | Warning | The application is stopping with jobs running. The first entry says that the scheduler waits for them and names them (the first ten); the second says that some were still running when the wait ended (a little before the host's shutdown timeout) and were recorded as `Cancelled`. |
| `SharpTicker.BackgroundServices.SharpTickerSchedulerBackgroundService` | Information | A job still running when the stop's wait ended had stored its outcome meanwhile, or its row was taken by another run or recorded as `Cancelled` by the EF Core lease, so the stop did not record it as `Cancelled`. |
| `SharpTicker.TickerExecutionTaskHandler` | Error | A job's outcome could not be stored, so its row stays `InProgress` for now. The entry names the job and the lost outcome and says what happens next: the scheduler writes it again, or (with a custom provider, without the background services, or while the scheduler stops) it is not written again and the job can run again after its row is released. |
| `SharpTicker.BackgroundServices.LostOutcomeDrain` | Information | An outcome that could not be stored was written again and is stored. The attempts that failed before it are written at `Debug`. |
| `SharpTicker.BackgroundServices.LostOutcomeDrain` | Warning | An outcome that could not be stored was not written again because its row was released, taken by another run, or recorded as `Cancelled` by the EF Core lease meanwhile; a released job may run again. A second entry at the same level says that the chain children of a stored outcome could not be followed up; the fallback runs the delayed ones the outcome lets run, and an inline one that is still `Idle` is not run. |
| `SharpTicker.BackgroundServices.LostOutcomeDrain` | Error, Critical | Outcomes could still not be stored 5 minutes after the oldest was lost; the entry names them (the first ten). It repeats every 5 minutes, at `Critical` once the oldest has waited 10 minutes. At `Error` too: an outcome that could not be stored before the scheduler stopped. |
| `SharpTicker.Utilities.Managers.InternalTickerManager` | Warning | An outcome written again, or a job recorded as `Cancelled` at the stop, was stored, but the dashboard could not be told. |
| `SharpTicker.EntityFrameworkCore.StartupRelease` | Error | Releasing the jobs that a dead node or an earlier run of this node left locked failed when the application started. |
| `SharpTicker.EntityFrameworkCore.InProgressLease` | Information | Jobs of other instance names whose lock was not renewed for the lease were recorded as `Cancelled`; the entry has their number and the names that held them (the ten with the most jobs). |
| `SharpTicker.EntityFrameworkCore.InProgressLease` | Warning | Renewing the lock of this instance's jobs, or recording the jobs of other instances whose lease ended as `Cancelled`, failed. It is tried again at the next pass. If renewing fails for the whole lease, the other instances record this instance's jobs as `Cancelled`. |
| `SharpTicker.Caching.StackExchangeRedis.NodeHeartBeatBackgroundService` | Warning | With Redis as the job store: releasing the jobs an earlier run of this node left locked failed after the application started. It is tried again every 30 seconds; the release that succeeds is written at `Information`. A single job that could not be released is named in an entry of its own. |
| `SharpTicker.Caching.StackExchangeRedis.NodeHeartBeatBackgroundService` | Error | With Redis as the job store: this node could not write its heartbeat for one TTL. It asked its running jobs to stop (the entry says how many) and starts no job until a heartbeat is written again. One entry each time it happens. The failed heartbeat writes before it are at `Warning`, one for each try. |
| `SharpTicker.Caching.StackExchangeRedis.NodeHeartBeatBackgroundService` | Information | The heartbeat is written again; the node starts jobs again. |
| `SharpTicker.Caching.StackExchangeRedis.NodeHeartBeatBackgroundService` | Error | With Redis as the job store: the connection is a Redis Cluster, which the job store does not support (see [Redis](storage.md#redis), [#225](https://github.com/msx752/SharpTicker/issues/225)). Written once per start, after the first heartbeat is written. Nothing else changes; the scheduler loop and the fallback log their own failures when they read more than one job. |
| `SharpTicker.Caching.StackExchangeRedis.NodeHeartBeatBackgroundService` | Warning | A job that was asked to stop had registered a callback on its cancellation token, and the callback threw. The entry names the job. |
| `SharpTicker.TickerExecutionTaskHandler` | Warning | A job was asked to stop because its node could not write its heartbeat. No outcome is stored for that run; its row is put back once the store answers, unless another instance released it first, and the job runs again. |
| `SharpTicker.BackgroundServices.ClaimedWorkQueue` | Information | The row of a job that was stopped that way was put back, or had already moved on. |
| `SharpTicker.BackgroundServices.ClaimedWorkQueue` | Warning | A job that was already claimed was not started because its node could not write its heartbeat; it was put back. |
| `SharpTicker.TickerExecutionTaskHandler` | Warning | Starting the delayed chain children of a parent that ended (or, for `InProgress` children, started) failed, or waking the scheduler for one due later failed. The fallback runs them. At `Error`: the children were claimed but could not be dispatched, and they stay `InProgress` until their store releases the rows of this node. |
| `SharpTicker.TickerExecutionTaskHandler` | Warning, Error | An inline chain child (no time of its own) did not start after its parent's outcome was stored late. At `Warning`: it was not claimed, because its row was taken or changed meanwhile or the claim failed. At `Error`: the outcome was stored only while the scheduler stopped. The entry names the child and its parent. If the child's row is still `Idle`, the fallback takes it once its parent ended 30 seconds ago. |
| `SharpTicker.TickerExecutionTaskHandler` | Warning | An inline chain child was not started inside its parent's run because its start claim failed (the store could not be reached). The child is not run without its row; the row keeps waiting and the fallback takes it once its parent ended 30 seconds ago. |
| `SharpTicker.Utilities.Managers.InternalTickerManager` | Warning | Claiming delayed chain children after their parent ended failed. The fallback runs them. A second entry at the same level says that they were claimed but the dashboard could not be told; they run. At `Error`: some rows the claim had taken could not be put back either; the scheduler puts them back later, as above. |
| `SharpTicker.EntityFrameworkCore`, `SharpTicker.MongoDB` | Warning | The fallback could not mark the delayed chain children whose parent ended with an outcome they do not run on as `Skipped`. The fallback still takes the other overdue jobs, and the next pass tries again. |

## Dashboard failures

When the dashboard refuses an add or update, for example for an invalid cron expression, it logs a warning with the reason. A failure of the store is logged as an error with the exception. Both use the `SharpTicker.Dashboard.Endpoints.DashboardEndpoints` logger category. The browser gets the reason only for a refusal; for a failure of the store it gets a short message, so table and constraint names stay out of the page.
