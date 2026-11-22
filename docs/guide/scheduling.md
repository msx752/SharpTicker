# Scheduling

SharpTicker has two kinds of jobs:

- **Time tickers** (`TimeTickerEntity`) run once, at a given time.
- **Cron tickers** (`CronTickerEntity`) run repeatedly on a cron expression. Each run is stored as an occurrence.

Both are managed through injectable managers: `ITimeTickerManager<TimeTickerEntity>` and `ICronTickerManager<CronTickerEntity>`. Their `AddAsync`, `UpdateAsync` and `DeleteAsync` methods, and the `*BatchAsync` variants, return a `TickerResult<T>`. A failed result carries the reason in `Exception`. `UpdateAsync` of a ticker that does not exist fails with a `TickerValidatorException` and creates nothing, on every store; use `AddAsync` to create one. A `*BatchAsync` call with a null or empty list does nothing and succeeds, and a null ticker inside a list is a failed result.

## Time jobs

```csharp
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Interfaces.Managers;

public class OrderService(ITimeTickerManager<TimeTickerEntity> tickers)
{
    public Task ScheduleReminderAsync(Guid orderId) =>
        tickers.AddAsync(new TimeTickerEntity
        {
            Function = "SendReminder",
            ExecutionTime = DateTime.UtcNow.AddHours(24),
            Description = $"Reminder for order {orderId}",
            Request = TickerHelper.CreateTickerRequest(orderId),
            Retries = 3,
            RetryIntervals = [30, 60, 300]
        });
}
```

`ExecutionTime` rules, on add and on update (`UpdateAsync`, `UpdateBatchAsync`):

