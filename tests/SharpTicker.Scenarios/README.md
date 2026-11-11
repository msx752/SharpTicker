# Scenario tests

End-to-end tests that run real SharpTicker nodes in containers against a real store, inject faults (stops, kills,
pauses, store cuts, latency) and check what the scheduler did. Each scenario checks one fixed bug and fails on the code
before its fix and passes on the current code.

These tests are local only: they are not part of `SharpTicker.slnx` and do not run in CI. A full run takes a long time.

## What runs

- `tests/SharpTicker.Scenarios.Host`: the node, an ASP.NET Core app built from the `src` projects (not packages). The
  store, the node identifier and the timings come from environment variables. Test-only endpoints under `/scenario/*`
  add jobs; the dashboard API is used to read them. Every job start and end writes one `SCENARIO_RUN {...}` JSON line.
- `tests/SharpTicker.Scenarios`: the xUnit tests. Each scenario starts a Docker network, a store (PostgreSQL 16,
  MongoDB 7 or Redis 7), a Toxiproxy in front of the store, and the nodes. Faults go through Docker and Toxiproxy.
  A scenario can give a node a store proxy of its own (the store is then cut for some nodes and not for others), and can
  add a Redis next to an EF Core store for the node heartbeat.

## Prerequisites

- Docker, with the `docker` CLI on the `PATH`.
- The .NET 10 SDK.

The node image is built once per run from the working tree (`sharpticker-scenarios-host:dev`).

## Running

```sh
# Everything except the soak (long).
dotnet test SharpTicker.Scenarios.slnx -c Release

# One scenario class, one test, or one store.
dotnet test SharpTicker.Scenarios.slnx -c Release --filter "FullyQualifiedName~StopScenarios"
dotnet test SharpTicker.Scenarios.slnx -c Release --filter "FullyQualifiedName~AGrandchild_RunsAfterItsParent"
dotnet test SharpTicker.Scenarios.slnx -c Release --filter "DisplayName~store: Redis"

# The harness unit tests only (no Docker).
dotnet test SharpTicker.Scenarios.slnx -c Release --filter "FullyQualifiedName~HarnessTests|FullyQualifiedName~InvariantTests|FullyQualifiedName~SummaryWriterTests|FullyQualifiedName~RedisIndexCheckTests"
```

Two tests run in parallel (`xunit.runner.json`). Each one starts its own store, proxy and nodes; with four at once
Docker Desktop stopped answering on the development machine.

### On the code before a fix

`SCENARIO_SOURCE_REF` builds the nodes from an older commit (a git worktree under `artifacts/scenarios/source-<sha>`)
with the current host project, so a scenario can be shown to fail on the code before its fix:

```sh
SCENARIO_SOURCE_REF="<fix commit>^1" dotnet test SharpTicker.Scenarios.slnx -c Release --no-build --filter "FullyQualifiedName~AGrandchild_RunsAfterItsParent"
```

Logs of such a run go to `artifacts/scenarios/at-<ref>/`. Remove the worktrees afterwards with `git worktree remove`.

A red run on an older commit proves the fix only if it fails on that fix's bug: older code can also fail on a bug fixed
earlier or later (a store that could not read its rows yet, a run-now that answered 500). When the code before the fix
cannot show the bug at all (before #61 the worker threads had no names, so the #23 scenario cannot read worker ids
there), the control is the current code with only that fix taken out, as a local commit given to `SCENARIO_SOURCE_REF`.

The composite scenarios and the soak have no single commit before a fix: they run several faults together (kills,
stops, pauses, store cuts, latency) and check the invariants (no job started twice without a reason, nothing left
InProgress, every due job ended). They do not prove one fix; the scenarios above do. Rows left by #84 (open) and by
the documented at-least-once limit of #74 are allowed only when an injected fault explains them, and are counted in the
output. That limit is a node killed or restarted before it wrote a lost outcome again; a node that lives on writes it
(`AStoreCutAtTheEndOfJobs_TheirOutcomesAreStoredOnceTheStoreIsBack`).

### The soak

```sh
SCENARIO_SOAK=1 dotnet test SharpTicker.Scenarios.slnx -c Release --filter "Category=Soak"
```

An hour of mixed load without faults, then half an hour with random faults, on each store (the three stores
run one after another, about 1.5 h each). Without `SCENARIO_SOAK=1` the soak shows as skipped.

### Random faults and the seed

The composite scenarios and the soak inject random faults. The seed and every fault are written to `faults.txt` in the
scenario's log folder; `SCENARIO_SEED=<seed>` repeats the same sequence.

## Logs and the summary

Each scenario writes to `artifacts/scenarios/<scenario>-<store>/`: one log per container (`node-0.log`, a restarted node
as `node-0-r1.log`, `store.log`, `proxy.log`, `heartbeat.log` for a heartbeat Redis), `run.json` (the test, the counts of jobs, starts and ends), and for
some scenarios `faults.txt` or `memory.csv`. A failing wait names the containers' state and this folder.

The summary (`artifacts/scenarios/summary.md`, one row per test, failures first) is written in a second step from the
run's TRX file:

```sh
dotnet test SharpTicker.Scenarios.slnx -c Release --logger "trx;LogFileName=scenarios.trx" --results-directory artifacts/scenarios
SCENARIO_SUMMARY_TRX=artifacts/scenarios/scenarios.trx dotnet test SharpTicker.Scenarios.slnx -c Release --no-build --filter "Category=Summary"
```

Use the same `SCENARIO_SOURCE_REF` in both steps for a run on an older commit.

## Adding a scenario

1. If the scenario needs a new kind of job, add it to `ScenarioJobs.cs` in the host, with its run-log lines.
2. Start an environment with `ScenarioEnvironment.StartAsync("<scenario>", store, nodes: N)` in an `await using`, add jobs
   through `node.Api`, inject the fault, and wait with `env.UntilAsync(...)`, which names the state on a timeout.
3. Assert on the run log (`env.Runs`: starts and ends per job, node, thread, time) and on the dashboard API
   (`node.Dashboard`), not on timing alone.
4. Show that it can fail: run it with `SCENARIO_SOURCE_REF` set to the commit before the fix it covers, check that it
   fails for that bug (read the message and the node log), and that it passes on the working tree. Repeat it a few times
   for flakiness.
