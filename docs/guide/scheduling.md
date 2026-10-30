# Scheduling

SharpTicker has two kinds of jobs:

- **Time tickers** (`TimeTickerEntity`) run once, at a given time.
- **Cron tickers** (`CronTickerEntity`) run repeatedly on a cron expression. Each run is stored as an occurrence.

Both are managed through injectable managers: `ITimeTickerManager<TimeTickerEntity>` and `ICronTickerManager<CronTickerEntity>`. Their `AddAsync`, `UpdateAsync` and `DeleteAsync` methods, and the `*BatchAsync` variants, return a `TickerResult<T>`.

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
