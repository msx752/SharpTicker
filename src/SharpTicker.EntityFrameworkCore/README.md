# SharpTicker.EntityFrameworkCore

An Entity Framework Core store for [SharpTicker](https://github.com/msx752/SharpTicker), a background job scheduler for .NET. It keeps time jobs, cron jobs and their runs in a relational database, so they survive restarts and several instances can share them.

## Install

```bash
dotnet add package SharpTicker.EntityFrameworkCore
```

Add the EF Core provider for your database as well, for example `Npgsql.EntityFrameworkCore.PostgreSQL`. The [Storage guide](https://msx752.github.io/SharpTicker/guide/storage.html) lists the providers our tests run on.

## Setup

With a separate context that SharpTicker brings:

```csharp
using Microsoft.EntityFrameworkCore;
using SharpTicker.DependencyInjection;
using SharpTicker.EntityFrameworkCore.DbContextFactory;
using SharpTicker.EntityFrameworkCore.DependencyInjection;

builder.Services.AddSharpTicker(options =>
{
    options.AddOperationalStore(ef =>
    {
        ef.UseSharpTickerDbContext<SharpTickerDbContext>(db =>
            db.UseNpgsql(builder.Configuration.GetConnectionString("Scheduler"),
                npgsql => npgsql.MigrationsAssembly("MyApp")));
    });
});
```

Then create the tables with EF Core migrations (the `dotnet-ef` tool and `Microsoft.EntityFrameworkCore.Design` in the application project):

```bash
dotnet ef migrations add AddSharpTicker --context SharpTickerDbContext
dotnet ef database update --context SharpTickerDbContext
```

To keep the tables in your application's own context and migrations instead, use `ef.UseApplicationDbContext<AppDbContext>(ConfigurationType.UseModelCustomizer)`. Tables are created in the `ticker` schema; change it with `ef.SetSchema("...")`.

## Documentation

[Storage guide](https://msx752.github.io/SharpTicker/guide/storage.html): both ways to set up the context, schema and tables, the lease on running jobs, and the other stores.
