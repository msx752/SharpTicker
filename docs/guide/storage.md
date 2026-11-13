# Storage

The store holds tickers, cron occurrences, run history and locks. Without configuration SharpTicker uses an **in-memory** store, which is lost when the process stops and cannot be shared between instances.

| Store | Package | Shared between instances |
|---|---|---|
| In-memory (default) | `SharpTicker` | No |
| Entity Framework Core | `SharpTicker.EntityFrameworkCore` | Yes |
| MongoDB | `SharpTicker.MongoDB` | Yes |
| Redis | `SharpTicker.Caching.StackExchangeRedis` | Yes |

## Entity Framework Core

Works with any relational EF Core provider, such as SQL Server, PostgreSQL, SQLite or MySQL. There are two ways to use it.

### A separate DbContext

SharpTicker brings its own `SharpTickerDbContext`:

```csharp
using Microsoft.EntityFrameworkCore;
using SharpTicker.DependencyInjection;
using SharpTicker.EntityFrameworkCore.DbContextFactory;
using SharpTicker.EntityFrameworkCore.DependencyInjection;

builder.Services.AddSharpTicker(options =>
{
    options.AddOperationalStore(ef =>
    {
        ef.UseSharpTickerDbContext<SharpTickerDbContext>(db =>
            db.UseNpgsql(builder.Configuration.GetConnectionString("Scheduler")));
    });
});
```

### Your own DbContext

To keep the tables in your application's database and migrations, map them into your own context:

```csharp
using SharpTicker.EntityFrameworkCore.Customizer;

options.AddOperationalStore(ef =>
{
    ef.UseApplicationDbContext<AppDbContext>(ConfigurationType.UseModelCustomizer);
});
```

With `ConfigurationType.UseModelCustomizer` the tables are added to your model automatically. With `IgnoreModelCustomizer`, apply (from `SharpTicker.EntityFrameworkCore.Configurations`) `TimeTickerConfigurations<TimeTickerEntity>`, `CronTickerConfigurations<CronTickerEntity>` and `CronTickerOccurrenceConfigurations<CronTickerEntity>` yourself in `OnModelCreating`.

### Schema and tables

Tables are created in the `ticker` schema: `TimeTickers`, `CronTickers` and `CronTickerOccurrences`. Change the schema with `ef.SetSchema("...")`. Create the tables with EF Core migrations, or with `EnsureCreated()` / `GenerateCreateScript()` when you do not use migrations.

Other options: `SetDbContextPoolSize(int)` (default 1024).

## MongoDB

```csharp
using SharpTicker.MongoDB.DependencyInjection;

options.AddOperationalStore(mongo =>
{
    mongo.UseSharpTickerMongoClient("mongodb://localhost:27017", "scheduler");
});
```

- `UseExistingMongoClient(databaseName)` reuses an `IMongoClient` that is already registered in dependency injection.
- One of the two is required; startup fails otherwise.
- Collections use the prefix `ticker_` (`ticker_TimeTickers`, `ticker_CronTickers`, `ticker_CronTickerOccurrences`). Change it with `SetCollectionPrefix("...")`.
- Indexes are created at startup.

## Redis

```csharp
using SharpTicker.Caching.StackExchangeRedis.DependencyInjection;

options.AddStackExchangeRedis(redis =>
{
    redis.Configuration = "localhost:6379";
});
```

- Set one of `Configuration`, `ConfigurationOptions`, `ConnectionMultiplexer` or `ConnectionMultiplexerFactory`; startup fails otherwise.
- `InstanceName` (default `sharpticker:`) is the prefix under which nodes report that they are alive. Job data uses its own `tq:*` keys.
- Each node sends a heartbeat every `NodeHeartbeatInterval` (default one minute). Jobs held by a node that stopped sending heartbeats are released by another running instance, about two heartbeat TTLs (each `NodeHeartbeatInterval` plus 20 seconds) after its last heartbeat; an outage of Redis itself does not release the nodes that are still running. With Redis as the job store, a node that starts again under the same name releases the jobs its earlier run left locked (see [Operations](operations.md)).
- Each child and grandchild of a chain is a record of its own, linked to its parent. Upgrade all nodes together: a version before 1.3.0 reads only chains that hold their children inside the parent's record (see [Delayed children](scheduling.md#delayed-children)).
- Since 1.4.0 the Redis store keeps one more key, the hash `tq:tt:waiting-children` (chain children without a time that wait for their start). The first fallback pass of each process fills it from the stored jobs, so nothing has to be done at an upgrade. An instance of an earlier version neither writes nor reads it.
- For trimmed or AOT applications, set `JsonSerializerContext`.
- A custom entity can add properties. Redis rewrites the stored record when a script changes it (a claim, a release, a skip), and that rewrite stores an empty collection as an empty object. Up to 1.4.0 a record with an empty collection of the application's own type could not be read after such a rewrite ([#146](https://github.com/msx752/SharpTicker/issues/146)): a time job in that state was not handed out, and an occurrence of a cron type with such a collection ran without its outcome being stored. With the fix for #146 the record is read with that collection empty, whatever the property is called and also inside a list or a dictionary, with the exceptions below. What remains:
  - The record is read once more for each such empty object, on every read, until the application side writes it again. A record with many of them (a list of thousands of empty lists) is slow to read.
  - A property typed `object`, `JsonElement` or `JsonNode` that held an empty list comes back as an empty object.
  - A collection under a property name or dictionary key that contains `']`, or anything below such a name, is not read this way; the record stays unreadable and is logged with its key.
  - The scripts restore `Children` and `RetryIntervals` under those exact (PascalCase) names, at any depth. An empty dictionary of your own under one of these names comes back as an empty list and cannot be read, and with a camelCase JSON context the records take the second read after every script write. Avoid both with Redis.
