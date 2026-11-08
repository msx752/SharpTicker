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

`ExecutionTime` rules:

- Empty means "as soon as possible".
- A UTC value is used as it is, and a `DateTimeKind.Local` value is converted to UTC.
- A value with `DateTimeKind.Unspecified` is read in the scheduler time zone (see [Time zone](#time-zone)). Prefer UTC values to avoid surprises.

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

A five-field expression without seconds is accepted on `[TickerFunction]` and by `CronExpression.Parse`, which add `0` seconds in front. Expressions given directly to `CronTickerEntity.Expression` through the cron manager are not expanded, so use six fields there.

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

When the application stops while a parent runs, the parent is recorded as `Cancelled`. Its `OnCancelled`, `OnFailureOrCancelled` and `OnAnyCompletedStatus` children then run, and its `OnSuccess` children are skipped. A deploy can therefore start the chains you set up for cancellations.

### Delayed children

A child or grandchild can have an execution time of its own (`SetExecutionTime` on the child or grandchild builder). Such a **delayed child** follows the same rules as any other child, and in addition does not start before its own time:

- It runs only when its parent has ended with a status its `RunCondition` accepts, and never before its own execution time. If that time has already passed when the parent ends, it runs right after the parent.
- When the parent ends with a status its condition does not accept, it is skipped with everything below it.
- An `InProgress` delayed child runs at its time once its parent has started, whatever the parent's outcome. It is skipped only when the parent was skipped.
- A delayed grandchild depends on its own parent (the child), not on the root of the chain.
- It runs once, on one node, also with several nodes and across restarts. A child that has already started keeps the status of its own run.

Pass the child's time in UTC.

When a parent ends, the node that ran it at once starts the delayed children that may run and whose time has come, and wakes its scheduler for the next one due later. An `InProgress` delayed child is handled the same way when its parent starts. If that node stops before this, for example when the application stops while the parent runs, the fallback starts them after the restart, up to `FallbackIntervalChecker` later. A delayed child started right after its parent ends as `Done`, one started by the fallback as `DueDone`; a `RunCondition` treats both as success.

A child without a time of its own runs inside its parent's run and is recorded as `InProgress` while it runs, so an `InProgress` delayed grandchild below it starts while it runs. Known limit: if the node stops while such a child runs after its parent has ended, the child stays `InProgress` and is not run again.

Store support: EF Core, MongoDB and the in-memory store behave as described. On Redis, children are stored inside the parent's record, so a delayed child runs right after its parent, by its `RunCondition`, and its own time is ignored. Redis follows in a later release.

Upgrade all nodes together: a node on an older version hands out delayed children without waiting for their parent. Delayed children stored before the upgrade follow these rules from then on: one whose parent has not ended waits for it, and one whose parent has already ended with a status its condition does not accept is recorded as `Skipped` once its time has passed.

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
