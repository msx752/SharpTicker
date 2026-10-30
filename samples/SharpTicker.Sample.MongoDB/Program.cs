using SharpTicker.Dashboard.DependencyInjection;
using SharpTicker.DependencyInjection;
using SharpTicker.MongoDB.DependencyInjection;
using SharpTicker.Utilities.Base;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Interfaces.Managers;

var builder = WebApplication.CreateBuilder(args);

// SharpTicker setup with MongoDB operational store.
// Start a local Mongo with: docker run -d -p 27017:27017 mongo:7
builder.Services.AddSharpTicker(options =>
{
    options.AddOperationalStore(mongoOptions =>
    {
        mongoOptions.UseSharpTickerMongoClient(
            connectionString: builder.Configuration["Mongo:ConnectionString"] ?? "mongodb://localhost:27017",
            databaseName: builder.Configuration["Mongo:Database"] ?? "sharpticker");
    });
    options.AddDashboard();
});

var app = builder.Build();

// Activate SharpTicker job processor (indexes are provisioned automatically by IHostedService)
app.UseSharpTicker();

// The browser opened by launchSettings.json lands on the site root; send it to the dashboard
app.MapGet("/", () => Results.Redirect("/ticker/dashboard"));

// Minimal endpoint to schedule the sample job
app.MapPost("/schedule-sample", async (ITimeTickerManager<TimeTickerEntity> manager) =>
{
    var result = await manager.AddAsync(new TimeTickerEntity
    {
        Function = "MongoSample_HelloWorld",
        ExecutionTime = DateTime.UtcNow.AddSeconds(5)
    });

    return Results.Ok(new { result.Result.Id, ScheduledFor = result.Result.ExecutionTime });
});

app.Run();

public class SampleJobs
{
    [TickerFunction("MongoSample_HelloWorld")]
    public Task HelloWorldAsync(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[Mongo] Hello from SharpTicker! Id={context.Id}, ScheduledFor={context.ScheduledFor:O}");
        return Task.CompletedTask;
    }
}
