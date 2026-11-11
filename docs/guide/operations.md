# Running in production

## Several instances

Instances that use the same shared store ([EF Core, MongoDB or Redis](storage.md)) work as one scheduler. When a job becomes due, an instance claims it with a conditional update on the stored record and marks itself as the lock holder, so one instance wins each claim. [What is guaranteed](#what-is-guaranteed) lists where a job can still run more than once.

Give every running instance a stable name that no other running instance uses. The default is the machine name, which is not unique when several containers share a host name pattern or run on one machine. The name is the lock holder on a job, and a starting instance treats every job held under its own name as left behind (see the next paragraph), so two running instances with one name release each other's running jobs:

```csharp
builder.Services.AddSharpTicker(options =>
{
    options.ConfigureScheduler(scheduler =>
    {
        scheduler.NodeIdentifier = Environment.GetEnvironmentVariable("POD_NAME") ?? Environment.MachineName;
    });
});
```

When an instance stops while holding jobs, they are released for the others: with EF Core and with Redis when that node starts again (every job held under the name of the starting instance, `Queued` or `InProgress`, that was locked before this run started; jobs the new run has claimed in the meantime are kept), and with Redis also by another running instance once the node's heartbeat stopped. With Redis the release at startup is tried again every 30 seconds while it fails, for example while Redis cannot be reached. A heartbeat entry expires `NodeHeartbeatInterval` plus 20 seconds (the heartbeat TTL) after the node's last heartbeat; another instance releases the node's jobs when every one of its checks over one more TTL (at most 10 seconds apart) found the entry missing. A check that fails or takes longer than 10 seconds, or 20 seconds without a check, starts that TTL again. So the release comes about two TTLs after the node stopped. A running instance never releases the jobs of its own run, and after an outage of Redis itself the instances that are still running are not released: each writes its heartbeat again before the others' confirmation ends. A restart under the same name before another instance released the node (about two TTLs) is not seen as a stop by the others; the restarted instance releases what its earlier run left, as described above. MongoDB has no release step for an instance that stopped, so a job left `InProgress` by a process that ended stays so until you change it. A running instance puts back its own claims whose job never started, with every store; see [What is guaranteed](#what-is-guaranteed).

### What is guaranteed

Of all instances that try to start a due job, one wins: the job goes to a worker only after a conditional write moved its row to `InProgress` and that write was the winner. The regular claim needs the row to be `Queued` and held by this node. The fallback for missed jobs takes a row that is `Idle` or `Queued` (held by any node with EF Core and MongoDB, unlocked or held by this node with Redis), and "run now" takes an `Idle` or `Queued` row that is unlocked or held by this node. A node that loses a claim logs it at `Information` level and does not run the job. With Redis, a cron expression also produces one occurrence per time slot, however many instances create it, provided the cron itself exists once (see below).

Each claim starts its job at most once. A claim that was won but whose job never starts is put back: its row returns to `Idle` without a lock holder, and the fallback runs the job once. This holds for the regular claim, the fallback and "run now", when the application stops while the job waits for its concurrency slot, when the scheduler is stopped or refuses the job, and when a claim call fails after it had claimed some rows, or after its own write landed and its reply was lost. The put-back is a conditional write that matches only this claim (the row is still `InProgress`, held by this instance, and its `UpdatedAt` is the marker the claim wrote), so it never changes a row that another claim took or a job that ran, and it changes nothing when the failed write did not land. When the store cannot be reached, the instance tries again while it runs (each attempt waits at most 5 seconds, and a pass stops at the first one that fails), and the job does not start in the meantime. At a stop, the put-backs use the time the host gives the stop (`HostOptions.ShutdownTimeout`); what has not been put back by then is logged as an error.

"Run now" (adding jobs that are due within a second) returns success once the jobs are stored, even when they cannot run now: on an instance whose scheduler is stopped, while the application stops, or when the store fails while they are claimed. The jobs that were claimed are put back, and all of them run when the fallback of a running instance picks them up. A failure of the claim is logged (see [Scheduler log entries](#scheduler-log-entries)); do not retry the add, or the jobs are added twice.

The put-back has these limits:

- A job whose put-back had not succeeded when the process ended stays `InProgress` until its store releases it: EF Core when the instance starts again, Redis when the instance starts again or another instance sees its heartbeat stop, MongoDB not at all.
- The marker is the instance's clock in whole milliseconds. An `UpdatedAt` column that keeps less than that, such as SQL Server `datetime` or MySQL `datetime` without fractional seconds, cannot hold it exactly, so a put-back may not find its claim. The default mappings (`datetime2`, `datetime(6)`) keep enough.
- A cron occurrence that was put back keeps its time slot, so it counts as missed: with `SkipStaleCronOccurrencesOnStartup` it is skipped when an instance starts later than the threshold.
- With a [custom persistence provider](storage.md#custom-persistence-providers) that keeps the default put-back methods, nothing is put back.

The writes of a running job are fenced by the marker its claim wrote, with every built-in store: each write of the run (a failed attempt, the next retry, the outcome) lands only while the row is still held by this instance with that marker, and keeps the marker in `UpdatedAt`. So a run whose row was released and claimed again, by another instance or by this one, can no longer overwrite the new run: its late write changes nothing and is logged as an outcome that was not stored. A chain child without a time of its own is held the same way from its start, with the lock of the instance that runs it. Because the marker stays in `UpdatedAt` while the job runs, a finished time ticker shows its claim time there; `ExecutedAt` holds when it ended. A [custom persistence provider](storage.md#custom-persistence-providers) writes by id as before. The marker is unique per instance name only within one process, so give every process its own `NodeIdentifier` (two processes on one machine with the default machine name share a lock holder).

When the final write of a job (its outcome) fails, for example while the store cannot be reached, the job's row stays `InProgress`. With a built-in store and the background services running, the scheduler writes that outcome again while the instance runs, fenced by the same marker: right away, and then on every pass of the scheduler, at most 5 seconds apart, until the store takes it. Each attempt waits at most 5 seconds with EF Core and MongoDB (Redis is bounded by its client's own timeout); a pass stops at the first attempt that runs out of that time, while an outcome the store refuses does not hold back the others. The row then ends with the job's real outcome, and the job does not run again. If the row was released, or released and claimed again, in the meantime, the write changes nothing and the outcome is dropped with a warning. The delayed chain children of a job whose outcome was stored late follow it as usual; its inline children (those without a time of their own) are neither run nor skipped. The scheduler starts no job while it does this. When the scheduler stops, it gives these outcomes one last write before it records the jobs that are still running as `Cancelled`. That `Cancelled` write is fenced too: it lands only on a row that this run still holds and that is still `InProgress`, so it never replaces an outcome the job stored, or the run of another instance. An outcome that is still not stored when the scheduler stops is logged as an error. If the scheduler is started again in the same process (`ISharpTickerHostScheduler`, or the dashboard), it writes that outcome again. With a [custom persistence provider](storage.md#custom-persistence-providers), or with the background services disabled, a lost outcome is logged and not written again.

While instances of the previous version still run against the store (see [Migrating](https://github.com/msx752/SharpTicker/blob/main/MIGRATING.md#rolling-deployment)), none of this holds: a job can run twice until the last of them is gone.

A job can still run more than once, or twice at about the same time, in these cases:

- A process dies while a job runs, or the job finishes, its final write to the store fails, and the process ends before the scheduler has written that outcome again (see above). The job's row stays `InProgress`, and after a restart (EF Core, Redis) or the heartbeat release (Redis) the job runs again, and a repeated parent repeats its children. The first run's outcome is not written over the second run. This is an at-least-once limit: a durable record of the outcome outside the store is not kept.
- A Redis node that alone could not send heartbeats for about two heartbeat TTLs (a long pause, a network split that cut it off) while the other instances kept reaching Redis has its `InProgress` jobs released while they may still be running.
- An instance starts with the name of another running instance, with EF Core: its startup release frees the jobs that instance is running.
- A write the store acknowledged is lost, for example MongoDB with a write concern of `w:1` after a primary change, or Redis after a failover to a replica that had not received it.
- A job record is updated with the whole object (`UpdateTimeTickers`, which the manager's update methods call, and the edit dialog of the dashboard) with a [custom persistence provider](storage.md#custom-persistence-providers) that writes every field: a copy loaded before the claim puts the old `Status` and lock back and the job can be claimed again. The built-in stores keep the run of a row that is `InProgress` (its status, lock, marker, retry count, times and messages) and write only the other fields, for every row of the chain, and a copy that still shows a run the store no longer has does not bring that run back. A job that has ended keeps its outcome the same way, so a dialog opened before the job started and saved after it ran does not write it back as not started; to run an ended job again, add a new job.

- Two instances start a new cron function at the same time. Seeding reads the existing crons and then inserts a new one with a new id, and none of the stores has a unique constraint on the function, so both can insert it and each cron then creates its own occurrences. Start new cron functions on one instance first, or check for duplicate crons afterwards.

Write jobs so that a second run does no harm: key side effects on the ticker id or on your own business key, and skip work that is already done.

### Clocks

Due times are compared against each instance's own clock. The scheduler picks a job up when it is due and treats it as missed one second later. Keep the clocks of all instances in sync with NTP. With a difference of one second or more, an instance with a fast clock starts a job earlier than the others expect, and an instance with a slow clock finds the job already taken and does not run it. The claim still has one winner either way.

The marker a claim writes into `UpdatedAt` grows with every claim of an instance. When its clock is set back, the markers keep growing from the last one, so the `UpdatedAt` of a claimed job can be ahead of the clock until it catches up.

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

The default `NodeIdentifier` is the machine name, which in a container is the container id and changes with every new container. Jobs keep the identifier of the node that locked them, so the Machines panel of the dashboard lists every identifier that ever locked a job, with its job count since the beginning, not only the nodes that are running now (the one marked Current is this node). Set a stable name, such as the pod name of a StatefulSet or a fixed name per service, to keep that list short, and so that jobs left running by a node that stopped can be released.

A worker thread runs a job up to its first `await` of a task that has not completed yet; what the job does after that runs on the .NET thread pool. So `MaxConcurrency` limits how many jobs start at once and how many synchronous (blocking) parts run at once, not how many asynchronous jobs are in progress. Because jobs start on the scheduler's own threads, they start on time even when the application has used up the .NET thread pool, but under such starvation a job still waits at its first `await` until the pool has a free thread. Known limit: this holds for a function without a request type. A function with a typed request (`TickerFunctionContext<TRequest>`, or `MapTicker` with a request type) first reads its request from the store, and its code then runs on the .NET thread pool from its first line: `MaxConcurrency` does not limit its blocking parts, and under starvation it does not start on time. A job that a worker runs does not inherit ambient state (`AsyncLocal` values, culture, `Activity.Current`) from the code that queued it or from earlier jobs on the same worker; set what it needs inside the job. A `LongRunning` job does not go through a worker and starts with the ambient state of the code that queued it.

To limit how many runs of one function execute at the same time on an instance, use `maxConcurrency` on `[TickerFunction]` or `WithMaxConcurrency` on code-first registrations.

## Start modes

```csharp
app.UseSharpTicker(SharpTickerStartMode.Manual);
```

- `Immediate` (default) starts processing when the host starts.
- `Manual` waits until you start it through `ISharpTickerHostScheduler.StartAsync()`. The same service has `StopAsync()`, `Restart()` and `IsRunning`.

`DisableBackgroundServices()` on the options builder turns processing off completely. Use it for instances that only schedule jobs, such as an API that adds jobs for separate worker instances.

The store must be reachable when the application starts. At startup SharpTicker reads the store, also when no cron is defined in code. If that read fails, the host fails to start (`Hosting failed to start`) and the process ends; it does not wait for the store. Start the instance again once the store answers, for example through the restart policy of your orchestrator. An outage while the instance runs does not stop it.

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
| `SharpTicker.Dispatcher.SharpTickerDispatcher` | Warning | Jobs without a claim marker were dispatched while the application was stopping, so they will not start. The entry names each function and ticker id. Their tickers stay `InProgress`. Jobs that SharpTicker claimed itself always carry a marker and are put back instead. |
| `SharpTicker.BackgroundServices.ClaimedWorkQueue` | Warning | A claimed job did not start (the reason is in the entry) and was put back; the fallback runs it. A second entry at the same level says when the dashboard could not be told. |
| `SharpTicker.BackgroundServices.ClaimedWorkQueue` | Error | A claimed job did not start and could not be put back, so its row stays `InProgress` for now. The scheduler tries again; the attempts after the first are written at `Debug`. The entry written while the application stops says that nothing tries again, and the row stays `InProgress` until its store releases this instance's rows. |
| `SharpTicker.Utilities.Managers.TickerManager` | Warning | Jobs added for "run now" were stored, but claiming them failed. The add returned success, and the jobs run when the fallback picks them up. At `Error`: some of the rows that had been claimed could not be put back either. They stay `InProgress` until the scheduler puts them back, which it tries again like any other put-back that failed; the fallback then runs them. With the background services disabled nothing tries again, and they stay `InProgress` until their store releases this instance's rows. |
| `SharpTicker.BackgroundServices.ClaimedWorkQueue` | Information | A claimed job did not start and its row was no longer this claim (another claim took it, or it was removed), so nothing was put back. A custom provider that keeps the default put-back writes this entry for every such job. |
| `SharpTicker.BackgroundServices.SharpTickerFallbackBackgroundService` | Error | The fallback failed, so overdue jobs are not picked up until it recovers. It tries again every `FallbackIntervalChecker`; the failures after the first are written at `Debug`, and the recovery at `Information`. When the entry says that some rows could not be put back, those rows stay `InProgress` until the scheduler puts them back, which it tries again like any other put-back that failed. |
| `SharpTicker.BackgroundServices.SharpTickerSchedulerBackgroundService` | Error | The scheduler loop failed, so no job is picked up until it recovers. It tries again every second; the failures after the first are written at `Debug`, and the recovery at `Information`. |
| `SharpTicker.Utilities.Managers.InternalTickerManager` | Warning | The fallback took a job but the dashboard could not be told. The job runs. |
| `SharpTicker.BackgroundServices.SharpTickerSchedulerBackgroundService` | Warning | The application is stopping with jobs running. The first entry says that the scheduler waits for them and names them (the first ten); the second says that some were still running when the wait ended (a little before the host's shutdown timeout) and were recorded as `Cancelled`. |
| `SharpTicker.BackgroundServices.SharpTickerSchedulerBackgroundService` | Information | A job still running when the stop's wait ended had stored its outcome meanwhile, or its row was taken by another run, so it was not recorded as `Cancelled`. |
| `SharpTicker.TickerExecutionTaskHandler` | Error | A job's outcome could not be stored, so its row stays `InProgress` for now. The entry names the job and the lost outcome and says what happens next: the scheduler writes it again, or (with a custom provider, without the background services, or while the scheduler stops) it is not written again and the job can run again after its row is released. |
| `SharpTicker.BackgroundServices.LostOutcomeDrain` | Information | An outcome that could not be stored was written again and is stored. The attempts that failed before it are written at `Debug`. |
| `SharpTicker.BackgroundServices.LostOutcomeDrain` | Warning | An outcome that could not be stored was not written again because its row was released or taken by another run meanwhile; the job may run again. A second entry at the same level says that the delayed chain children of a stored outcome could not be followed up; the fallback runs those the outcome lets run. |
| `SharpTicker.BackgroundServices.LostOutcomeDrain` | Error, Critical | Outcomes could still not be stored 5 minutes after the oldest was lost; the entry names them (the first ten). It repeats every 5 minutes, at `Critical` once the oldest has waited 10 minutes. At `Error` too: an outcome that could not be stored before the scheduler stopped. |
| `SharpTicker.Utilities.Managers.InternalTickerManager` | Warning | An outcome written again, or a job recorded as `Cancelled` at the stop, was stored, but the dashboard could not be told. |
| `SharpTicker.EntityFrameworkCore.StartupRelease` | Error | Releasing the jobs that a dead node or an earlier run of this node left locked failed when the application started. |
| `SharpTicker.Caching.StackExchangeRedis.NodeHeartBeatBackgroundService` | Warning | With Redis as the job store: releasing the jobs an earlier run of this node left locked failed after the application started. It is tried again every 30 seconds; the release that succeeds is written at `Information`. A single job that could not be released is named in an entry of its own. |
| `SharpTicker.TickerExecutionTaskHandler` | Warning | Starting the delayed chain children of a parent that ended (or, for `InProgress` children, started) failed, or waking the scheduler for one due later failed. The fallback runs them. At `Error`: the children were claimed but could not be dispatched, and they stay `InProgress` until their store releases the rows of this node. |
| `SharpTicker.Utilities.Managers.InternalTickerManager` | Warning | Claiming delayed chain children after their parent ended failed. The fallback runs them. A second entry at the same level says that they were claimed but the dashboard could not be told; they run. At `Error`: some rows the claim had taken could not be put back either; the scheduler puts them back later, as above. |
| `SharpTicker.EntityFrameworkCore`, `SharpTicker.MongoDB` | Warning | The fallback could not mark the delayed chain children whose parent ended with an outcome they do not run on as `Skipped`. The fallback still takes the other overdue jobs, and the next pass tries again. |

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