- Empty means "as soon as possible".
- A UTC value is used as it is, and a `DateTimeKind.Local` value is converted to UTC.
- A value with `DateTimeKind.Unspecified` is read in the scheduler time zone (see [Time zone](#time-zone)). Prefer UTC values to avoid surprises.

The built-in stores return the times they hold as UTC values (the EF Core store too, unless your context converts its times itself; see [Schema and tables](storage.md#schema-and-tables)). So a job read from such a store and given back to `UpdateAsync` keeps its time.

## Cron jobs

The simplest way is a cron expression on the attribute:

```csharp
[TickerFunction("SyncCatalog", "0 */15 * * * *")] // every 15 minutes, at second 0
public Task SyncCatalog(CancellationToken cancellationToken) => Task.CompletedTask;
```

Attribute crons are created in the store when the application starts, and are recognized again on later starts instead of being duplicated. To skip this seeding, call `IgnoreSeedDefinedCronTickers()` on the options builder.

To create crons at runtime, use the cron manager:

```csharp
await cronTickers.AddAsync(new CronTickerEntity
{
    Function = "SyncCatalog",
    Expression = "0 0 */2 * * *", // every two hours
    Retries = 2,
    RetryIntervals = [60]
});
```

Set `IsEnabled = false` and update the ticker to pause a cron without deleting it.

### Cron format

Expressions are parsed by [NCrontab](https://github.com/atifaziz/NCrontab) with seconds enabled. The format has **six fields**:

```text
┌───────────── second (0-59)
│ ┌─────────── minute (0-59)
│ │ ┌───────── hour (0-23)
│ │ │ ┌─────── day of month (1-31)
│ │ │ │ ┌───── month (1-12)
│ │ │ │ │ ┌─── day of week (0-6, Sunday = 0)
│ │ │ │ │ │
0 30 9 * * 1-5   → 09:30:00 on weekdays
```

Each field takes `*`, a value, a list (`1,15`), a range (`9-17`), and a step (`*/15`, `0-30/5`). Month and day of week also accept names in any case, for example `MON-FRI`, `Jan,Jul` or `Sunday`; a name can be shortened to its first letters (`MON`, `Sept`). The source generator checks the same rules at build time, so a cron it rejects is reported as a compile error instead of failing at startup. A range has to run forward: the source generator, the cron manager and the dashboard refuse a backwards one such as `22-2`. To cover hours that cross midnight, list two ranges, for example `22-23,0-2`.

A five-field expression without seconds is accepted on `[TickerFunction]` and by `CronExpression.Parse`, which add `0` seconds in front. A five-field value read from configuration through a `%Key%` placeholder gets the seconds in front as well, and a value there that is not a cron expression stops the startup with an error that names the function and the configuration key. Expressions given directly to `CronTickerEntity.Expression` through the cron manager are not expanded, so use six fields there.

### Time zone

Cron expressions are evaluated in the scheduler time zone, which is **the server's local time zone** (`TimeZoneInfo.Local`) by default. Set it explicitly so the schedule does not depend on where the application runs:

```csharp
builder.Services.AddSharpTicker(options =>
{
    options.ConfigureScheduler(scheduler =>
    {
        scheduler.SchedulerTimeZone = TimeZoneInfo.Utc;
    });
});
```

Daylight saving changes are handled. When the clock jumps forward, an occurrence that falls into the skipped hour does not run; the schedule continues with the next valid occurrence.

### Missed runs after downtime

When the application starts after being down, cron occurrences that were due in the meantime are picked up. To skip occurrences that are older than a threshold instead, call `SkipStaleCronOccurrencesOnStartup()` (default threshold 5 seconds) or pass your own `TimeSpan`.

## Retries

`Retries` sets how many times a failed run is tried again. `RetryIntervals` holds the wait before each retry, in seconds. When it has fewer entries than `Retries`, the last value is reused. The current attempt is available as `TickerFunctionContext.RetryCount`.

## Chains

A time ticker can have child tickers that run depending on how the parent ended. Build a chain with the fluent builder:

```csharp
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Managers;

var chain = FluentChainTickerBuilder<TimeTickerEntity>
    .BeginWith(parent => parent
        .SetFunction("ImportFile")
        .SetRequest(fileId)
        .SetRetries(2, 30))
    .WithFirstChild(child => child
        .SetFunction("IndexImportedRows")
        .SetRunCondition(RunCondition.OnSuccess))
    .WithFirstGrandChild(grandChild => grandChild
        .SetFunction("NotifyImportDone")
        .SetRunCondition(RunCondition.OnAnyCompletedStatus))
    .WithSecondChild(child => child
        .SetFunction("ReportImportFailure")
        .SetRunCondition(RunCondition.OnFailureOrCancelled))
    .Build();

await tickers.AddAsync(chain);
```

A chain has up to five children per parent and up to five grandchildren per child. `RunCondition` values:

| Value | The child runs when the parent |
|---|---|
| `OnSuccess` | succeeded |
| `OnFailure` | failed |
| `OnCancelled` | was cancelled |
| `OnFailureOrCancelled` | failed or was cancelled |
| `OnAnyCompletedStatus` | finished in any way except being skipped |
| `InProgress` | starts; the child runs in parallel with it |

A child stored without a condition runs as `OnAnyCompletedStatus`. A skipped parent runs none of its children, and a child that does not run is recorded as `Skipped` together with everything below it.

When the application stops while a parent runs, the parent is recorded as `Cancelled`. Its `OnCancelled`, `OnFailureOrCancelled` and `OnAnyCompletedStatus` children then run, and its `OnSuccess` and `OnFailure` children are skipped. A deploy can therefore start the chains you set up for cancellations. The same holds for a parent that the EF Core lease records as `Cancelled` because the instance that ran it stopped renewing its lock (see [Lease on running jobs](storage.md#lease-on-running-jobs)).

### Delayed children

A child or grandchild can have an execution time of its own (`SetExecutionTime` on the child or grandchild builder). Such a **delayed child** follows the same rules as any other child, and in addition does not start before its own time:

- It runs only when its parent has ended with a status its `RunCondition` accepts, and not before its own execution time. If that time has already passed when the parent ends, it runs right after the parent.
- When the parent ends with a status its condition does not accept, it is skipped with everything below it.
- An `InProgress` delayed child runs at its time once its parent has started, whatever the parent's outcome. It is skipped only when the parent was skipped.
- A delayed grandchild depends on its own parent (the child), not on the root of the chain.
- It is claimed like any other job, also with several nodes, and once it has ended a restart does not start it again. The cases in [When a job can run more than once](failure-modes.md#when-a-job-can-run-more-than-once) apply to it as to any job. A child that is given a time while its parent runs becomes a delayed child: the parent's run does not start it any more. A child that has already started keeps the status of its own run.

A child's or grandchild's time follows the same `ExecutionTime` rules as the root's, on add and when the update of the root carries it: a UTC value is used as it is, a `DateTimeKind.Local` value is converted to UTC, and an `Unspecified` one is read in the scheduler time zone. When a chain is added through the dashboard API, a `timeZoneId` applies to every time in the chain.

When a parent ends, the node that ran it at once starts the delayed children that may run and whose time has come, and wakes its scheduler for the next one due later. An `InProgress` delayed child is handled the same way when its parent starts. If that node stops before this, for example when the application stops while the parent runs, the fallback starts them after the restart, up to `FallbackIntervalChecker` later. A delayed child started right after its parent ends as `Done`, one started by the fallback as `DueDone`; a `RunCondition` treats both as success.

A child without a time of its own runs inside its parent's run and is recorded as `InProgress` while it runs, so an `InProgress` delayed grandchild below it can start as soon as that child starts. Its start is a claim on its row: the parent's run starts it only while the row still waits (`Idle`, no time, not held), so a child that was given a time or already runs elsewhere is not started a second time, and a child whose claim fails is not run without its row.

A child whose start never came waits as `Idle`: its claim failed, the node stopped between the parent's end and the child's start, or the node's process ended without a graceful stop (a kill, a crash) while the child ran and its row was released with the node's other rows (EF Core and Redis, and MongoDB with Redis next to it for the heartbeat; see [When an instance stops](failure-modes.md#when-an-instance-stops)). The fallback takes such a child once its parent ended at least 30 seconds ago. When the parent's outcome lets it run, it runs as a job of its own, with its own concurrency slot and priority, and ends `DueDone`; otherwise it is recorded as `Skipped`, and what it carries follows on a later pass. So it starts 30 seconds plus up to one `FallbackIntervalChecker` after its parent's end. A child that starts with its parent (`RunCondition.InProgress`) runs on any outcome of a parent that started and is skipped only when the parent was skipped. Known limit: with MongoDB alone, which has no release step, a child whose node was killed while it ran stays `InProgress`. A graceful stop records the running child as `Cancelled`.

Store support: EF Core, MongoDB, Redis and the in-memory store behave as described. On Redis, each child is a record of its own; a chain that Redis stored with a version before 1.3.0 keeps its children inside the parent's record, see [Upgrading](upgrading.md). On Redis, the scheduler looks past at most 1024 delayed children that wait for their parent and are due before other jobs; with more of them, those other jobs are started by the fallback, up to `FallbackIntervalChecker` late.

## Seeding

A seeder is code that creates tickers when the application starts. Register it on the options builder:

```csharp
builder.Services.AddSharpTicker(options =>
{
    options.UseTickerSeeder(async (ICronTickerManager<CronTickerEntity> crons) =>
    {
        // Runs on every start: only add what is missing.
        // IsCatalogSyncScheduledAsync stands for your own check, e.g. a flag in your database.
        if (!await IsCatalogSyncScheduledAsync())
            await crons.AddAsync(new CronTickerEntity { Function = "SyncCatalog", Expression = "0 0 * * * *" });
    });
});
```

Overloads take a time seeder, a cron seeder, or both.

> [!WARNING]
> Seeders run on **every** application start, on every node. A seeder that always calls `AddAsync` creates a new ticker each time. Check first, or declare fixed crons on `[TickerFunction]` instead, which are matched to their existing record and not duplicated.