- A property of a custom entity needs a type the serializer can read back. A property typed as an abstract class, without polymorphism configured, is written and then refused on read: a time job that holds a value there is added without an error, its record cannot be read, and it does not run. Up to 1.4.0 one such record made every read that loaded it throw, and in a running application the scheduler loop, the fallback and the job lists stopped for all jobs ([#150](https://github.com/msx752/SharpTicker/issues/150)). With the fix for #150 the record is left out of the read and logged as a warning with its key, and the other jobs run. The warning comes again with each read of the record, which includes the fallback's passes once the job is overdue; the fallback also takes the record then, cannot hand it out, and it is stored as `InProgress`. To end it, delete the job, or update it with a value that can be read back. An update of a time job whose entity holds such a value fails with the serializer's exception.

## Claiming jobs

With every built-in store a job is claimed per row by a conditional write that has one winner, and the job starts only if that write won. The regular claim needs the row to be `Queued` and held by the claiming node. The fallback takes `Idle` or `Queued` rows, and "run now" takes `Idle` or `Queued` rows that are unlocked or held by this node. "Run now" on several jobs or cron occurrences at once writes one row at a time, so a large selection costs one round trip for each. When such a call fails after it had claimed some rows, or after a write that may have landed before its reply was lost, it puts those rows back before it returns the error. The fallback does the same for the row whose claim write fails.

Every claim writes a marker into `UpdatedAt`: the instance's clock in whole milliseconds; an instance does not reuse a value. A claim whose job never starts is put back by a conditional write that matches this marker; see [A claim whose job does not start](operations.md#a-claim-whose-job-does-not-start).

## Custom persistence providers

`ITickerPersistenceProvider` has methods with default bodies, so a provider written for an earlier version still compiles. The defaults are not safe with several instances. Implement all of them:

| Methods | Default | Implement it so that |
|---|---|---|
| `TryMarkTimeTickerInProgress(id, claimMarker)`, `TryMarkCronOccurrenceInProgress(id, claimMarker)` | Writes `InProgress` without checking the current state, does not write the marker, and always returns `true`, so a job that another instance already took can run again. | The row changes only while it is `Queued` and held by this instance, `UpdatedAt` becomes `claimMarker`, and only the call that changed the row returns `true`. |
| `TryReleaseTimeTickerClaim(id, claimMarker)`, `TryReleaseCronOccurrenceClaim(id, claimMarker)` | Writes nothing and returns `false`, so a claim whose job never starts stays `InProgress`. | The row goes back to `Idle` with `LockHolder` and `LockedAt` cleared only while it is `InProgress`, held by this instance and its `UpdatedAt` equals `claimMarker`, and only a call that changed the row returns `true`. |
| `ReleaseDeadNodeTimeTickerResources(node, lockedBefore)`, `ReleaseDeadNodeOccurrenceResources(node, lockedBefore)` | Calls the overload without `lockedBefore`, which also releases the jobs this run has claimed since it started, so such a job can run twice. | Only rows locked before `lockedBefore` are released. |

`AcquireImmediateTimeTickersAsync` and `AcquireImmediateCronOccurrencesAsync` have no default, but the same contract: return each row this call claimed with the marker it wrote as `UpdatedAt`, and when the call fails after it claimed rows, put them back before it throws. Choose a row's marker before its write, so that a write that fails (it may have landed) is put back too. `QueueTimedOutTimeTickers` and `QueueTimedOutCronTickerOccurrences` hand back each row with the marker of its claim, and put back the row whose claim write fails before they throw.

The built-in stores also fence the writes of a running job by its claim marker and keep the run of a running row on a whole-object update (see [Writes of a running job](operations.md#writes-of-a-running-job) and [Updating a stored job](operations.md#updating-a-stored-job)). That goes through an internal interface, so a custom provider keeps writing by id: a late write of a run whose row was claimed again overwrites the new run, and an update of a running row can put its old state back.

A class that wraps a built-in provider must forward all of these methods, or it gets the defaults without any error. For the same reason, upgrade all SharpTicker packages together: a store package from an earlier version keeps the defaults.

## Choosing a store

- **In-memory** for development, tests and single-process tools where losing jobs on restart is acceptable.
- **EF Core** when you already run a relational database. With your own `DbContext` the tables live next to your data and in your migrations.
- **MongoDB** when your application already uses MongoDB.
- **Redis** for a fast, shared store when you already run Redis and accept its persistence model.
