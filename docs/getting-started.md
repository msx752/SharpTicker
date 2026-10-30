# Getting started

## Install

```bash
dotnet add package SharpTicker
```

`SharpTicker` contains the scheduler and the source generator. Add other packages only for the features you use; see [Storage](storage.md), [Dashboard](dashboard.md) and [Running in production](operations.md). Keep every SharpTicker package on the same version.

## Register the scheduler

```csharp
using SharpTicker.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSharpTicker();

var app = builder.Build();
app.UseSharpTicker();
app.Run();
```

`AddSharpTicker()` registers the scheduler with an in-memory store. `UseSharpTicker()` is an extension on `IHost`, so it works in web apps, worker services and console hosts alike.

> [!NOTE]
> The in-memory store loses every job when the process stops. Use it for development and tests, and choose a persistent store for anything else.

## Write a job

A job is a public or internal method marked with `[TickerFunction]`:

```csharp
using SharpTicker.Utilities.Base;

public class EmailJobs
{
    [TickerFunction("SendWelcomeEmail")]
    public Task SendWelcomeEmail(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Running job {context.Id}");
        return Task.CompletedTask;
    }
}
```

The source generator registers it at compile time under the name `SendWelcomeEmail`. Mistakes such as a duplicate name, an unsupported parameter or an invalid cron expression are reported as compile errors.

## Schedule it

Inject `ITimeTickerManager<TimeTickerEntity>` and add a time job:

```csharp
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Interfaces.Managers;

public class SignupService(ITimeTickerManager<TimeTickerEntity> tickers)
{
    public Task OnSignupAsync() =>
        tickers.AddAsync(new TimeTickerEntity
        {
            Function = "SendWelcomeEmail",
            ExecutionTime = DateTime.UtcNow.AddMinutes(5)
        });
}
```

The job runs about five minutes later. Leave `ExecutionTime` empty to run it as soon as possible.

## Run it on a schedule

Give the attribute a cron expression to run the job repeatedly. The format has six fields, with seconds first:

```csharp
[TickerFunction("NightlyCleanup", "0 0 2 * * *")] // every day at 02:00:00
public Task NightlyCleanup(CancellationToken cancellationToken) => Task.CompletedTask;
```

Crons declared on attributes are created in the store at startup. See [Scheduling](scheduling.md) for the cron format and time zones.

## Next steps

- [Defining jobs](jobs.md): payloads, dependency injection and supported signatures.
- [Storage](storage.md): keep jobs across restarts.
- [Dashboard](dashboard.md): watch and manage jobs in a browser.
- The [samples](https://github.com/msx752/SharpTicker/tree/main/samples) folder has complete web, worker, console, MongoDB and dashboard setups.
