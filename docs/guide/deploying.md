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

Stable means that the instance has the same name after it is replaced. The pod name of a Kubernetes StatefulSet and a fixed name per replica are; the pod name of a Deployment is new for every pod, and so is the container id, which is the machine name inside a container. With the EF Core store and its lease turned off, and no Redis, the name decides whether the jobs of a stopped instance are released at all (see [When an instance stops](failure-modes.md#when-an-instance-stops)).

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

The default `NodeIdentifier` is the machine name, which in a container is the container id and changes with every new container. Jobs keep the identifier of the node that locked them, so the Lock holders panel of the dashboard lists every identifier that locked a job still in the store, not only the nodes that are running now (the one marked Current is this node). Each name shows its number of jobs and how long ago it last locked one, latest first, and with Redis whether the node has a heartbeat; see [Dashboard](dashboard.md#several-nodes). Set a stable name, such as the pod name of a StatefulSet or a fixed name per service, to keep that list short, and, with the EF Core lease turned off, so that jobs left running by a node that stopped can be released.

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

## While older instances still run

While instances from before a switch to SharpTicker still run against the store (see [Migrating](https://github.com/msx752/SharpTicker/blob/main/MIGRATING.md#switching-instances)), the sections above do not describe them: a job can run twice until the last of them is gone. An instance of an earlier SharpTicker version behaves as that version did; see [Upgrading](upgrading.md) for what to check.
