# SharpTicker.MongoDB

A MongoDB store for [SharpTicker](https://github.com/msx752/SharpTicker), a background job scheduler for .NET. It keeps time jobs, cron jobs and their runs in MongoDB through the official `MongoDB.Driver`.

## Install

```bash
dotnet add package SharpTicker.MongoDB
```

## Setup

```csharp
using SharpTicker.DependencyInjection;
using SharpTicker.MongoDB.DependencyInjection;

builder.Services.AddSharpTicker(options =>
{
    options.AddOperationalStore(mongo =>
    {
        mongo.UseSharpTickerMongoClient(
            connectionString: "mongodb://localhost:27017",
            databaseName: "sharpticker");
    });
});
```

To reuse an `IMongoClient` that is already registered in dependency injection, call `mongo.UseExistingMongoClient(databaseName: "sharpticker")` instead. One of the two is required; startup fails otherwise.

Collections use the prefix `ticker_` (`ticker_TimeTickers`, `ticker_CronTickers`, `ticker_CronTickerOccurrences`); change it with `mongo.SetCollectionPrefix("...")`. Indexes are created at startup.

## Documentation

[Storage guide](https://msx752.github.io/SharpTicker/guide/storage.html): the MongoDB options, the other stores, and what happens when an instance stops.
