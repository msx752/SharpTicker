# Migrating to SharpTicker 1.0.0

> [!IMPORTANT]
> **Versions: TickerQ `10.4.0` → SharpTicker `1.0.0`.**
>
> - SharpTicker 1.0.0 corresponds to TickerQ 10.4.0, the last release published under MIT/Apache-2.0, plus the changes merged after it up to the base commit described in the [README](README.md#origin).
> - **On TickerQ 10.4.0:** follow this guide directly.
> - **On an older TickerQ version:** first upgrade to TickerQ 10.4.0 and deploy it, using that project's own upgrade notes, then follow this guide. The two steps stay separate, so each one can be verified on its own.
> - **On TickerQ 10.5.0 or later:** those releases are under a different license and are not covered. SharpTicker contains no code from them, and moving back to 1.0.0 is not verified.
> - Use **1.0.0 for every SharpTicker package**.
> - Later SharpTicker versions are upgraded from 1.0.0 like any other release.

This guide moves an application from TickerQ to SharpTicker. It was verified against a real application on TickerQ 10.4.0 and against EF Core migrations generated with the old package.

1.0.0 is the only release designed to match its origin. After you are on 1.0.0, upgrade like any other SharpTicker release.

There is a ready-to-use [prompt for AI coding assistants](#ai-assisted-migration) at the end of this page.

## No database changes

Coming from TickerQ 10.4.0, **the switch needs no change to your database, no data migration and no manual step on stored data.** It is a code and package change only. Every identifier SharpTicker writes to storage is the same as before:

| Stored data | Identifier | Changed? |
|---|---|---|
| EF Core schema, tables, columns | schema `ticker`; `TimeTickers`, `CronTickers`, `CronTickerOccurrences` | No |
| MongoDB collections and indexes | prefix `ticker_` | No |
| Redis job store keys | `tq:tt:*`, `tq:cron:*`, `tq:co:*` | No |
| Crons seeded from code | `InitIdentifier` = `MemoryTicker_Seeded_<function>` | No |
| Jobs | function names, cron expressions, request payloads, retries, history | No |

Existing jobs keep firing, history stays visible in the dashboard, and crons defined in code are neither duplicated nor removed.

## Rolling deployment

Old (TickerQ) and new (SharpTicker) instances can run against the same store at the same time, so you can roll the switch out one instance at a time without downtime. They read and write the same tables, documents and keys, and they coordinate through the same row locks, so a job is not run twice.

**If you use the Redis provider**, also keep the old coordination prefix for the rollout. Redis tracks which instances are alive under this prefix, and that is how jobs held by a crashed instance are released. With different prefixes, old and new instances cannot see each other:

```csharp
opt.AddStackExchangeRedis(redis =>
{
    redis.Configuration = "localhost:6379";
    redis.InstanceName = "tickerq:"; // the 10.4.0 default; keeps old and new instances visible to each other
});
```

You can keep this setting permanently, or remove it in a later deployment once no TickerQ instance is running.

## 1. Packages

Replace each package with its counterpart and use the same version for all of them:

| TickerQ | SharpTicker |
|---|---|
| `TickerQ` | `SharpTicker` |
| `TickerQ.Utilities` | `SharpTicker.Utilities` |
| `TickerQ.EntityFrameworkCore` | `SharpTicker.EntityFrameworkCore` |
| `TickerQ.MongoDB` | `SharpTicker.MongoDB` |
| `TickerQ.Caching.StackExchangeRedis` | `SharpTicker.Caching.StackExchangeRedis` |
| `TickerQ.Dashboard` | `SharpTicker.Dashboard` |
| `TickerQ.Instrumentation.OpenTelemetry` | `SharpTicker.Instrumentation.OpenTelemetry` |

```xml
<PackageReference Include="SharpTicker" Version="1.0.0" />
<PackageReference Include="SharpTicker.EntityFrameworkCore" Version="1.0.0" />
<PackageReference Include="SharpTicker.Dashboard" Version="1.0.0" />
```

There is one package version for .NET 8, 9 and 10; there are no separate version lines per .NET release.

## 2. Code

Namespaces and every type or member whose name contains the product name change from `TickerQ` to `SharpTicker`. Everything else keeps its name.

| Before | After |
|---|---|
| `using TickerQ;`, `using TickerQ.Utilities...;`, ... | `using SharpTicker;`, `using SharpTicker.Utilities...;`, ... |
| `services.AddTickerQ(...)` / `app.UseTickerQ()` | `services.AddSharpTicker(...)` / `app.UseSharpTicker()` |
| `UseTickerQDbContext<TickerQDbContext>(...)` | `UseSharpTickerDbContext<SharpTickerDbContext>(...)` |
| `TickerQEfCoreOptionBuilder` | `SharpTickerEfCoreOptionBuilder` |
| `UseTickerQMongoClient(...)`, `TickerQMongoOptionBuilder` | `UseSharpTickerMongoClient(...)`, `SharpTickerMongoOptionBuilder` |
| `TickerQRedisOptionBuilder`, `TickerQRedisContext` | `SharpTickerRedisOptionBuilder`, `SharpTickerRedisContext` |
| `ITickerQHostScheduler`, `ITickerQDispatcher`, `ITickerQInstrumentation` | `ISharpTickerHostScheduler`, `ISharpTickerDispatcher`, `ISharpTickerInstrumentation` |
| `TickerQStartMode`, `TickerQConstructorAttribute`, `AddTickerQDiscovery(...)` | `SharpTickerStartMode`, `SharpTickerConstructorAttribute`, `AddSharpTickerDiscovery(...)` |

Unchanged names you keep using as they are: `TimeTickerEntity`, `CronTickerEntity`, `ITimeTickerManager<T>`, `ICronTickerManager<T>`, `TickerFunctionAttribute`, `TickerFunctionContext`, `TickerStatus`, `AddOperationalStore`, `AddDashboard`, `SetBasePath`, `SetSchema`, `UseApplicationDbContext` and the other members without the product name.

> [!WARNING]
> **Do not replace the text `TickerQ` everywhere.** Several unrelated names contain the same letters followed by "uery": `ITickerQueryable`, `TimeTickerQueryFilter`, `CronTickerQueryFilter`, `TickerQueryableExtensions`, `EfTickerQueryable`, `MongoTickerQueryable`, `InMemoryTickerQueryable`, `TickerQueryExtensions`. A blind replace turns `ITickerQueryable` into `ISharpTickerueryable`.
>
> Replace only where `TickerQ` is not followed by a lowercase letter. With a regular expression: find `TickerQ(?![a-z])`, replace with `SharpTicker`.

Only touch C# code and project files with that rule. Configuration keys, connection string names and database names belong to your application; see [step 6](#6-your-own-names-optional).

## 3. Entity Framework Core

### Registration

```csharp
// Before
opt.AddOperationalStore(ef =>
{
    ef.UseTickerQDbContext<TickerQDbContext>(db => db.UseNpgsql(connectionString));
    ef.SetSchema("ticker");
});

// After
opt.AddOperationalStore(ef =>
{
    ef.UseSharpTickerDbContext<SharpTickerDbContext>(db => db.UseNpgsql(connectionString));
    ef.SetSchema("ticker");
});
```

If you map the tickers into your own `DbContext` with `UseApplicationDbContext<TContext>(...)`, only the `using` lines change.

### Existing migrations

Your existing migrations and the model snapshot refer to the entities by their old type names as strings, for example `modelBuilder.Entity("TickerQ.Utilities.Entities.CronTickerEntity", ...)`. That is expected and safe:

- **Do not edit the `Up`/`Down` methods of existing migrations.** The old type-name strings in `*.Designer.cs` files and in the snapshot compile as they are.
- If a migration file has a `using TickerQ...;` line, change it to `using SharpTicker...;` so it compiles.
- EF Core compares tables and columns, not .NET type names. The next `dotnet ef migrations add` rewrites the snapshot with the new names and contains **no table changes**.

**You do not need to add or apply a migration for the switch.** If you want to confirm it, run this once after switching; it does not touch the database:

```bash
dotnet ef migrations add VerifySharpTicker
```

- Snapshot generated with TickerQ 10.4.0: the migration is **empty**. Remove it with `dotnet ef migrations remove`, or keep it to record the new type names in the snapshot.
- Snapshot generated with an older TickerQ version: the migration may add the `IsEnabled` and `IsSystemPaused` columns to `CronTickers`. That comes from the 10.4.0 schema, not from the rename, and is safe to apply.
- **Stop if the migration drops, renames or creates any `ticker` table or column other than those two.** That means something else changed, for example the schema name or a custom configuration.

If you create the schema with `EnsureCreated()` or `GenerateCreateScript()` instead of migrations, the generated schema is identical.

`CronTickerEntity.IsSystemPaused` is still mapped so existing databases need no change, but it is marked `[Obsolete]` and is no longer used. Pause a cron with `IsEnabled`.

## 4. Runtime differences

| Area | Before | After | Action |
|---|---|---|---|
| Dashboard default path | `/tickerq/dashboard` | `/ticker/dashboard` | Only if you relied on the default. An explicit `SetBasePath(...)` keeps working; `SetBasePath("/tickerq/dashboard")` keeps the old URL. |
| Redis coordination prefix (`InstanceName`) | `tickerq:` | `sharpticker:` | Only for rolling deployments; see [Rolling deployment](#rolling-deployment). Job data keys (`tq:*`) are unchanged. |
| Tracing | ActivitySource `TickerQ`, tags `tickerq.*` | ActivitySource `SharpTicker`, tags `sharpticker.*` | Update OpenTelemetry source names, dashboards and alerts that filter on the old names. |
| Generated code | `TickerQInstanceFactory` | `SharpTickerInstanceFactory` | Only if your code refers to the generated class by name. |

## 5. Removed APIs

The integration with the hosted Hub (RemoteExecutor and SDK packages) is not part of SharpTicker. The members that only served it are removed:

- `SdkOfflineSkipException`
- `TickerFunctionProvider.MergeRemoteFunctions`, `MergeRemoteRequestInfo`, `ReplaceFunctions`, `ReplaceRequestInfo`, `FunctionNodeResolver`, `ResolveNodeName`
- `ITickerDashboardDataService.GetNodesAsync`, `GetNodeFunctionsAsync`, `GetNodeJobsAsync`, and the `NodeDto` and `NodeHealthStatus` types
- `CronTickerFlatDto.IsSystemPaused`

Running several instances of your application against one store does not need any of them; it works through the shared database or Redis as before.

## 6. Your own names (optional)

Configuration keys, connection string names, environment variables and database names are your application's, not the library's. Nothing requires you to rename them.

If you do rename them, keep reading the old name for a while. A missing connection string often falls back to a default, so a renamed key that is not yet set on a server can silently point the scheduler at the wrong database:

```csharp
var schedulerConnection =
    builder.Configuration.GetConnectionString("SharpTicker")
    ?? builder.Configuration.GetConnectionString("TickerQ"); // old name, remove once every environment is updated
```

Never rename the database itself as part of this migration.

## 7. Checklist

- [ ] Every `TickerQ*` package is replaced by its `SharpTicker*` counterpart, all on the same version.
- [ ] The solution builds, and no project references a `TickerQ*` package, directly or transitively.
- [ ] Your tests pass.
- [ ] EF Core: `dotnet ef migrations add VerifySharpTicker` is empty, or only adds `IsEnabled`/`IsSystemPaused`.
- [ ] The dashboard opens at the expected URL.
- [ ] Tracing dashboards and alerts use the `SharpTicker` source and `sharpticker.*` names.
- [ ] Existing cron jobs fire and their history shows in the dashboard.

## AI-assisted migration

Copy this prompt into your AI coding assistant, from the root of your repository:

````text
Migrate this repository from TickerQ to SharpTicker 1.0.0. Work on a new branch and do not commit.

Rules:
1. Packages: replace every TickerQ* PackageReference with its SharpTicker* counterpart
   (TickerQ -> SharpTicker, TickerQ.X -> SharpTicker.X), all on version 1.0.0.
   Include Directory.Packages.props if the repository uses central package management.
2. Code (*.cs, *.csproj, *.props, *.razor, *.cshtml only): replace "TickerQ" with "SharpTicker" ONLY where
   it is not followed by a lowercase letter (regex: TickerQ(?![a-z])). Never change ITickerQueryable,
   TimeTickerQueryFilter, CronTickerQueryFilter or any other name where "TickerQ" is followed by "uery".
3. Do NOT change: configuration keys, connection string names, environment variables, database names,
   database schema names (keep "ticker"), table names, or job function names. List them in your
   summary instead, so I can decide separately.
4. EF Core migrations: do not edit Up/Down of existing migrations. Old type-name strings such as
   "TickerQ.Utilities.Entities.CronTickerEntity" in *.Designer.cs and the model snapshot stay as they are.
   Only fix "using TickerQ..." lines so the files compile.
5. Dashboard: if AddDashboard(...) does not call SetBasePath, tell me that the default URL changes
   from /tickerq/dashboard to /ticker/dashboard.
6. Tracing: list every place that refers to the "TickerQ" ActivitySource or "tickerq.*" tag names.
7. If the code uses SdkOfflineSkipException, MergeRemoteFunctions, MergeRemoteRequestInfo,
   ReplaceFunctions, ReplaceRequestInfo, FunctionNodeResolver, ResolveNodeName, GetNodesAsync,
   GetNodeFunctionsAsync, GetNodeJobsAsync, NodeDto or CronTickerFlatDto.IsSystemPaused, stop and
   tell me; these were removed.
8. Never run a command that changes a database (no "dotnet ef database update", no applying migrations,
   no SQL). The switch needs no database change.
9. If the Redis provider is used (AddStackExchangeRedis) and the application runs more than one instance,
   set redis.InstanceName = "tickerq:" inside AddStackExchangeRedis(...) so old and new instances can
   coordinate during a rolling deployment.

Then verify:
- Build the solution and fix compile errors caused by the rename only.
- Confirm no TickerQ* package remains in any project.assets.json.
- Run the tests that cover scheduling and the job store.
- If EF Core migrations are used, run "dotnet ef migrations add VerifySharpTicker" and show me the Up()
  method. It must be empty or only add the IsEnabled/IsSystemPaused columns to CronTickers.
  If it drops, renames or creates anything else, stop and report.

Finish with a summary: files changed, the names you deliberately left unchanged (rule 3),
and the results of every verification step.
````
