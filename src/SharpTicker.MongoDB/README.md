# SharpTicker.MongoDB

MongoDB persistence provider for [SharpTicker](https://github.com/msx752/SharpTicker). Drop-in alternative to `SharpTicker.EntityFrameworkCore` — use one or the other, not both.

Uses the official `MongoDB.Driver` directly. No EF Core, no ODM.

## Install

```bash
dotnet add package SharpTicker.MongoDB
```

## Usage

```csharp
builder.Services.AddSharpTicker(options =>
{
    options.AddOperationalStore(mongoOptions =>
    {
        mongoOptions.UseSharpTickerMongoClient(
            connectionString: "mongodb://localhost:27017",
            databaseName: "sharpticker");
    });
});
```

Or reuse an `IMongoClient` already registered in DI:

```csharp
services.AddSingleton<IMongoClient>(_ => new MongoClient("mongodb://localhost:27017"));

builder.Services.AddSharpTicker(options =>
{
    options.AddOperationalStore(mongoOptions =>
    {
        mongoOptions.UseExistingMongoClient(databaseName: "sharpticker");
    });
});
```

## Notes

- Concurrency uses single-document atomic `findAndModify` with an `UpdatedAt` CAS guard. No MongoDB transactions needed — works on standalone Mongo, replica sets, and Atlas free tier.
- Indexes are created idempotently on startup, including a unique index on `(CronTickerId, ExecutionTime)` for cron occurrence deduplication.
- Collection names default to `ticker_TimeTickers`, `ticker_CronTickers`, `ticker_CronTickerOccurrences`. Override with `SetCollectionPrefix`.
