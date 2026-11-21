# Failure handling

## When an instance stops

An instance that stops while it holds jobs leaves them locked. What happens to them depends on the store:

| Store | What happens to the jobs |
|---|---|
| EF Core | Released when an instance starts again under the same name. With Redis next to the store, also by another running instance once the heartbeat of the stopped one has ended. A released job runs again. Once the lease of a job has ended (5 minutes without a renewal by default), another running instance records it as `Cancelled` instead, and it does not run again. |
| Redis | Released when the instance starts again, or by another running instance once the heartbeat of the stopped one has ended. A released job runs again. |
| MongoDB | Not released when MongoDB is used alone: it has no release step, so a job left `InProgress` by a process that ended stays so until you change it. With Redis next to it for the heartbeat, another running instance releases the jobs of a node once its heartbeat has ended, and a released job runs again; the self-fence does not apply to MongoDB, so a node that is only cut off from Redis runs on, and the job can run twice at the same time ([#223](https://github.com/msx752/SharpTicker/issues/223)). |

**By another instance (EF Core lease).** Every instance that runs the background services renews the lock of the jobs it holds every fifth of the lease, and records the `InProgress` jobs of other instance names whose lock was not renewed for the whole lease as `Cancelled`, the way a graceful stop records a job that had not finished. Such a job does not run again. So a job of an instance that ended without a signal (killed, out of memory, a lost machine) ends as `Cancelled` whatever name the next instance has, also when that instance has been gone for days. The lease is set with `SetInProgressLease` (see [Storage](storage.md#lease-on-running-jobs)). With the default of 5 minutes:

| Time | What happens |
|---|---|
| 0 | The instance that holds the job renews its lock for the last time, and stops. |
| 5 min | The lock is older than the lease. |
| up to 6 min | The next pass of another instance (every minute) records the job as `Cancelled`, and with it an `InProgress` chain child that had started with it on that instance. For a cron, only that occurrence is cancelled; its next occurrences run at their own time. |
| up to 6.5 min | The fallback of an instance (every `FallbackIntervalChecker`, 30 seconds by default) runs or skips a delayed chain child waiting for that job, once its own time has come, by its run condition on a `Cancelled` parent: an `OnCancelled`, `OnFailureOrCancelled` or `OnAnyCompletedStatus` child runs, and so does an `InProgress` child that had not started yet, an `OnSuccess` or `OnFailure` child is skipped. A child without a time of its own is decided this way 30 seconds after the cancel at the earliest. |

What this does not cover:

- An instance that is still running but cannot renew for the whole lease has its jobs recorded as `Cancelled` by the others while they may still run on it: an instance cut off from the database alone, or a process that was paused for that long (a suspended virtual machine). The lease does not start the job again; the late outcome of the run on that instance is not stored, and its log says so.
- When the database cannot be reached by any instance, no instance renews and no instance cancels. After an outage longer than the lease, the next pass may record jobs that are still running as `Cancelled`, before their instance renews them.
- `LockedAt` is written with the clock of the instance that renews and compared with the clock of the instance that cancels, so a difference between the clocks shortens or lengthens the lease by that much (see [Clocks](deploying.md#clocks)).
- A job that is `InProgress` with no `LockedAt` or no lock holder (not written by SharpTicker) is not changed by the lease.
- An instance with the lease off does not renew its locks, and an instance with a different lease cancels at its own lease. Set the same lease on every instance, or turn it off on all of them (see [Storage](storage.md#lease-on-running-jobs)).
- With Redis next to the store, the heartbeat release of a node Redis knows of comes about two heartbeat TTLs after the node stopped (see below), which with the default settings is before its lease ends. That release puts the jobs back to `Idle`, and they run again. The lease records as `Cancelled` what is left, such as the jobs under names that never wrote a heartbeat.
- With the lease turned off (`TimeSpan.Zero`) and no Redis, a job that is `InProgress` under a name no instance starts with again stays `InProgress`. To get such a job released, start an instance under that name. A time job can also be removed on the dashboard: Cancel is refused for a job no instance runs, and then offers Force delete.

A starting instance writes the names that hold `InProgress` jobs of other instances to its log once, as Information in the category `SharpTicker.EntityFrameworkCore.StartupRelease`: the ten names with the most jobs, each with its count, and how many more names there are. With several running instances the list also has the jobs the others are running now, which end as usual. Without Redis next to the store this entry is written with the lease on and off.

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

**A node that cannot write its heartbeat (Redis as the job store).**

> From 1.5.0: an instance of an earlier version runs its jobs on when it cannot write its heartbeat, so they can run twice after the release; see [Upgrading](upgrading.md).

A node that cannot reach Redis for its heartbeat is still running its jobs, and after about two TTLs the others release them and start them again. So the node acts first:

- One TTL after the last heartbeat it wrote (counted from the moment that write was sent), the node asks the jobs it is running to stop, through their cancellation token, and starts no job. It checks this every second. The log has one entry at `Error` that says so and how many jobs were asked.
- A job that ends then is not recorded as `Cancelled`, and your exception handler is not called for it. Its row is put back once Redis answers, unless another instance has released it by then, and the job runs again from the start. A job has to be safe to run twice for this, as for any release.
- A job that returns normally after it was asked has done its work: its outcome is stored as usual.
- A chain child that runs together with its parent (`RunCondition.InProgress`, no time of its own) is asked to stop like any other job. As for other jobs, its row is put back or released, and the child then starts again with its parent's next run or, if the parent has already ended, from the fallback once the parent ended at least 30 seconds ago.
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

- A job that ignores its cancellation token runs on. If the others release it meanwhile, its late outcome is not stored.
- A process that was paused for longer than that (a long garbage collection, a suspended virtual machine) does not notice in time.
- A run that has not written a heartbeat yet counts the TTL from its own start.
- A Redis failover that loses the node's last heartbeat write, or an eviction of the heartbeat key, shortens the time the others wait.
- With EF Core or MongoDB as the job store and Redis only for the heartbeat, none of this applies.

**A Redis outage that every instance sees.** No instance is released by another (each writes its heartbeat again before the others' confirmation ends), but every instance stops its own running jobs once the outage has lasted one TTL, and they run again afterwards. To keep the jobs running through such an outage, set `StopJobsWhenHeartbeatIsLost` to `false`; a node that alone cannot reach Redis can then have its running jobs started a second time.

```csharp
options.AddStackExchangeRedis(redis =>
{
    redis.Configuration = "...";
    redis.StopJobsWhenHeartbeatIsLost = false; // default: true
});
```

If the application stops while the node is in this state, a job that is still running is recorded as `Cancelled`, as any job still running at a stop is. The cancel button on a job that was already asked to stop this way does not change what happens to it: it is put back and runs again.

Apart from all this, a running instance puts back its own claims whose job never started, with every store; see [A claim whose job does not start](#a-claim-whose-job-does-not-start).

## How a job is claimed

A job goes to a worker only after a conditional write moved its row to `InProgress`. When several instances try to start the same due job, one of these writes lands. An instance whose write did not land logs that at `Information` level and does not run the job.

Which rows a claim can take depends on where the claim comes from:

| Claim | Rows it takes |
|---|---|
| Regular claim | `Queued` and held by this node. |
| Fallback for missed jobs | `Idle` or `Queued`. With EF Core and MongoDB the row can be held by any node; with Redis it is unlocked or held by this node. |
| "Run now" | `Idle` or `Queued`, unlocked or held by this node. |

A cron also gets one stored occurrence per time slot when several instances create it at the same time: EF Core and MongoDB through a unique index on the cron and the time, Redis through a key made from them. This is per stored cron; see the last entry of [When a job can run more than once](#when-a-job-can-run-more-than-once) for a cron that is stored twice.

## A claim whose job does not start

A claim whose job does not start is put back: the row returns to `Idle` without a lock holder, so the fallback can pick the job up.

This applies to the regular claim, the fallback and "run now", in these situations:

- The application stops while the job waits for its concurrency slot.
- The scheduler is stopped or refuses the job.
- A claim call fails after it had claimed some rows, or after its own write landed and its reply was lost.

The put-back is a conditional write that matches only this claim: the row is still `InProgress`, held by this instance, and its `UpdatedAt` is the marker the claim wrote. A row that another claim took, or whose job ran, does not match and stays as it is. When the failed write did not land, the put-back changes nothing either.

When the store cannot be reached, the instance tries the put-back again while it runs, and the job does not start in the meantime. Each attempt waits at most 5 seconds, and a pass stops at the first one that fails. At a stop, the put-backs use the time the host gives the stop (`HostOptions.ShutdownTimeout`); what has not been put back by then is logged as an error.

The put-back has these limits:

- A job whose put-back had not succeeded when the process ended stays `InProgress` until its store releases it: EF Core when an instance starts again under the same name (once the job's lease ends, another instance records it as `Cancelled` instead), Redis when the instance starts again or another instance sees its heartbeat stop, MongoDB only when Redis is next to it for the heartbeat and another instance sees the heartbeat stop.
- The marker is the instance's clock in whole milliseconds. An `UpdatedAt` column that keeps less than that, such as SQL Server `datetime` or MySQL `datetime` without fractional seconds, cannot hold it exactly, so a put-back may not find its claim. The default mappings (`datetime2`, `datetime(6)`) keep enough.
- A cron occurrence that was put back keeps its time slot, so it counts as missed: with `SkipStaleCronOccurrencesOnStartup` it is skipped when an instance starts later than the threshold.
- With a [custom persistence provider](storage.md#custom-persistence-providers) that keeps the default put-back methods, nothing is put back.

## "Run now" when the job cannot start

"Run now" (adding jobs that are due within a second) returns success once the jobs are stored, even when they cannot run now:

- on an instance whose scheduler is stopped,
- while the application stops,
- when the store fails while they are claimed.

The jobs that were claimed are put back, and all of them are left for the fallback of a running instance. A failure of the claim is logged (see [Scheduler log entries](observability.md#scheduler-log-entries)). Do not retry the add, or the jobs are added twice.

## Writes of a running job

With every built-in store, the writes of a running job are fenced by the marker its claim wrote. Each write of the run (a failed attempt, the next retry, the outcome) lands only while the row is still held by this instance with that marker, and keeps the marker in `UpdatedAt`.

So when a row was released and claimed again, by another instance or by this one, a late write of the earlier run does not overwrite the new run. It changes nothing and is logged as an outcome that was not stored.

Related details:

- A chain child without a time of its own is held the same way from its start, with the lock of the instance that runs it.
- Because the marker stays in `UpdatedAt` while the job runs, a finished time ticker shows its claim time there; `ExecutedAt` holds when it ended.
- A [custom persistence provider](storage.md#custom-persistence-providers) writes by id, without this fence.
- The marker is unique per instance name only within one process, so give every process its own `NodeIdentifier`. Two processes on one machine with the default machine name share a lock holder.

## Updating a stored job

`UpdateTimeTickers`, which the manager's update methods call, and the edit dialog of the dashboard update a job record with the whole object. With the built-in stores such an update leaves the run alone:

- A row that is `InProgress` keeps its run (its status, lock, marker, retry count, times and messages), and only the other fields are written. This applies to every row of the chain. A copy that still shows a run the store no longer has does not bring that run back.
- A job that has ended keeps its outcome the same way. A dialog opened before the job started and saved after it ran does not write the job back as not started. To run an ended job again, add a new job.
- A stored chain child also keeps its parent, and its run condition when the update carries none, so an update does not take a child out of its chain. To do that, delete the child and add a new job.
- The manager's update needs an `ExecutionTime`, with one exception: a stored child that has no time of its own can be updated without one and stays a child that its parent's run starts. A time is not taken from a job that has one.

A [custom persistence provider](storage.md#custom-persistence-providers) that writes every field does not have this; see [When a job can run more than once](#when-a-job-can-run-more-than-once).

## When an outcome cannot be stored

When the final write of a job (its outcome) fails, for example while the store cannot be reached, the job's row stays `InProgress`.

With a built-in store and the background services running, the scheduler writes that outcome again while the instance runs, fenced by the same marker:

- It writes right away, and then on every pass of the scheduler, at most 5 seconds apart, until the store takes it.
- Each attempt waits at most 5 seconds with EF Core and MongoDB. Redis is bounded by its client's own timeout.
- A pass stops at the first attempt that runs out of that time. An outcome the store refuses does not hold back the others.

The row then ends with the job's outcome, so nothing picks the job up again. If the row was released, released and claimed again, or recorded as `Cancelled` by the EF Core lease in the meantime, the write changes nothing and the outcome is dropped with a warning.

With a [custom persistence provider](storage.md#custom-persistence-providers), or with the background services disabled, a lost outcome is logged and not written again.

**Chain children.** The chain children of a job whose outcome was stored late follow it:

- The delayed ones follow as usual.
- The inline ones (those without a time of their own) that the outcome lets run are claimed from the store. Each then runs as a job of its own instead of inside its parent's run: with its own concurrency slot and priority, and it ends `Done` also when the fallback had started its parent.
- Those the outcome does not let run are skipped.
- An `InProgress` child, which started with its parent, is not started again.

An inline child is started this way only while the scheduler runs. When the outcome is stored by the last write at the stop, or the claim fails, or the claimed child is put back, its row stays `Idle` and the fallback takes it once its parent ended at least 30 seconds ago (see [Chains](scheduling.md#chains)). An error or a warning names such a child.

**When the scheduler stops.** The scheduler gives these outcomes one last write before it records the jobs that are still running as `Cancelled`. That `Cancelled` write is fenced too: it lands only on a row that this run still holds and that is still `InProgress`, so it does not replace an outcome the job stored, or the run of another instance. An outcome that is still not stored when the scheduler stops is logged as an error. If the scheduler is started again in the same process (`ISharpTickerHostScheduler`, or the dashboard), it writes that outcome again.

## When a job can run more than once

These are the cases we know of in which a job runs more than once, or twice at about the same time:

- **A process dies while a job runs.** The same happens when the job finishes, its final write to the store fails, and the process ends before the scheduler has written that outcome again (see [When an outcome cannot be stored](#when-an-outcome-cannot-be-stored)). The job's row stays `InProgress`, and after a restart (EF Core, Redis) or the heartbeat release (Redis) the job runs again, and a repeated parent repeats its children. The EF Core lease records such a job as `Cancelled` instead. The first run's outcome is not written over the second run. This is an at-least-once limit: a durable record of the outcome outside the store is not kept.
- **A Redis node is cut off alone.** A node that alone could not send heartbeats for about two heartbeat TTLs while the other instances kept reaching Redis has its `InProgress` jobs released. With Redis as the job store the node has asked its jobs to stop one TTL earlier (see [A node that cannot write its heartbeat](#when-an-instance-stops)), so what still runs at the release is a job that ignored its cancellation token or took longer than about one TTL to stop, or any job of a process that was paused. With `StopJobsWhenHeartbeatIsLost` off, or with another job store and the Redis heartbeat, every job of that node may still be running.
- **Two running instances share a name, with EF Core or Redis.** An instance that starts with the name of another running instance frees the jobs that instance is running, through its startup release.
- **The store loses a write it acknowledged.** For example MongoDB with a write concern of `w:1` after a primary change, or Redis after a failover to a replica that had not received the write.
- **A custom provider writes every field on an update.** With a [custom persistence provider](storage.md#custom-persistence-providers) that writes the whole object, a copy loaded before the claim puts the old `Status` and lock back, and the job can be claimed again. The built-in stores keep the run; see [Updating a stored job](#updating-a-stored-job).
- **The same cron is stored more than once.** Each stored cron creates its own occurrences. A cron declared on `[TickerFunction]` is stored under an id made from the function name, so instances that start together normally store it once. The exception is an id that a cron of another function already holds: the cron then gets a random id, and that start has no such protection. Crons added by a [seeder](scheduling.md#seeding), through the API or on the dashboard are not matched to an existing one, and none of the stores has a unique constraint on the function: adding the same cron twice, or from two instances, stores two. Check for duplicate crons after a first rollout, or declare the cron on `[TickerFunction]`.

Write jobs so that a second run does no harm: key side effects on the ticker id or on your own business key, and skip work that is already done.

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
