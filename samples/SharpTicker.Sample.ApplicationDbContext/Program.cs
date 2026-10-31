using Microsoft.EntityFrameworkCore;
using SharpTicker.DependencyInjection;
using SharpTicker.EntityFrameworkCore.DependencyInjection;
using SharpTicker.Sample.ApplicationDbContext.Data;
using SharpTicker.Utilities.Base;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Interfaces.Managers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(dbOptions =>
{
    dbOptions.UseSqlite(
        "Data Source=sharpticker-webapi.db",
        b => b.MigrationsAssembly("SharpTicker.Sample.ApplicationDbContext"));
});

// SharpTicker setup with SQLite operational store (file-based)
builder.Services.AddSharpTicker(options =>
{
    options.AddOperationalStore(efOptions =>
    {
        efOptions.UseApplicationDbContext<AppDbContext>(SharpTicker.EntityFrameworkCore.Customizer.ConfigurationType.UseModelCustomizer);
    });
});

var app = builder.Build();

// Ensure SharpTicker operational store schema is applied
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

// Activate SharpTicker job processor (mirrors docs' minimal setup)
app.UseSharpTicker();

// Landing page so the browser opened by launchSettings.json shows something useful
app.MapGet("/", () => Results.Text(
    "SharpTicker + your own EF Core DbContext sample (no dashboard here).\n" +
    "Schedule the sample job: POST /schedule-sample"));

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
