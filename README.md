<h1 align="center">
  <img src="assets/logo.svg" alt="SharpTicker logo" width="120" />
  <br />
  SharpTicker
</h1>

<p align="center">
  <strong>Background job scheduling for .NET</strong><br />
  One-off and cron jobs with source-generated registration, pluggable persistence and a built-in dashboard.<br />
  <sub>An independently maintained open-source project with its own roadmap. See <a href="#origin">Origin</a>.</sub>
</p>

<p align="center">
  <a href="https://github.com/msx752/SharpTicker/actions/workflows/ci.yml"><img src="https://github.com/msx752/SharpTicker/actions/workflows/ci.yml/badge.svg?branch=main" alt="CI" /></a>
  <a href="https://www.nuget.org/packages/SharpTicker"><img src="https://img.shields.io/nuget/v/SharpTicker?logo=nuget" alt="NuGet version" /></a>
  <a href="https://www.nuget.org/packages/SharpTicker"><img src="https://img.shields.io/nuget/dt/SharpTicker?logo=nuget" alt="NuGet downloads" /></a>
  <a href="https://github.com/msx752/SharpTicker/stargazers"><img src="https://img.shields.io/github/stars/msx752/SharpTicker?logo=github" alt="GitHub stars" /></a>
</p>

<p align="center">
  <a href="https://msx752.github.io/SharpTicker/">Documentation</a> &middot;
  <a href="https://github.com/msx752/SharpTicker">Star</a> &middot;
  <a href="https://github.com/msx752/SharpTicker/fork">Fork</a>
</p>

---

## Highlights

- **Time and cron jobs**: schedule one-off jobs at a point in time, or recurring jobs with 6-field (seconds) cron expressions.
- **Source-generated registration**: job functions are discovered at compile time, with no runtime assembly scanning.
- **Pluggable persistence**: in-memory, Entity Framework Core (SQL Server, PostgreSQL, SQLite, MySQL, ...), MongoDB or Redis.
- **Built-in dashboard**: a real-time UI over SignalR to monitor, inspect, run and manage jobs.
- **Retries**: per-job retry counts and intervals.
- **Multiple nodes**: lock-based coordination so several instances can share one store.
- **OpenTelemetry**: optional tracing for job execution.
- **.NET 8, 9 and 10 in one package**: every package contains a separate build for each target, and NuGet picks the one that matches your project.

## Quick start

```bash
dotnet add package SharpTicker
```

### 1. Register the scheduler

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSharpTicker();

var app = builder.Build();
app.UseSharpTicker();
app.Run();
```

### 2. Write a job

```csharp
using SharpTicker.Utilities.Base;

public class MyJobs
{
    [TickerFunction("HelloWorld")]
    public Task HelloWorld(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Hello from SharpTicker! Job ID: {context.Id}");
        return Task.CompletedTask;
    }
}
```

### 3. Schedule it

```csharp
public class MyService(ITimeTickerManager<TimeTickerEntity> manager)
{
    public Task ScheduleAsync() =>
        manager.AddAsync(new TimeTickerEntity
        {
            Function = "HelloWorld",
            ExecutionTime = DateTime.UtcNow.AddSeconds(10)
        });
}
```

More complete setups are in [`samples/`](samples).

## Packages

| Package | Description |
|---------|-------------|
| `SharpTicker` | Scheduler engine (includes the source generator) |
| `SharpTicker.Utilities` | Shared entities, interfaces and helpers |
| `SharpTicker.EntityFrameworkCore` | Entity Framework Core persistence |
| `SharpTicker.MongoDB` | MongoDB persistence |
| `SharpTicker.Caching.StackExchangeRedis` | Redis persistence and coordination |
| `SharpTicker.Dashboard` | Real-time dashboard UI |
| `SharpTicker.Instrumentation.OpenTelemetry` | OpenTelemetry tracing |
| `SharpTicker.SourceGenerator` | Compile-time job registration (already bundled with `SharpTicker`) |

All packages share one version number and are released together; keep them on the same version. Each package contains three builds side by side (`lib/net8.0`, `lib/net9.0`, `lib/net10.0`), each compiled against the matching major version of Microsoft.Extensions and Entity Framework Core.

## Versioning

SharpTicker follows [Semantic Versioning](https://semver.org), with one version line for all packages:

- **Major** (`2.0.0`): breaking changes to the public API, behavior or database schema, and dropping a target framework.
- **Minor** (`1.1.0`): new features and new target frameworks. APIs are marked `[Obsolete]` before they are removed in a later major version.
- **Patch** (`1.0.1`): bug and security fixes.

**Supported .NET versions.** Every 1.x release targets .NET 8, 9 and 10. Microsoft ends support for .NET 8 and .NET 9 on November 10, 2026, but SharpTicker keeps building for them throughout 1.x, so updating SharpTicker never forces you to upgrade .NET within a major version. They will be dropped no earlier than 2.0.0.

## Building from source

Requirements: the .NET 10 SDK (pinned in `global.json`), plus the .NET 8 and 9 runtimes to run the tests on every target. Node.js 20+ is only needed for the dashboard UI; without it the libraries still build and test.

```bash
# Dashboard UI (embedded into SharpTicker.Dashboard)
cd src/SharpTicker.Dashboard/wwwroot
npm ci
npm run build
cd ../../..

dotnet build SharpTicker.slnx -c Release
dotnet test SharpTicker.slnx -c Release
```

## Documentation

Documentation: **[msx752.github.io/SharpTicker](https://msx752.github.io/SharpTicker/)**. Its source is in [`docs/`](docs).

## Contributing

Issues and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Origin

SharpTicker began as a fork of the MIT/Apache-2.0 dual-licensed TickerQ, based on commit [`3acd0f314a351817a3159d7d29a3798c8612a263`](https://github.com/msx752/TickerQ/commit/3acd0f314a351817a3159d7d29a3798c8612a263) (2026-07-05), and has been modified since. The original copyright notices are retained in [LICENSE](LICENSE).

SharpTicker **1.0.0** corresponds to TickerQ **10.4.0**, the last release published under MIT/Apache-2.0, plus the changes merged after it up to the base commit above: the MongoDB persistence provider, tracing diagnostics moved into the main package, and an OpenTelemetry security update. It contains no code from later TickerQ releases.

### A new project, not a replacement

The fork is where SharpTicker starts, not what it intends to stay. Version 1.0.0 is the one release built to match its origin, so existing users have a clear and predictable way across. From there on, SharpTicker is its own project: it is maintained independently, follows its own roadmap and release cycle, and makes its own design decisions.

In practice this means:

- **Divergence is expected.** APIs, behavior and storage will evolve on their own terms after 1.0.0. Breaking changes follow [Semantic Versioning](https://semver.org) and are announced in the release notes.
- **No tracking of the original project.** Changes made there are not followed, mirrored or ported, and SharpTicker makes no compatibility promise beyond 1.0.0.
- **No affiliation.** SharpTicker is not affiliated with, endorsed by or supported by the original authors. Please report SharpTicker issues here, not upstream.

### Migrating to 1.0.0

Moving from TickerQ? Follow the **[migration guide](MIGRATING.md)**.

## License

Dual-licensed under the [MIT License](LICENSE) or the [Apache License 2.0](LICENSE), at your option.
