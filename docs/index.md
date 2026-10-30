---
_layout: landing
---

# SharpTicker

Background job scheduling for .NET: one-off and cron jobs with source-generated registration, pluggable persistence and a built-in dashboard.

```bash
dotnet add package SharpTicker
```

- **Time and cron jobs.** Run a job once at a point in time, or repeatedly with a cron expression that includes seconds.
- **Source-generated registration.** Job methods are found at compile time, with no runtime assembly scanning. Invalid jobs are compile errors.
- **Pluggable persistence.** In-memory by default, or Entity Framework Core, MongoDB or Redis.
- **Built-in dashboard.** A real-time UI to watch, run and manage jobs.
- **Retries and chains.** Per-job retry counts and intervals, and child jobs that run depending on how their parent ended.
- **Several nodes, one store.** Instances that share a store coordinate through locks, so a job runs once.
- **.NET 8, 9 and 10.** Every package contains a separate build for each target.

## Where to start

- [Getting started](getting-started.md): install, register, write and schedule a first job.
- [Defining jobs](jobs.md): job methods, payloads, dependency injection and code-first registration.
- [Scheduling](scheduling.md): time jobs, cron jobs, retries and chains.
- [Storage](storage.md) and [Dashboard](dashboard.md) for persistence and the UI.
- [Running in production](operations.md): several nodes, start modes, error handling and tracing.
- [API reference](api/index.md), generated from the source.

Moving an existing application to SharpTicker? See the [migration guide](migrating.md).

SharpTicker is open source under MIT or Apache-2.0. The source, issues and samples are on [GitHub](https://github.com/msx752/SharpTicker).
