# SharpTicker.Utilities

Shared types of [SharpTicker](https://github.com/msx752/SharpTicker), a background job scheduler for .NET: the ticker entities, the `[TickerFunction]` attribute, the managers' interfaces (`ITimeTickerManager<T>`, `ICronTickerManager<T>`), the enums, and the core options builders (`TickerOptionsBuilder`, `SchedulerOptionsBuilder`).

## Install

Most applications do not add this package directly: `SharpTicker`, the store packages and the dashboard depend on it.

```bash
dotnet add package SharpTicker
```

Add `SharpTicker.Utilities` on its own to a project that only refers to these types, for example a class library that defines custom ticker types or schedules jobs through the manager interfaces. Keep it on the same version as the other SharpTicker packages.

```bash
dotnet add package SharpTicker.Utilities
```

## Documentation

- [Getting started](https://msx752.github.io/SharpTicker/guide/getting-started.html)
- [API reference](https://msx752.github.io/SharpTicker/api/index.html)
