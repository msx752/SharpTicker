# Storage

The store holds tickers, cron occurrences, run history and locks. Without configuration SharpTicker uses an **in-memory** store, which is lost when the process stops and cannot be shared between instances.

| Store | Package | Shared between instances |
|---|---|---|
| In-memory (default) | `SharpTicker` | No |
| Entity Framework Core | `SharpTicker.EntityFrameworkCore` | Yes |
| MongoDB | `SharpTicker.MongoDB` | Yes |
| Redis | `SharpTicker.Caching.StackExchangeRedis` | Yes |

## Entity Framework Core

Written for relational EF Core providers. Our tests run it on SQLite, on PostgreSQL (Npgsql) and on MySQL through the official provider, `MySql.EntityFrameworkCore`. Each occurrence of a cron is written through [FlexLabs.EntityFrameworkCore.Upsert](https://github.com/artiomchi/FlexLabs.Upsert), so the provider has to be one that library knows; see its documentation for the list. Providers other than the three above are not part of our test runs. With a provider that library does not know, the scheduler loop fails with `Database provider not supported yet!` and no cron runs. On MySQL a schema is a database: the store's `ticker` schema is a database named `ticker`, which the application's user has to be allowed to create and use. There are two ways to use it.

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

The EF Core tools (`dotnet ef migrations add`, `dotnet ef dbcontext info`) find this context in the application's services and create it with the options given here, so the first migration can be added without a design-time factory. `SharpTickerDbContext` is in the SharpTicker assembly, so name the project that holds the migrations, for example `db.UseNpgsql(connectionString, b => b.MigrationsAssembly("MyApp"))`; without it the tools stop with `Your target project 'MyApp' doesn't match your migrations assembly 'SharpTicker.EntityFrameworkCore'`. When the application also registers a context of its own, the tools find both and stop with `More than one DbContext was found`, so each command names the one it is for: `--context AppDbContext` or `--context SharpTickerDbContext`.

For that lookup the options of this context are in the application's services as the non-generic `DbContextOptions` too. A context of your own whose constructor takes the non-generic `DbContextOptions`, and that you register by hand (`AddScoped<AppDbContext>()` instead of `AddDbContext`), is then given these options, and EF Core stops with `The DbContextOptions passed to the AppDbContext constructor must be a DbContextOptions<AppDbContext>`. Give that constructor a `DbContextOptions<AppDbContext>` parameter, or register the context with `AddDbContext`.

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

SharpTicker writes the times of these tables (`ExecutionTime`, `LockedAt`, `ExecutedAt`, `CreatedAt`, `UpdatedAt`) in UTC; they are read back as `DateTimeKind.Utc`, through the store and through your own queries on a context that has the tables. The column type is the one your provider gives a `DateTime`.

When your context converts every `DateTime` itself, these columns follow your conversion instead: a convention in `ConfigureConventions` is kept, and a loop in `OnModelCreating` that sets a converter on every `DateTime` property after the tables are added (with `UseModelCustomizer` they are added before your `OnModelCreating` runs) replaces SharpTicker's. If your conversion reads these times back as `Unspecified`, an update (`UpdateAsync`) of a job read back from the store reads them in the scheduler time zone, so the job and its children move by that zone's offset. With a compiled EF Core model (`dotnet ef dbcontext optimize`), generate it again after upgrading SharpTicker; until then the times read back as before.

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

Every instance that runs the background services renews the lock of the `InProgress` jobs it holds every fifth of the lease (every minute by default), and records the `InProgress` jobs of other instance names whose lock was not renewed for the lease as `Cancelled`. Such a job does not run again. See [When an instance stops](failure-modes.md#when-an-instance-stops) for what a pass changes, what happens to chain children and cron occurrences, how the lease works with the Redis heartbeat release, and its limits.

> From 1.6.2: a job whose lease ran out is recorded as `Cancelled`. An instance of 1.6.0 or 1.6.1 releases it to `Idle` instead, and it runs again; see [Upgrading](upgrading.md).

Set the same lease on every instance that runs the background services, and turn it off on all of them or on none:

- An instance with the lease off does not renew its locks, so an instance with the lease on records the jobs it runs for longer than the lease as `Cancelled` while they still run there, and their outcome is not stored.
- With different values, an instance with a shorter lease records the jobs of the others as `Cancelled` earlier than their longer lease expects.

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
- Redis Cluster is not supported as the job store ([#225](https://github.com/msx752/SharpTicker/issues/225)). The store reads several job records with one command, and a cluster refuses a command whose keys are in different hash slots, so the scheduler, the fallback and the job lists fail once they read more than one job. Use a standalone Redis, Sentinel or a managed primary/replica setup. A node with Redis as the job store writes one Error to its log when its connection is a cluster (see [Scheduler log entries](observability.md#scheduler-log-entries)). With EF Core or MongoDB as the job store, Redis next to it is used with single-key calls only (the heartbeat, the node registry, the cron list), which a cluster serves, and no such Error is written.
- Each node sends a heartbeat every `NodeHeartbeatInterval` (default one minute); the heartbeat TTL is that interval plus 20 seconds. The other instances release the jobs of a node whose heartbeat has ended, and with Redis as the job store a node that could not write its own heartbeat for one TTL asks its running jobs to stop and starts none until it can; `StopJobsWhenHeartbeatIsLost` (default `true`) turns that off. See [When an instance stops](failure-modes.md#when-an-instance-stops).
- With the EF Core store and Redis next to it, the list of cron expressions the scheduler plans from is kept in Redis for up to ten minutes after it is read. It is cleared when a cron is added, changed or removed through the managers or the dashboard, and when an application seeds the crons defined in its code at startup. If Redis does not answer when the seed clears the entry, the application starts all the same and logs a warning; the earlier list is then used until the entry ends. A cron row changed directly in the database is seen when the entry ends.
- Each child and grandchild of a chain is a record of its own, linked to its parent.
- The Redis store keeps the hash `tq:tt:waiting-children` (chain children without a time that wait for their start). The first fallback pass of each process fills it from the stored jobs.
- Ticker types of the application's own need no setting for the serializer: the store reads a type it does not know through reflection.
- For trimmed or AOT applications, where the serializer has reflection switched off, set `JsonSerializerContext`. With ticker types of the application's own, the context names the time type, the cron type and `CronTickerOccurrenceEntity<TCron>` with `[JsonSerializable]`. The members the entities fill themselves (`Status`, `LockedAt` and others) have setters a context generated in the application's assembly cannot reach: the build reports each of them with the warning `SYSLIB1038`, and the store sets them itself when it reads a record. A record written without a context is read with one, and the other way round.
- A custom entity can add properties. The store's claims, releases and skips write a record with the serializer that wrote it, so the numbers in a property of a custom entity are not changed by them.
- A property of a custom entity needs a type the serializer can read back. A property typed as an abstract class, without polymorphism configured, is written and then refused on read: a time job that holds a value there is added without an error, its record cannot be read, and it does not run. The record is left out of the read and logged as a warning with its key, and the other jobs run. The warning comes again with each read of the record, which includes the fallback's passes once the job is overdue; the record is left as it is. To end it, delete the job, or update it with a value that can be read back. An update of a time job whose entity holds such a value fails with the serializer's exception.
- A property of a custom entity also needs a value the serializer can write again. A converter that reads a value it does not write (for example an older form of the value, written by an earlier version of the application) gives a record that reads but cannot be written back, and the store's claims, releases and skips write the record with the serializer. Such a record is left as it is and named in a warning in the log with its key (`cannot be written as`), on each pass that meets it; the other jobs go on, and this job does not run. A record that a stopped node held when it stopped (`Queued` or `InProgress`) and that cannot be written back stays held under that node; it is not set back to `Idle`. Only the exceptions the serializer raises for a value (`JsonException`, `NotSupportedException`, `InvalidOperationException`) are handled this way; another exception thrown by a converter, and a failure of Redis, still fail the pass. To end it, delete the job, or update it with a value that can be written. An update of such a job fails with the serializer's exception.

## Custom persistence providers

`ITickerPersistenceProvider` has methods with default bodies, so a provider written for an earlier version still compiles. The defaults are not safe with several instances. Implement all of them:

| Methods | Default | Implement it so that |
|---|---|---|
| `TryMarkTimeTickerInProgress(id, claimMarker)`, `TryMarkCronOccurrenceInProgress(id, claimMarker)` | Writes `InProgress` without checking the current state, does not write the marker, and always returns `true`, so a job that another instance already took can run again. | The row changes only while it is `Queued` and held by this instance, `UpdatedAt` becomes `claimMarker`, and only the call that changed the row returns `true`. |
| `TryReleaseTimeTickerClaim(id, claimMarker)`, `TryReleaseCronOccurrenceClaim(id, claimMarker)` | Writes nothing and returns `false`, so a claim whose job never starts stays `InProgress`. | The row goes back to `Idle` with `LockHolder` and `LockedAt` cleared only while it is `InProgress`, held by this instance and its `UpdatedAt` equals `claimMarker`, and only a call that changed the row returns `true`. |
| `ReleaseDeadNodeTimeTickerResources(node, lockedBefore)`, `ReleaseDeadNodeOccurrenceResources(node, lockedBefore)` | Calls the overload without `lockedBefore`, which also releases the jobs this run has claimed since it started, so such a job can run twice. | Only rows locked before `lockedBefore` are released. |

`AcquireImmediateTimeTickersAsync` and `AcquireImmediateCronOccurrencesAsync` have no default, but the same contract: return each row this call claimed with the marker it wrote as `UpdatedAt`, and when the call fails after it claimed rows, put them back before it throws. Choose a row's marker before its write, so that a write that fails (it may have landed) is put back too. `QueueTimedOutTimeTickers` and `QueueTimedOutCronTickerOccurrences` hand back each row with the marker of its claim, and put back the row whose claim write fails before they throw.

The built-in stores also fence the writes of a running job by its claim marker and keep the run of a running row on a whole-object update (see [Writes of a running job](failure-modes.md#writes-of-a-running-job) and [Updating a stored job](failure-modes.md#updating-a-stored-job)). That goes through an internal interface, so a custom provider keeps writing by id: a late write of a run whose row was claimed again overwrites the new run, and an update of a running row can put its old state back.

A class that wraps a built-in provider must forward all of these methods, or it gets the defaults without any error. For the same reason, upgrade all SharpTicker packages together: a store package from an earlier version keeps the defaults.

## Choosing a store

- **In-memory** for development, tests and single-process tools where losing jobs on restart is acceptable.
- **EF Core** when you already run a relational database. With your own `DbContext` the tables live next to your data and in your migrations.
- **MongoDB** when your application already uses MongoDB.
- **Redis** for a fast, shared store when you already run Redis and accept its persistence model.
