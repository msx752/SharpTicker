# Upgrading

All `SharpTicker.*` packages share one version number. Update them together.

No release so far asks for a schema migration: the EF Core schema and the MongoDB collections and indexes are those of 1.0.0, and the Redis store only gained keys. Some releases change what is done with rows already stored, and some change behaviour. This page is a summary of what may need a decision or a step from you; the [release notes](https://github.com/msx752/SharpTicker/releases) are the source.

Coming from the project SharpTicker was forked from: see the [migration guide](migrating.md).

## Before you upgrade

- The versions are listed newest first. Read the section of every version between the one you run and the one you install, not only the last one. Each section says what changed that you may notice, and then the steps, if there are any.
- The guide pages describe the current version. Where a section needs the details, it links to them.
- Upgrade all instances together where you can. An instance of an earlier version behaves as that version did, so what a newer release changed does not apply to the jobs it runs. The sections below name the cases in which a mix of versions can run a job twice, or not at all.

### Coming from a version before 1.3.0

- Upgrade all instances together, and do not go back below 1.3.0 once chains were added on the new version (see [1.3.0](#130)).
- With Redis as the job store, a store that a version before 1.3.0 ever used can hold two kinds of jobs that version left, also when it has been on 1.3.x or 1.4.0 since: time jobs that are `InProgress` with no instance running them, and chains that keep their children inside the parent's record. From 1.5.0 both are read and run (see [1.5.0](#150)). Look for them before the upgrade:
  - Delete the `InProgress` time jobs that should not run any more. They run when an instance starts under the `NodeIdentifier` that holds them, however old they are.
  - Delete the chains that should not run and whose parent has not run yet: their children run after the upgrade.
  - A chain whose parent already ended is left as it is. Delete it and add it again for its children to run.
  - When you upgrade to 1.4.0 only, neither kind runs there: delete such jobs and add them again afterwards.

### From 1.5.x or earlier straight to 1.6.2 or later

With the EF Core store, the lease of 1.6.0 and the change of 1.6.2 meet. At the first pass after the upgrade, about a minute after the start, every job and cron occurrence that is `InProgress` under a name no running instance renews is recorded as `Cancelled`, however old it is, and does not run. Jobs left by replaced containers or pods can be weeks old.

- Before the upgrade, look for these jobs as the [1.6.0](#160) steps say.
- After the upgrade, add again the time jobs that should still run. For a cron, the next occurrence runs at its time.
- Or upgrade with `SetInProgressLease(TimeSpan.Zero)`, and turn the lease on once they are dealt with.

A chain child that waits for such a job follows its run condition on a `Cancelled` parent; see [Lease on running jobs](storage.md#lease-on-running-jobs).

<a id="release-by-release"></a>

<!-- Add the next version here, above 1.6.4, in the same shape: what changed, then the steps. -->

## 1.6.4

[Release notes](https://github.com/msx752/SharpTicker/releases/tag/v1.6.4)

- Redis as the job store: when a node cannot write its heartbeat, the self-fence now also asks a chain child that started with its parent to stop ([#221](https://github.com/msx752/SharpTicker/issues/221)). Up to 1.6.3 such a child was not asked to stop, and it could run twice at the same time: on the cut-off node and on another instance after the release. A child that stops when asked no longer runs on two nodes at once.
- Redis Cluster is not supported as the job store. On a cluster, a node with Redis as the job store writes one Error at startup ([#225](https://github.com/msx752/SharpTicker/issues/225)); see [Redis](storage.md#redis).
- MongoDB with the Redis heartbeat: the docs now say that the jobs of a stopped node are released and run again ([#223](https://github.com/msx752/SharpTicker/issues/223)).
- Dashboard: npm security updates, among them echarts 6 and Vuetify 3.8. Charts and fields look slightly different.

**Steps:** with Redis as the job store on a Redis Cluster, move to a standalone Redis, Sentinel or a managed primary/replica setup.

## 1.6.3

[Release notes](https://github.com/msx752/SharpTicker/releases/tag/v1.6.3)

- Scheduler: a restart of the loop that cuts off a store command is handled as a restart, whatever exception the store's provider throws ([#214](https://github.com/msx752/SharpTicker/issues/214)). With SQL Server it was logged as a loop failure at `Error` and shown on the dashboard. Alerts on the loop's `Error` entries now fire less often.

**Steps:** none.

## 1.6.2

[Release notes](https://github.com/msx752/SharpTicker/releases/tag/v1.6.2)

- EF Core: a job whose lease ran out is recorded as `Cancelled` and does not run again ([#211](https://github.com/msx752/SharpTicker/issues/211)). 1.6.0 and 1.6.1 released it to `Idle`, and the fallback ran it again. Its chain children follow their run condition on a `Cancelled` parent, and for a cron only the stuck occurrence is cancelled; see [Lease on running jobs](storage.md#lease-on-running-jobs).
- Coming from a version before 1.6.0, this changes the advice of 1.6.0; see [From 1.5.x or earlier straight to 1.6.2 or later](#from-15x-or-earlier-straight-to-162-or-later).
- With a mix of versions:
  - An instance on 1.6.0 or 1.6.1 still releases such jobs to `Idle`, and they run again. On those versions this also happens to an instance that is still running but cannot renew its lease (cut off from the database alone, or a process paused for longer than the lease): the other instances of those versions release its `InProgress` jobs while they may still run on it, and they run again.
  - An instance of a version before 1.6.0 does not renew its locks. An instance on 1.6.2 records the jobs it runs for longer than the lease as `Cancelled` while they still run there; their outcome is not stored, and their chain children follow `Cancelled`.
- Not changed: the startup release (an instance that starts again under the same name) and, with Redis next to the store, the heartbeat release still put a job back to `Idle`, and it runs again. With the default settings the heartbeat release comes about two heartbeat TTLs (some 2.5 minutes) after a node stops, before its lease ends. With a stable name, the startup release puts a job back only when the instance starts again before the job's lease ends; after a longer stop, another running instance records it as `Cancelled` first.

**Steps:** upgrade all instances together.

## 1.6.1

[Release notes](https://github.com/msx752/SharpTicker/releases/tag/v1.6.1)

Redis as the job store, chains stored by a version before 1.3.0 whose children are being written as records of their own (see [1.5.0](#150)):

- When the fallback or run-now of an instance takes such a parent while the scheduling loop of the same instance writes those records, it reads the children again after it took the parent, and hands the parent out with the children written by then ([#154](https://github.com/msx752/SharpTicker/issues/154)). If that read fails, a warning is logged, the parent is not run, and the next pass tries again. Up to 1.6.0 the parent could run with only the children written before it read them, and a missing child without a time of its own was left to the fallback (see [Delayed children](scheduling.md#delayed-children)).
- A delete of such a chain removes those records as well ([#153](https://github.com/msx752/SharpTicker/issues/153)). The delete reads the chain's children again after it removed the parent's record. The instance that writes the records removes the ones it wrote when it finds the parent's record gone, and then does not run the parent. If that check or that removal fails, a warning is logged. Then, or when that instance stops before it, the records that were not removed stay as `Idle` children without a parent, which do not run. Up to 1.6.0 the records written after the delete read the chain's children could stay that way.

**Steps:** upgrade all instances together, as before.

## 1.6.0

[Release notes](https://github.com/msx752/SharpTicker/releases/tag/v1.6.0)

- EF Core: a new option, `SetInProgressLease(TimeSpan)`, is on by default with 5 minutes ([#24](https://github.com/msx752/SharpTicker/issues/24)). A running instance renews the lock of its `InProgress` jobs, and handles the `InProgress` jobs of other instance names whose lock was not renewed for the lease; see [Lease on running jobs](storage.md#lease-on-running-jobs) and [When an instance stops](operations.md#when-an-instance-stops). On 1.6.0 and 1.6.1 such a job is released to `Idle` and runs again; from 1.6.2 it is recorded as `Cancelled` (see [1.6.2](#162)). Up to 1.5.2 it stayed `InProgress` until an instance started under the name that held it.
  - At the first pass after the upgrade, about a minute after the start, this covers every job that is `InProgress` under a name no running instance renews, however old it is: jobs left by replaced containers or pods can be weeks old. On 1.6.0 and 1.6.1 they run again, cron occurrences late, and a chain child that waits for such a job follows by its run condition.
  - An instance of an earlier version, or one with the lease off, does not renew its locks. On 1.6.0 and 1.6.1 an instance with the lease on releases the jobs that such an instance runs for longer than the lease, and they run twice. With different lease values, the shorter lease releases the jobs of the others earlier than their lease expects.
  - A row with no `LockedAt` or no lock holder is not touched by the lease. For a job that is `InProgress`, `LockedAt` and the dashboard's "Locked At" show when the lock was last renewed.
- EF Core with `UseSharpTickerDbContext`: the EF Core tools find `SharpTickerDbContext` in the application's services, so `dotnet ef migrations add` works in a project that has no migrations for it yet ([#89](https://github.com/msx752/SharpTicker/issues/89)). Up to 1.5.2 the tools stopped in such a project with `No DbContext was found in assembly ...`. In a project that also registers a context of its own (`AddDbContext<AppDbContext>`) and has no migrations for `SharpTickerDbContext`, the tools picked the application's context up to 1.5.2 when no `--context` was given; now they find both and stop with `More than one DbContext was found`.
- Dashboard with ticker types of your own: lists, add and update work and show their public properties ([#95](https://github.com/msx752/SharpTicker/issues/95)). Up to 1.5.2 these requests failed with a `NotSupportedException`.
- Dashboard with a custom `ITickerPersistenceProvider`: the status charts, the statistics and the jobs per node count through `TimeTickersQuery()` and `CronTickerOccurrencesQuery()` with `Where` and `CountAsync` ([#96](https://github.com/msx752/SharpTicker/issues/96)). A provider whose query objects throw makes `/time-tickers/graph-data`, `/cron-tickers/graph-data`, `/ticker/statuses/get` and `/ticker/machine/jobs` fail. Up to 1.5.2 these read the rows through `GetTimeTickers` and `GetAllCronTickerOccurrences`.
- Dashboard: the Machines panel is called Lock holders ([#31](https://github.com/msx752/SharpTicker/issues/31)). The answer of `/ticker/machine/jobs` is ordered by the latest lock of each name, latest first; up to 1.5.2 it was ordered by the number of jobs, and the panel showed neither the latest lock nor the heartbeat. It keeps `item1` (the name) and `item2` (the number of jobs), and adds `lastLockedAt` (the latest lock, in UTC) and `hasHeartbeat` (with Redis, whether the node has a heartbeat entry; otherwise null).
- A ticker added after `UseSharpTicker` and before the host starts finds its function ([#75](https://github.com/msx752/SharpTicker/issues/75)).

**Steps:**

- With EF Core, look for `InProgress` jobs under names no instance runs before the upgrade. Without Redis next to the store, the startup log of 1.5.0 and later names the instance names that hold them (Information, category `SharpTicker.EntityFrameworkCore.StartupRelease`). In the database they are the rows with `Status` 2 (`InProgress`) in `TimeTickers` and `CronTickerOccurrences`, with their `LockHolder` and `LockedAt`. Delete the ones that should not run (Force delete on the dashboard for a time job), or upgrade with `SetInProgressLease(TimeSpan.Zero)` and turn the lease on once they are dealt with.
- Upgrade all instances together, and set the same lease on all of them, or turn it off on all of them.
- In a project with a context of its own, scripts that run `dotnet ef database update`, `dotnet ef migrations script` or `dotnet ef migrations bundle` without `--context` need `--context AppDbContext`.
- A script that relies on the old order of `/ticker/machine/jobs` can sort by `item2` itself.

## 1.5.2

[Release notes](https://github.com/msx752/SharpTicker/releases/tag/v1.5.2)

- A delayed chain child or grandchild whose time is given as `DateTimeKind.Local` or `Unspecified` has its time converted to UTC like the root's, with `Unspecified` read in the scheduler time zone ([#81](https://github.com/msx752/SharpTicker/issues/81)). Through the dashboard API, a `timeZoneId` applies to every time of the chain. Up to 1.5.1 such a time was stored as given, so on a machine not on UTC the child ran hours off; on those versions, pass a child's time in UTC. Times already stored are not changed. A caller that built an `Unspecified` value meaning UTC on such a machine now gets it shifted, as the root's time was on earlier versions too.
- A chain child that starts with its parent (`RunCondition.InProgress`, no time of its own) and still waits after its parent ended is run by the fallback when the parent started (`Done`, `DueDone`, `Failed`, `Cancelled`), and recorded as `Skipped` only when the parent was skipped ([#158](https://github.com/msx752/SharpTicker/issues/158)). Up to 1.5.1 it was recorded as `Skipped` in every case, also when its run had been interrupted by a stopped node. A child 1.5.1 already recorded as `Skipped` stays so, and nothing is run again for it.
- Redis as the job store: claims, releases and skips write a record with the serializer that wrote it ([#151](https://github.com/msx752/SharpTicker/issues/151)). Up to 1.5.1 the store's scripts wrote it back with the JSON library of Redis:
  - A number kept 14 significant digits. A `long` of 15 digits or more, such as a tick count, a snowflake id or `long.MaxValue`, was written in exponent form (`1e+14`), which is not read as a `long`, so the job did not run. A `double` with more than 14 significant digits was rounded. Now such numbers in a ticker type of your own are kept, and the job runs.
  - An empty collection was stored as an empty object. The writes from 1.5.2 keep it a collection; for records written that way earlier, see [#146 in 1.5.0](#150).
  - A record 1.5.1 already wrote in exponent form is not repaired, because its digits are lost. It is left out of the reads and named in a warning with its key (`tq:tt:<id>`).
  - A record that cannot be read is no longer taken by the fallback and left `InProgress`; up to 1.5.1 the fallback took it once the job was overdue and could not hand it out. It stays as it is, and is named on each read.
- A cron whose expression matches no date (such as `0 0 0 30 2 *`) is skipped by the scheduler, which plans the other jobs, and adding or updating one is refused ([#194](https://github.com/msx752/SharpTicker/issues/194)). Up to 1.5.1 such a cron ticker, which `WithCron` in code accepts, made the scheduler loop fail on every pass, so no job started on time while it was stored.
- Each enabled cron ticker whose expression has no next occurrence is named in a Warning at startup ([#79](https://github.com/msx752/SharpTicker/issues/79)).

**Steps:**

- Check delayed child times given as `Local` or `Unspecified`.
- With Redis as the job store, upgrade all instances together: an instance still on 1.5.1 rewrites a record when it claims it. Delete the jobs named as unreadable, or update them with a value.
- Correct or remove the cron tickers named in the startup warning.

## 1.5.1

[Release notes](https://github.com/msx752/SharpTicker/releases/tag/v1.5.1)

- Redis as the job store: an update (`UpdateAsync`, or saving it in the dashboard) of a chain stored by a version before 1.3.0 whose parent already ended changes the parent's record only ([#152](https://github.com/msx752/SharpTicker/issues/152)). The children stay inside it and are not run, and what the update carries for them is not stored. Up to 1.5.0 the update wrote the children as records of their own, and in a test the fallback then ran the ones the parent's outcome lets run; on those versions, delete such a chain instead of updating it when its children should not run.
- A job added while the scheduler loop reads its next plan ([#78](https://github.com/msx752/SharpTicker/issues/78)), and, with EF Core or MongoDB and the Redis heartbeat, a job that waits while a stopped node is released ([#90](https://github.com/msx752/SharpTicker/issues/90)), starts at its time. Up to 1.5.0 it could start up to `FallbackIntervalChecker` late and end `DueDone`.

**Steps:** none.

## 1.5.0

[Release notes](https://github.com/msx752/SharpTicker/releases/tag/v1.5.0)

Redis as the job store:

- A node that cannot write its heartbeat for one heartbeat TTL (80 seconds with the default interval) asks its running jobs to stop, and they run again later ([#46](https://github.com/msx752/SharpTicker/issues/46)). This also happens in a Redis outage that every instance sees, where the jobs ran on before. Jobs that must not be interrupted that way need `StopJobsWhenHeartbeatIsLost = false`; see [When an instance stops](operations.md#when-an-instance-stops).
- An application with ticker types of its own can add its jobs and they run, without a `JsonSerializerContext` and with a source-generated one ([#162](https://github.com/msx752/SharpTicker/issues/162)). Up to 1.4.0 adding a job failed with a `NotSupportedException` without a context, and with one the scheduler loop and the fallback failed on reading the jobs, because the store did not set the members the entities fill themselves (`Status`, `LockedAt` and others) when it read a record. The limits that remain are in [Redis](storage.md#redis).
- A time job that is added or changed shortly before it is due is started at its time ([#176](https://github.com/msx752/SharpTicker/issues/176)). Up to 1.4.0 the scheduler loop did not take such a job when the last digit of its `UpdatedAt` was a zero, which is about one job in ten, and every one with a clock that gives whole milliseconds. The fallback started it, up to `FallbackIntervalChecker` (30 seconds by default) after its time, and it ended `DueDone`. Jobs start earlier than they did, and fewer end `DueDone`.
- A ticker type of your own with a property typed as an abstract class: a record whose value there cannot be read back is left out of the read and logged as a warning with its key, and the other jobs run ([#150](https://github.com/msx752/SharpTicker/issues/150)). Up to 1.4.0 one such record made every read that loaded it throw, and in a running application the scheduler loop, the fallback and the job lists stopped for all jobs. For the fallback up to 1.5.1, see [1.5.2](#152).
- A ticker type of your own that has a collection property ([#146](https://github.com/msx752/SharpTicker/issues/146)):
  - Up to 1.4.0, a time job whose collection was empty was claimed and not handed out. It stayed held by its instance, mostly as `InProgress`.
  - Up to 1.4.0, an occurrence of a cron type whose collection is empty in a new instance of the type (for example `List<string> Tags = []`) ran while its outcome was not stored, and stayed `InProgress`. This does not depend on what the stored cron ticker holds, so it applies to every occurrence of such a type. The log of that version has `Failed to deserialize` warnings for them.
  - Up to 1.5.1 a claim, a release or a skip rewrote the record with the JSON library of Redis, which stored an empty collection as an empty object. Records written that way stay so until their next write. Up to 1.4.0 a record with an empty collection of the application's own type could not be read after such a rewrite.
  - From 1.5.0 such a record is read with that collection empty, whatever the property is called, and also inside a list or a dictionary. These limits remain for records written that way:
    - The record is read once more for each such empty object, on every read, until the application side writes it again. A record with many of them (a list of thousands of empty lists) is slow to read.
    - A property typed `object`, `JsonElement` or `JsonNode` that held an empty list comes back as an empty object.
    - A collection under a property name or dictionary key that contains `']`, or anything below such a name, is not read this way; the record stays unreadable and is logged with its key.
    - The scripts of versions up to 1.5.1 restored `Children` and `RetryIntervals` under those exact (PascalCase) names, at any depth. An empty dictionary of your own under one of these names came back as an empty list and cannot be read. Avoid it with Redis.
  - From 1.5.0 these records are released by an instance that starts under the `NodeIdentifier` that holds them, or by another instance that finds their instance stopped, and they run then: the time jobs late, and every such occurrence still in the store one more time although it ran before, all of them together.
- Jobs that a version before 1.3.0 left. In a run that went from 1.2.3 to 1.4.0, two kinds of stored jobs did not carry over; 1.5.0 reads both:
  - Time jobs that 1.2.3 had picked up were `InProgress` without having run, and stayed so on 1.4.0 under the same `NodeIdentifier`. Its log has `Failed to deserialize JSON payload as TimeTickerEntity` for them. From 1.5.0 ([#144](https://github.com/msx752/SharpTicker/issues/144)) they are released when an instance starts under the `NodeIdentifier` that holds them, and run then, however old they are. In a run from 1.2.3 each ran once, late.
  - A chain stored by such a version keeps its children inside the parent's record. On 1.4.0 the parent ran, and its children were not started. From 1.5.0 ([#145](https://github.com/msx752/SharpTicker/issues/145)) the children and grandchildren are written as records of their own when an instance takes the parent to run it. In a run from 1.2.3, the children of the three stored chains then ran once each after their parents, by their run conditions, and the ones whose condition did not match were recorded as `Skipped`. What to know about it:
    - It applies to chains whose parent has not run yet. A chain whose parent already ended, on the earlier version or on 1.4.0, is left as it is, and its children are not run.
    - A child with a time of its own waits for that time from then on. A version before 1.3.0 ran it right after its parent.
    - If writing the records fails, a warning is logged, the parent is not run, and the next pass tries again.
    - While the children are inside the parent's record, the dashboard does not show their results, and such a child cannot be deleted on its own (delete the chain).
    - An instance on 1.4.0 that takes such a parent still runs it without its children.
    - Later changes for these chains are in [1.5.1](#151) (update) and [1.6.1](#161) (delete, and run-now while the records are written).

EF Core:

- With Redis next to the store, the list of cron expressions kept in Redis is cleared when the crons defined in code are seeded at startup, and ends ten minutes after it was read ([#93](https://github.com/msx752/SharpTicker/issues/93)). Up to 1.4.0 the entry had no lifetime and the seed did not clear it: a cron whose expression was changed in code kept its earlier schedule after a deploy, a cron that was new in code was missing from the list, and one removed from the code stayed in it, also after every node was restarted, until a cron was added, changed or removed. After the upgrade, a cron whose expression was changed in code runs on its new schedule.
  - An entry written by 1.4.0 or earlier has no end. The first start on the new build clears it. If Redis does not answer at that moment (a warning in the log says so), or the application does not seed (`IgnoreSeedDefinedCronTickers`), it stays until a cron is added, changed or removed.
  - An instance that still runs the earlier version writes the entry again without an end.
- On net8.0, `SharpTicker.EntityFrameworkCore` asks for `FlexLabs.EntityFrameworkCore.Upsert` 8.1.2 or later, where it asked for 8.0.0 or later ([#91](https://github.com/msx752/SharpTicker/issues/91)). A project that pins that package below 8.1.2 gets a restore error until the pin is raised. With 8.1.2 the crons run on the official MySQL provider (`MySql.EntityFrameworkCore`) on net8.0, where they did not run up to 1.4.0; see [Storage](storage.md) for the providers the tests run on.
- Without Redis next to the store, a starting instance writes one Information entry that names the other node identifiers holding `InProgress` jobs: the ten with the most such jobs, each with its count, and how many more names there are ([#24](https://github.com/msx752/SharpTicker/issues/24)). Nothing is released by it. A job held under a name no instance starts with again stays `InProgress`, as before; see [When an instance stops](operations.md#when-an-instance-stops).

All stores:

- A cron that takes its expression from configuration (`%Key%`): a value with five fields gets the seconds field, so a job that never ran up to 1.4.0 starts to run after the upgrade ([#79](https://github.com/msx752/SharpTicker/issues/79)). A value that is not a cron expression, one of blanks only included, stops the startup with an error that names the function, the configuration key and the value; up to 1.4.0 the application started and the job never ran. A key that is missing or empty is handled as before.

**Steps:**

- With Redis as the job store, check that your jobs honour their cancellation token and are safe to run twice, or set `StopJobsWhenHeartbeatIsLost = false`.
- Check the configured cron values.
- Raise a pin of `FlexLabs.EntityFrameworkCore.Upsert` below 8.1.2.
- Upgrade all instances together.
- With Redis as the job store, delete the stored jobs that should not run before the upgrade: those a version before 1.3.0 left (see [Coming from a version before 1.3.0](#coming-from-a-version-before-130)), and those with an empty collection (#146). A version up to 1.4.0 does not list the latter, in its reads or in the dashboard, so look in Redis: the keys `tq:tt:<id>` (time jobs) and `tq:co:<id>` (occurrences) whose value has `"Status":2`, with no instance running them. Delete the key and take its id out of the index keys: `tq:tt:ids` and `tq:tt:pending` for a time job; `tq:co:ids`, `tq:co:pending` and `tq:cron:<cron id>:occurrences` for an occurrence.

## 1.4.0

[Release notes](https://github.com/msx752/SharpTicker/releases/tag/v1.4.0)

- Chain children without a time that earlier versions left `Idle` after their parent ended are run or skipped by the first fallback pass, however old they are. One that runs ends `DueDone`.
- Some log texts changed.
- Redis keeps one more key, `tq:tt:waiting-children`. The first fallback pass of each process fills it from the stored jobs, so nothing has to be done for it at the upgrade. An instance of an earlier version neither writes nor reads it.

**Steps:** before upgrading, delete the `Idle` children you do not want to run.

## 1.3.1

[Release notes](https://github.com/msx752/SharpTicker/releases/tag/v1.3.1)

- Stored copies of a cron seeded from code are deleted at the first start, with their occurrence history; the oldest of them stays. This covers only copies that still carry the seed's marker.
- A chain child without a registered function ends `Failed`.
- `UpdateAsync` keeps the parent of a stored child.

**Steps:** none.

## 1.3.0

[Release notes](https://github.com/msx752/SharpTicker/releases/tag/v1.3.0)

- On EF Core and MongoDB, the chain rules also apply to rows stored before the upgrade. Delayed children stored before it follow the rules of [Delayed children](scheduling.md#delayed-children): one whose parent has not ended waits for it, and one whose parent has already ended with a status its condition does not accept is recorded as `Skipped` once its time has passed.
- An inline child is `InProgress` while it runs.
- `UpdatedAt` of a running job holds its claim marker.
- An ended job is not run again by saving it.
- Filters on `DueDone` see fewer rows.
- Custom persistence providers keep the behaviour of 1.2.3 for the fenced writes.
- Redis stores each child and grandchild of a chain as a record of its own, linked to its parent.
- With a mix of versions: a node on a version before 1.3.0 hands out delayed children without waiting for their parent. With Redis, it reads only chains that hold their children inside the parent's record, so it does not read the new child records, and it runs only the parent of a newer chain, not its children.

**Steps:**

- Upgrade all instances together. Do not go back to 1.2.3 or earlier once chains exist.
- With Redis as the job store, give every running instance its own `NodeIdentifier`: an instance that starts now releases what an earlier run under its name left locked.

## 1.2.1 to 1.2.3

Release notes: [1.2.1](https://github.com/msx752/SharpTicker/releases/tag/v1.2.1), [1.2.2](https://github.com/msx752/SharpTicker/releases/tag/v1.2.2), [1.2.3](https://github.com/msx752/SharpTicker/releases/tag/v1.2.3)

Fixes; their notes list no behaviour change to check.

**Steps:** none.

## 1.2.0

[Release notes](https://github.com/msx752/SharpTicker/releases/tag/v1.2.0)

- The priority on `[TickerFunction]` takes effect.
- Dashboard: status of a chain, columns, wording, statistics.

**Steps:** none.

## 1.1.0

[Release notes](https://github.com/msx752/SharpTicker/releases/tag/v1.1.0)

- Source generator diagnostics are `ST001` to `ST011` (were `TQ001` to `TQ011`).
- A cron with a backwards range such as `22-2` is refused when added or updated.
- Updating a ticker that does not exist fails on every store.
- How a cancelled job and a timed-out HTTP call inside a job are recorded changed.
- At a stop, the scheduler waits for running jobs.

**Steps:** update `NoWarn`, `.editorconfig` and `#pragma` lines that name the old diagnostic ids.
