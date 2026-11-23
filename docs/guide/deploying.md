# Deploying

## Several instances

Instances that use the same shared store ([EF Core, MongoDB or Redis](storage.md)) work as one scheduler. When a job becomes due, each instance that sees it tries to claim it with a conditional update on the stored record. The instance whose update lands marks itself as the lock holder and runs the job; the others leave it.

A job can still run more than once in some cases. [When a job can run more than once](failure-modes.md#when-a-job-can-run-more-than-once) lists the ones we know of. Write jobs so that a second run does no harm.

## Instance names

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

Stable means that the instance has the same name after it is replaced. The pod name of a Kubernetes StatefulSet and a fixed name per replica are; the pod name of a Deployment is new for every pod, and so is the container id, which is the machine name inside a container. With the EF Core store and its lease turned off, the name decides whether the jobs of a stopped instance are released at all (see [When an instance stops](failure-modes.md#when-an-instance-stops)).

The name is the lock holder on a job, and a starting instance treats every job held under its own name as left behind by an earlier run (see [When an instance stops](failure-modes.md#when-an-instance-stops)). So two running instances with one name release each other's running jobs.

## Clocks

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

Jobs keep the identifier of the node that locked them, so the Lock holders panel of the dashboard lists every identifier that locked a job still in the store, not only the nodes that are running now (the one marked Current is this node). Each name shows its number of jobs and how long ago it last locked one, latest first, and with Redis whether the node has a heartbeat; see [Dashboard](dashboard.md#several-nodes). A stable name (see [Instance names](#instance-names)) keeps that list short.

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

## Kubernetes

SharpTicker has no Kubernetes integration of its own. These notes apply what the sections above and [Failure handling](failure-modes.md) describe to a cluster.

### Node names

Run the scheduling pods as a StatefulSet and pass the pod name in, so that a replaced pod keeps its name (see [Instance names](#instance-names)):

```yaml
env:
  - name: POD_NAME
    valueFrom:
      fieldRef:
        fieldPath: metadata.name
```

- A StatefulSet starts the replacement of a pod after the old pod with that name has gone, so two running pods do not share a name. A pod that is force-deleted while its process still runs breaks that: with EF Core or Redis, the new pod's startup release frees the jobs the old one is still running (see [When a job can run more than once](failure-modes.md#when-a-job-can-run-more-than-once)).
- A pod that comes back under its name releases what its earlier run left at its start, and those jobs run again (see [When an instance stops](failure-modes.md#when-an-instance-stops)).
- With a Deployment every pod has a new name. The jobs of a pod that was killed are then recorded as `Cancelled` by the EF Core lease, or released by the Redis heartbeat release with Redis as the job store. With MongoDB, and with EF Core when its lease is off, they are not released.
- Pods that only add jobs, such as an API, can run as a Deployment with `DisableBackgroundServices()` (see [Start modes](#start-modes)).

### Stopping a pod

When a pod is stopped, the host stops and the scheduler waits for the running jobs for a little less than `HostOptions.ShutdownTimeout` (30 seconds by default); see [Cancellation](jobs.md#cancellation). Kubernetes kills the process when `terminationGracePeriodSeconds` (also 30 seconds by default) ends. Keep the grace period above the shutdown timeout. A process that is killed before the scheduler has recorded its running jobs leaves them `InProgress`, and they are handled as for an instance that stopped without a signal.

### Probes

SharpTicker registers no health check. Points to consider for your own probes:

- When the store cannot be reached at startup, the host fails to start and the process ends (see [Start modes](#start-modes)). The restart policy of the pod then starts it again.
- An outage of the store while the instance runs does not stop it. A liveness probe that restarts the pod during such an outage ends the jobs that are still running, and a pod that restarts while the store cannot be reached does not start.

### Lease and heartbeat settings

- With EF Core, set the same `SetInProgressLease` on every pod that runs the background services, or turn it off on all of them (see [Lease on running jobs](storage.md#lease-on-running-jobs)). A pod that is paused, or cut off from the database, for longer than the lease has its jobs recorded as `Cancelled` by the others.
- With Redis as the job store, `NodeHeartbeatInterval` sets how soon the others release the jobs of a pod that stopped: about two heartbeat TTLs (each the interval plus 20 seconds). With Redis as the job store, a pod that cannot write its heartbeat for one TTL asks its jobs to stop; `StopJobsWhenHeartbeatIsLost` turns that off (see [When an instance stops](failure-modes.md#when-an-instance-stops)).
- Keep the clocks of the nodes in sync (see [Clocks](#clocks)).

### Rolling upgrades

A rolling update runs pods of the old and the new version side by side for a while. An instance of an earlier version behaves as that version did, and [Upgrading](upgrading.md) names the version mixes in which a job can run twice, or not at all. Upgrade all pods together where you can, for example by scaling the scheduling pods down to zero and up again.

## While older instances still run

While instances from before a switch to SharpTicker still run against the store (see [Migrating](https://github.com/msx752/SharpTicker/blob/main/MIGRATING.md#switching-instances)), the sections above do not describe them: a job can run twice until the last of them is gone. An instance of an earlier SharpTicker version behaves as that version did; see [Upgrading](upgrading.md) for what to check.
