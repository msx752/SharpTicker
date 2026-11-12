# Upgrading

All `SharpTicker.*` packages share one version number. Update them together.

None of the release notes so far asks for a schema migration: the EF Core schema and the MongoDB collections and indexes are those of 1.0.0, and the Redis store only gained keys. Releases did change what is done with stored rows (1.3.0 stores each chain child on Redis as a record of its own, 1.3.1 deletes copies of a seeded cron, 1.4.0 decides chain children that were left `Idle`) and they changed behaviour. Every release lists that in its notes.

This page points to those notes and names what needs a decision or a step from you. It is a summary; the [release notes](https://github.com/msx752/SharpTicker/releases) are the source.

## Before you upgrade

- Read the notes of every release between the version you run and the one you install, not only the last one.
- Upgrade all instances together where you can. An instance of an earlier version behaves as that version did, so what a newer release changed does not apply to the jobs it runs.
- Coming from a version before 1.3.0: upgrade all instances together, and do not go back below 1.3.0 once chains were added on the new version.
- Coming from a version before 1.3.0 with Redis as the job store, two kinds of stored jobs did not carry over in a run that went from 1.2.3 to 1.4.0:
  - Time jobs that 1.2.3 had picked up were `InProgress` without having run, and stayed so on 1.4.0 under the same `NodeIdentifier`. Its log has `Failed to deserialize JSON payload as TimeTickerEntity` for them. A build with the fix for [#144](https://github.com/msx752/SharpTicker/issues/144), which comes after 1.4.0, read these rows in a run from 1.2.3: started under the same `NodeIdentifier`, it released them, and each ran once, late.
  - A chain stored by that version keeps its children inside the parent's record. On 1.4.0 the parent ran, and its children were not started.

  Look for both before the upgrade. On 1.4.0, delete such jobs and add them again afterwards. With the fix for #144, the `InProgress` time jobs run after the upgrade, so delete the ones that should not run any more; the chains still have to be deleted and added again. See [Scheduling](scheduling.md#delayed-children).

## Release by release

| Version | What its notes ask you to look at | Step |
|---|---|---|
| [1.1.0](https://github.com/msx752/SharpTicker/releases/tag/v1.1.0) | Source generator diagnostics are `ST001` to `ST011` (were `TQ001` to `TQ011`). A cron with a backwards range such as `22-2` is refused when added or updated. Updating a ticker that does not exist fails on every store. How a cancelled job and a timed-out HTTP call inside a job are recorded. At a stop, the scheduler waits for running jobs. | Update `NoWarn`, `.editorconfig` and `#pragma` lines that name the old diagnostic ids. |
| [1.2.0](https://github.com/msx752/SharpTicker/releases/tag/v1.2.0) | The priority on `[TickerFunction]` takes effect. Dashboard: status of a chain, columns, wording, statistics. | None. |
| 1.2.1, 1.2.2, 1.2.3 | Fixes; their notes list no behaviour change to check. | None. |
| [1.3.0](https://github.com/msx752/SharpTicker/releases/tag/v1.3.0) | On EF Core and MongoDB, the chain rules also apply to rows stored before the upgrade. An inline child is `InProgress` while it runs. `UpdatedAt` of a running job holds its claim marker. An ended job is not run again by saving it. Filters on `DueDone` see fewer rows. Custom persistence providers keep the behaviour of 1.2.3 for the fenced writes. Redis stores each chain child as a record of its own. | Upgrade all instances together. Do not go back to 1.2.3 or earlier once chains exist. With Redis as the job store, give every running instance its own `NodeIdentifier`: an instance that starts now releases what an earlier run under its name left locked. |
| [1.3.1](https://github.com/msx752/SharpTicker/releases/tag/v1.3.1) | Stored copies of a cron seeded from code are deleted at the first start, with their occurrence history; one of them stays. A chain child without a registered function ends `Failed`. `UpdateAsync` keeps the parent of a stored child. | None. |
| [1.4.0](https://github.com/msx752/SharpTicker/releases/tag/v1.4.0) | Chain children without a time that earlier versions left `Idle` after their parent ended are run or skipped by the first fallback pass, however old they are; one that runs ends `DueDone`. Some log texts changed. Redis keeps one more key, `tq:tt:waiting-children`. | Before upgrading, delete the `Idle` children you do not want to run. |

Coming from the project SharpTicker was forked from: see the [migration guide](migrating.md).
