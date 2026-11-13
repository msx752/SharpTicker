# Running in production

## Several instances

Instances that use the same shared store ([EF Core, MongoDB or Redis](storage.md)) work as one scheduler. When a job becomes due, each instance that sees it tries to claim it with a conditional update on the stored record. The instance whose update lands marks itself as the lock holder and runs the job; the others leave it.

A job can still run more than once in some cases. [When a job can run more than once](#when-a-job-can-run-more-than-once) lists the ones we know of. Write jobs so that a second run does no harm.

### Instance names

Give every running instance a stable name that no other running instance uses:

```csharp
builder.Services.AddSharpTicker(options =>
{
    options.ConfigureScheduler(scheduler =>
    {
        scheduler.NodeIdentifier = Environment.GetEnvironmentVariable("POD_NAME") ?? Environment.MachineName;
    });
});
```

The default is the machine name, which is not unique when several containers share a host name pattern or run on one machine.

The name is the lock holder on a job, and a starting instance treats every job held under its own name as left behind by an earlier run (see [When an instance stops](#when-an-instance-stops)). So two running instances with one name release each other's running jobs.

### When an instance stops

An instance that stops while it holds jobs leaves them locked. What releases them for the other instances depends on the store:

| Store | The jobs are released |
|---|---|
| EF Core | When the instance starts again. |
| Redis | When the instance starts again, or by another running instance once the heartbeat of the stopped one has ended. |
| MongoDB | Not at all. MongoDB has no release step, so a job left `InProgress` by a process that ended stays so until you change it. |

**At startup (EF Core, Redis).** A starting instance releases every job held under its own name, `Queued` or `InProgress`, that was locked before this run started. Jobs the new run has claimed in the meantime are kept. With Redis the release is tried again every 30 seconds while it fails, for example while Redis cannot be reached.

**By another instance (Redis).**

- A heartbeat entry expires `NodeHeartbeatInterval` plus 20 seconds after the node's last heartbeat. That time is the heartbeat TTL.
- Another instance releases the node's jobs when every one of its checks over one more TTL found the entry missing. The checks are at most 10 seconds apart.
- A check that fails or takes longer than 10 seconds, or 20 seconds without a check, starts that TTL again.
- So the release comes about two TTLs after the node stopped.

What this release does not do:

- A running instance does not release the jobs of its own run.
- After an outage of Redis itself, the instances that are still running are not released by each other: each writes its heartbeat again before the others' confirmation ends. With Redis as the job store each of them has stopped its own running jobs if the outage lasted one TTL; see below.
- A restart under the same name before another instance released the node (about two TTLs) is not seen as a stop by the others. The restarted instance releases what its earlier run left, as described under "At startup".

**A node that cannot write its heartbeat (Redis as the job store, since 1.5.0).**

A node that cannot reach Redis for its heartbeat is still running its jobs, and after about two TTLs the others release them and start them again. So the node acts first:

- One TTL after the last heartbeat it wrote (counted from the moment that write was sent), the node asks the jobs it is running to stop, through their cancellation token, and starts no job. It checks this every second. The log has one entry at `Error` that says so and how many jobs were asked.
- A job that ends then is not recorded as `Cancelled`, and your exception handler is not called for it. Its row is put back once Redis answers, unless another instance has released it by then, and the job runs again from the start. A job has to be safe to run twice for this, as for any release.
- A job that returns normally after it was asked has done its work: its outcome is stored as usual.
- While the node is in this state the scheduler and the fallback claim nothing, and a job that was already claimed is put back instead of started.
- The next heartbeat that is written ends the state, and the log says so at `Information`.

With the default `NodeHeartbeatInterval` of one minute:

| Time | What happens |
|---|---|
| 0 s | The last heartbeat that was written is sent. |
| 80 s | The node asks its jobs to stop and starts none (one TTL). |
| 80 s at the earliest | Another instance first finds the entry missing. |
| 160 s at the earliest | Another instance releases the node's jobs (the entry stayed missing for one more TTL). |

So a job that stops within about one TTL of being asked does not run at the same time as its second run.

What this does not cover:

- A job that ignores its cancellation token runs on. If the others release it meanwhile, its late outcome is not stored, as before.
- A chain child that runs together with its parent (`RunCondition.InProgress`, no time of its own) is not asked to stop. Its parent is, and the child is not started a second time when the parent runs again. ([#158](https://github.com/msx752/SharpTicker/issues/158) is the reason: put back, such a child would be skipped.)
- A process that was paused for longer than that (a long garbage collection, a suspended virtual machine) does not notice in time.
- A run that has not written a heartbeat yet counts the TTL from its own start.
- A Redis failover that loses the node's last heartbeat write, or an eviction of the heartbeat key, shortens the time the others wait.
- With EF Core or MongoDB as the job store and Redis only for the heartbeat, none of this applies.

**A Redis outage that every instance sees.** No instance is released by another (each writes its heartbeat again before the others' confirmation ends), but since 1.5.0 every instance stops its own running jobs once the outage has lasted one TTL, and they run again afterwards. To keep the jobs running through such an outage, as before 1.5.0, set `StopJobsWhenHeartbeatIsLost` to `false`; a node that alone cannot reach Redis can then have its running jobs started a second time.

```csharp
options.AddStackExchangeRedis(redis =>
{
    redis.Configuration = "...";
    redis.StopJobsWhenHeartbeatIsLost = false; // default: true
});
```

If the application stops while the node is in this state, a job that is still running is recorded as `Cancelled`, as any job still running at a stop is. The cancel button on a job that was already asked to stop this way does not change what happens to it: it is put back and runs again.

Apart from all this, a running instance puts back its own claims whose job never started, with every store; see [A claim whose job does not start](#a-claim-whose-job-does-not-start).

### How a job is claimed

A job goes to a worker only after a conditional write moved its row to `InProgress`. When several instances try to start the same due job, one of these writes lands. An instance whose write did not land logs that at `Information` level and does not run the job.

Which rows a claim can take depends on where the claim comes from:

| Claim | Rows it takes |
|---|---|
| Regular claim | `Queued` and held by this node. |
| Fallback for missed jobs | `Idle` or `Queued`. With EF Core and MongoDB the row can be held by any node; with Redis it is unlocked or held by this node. |
| "Run now" | `Idle` or `Queued`, unlocked or held by this node. |

A cron also gets one stored occurrence per time slot when several instances create it at the same time: EF Core and MongoDB through a unique index on the cron and the time, Redis through a key made from them. This is per stored cron; see the last entry of [When a job can run more than once](#when-a-job-can-run-more-than-once) for a cron that is stored twice.

### A claim whose job does not start

A claim whose job does not start is put back: the row returns to `Idle` without a lock holder, so the fallback can pick the job up.

This applies to the regular claim, the fallback and "run now", in these situations:

- The application stops while the job waits for its concurrency slot.
- The scheduler is stopped or refuses the job.
- A claim call fails after it had claimed some rows, or after its own write landed and its reply was lost.

The put-back is a conditional write that matches only this claim: the row is still `InProgress`, held by this instance, and its `UpdatedAt` is the marker the claim wrote. A row that another claim took, or whose job ran, does not match and stays as it is. When the failed write did not land, the put-back changes nothing either.

When the store cannot be reached, the instance tries the put-back again while it runs, and the job does not start in the meantime. Each attempt waits at most 5 seconds, and a pass stops at the first one that fails. At a stop, the put-backs use the time the host gives the stop (`HostOptions.ShutdownTimeout`); what has not been put back by then is logged as an error.

The put-back has these limits:

- A job whose put-back had not succeeded when the process ended stays `InProgress` until its store releases it: EF Core when the instance starts again, Redis when the instance starts again or another instance sees its heartbeat stop, MongoDB not at all.
- The marker is the instance's clock in whole milliseconds. An `UpdatedAt` column that keeps less than that, such as SQL Server `datetime` or MySQL `datetime` without fractional seconds, cannot hold it exactly, so a put-back may not find its claim. The default mappings (`datetime2`, `datetime(6)`) keep enough.
- A cron occurrence that was put back keeps its time slot, so it counts as missed: with `SkipStaleCronOccurrencesOnStartup` it is skipped when an instance starts later than the threshold.
- With a [custom persistence provider](storage.md#custom-persistence-providers) that keeps the default put-back methods, nothing is put back.

### "Run now" when the job cannot start

"Run now" (adding jobs that are due within a second) returns success once the jobs are stored, even when they cannot run now:

- on an instance whose scheduler is stopped,
- while the application stops,
- when the store fails while they are claimed.

The jobs that were claimed are put back, and all of them are left for the fallback of a running instance. A failure of the claim is logged (see [Scheduler log entries](#scheduler-log-entries)). Do not retry the add, or the jobs are added twice.

### Writes of a running job

With every built-in store, the writes of a running job are fenced by the marker its claim wrote. Each write of the run (a failed attempt, the next retry, the outcome) lands only while the row is still held by this instance with that marker, and keeps the marker in `UpdatedAt`.

So when a row was released and claimed again, by another instance or by this one, a late write of the earlier run does not overwrite the new run. It changes nothing and is logged as an outcome that was not stored.

Related details:

- A chain child without a time of its own is held the same way from its start, with the lock of the instance that runs it.
- Because the marker stays in `UpdatedAt` while the job runs, a finished time ticker shows its claim time there; `ExecutedAt` holds when it ended.
- A [custom persistence provider](storage.md#custom-persistence-providers) writes by id as before.
- The marker is unique per instance name only within one process, so give every process its own `NodeIdentifier`. Two processes on one machine with the default machine name share a lock holder.

### Updating a stored job

`UpdateTimeTickers`, which the manager's update methods call, and the edit dialog of the dashboard update a job record with the whole object. With the built-in stores such an update leaves the run alone:

- A row that is `InProgress` keeps its run (its status, lock, marker, retry count, times and messages), and only the other fields are written. This applies to every row of the chain. A copy that still shows a run the store no longer has does not bring that run back.
- A job that has ended keeps its outcome the same way. A dialog opened before the job started and saved after it ran does not write the job back as not started. To run an ended job again, add a new job.
- A stored chain child also keeps its parent, and its run condition when the update carries none, so an update does not take a child out of its chain. To do that, delete the child and add a new job.
- The manager's update needs an `ExecutionTime`, with one exception: a stored child that has no time of its own can be updated without one and stays a child that its parent's run starts. A time is not taken from a job that has one.

A [custom persistence provider](storage.md#custom-persistence-providers) that writes every field does not have this; see [When a job can run more than once](#when-a-job-can-run-more-than-once).

### When an outcome cannot be stored

When the final write of a job (its outcome) fails, for example while the store cannot be reached, the job's row stays `InProgress`.

With a built-in store and the background services running, the scheduler writes that outcome again while the instance runs, fenced by the same marker:

- It writes right away, and then on every pass of the scheduler, at most 5 seconds apart, until the store takes it.
- Each attempt waits at most 5 seconds with EF Core and MongoDB. Redis is bounded by its client's own timeout.
- A pass stops at the first attempt that runs out of that time. An outcome the store refuses does not hold back the others.

The row then ends with the job's outcome, so nothing picks the job up again. If the row was released, or released and claimed again, in the meantime, the write changes nothing and the outcome is dropped with a warning.

With a [custom persistence provider](storage.md#custom-persistence-providers), or with the background services disabled, a lost outcome is logged and not written again.

**Chain children.** The chain children of a job whose outcome was stored late follow it:

- The delayed ones follow as usual.
- The inline ones (those without a time of their own) that the outcome lets run are claimed from the store. Each then runs as a job of its own instead of inside its parent's run: with its own concurrency slot and priority, and it ends `Done` also when the fallback had started its parent.
- Those the outcome does not let run are skipped.
- An `InProgress` child, which started with its parent, is not started again.

An inline child is started this way only while the scheduler runs. When the outcome is stored by the last write at the stop, or the claim fails, or the claimed child is put back, its row stays `Idle` and the fallback takes it once its parent ended at least 30 seconds ago (see [Chains](scheduling.md#chains)). An error or a warning names such a child.

**When the scheduler stops.** The scheduler gives these outcomes one last write before it records the jobs that are still running as `Cancelled`. That `Cancelled` write is fenced too: it lands only on a row that this run still holds and that is still `InProgress`, so it does not replace an outcome the job stored, or the run of another instance. An outcome that is still not stored when the scheduler stops is logged as an error. If the scheduler is started again in the same process (`ISharpTickerHostScheduler`, or the dashboard), it writes that outcome again.

### While older instances still run

While instances from before a switch to SharpTicker still run against the store (see [Migrating](https://github.com/msx752/SharpTicker/blob/main/MIGRATING.md#switching-instances)), the sections above do not describe them: a job can run twice until the last of them is gone. An instance of an earlier SharpTicker version behaves as that version did; see [Upgrading](upgrading.md) for what to check.

### When a job can run more than once

These are the cases we know of in which a job runs more than once, or twice at about the same time:

- **A process dies while a job runs.** The same happens when the job finishes, its final write to the store fails, and the process ends before the scheduler has written that outcome again (see [When an outcome cannot be stored](#when-an-outcome-cannot-be-stored)). The job's row stays `InProgress`, and after a restart (EF Core, Redis) or the heartbeat release (Redis) the job runs again, and a repeated parent repeats its children. The first run's outcome is not written over the second run. This is an at-least-once limit: a durable record of the outcome outside the store is not kept.
- **A Redis node is cut off alone.** A node that alone could not send heartbeats for about two heartbeat TTLs while the other instances kept reaching Redis has its `InProgress` jobs released. With Redis as the job store the node has asked its jobs to stop one TTL earlier (see [A node that cannot write its heartbeat](#when-an-instance-stops)), so what still runs at the release is a job that ignored its cancellation token or took longer than about one TTL to stop, a chain child that runs together with its parent, or any job of a process that was paused. With `StopJobsWhenHeartbeatIsLost` off, or with another job store and the Redis heartbeat, every job of that node may still be running.
- **Two running instances share a name, with EF Core or Redis.** An instance that starts with the name of another running instance frees the jobs that instance is running, through its startup release.
- **The store loses a write it acknowledged.** For example MongoDB with a write concern of `w:1` after a primary change, or Redis after a failover to a replica that had not received the write.
- **A custom provider writes every field on an update.** With a [custom persistence provider](storage.md#custom-persistence-providers) that writes the whole object, a copy loaded before the claim puts the old `Status` and lock back, and the job can be claimed again. The built-in stores keep the run; see [Updating a stored job](#updating-a-stored-job).
- **The same cron is stored more than once.** Each stored cron creates its own occurrences. A cron declared on `[TickerFunction]` is stored under an id made from the function name, so instances of 1.3.1 or later that start together normally store it once. The exception is an id that a cron of another function already holds: the cron then gets a random id, and that start has no such protection. Copies left by earlier versions are removed at startup with their occurrences, and the oldest is kept; this covers only copies that still carry the seed's marker. Crons added by a [seeder](scheduling.md#seeding), through the API or on the dashboard are not matched to an existing one, and none of the stores has a unique constraint on the function: adding the same cron twice, or from two instances, stores two. Check for duplicate crons after a first rollout, or declare the cron on `[TickerFunction]`.

Write jobs so that a second run does no harm: key side effects on the ticker id or on your own business key, and skip work that is already done.

### Clocks

Due times are compared against each instance's own clock. The scheduler picks a job up when it is due and treats it as missed one second later. Keep the clocks of all instances in sync with NTP.

With a difference of one second or more, an instance with a fast clock starts a job earlier than the others expect, and an instance with a slow clock finds the job already taken and does not run it. The claim itself works as described above either way: one conditional write lands.

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
| `SharpTicker.BackgroundServices.LostOutcomeDrain` | Warning | An outcome that could not be stored was not written again because its row was released or taken by another run meanwhile; the job may run again. A second entry at the same level says that the chain children of a stored outcome could not be followed up; the fallback runs the delayed ones the outcome lets run, and an inline one that is still `Idle` is not run. |
| `SharpTicker.BackgroundServices.LostOutcomeDrain` | Error, Critical | Outcomes could still not be stored 5 minutes after the oldest was lost; the entry names them (the first ten). It repeats every 5 minutes, at `Critical` once the oldest has waited 10 minutes. At `Error` too: an outcome that could not be stored before the scheduler stopped. |
| `SharpTicker.Utilities.Managers.InternalTickerManager` | Warning | An outcome written again, or a job recorded as `Cancelled` at the stop, was stored, but the dashboard could not be told. |
| `SharpTicker.EntityFrameworkCore.StartupRelease` | Error | Releasing the jobs that a dead node or an earlier run of this node left locked failed when the application started. |
| `SharpTicker.Caching.StackExchangeRedis.NodeHeartBeatBackgroundService` | Warning | With Redis as the job store: releasing the jobs an earlier run of this node left locked failed after the application started. It is tried again every 30 seconds; the release that succeeds is written at `Information`. A single job that could not be released is named in an entry of its own. |
| `SharpTicker.Caching.StackExchangeRedis.NodeHeartBeatBackgroundService` | Error | With Redis as the job store: this node could not write its heartbeat for one TTL. It asked its running jobs to stop (the entry says how many) and starts no job until a heartbeat is written again. One entry each time it happens. The failed heartbeat writes before it are at `Warning`, one for each try. |
| `SharpTicker.Caching.StackExchangeRedis.NodeHeartBeatBackgroundService` | Information | The heartbeat is written again; the node starts jobs again. |
| `SharpTicker.Caching.StackExchangeRedis.NodeHeartBeatBackgroundService` | Warning | A job that was asked to stop had registered a callback on its cancellation token, and the callback threw. The entry names the job. |
| `SharpTicker.TickerExecutionTaskHandler` | Warning | A job was asked to stop because its node could not write its heartbeat. No outcome is stored for that run; its row is put back once the store answers, unless another instance released it first, and the job runs again. |
| `SharpTicker.BackgroundServices.ClaimedWorkQueue` | Information | The row of a job that was stopped that way was put back, or had already moved on. |
| `SharpTicker.BackgroundServices.ClaimedWorkQueue` | Warning | A job that was already claimed was not started because its node could not write its heartbeat; it was put back. |
| `SharpTicker.TickerExecutionTaskHandler` | Warning | Starting the delayed chain children of a parent that ended (or, for `InProgress` children, started) failed, or waking the scheduler for one due later failed. The fallback runs them. At `Error`: the children were claimed but could not be dispatched, and they stay `InProgress` until their store releases the rows of this node. |
| `SharpTicker.TickerExecutionTaskHandler` | Warning, Error | An inline chain child (no time of its own) did not start after its parent's outcome was stored late. At `Warning`: it was not claimed, because its row was taken or changed meanwhile or the claim failed. At `Error`: the outcome was stored only while the scheduler stopped. The entry names the child and its parent. If the child's row is still `Idle`, the fallback takes it once its parent ended 30 seconds ago. |
| `SharpTicker.TickerExecutionTaskHandler` | Warning | An inline chain child was not started inside its parent's run because its start claim failed (the store could not be reached). The child is not run without its row; the row keeps waiting and the fallback takes it once its parent ended 30 seconds ago. |
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
