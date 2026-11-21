# Storage

The store holds tickers, cron occurrences, run history and locks. Without configuration SharpTicker uses an **in-memory** store, which is lost when the process stops and cannot be shared between instances.

| Store | Package | Shared between instances |
|---|---|---|
| In-memory (default) | `SharpTicker` | No |
| Entity Framework Core | `SharpTicker.EntityFrameworkCore` | Yes |
| MongoDB | `SharpTicker.MongoDB` | Yes |
| Redis | `SharpTicker.Caching.StackExchangeRedis` | Yes |

## Entity Framework Core

Written for relational EF Core providers. Our tests run it on SQLite, on PostgreSQL (Npgsql) and on MySQL through the official provider, `MySql.EntityFrameworkCore`. Each occurrence of a cron is written through [FlexLabs.EntityFrameworkCore.Upsert](https://github.com/artiomchi/FlexLabs.Upsert), so the provider has to be one that library knows; see its documentation for the list. Providers other than the three above are not part of our test runs. With a provider that library does not know, the scheduler loop fails with `Database provider not supported yet!` and no cron runs. Up to 1.4.0 that happened on net8.0 with `MySql.EntityFrameworkCore` ([#91](https://github.com/msx752/SharpTicker/issues/91)). On MySQL a schema is a database: the store's `ticker` schema is a database named `ticker`, which the application's user has to be allowed to create and use. There are two ways to use it.

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

The EF Core tools (`dotnet ef migrations add`, `dotnet ef dbcontext info`) find this context in the application's services and create it with the options given here, so the first migration can be added without a design-time factory. `SharpTickerDbContext` is in the SharpTicker assembly, so name the project that holds the migrations, for example `db.UseNpgsql(connectionString, b => b.MigrationsAssembly("MyApp"))`; without it the tools stop with `Your target project 'MyApp' doesn't match your migrations assembly 'SharpTicker.EntityFrameworkCore'`. When the application also registers a context of its own, the tools find both and stop with `More than one DbContext was found`, so each command names the one it is for: `--context AppDbContext` or `--context SharpTickerDbContext`. Up to 1.5.2 the tools did not find the context in a project without migrations and stopped with `No DbContext was found in assembly ...` ([#89](https://github.com/msx752/SharpTicker/issues/89)).

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

### Lease on running jobs

`SetInProgressLease(TimeSpan)` sets how long a job that is `InProgress` stays locked to an instance that no longer renews its lock. The default is 5 minutes, which is the recommended value; `TimeSpan.Zero` turns the lease off. Other values are accepted from 1 second up to 30 days, and anything else is refused with an `ArgumentOutOfRangeException`. Keep the lease well above the time a renewal pass can take on your database: a pass that takes longer than the lease lets the other instances record jobs that are still running as `Cancelled`.

```csharp
options.AddOperationalStore(ef =>
{
    ef.UseApplicationDbContext<AppDbContext>(ConfigurationType.UseModelCustomizer);
    ef.SetInProgressLease(TimeSpan.FromMinutes(5)); // TimeSpan.Zero turns it off
});
```

With the lease on, every instance that runs the background services does this every fifth of the lease (every minute by default):

- It sets `LockedAt` to now on the jobs and cron occurrences it holds `InProgress` since it started. This includes a job that waits for a free slot of its function and a job whose outcome waits to be written again. `UpdatedAt` is not changed. A job that another instance released or took meanwhile is not taken back.
- It records the jobs and cron occurrences that are `InProgress` under another instance name and whose `LockedAt` is older than the lease as `Cancelled`, the way a graceful stop records a job that had not finished: `ExecutedAt` is the time of the pass, the exception message is "The lock of the instance that ran the job was not renewed for the lease.", and the lock holder and `LockedAt` are cleared. Such a job does not run again. A chain child that waits for it follows its run condition on a `Cancelled` parent: an `OnSuccess` or `OnFailure` child is skipped, an `OnCancelled`, `OnFailureOrCancelled` or `OnAnyCompletedStatus` child runs, and so does an `InProgress` child that had not started yet; an `InProgress` child that had started with its parent on that instance is `InProgress` under the same name and is recorded as `Cancelled` with it. For a cron, only that occurrence is cancelled; its next occurrences run at their own time. A row with no `LockedAt` or no lock holder is not changed this way. The log has one entry at `Information` with their number and the names that held them, in the category `SharpTicker.EntityFrameworkCore.InProgressLease`.

From 1.6.2 these jobs are recorded as `Cancelled`. Up to 1.6.1 the lease released them to `Idle`, as the startup release does, and the fallback ran them again, also when the instance that held them had been gone for days.

Set the same lease on every instance that runs the background services, and turn it off on all of them or on none:

- An instance with the lease off does not renew its locks, so an instance with the lease on records the jobs it runs for longer than the lease as `Cancelled` while they still run there, and their outcome is not stored.
- With different values, an instance with a shorter lease records the jobs of the others as `Cancelled` earlier than their longer lease expects.

An instance does not cancel its own jobs this way, and jobs an earlier run under its own name left are released by the startup release, as before, and run again. A host that does not run the background services (`DisableBackgroundServices()`) neither renews nor cancels. The lease runs with Redis next to the store too; see [When an instance stops](operations.md#when-an-instance-stops) for how it works with the heartbeat release, and for its limits.

For a job that is `InProgress`, the dashboard's "Locked At" shows when the lock was last renewed, not when the job was claimed.

A [custom persistence provider](#custom-persistence-providers) that does not derive from the EF Core provider of SharpTicker has no lease; the log says so once at `Information`.

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
- Redis Cluster is not supported as the job store yet ([#225](https://github.com/msx752/SharpTicker/issues/225)). The store reads several job records with one command, and a cluster refuses a command whose keys are in different hash slots, so the scheduler, the fallback and the job lists fail once they read more than one job. Use a standalone Redis, Sentinel or a managed primary/replica setup. From 1.6.4 a node with Redis as the job store writes one Error to its log when its connection is a cluster (see [Scheduler log entries](operations.md#scheduler-log-entries)). With EF Core or MongoDB as the job store, Redis next to it is used with single-key calls only (the heartbeat, the node registry, the cron list), which a cluster serves, and no such Error is written.
- Each node sends a heartbeat every `NodeHeartbeatInterval` (default one minute). Jobs held by a node that stopped sending heartbeats are released by another running instance, about two heartbeat TTLs (each `NodeHeartbeatInterval` plus 20 seconds) after its last heartbeat; an outage of Redis itself does not release the nodes that are still running. With Redis as the job store, a node that starts again under the same name releases the jobs its earlier run left locked (see [Operations](operations.md)). Since 1.5.0, with Redis as the job store, a node that could not write its own heartbeat for one TTL asks its running jobs to stop and starts none until it can; `StopJobsWhenHeartbeatIsLost` (default `true`) turns that off (see [Operations](operations.md#when-an-instance-stops)).
- With the EF Core store and Redis next to it, the list of cron expressions the scheduler plans from is kept in Redis for up to ten minutes after it is read. It is cleared when a cron is added, changed or removed through the managers or the dashboard, and when an application seeds the crons defined in its code at startup. Up to 1.4.0 the entry had no lifetime and the seed did not clear it: a cron whose expression was changed in code kept its earlier schedule after a deploy, a cron that was new in code was missing from the list and one removed from the code stayed in it, also after every node was restarted, until a cron was added, changed or removed ([#93](https://github.com/msx752/SharpTicker/issues/93)). If Redis does not answer when the seed clears the entry, the application starts all the same and logs a warning; the earlier list is then used until the entry ends. A cron row changed directly in the database is seen when the entry ends. An entry written by 1.4.0 or earlier has no end: it stays until the seed of a later version clears it or a cron is added, changed or removed, and a node that still runs such a version writes it again without an end.
- Each child and grandchild of a chain is a record of its own, linked to its parent. Upgrade all nodes together: a version before 1.3.0 reads only chains that hold their children inside the parent's record (see [Delayed children](scheduling.md#delayed-children)).
- Since 1.4.0 the Redis store keeps one more key, the hash `tq:tt:waiting-children` (chain children without a time that wait for their start). The first fallback pass of each process fills it from the stored jobs, so nothing has to be done at an upgrade. An instance of an earlier version neither writes nor reads it.
- Ticker types of the application's own need no setting for the serializer: the store reads a type it does not know through reflection. Up to 1.4.0 adding a job of such a type failed with a `NotSupportedException` unless `JsonSerializerContext` was set ([#162](https://github.com/msx752/SharpTicker/issues/162)).
- For trimmed or AOT applications, where the serializer has reflection switched off, set `JsonSerializerContext`. With ticker types of the application's own, the context names the time type, the cron type and `CronTickerOccurrenceEntity<TCron>` with `[JsonSerializable]`. The members the entities fill themselves (`Status`, `LockedAt` and others) have setters a context generated in the application's assembly cannot reach: the build reports each of them with the warning `SYSLIB1038`, and the store sets them itself when it reads a record. Up to 1.4.0 it did not, and with such a context the scheduler loop and the fallback failed on reading the jobs ([#162](https://github.com/msx752/SharpTicker/issues/162)). A record written without a context is read with one, and the other way round.
- A custom entity can add properties. Up to 1.5.1 the store changed a record with a script (a claim, a release, a skip) that rewrote it with the JSON library of Redis, and that rewrite stored an empty collection as an empty object; records written that way stay so until their next write. Up to 1.4.0 a record with an empty collection of the application's own type could not be read after such a rewrite ([#146](https://github.com/msx752/SharpTicker/issues/146)): a time job in that state was not handed out, and an occurrence of a cron type with such a collection ran without its outcome being stored. From 1.5.0 (#146) the record is read with that collection empty, whatever the property is called and also inside a list or a dictionary, with the exceptions below. What remains:
  - The record is read once more for each such empty object, on every read, until the application side writes it again. A record with many of them (a list of thousands of empty lists) is slow to read.
  - A property typed `object`, `JsonElement` or `JsonNode` that held an empty list comes back as an empty object.
  - A collection under a property name or dictionary key that contains `']`, or anything below such a name, is not read this way; the record stays unreadable and is logged with its key.
  - The scripts of versions up to 1.5.1 restored `Children` and `RetryIntervals` under those exact (PascalCase) names, at any depth. An empty dictionary of your own under one of these names came back as an empty list and cannot be read. Avoid it with Redis.
- The store's claims, releases and skips write a record with the serializer that wrote it, so the numbers in a property of a custom entity are not changed by them. Up to 1.5.1 a number kept 14 significant digits ([#151](https://github.com/msx752/SharpTicker/issues/151)): the store's scripts read a record and wrote it back with the JSON library of Redis, which holds a number as a double and writes it with 14 digits. A `long` of 15 digits or more, such as a tick count, a snowflake id or `long.MaxValue`, was written back in exponent form (`1e+14`), which is not read as a `long`, so the job did not run; a `double` with more than 14 significant digits was rounded. What remains:
  - A record that a version up to 1.5.1 already rewrote in exponent form is not repaired, because its digits are lost. It is left out of the reads and named in a warning in the log with its key (`tq:tt:<id>`); delete the job, or update it with a value.
  - With several instances, upgrade all of them: an instance still on 1.5.1 rewrites a record when it claims it.
- A property of a custom entity needs a type the serializer can read back. A property typed as an abstract class, without polymorphism configured, is written and then refused on read: a time job that holds a value there is added without an error, its record cannot be read, and it does not run. Up to 1.4.0 one such record made every read that loaded it throw, and in a running application the scheduler loop, the fallback and the job lists stopped for all jobs ([#150](https://github.com/msx752/SharpTicker/issues/150)). From 1.5.0 the record is left out of the read and logged as a warning with its key, and the other jobs run. The warning comes again with each read of the record, which includes the fallback's passes once the job is overdue; the record is left as it is. Up to 1.5.1 the fallback took the record then, could not hand it out, and it was stored as `InProgress`. To end it, delete the job, or update it with a value that can be read back. An update of a time job whose entity holds such a value fails with the serializer's exception.

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
