---
_layout: landing
---

<div class="hero">
  <img src="../assets/logo.svg" alt="" width="96" height="96" />
  <div>

# SharpTicker

Background job scheduling for .NET: one-off and cron jobs with source-generated registration, pluggable persistence and a built-in dashboard.

  </div>
</div>

```bash
dotnet add package SharpTicker
```

<p class="support">
  <a class="support-btn" href="https://github.com/msx752/SharpTicker" rel="noopener"><svg class="support-icon" viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path fill="currentColor" d="M8 .25a.75.75 0 0 1 .673.418l1.882 3.815 4.21.612a.75.75 0 0 1 .416 1.279l-3.046 2.97.719 4.192a.751.751 0 0 1-1.088.791L8 12.347l-3.766 1.98a.75.75 0 0 1-1.088-.79l.72-4.194L.818 6.374a.75.75 0 0 1 .416-1.28l4.21-.611L7.327.668A.75.75 0 0 1 8 .25Z"/></svg> Star on GitHub</a>
  <a class="support-btn" href="https://github.com/msx752/SharpTicker/fork" rel="noopener"><svg class="support-icon" viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path fill="currentColor" d="M5 5.372v.878c0 .414.336.75.75.75h4.5a.75.75 0 0 0 .75-.75v-.878a2.25 2.25 0 1 1 1.5 0v.878a2.25 2.25 0 0 1-2.25 2.25h-1.5v2.128a2.251 2.251 0 1 1-1.5 0V8.5h-1.5A2.25 2.25 0 0 1 3.5 6.25v-.878a2.25 2.25 0 1 1 1.5 0ZM5 3.25a.75.75 0 1 0-1.5 0 .75.75 0 0 0 1.5 0Zm6.75.75a.75.75 0 1 0 0-1.5.75.75 0 0 0 0 1.5Zm-3 8.75a.75.75 0 1 0-1.5 0 .75.75 0 0 0 1.5 0Z"/></svg> Fork</a>
</p>

- **Time and cron jobs.** Run a job once at a point in time, or repeatedly with a cron expression that includes seconds.
- **Source-generated registration.** Job methods are found at compile time, with no runtime assembly scanning. Invalid jobs are compile errors.
- **Pluggable persistence.** In-memory by default, or Entity Framework Core, MongoDB or Redis.
- **Built-in dashboard.** A real-time UI to watch, run and manage jobs.
- **Retries and chains.** Per-job retry counts and intervals, and child jobs that run depending on how their parent ended.
- **Several nodes, one store.** Instances that share a store coordinate through locks, so a job runs once.
- **.NET 8, 9 and 10.** Every package contains a separate build for each target.

## Where to start

- [Getting started](guide/getting-started.md): install, register, write and schedule a first job.
- [Defining jobs](guide/jobs.md): job methods, payloads, dependency injection and code-first registration.
- [Scheduling](guide/scheduling.md): time jobs, cron jobs, retries and chains.
- [Storage](guide/storage.md) and [Dashboard](guide/dashboard.md) for persistence and the UI.
- [Running in production](guide/operations.md): several nodes, start modes, error handling and tracing.
- [API reference](api/index.md), generated from the source.

Moving an existing application to SharpTicker? See the [migration guide](guide/migrating.md).

SharpTicker is open source under MIT or Apache-2.0. The source, issues and samples are on [GitHub](https://github.com/msx752/SharpTicker).
