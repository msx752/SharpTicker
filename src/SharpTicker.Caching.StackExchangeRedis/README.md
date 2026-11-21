# SharpTicker.Caching.StackExchangeRedis

A Redis store for [SharpTicker](https://github.com/msx752/SharpTicker), a background job scheduler for .NET, built on `StackExchange.Redis`. It keeps time jobs, cron jobs and their runs in Redis, and each node writes a heartbeat there so that the other nodes can release the jobs of a node that stopped.

## Install

```bash
dotnet add package SharpTicker.Caching.StackExchangeRedis
```

## Setup

```csharp
using SharpTicker.Caching.StackExchangeRedis.DependencyInjection;
using SharpTicker.DependencyInjection;

builder.Services.AddSharpTicker(options =>
{
    options.AddStackExchangeRedis(redis =>
    {
        redis.Configuration = builder.Configuration.GetConnectionString("Redis");
    });
});
```

Set one of `Configuration`, `ConfigurationOptions`, `ConnectionMultiplexer` or `ConnectionMultiplexerFactory`; startup fails otherwise. `NodeHeartbeatInterval` (default 1 minute) sets how often each node writes its heartbeat.

## Job store or heartbeat only

- **Redis alone:** Redis is the job store.
- **Next to the EF Core or MongoDB store:** that store keeps the jobs, and Redis carries the node heartbeat (with EF Core also a short-lived copy of the cron list).

Redis Cluster is not supported as the job store: the store reads several job records with one command, which a cluster refuses when the keys are in different hash slots. Use a standalone Redis, Sentinel or a managed primary/replica setup. Next to another store, Redis is used with single-key calls only, which a cluster serves.

## Documentation

[Storage guide](https://msx752.github.io/SharpTicker/guide/storage.html): the Redis options and limits, the heartbeat, and the other stores.
