using Microsoft.EntityFrameworkCore;
using SharpTicker.Dashboard.DependencyInjection;
using SharpTicker.DependencyInjection;
using SharpTicker.EntityFrameworkCore.DbContextFactory;
using SharpTicker.EntityFrameworkCore.DependencyInjection;
using SharpTicker.Utilities.Base;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Interfaces.Managers;

var builder = WebApplication.CreateBuilder(args);

// SharpTicker setup with SQLite operational store (file-based)
builder.Services.AddSharpTicker(options =>
{
    options.AddOperationalStore(efOptions =>
    {
        efOptions.UseSharpTickerDbContext<SharpTickerDbContext>(dbOptions =>
        {
            dbOptions.UseSqlite(
                "Data Source=sharpticker-webapi.db",
                b => b.MigrationsAssembly("SharpTicker.Sample.WebApi"));
        });
    });
    options.AddDashboard();
});

var app = builder.Build();

// Ensure SharpTicker operational store schema is applied
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SharpTickerDbContext>();
    db.Database.Migrate();
}

// Activate SharpTicker job processor (mirrors docs' minimal setup)
app.UseSharpTicker();

// Minimal endpoint to schedule the sample job
app.MapPost("/schedule-sample", async (ITimeTickerManager<TimeTickerEntity> manager) =>
{
    var result = await manager.AddAsync(new TimeTickerEntity
    {
        Function = "WebApiSample_HelloWorld",
        ExecutionTime = DateTime.UtcNow.AddSeconds(5)
    });

    return Results.Ok(new { result.Result.Id, ScheduledFor = result.Result.ExecutionTime });
});

app.Run();

// Simple sample job
public class SampleJobs
{
    [TickerFunction("WebApiSample_HelloWorld")]
    public Task HelloWorldAsync(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[WebApi] Hello from SharpTicker! Id={context.Id}, ScheduledFor={context.ScheduledFor:O}");
        return Task.CompletedTask;
    }
}
