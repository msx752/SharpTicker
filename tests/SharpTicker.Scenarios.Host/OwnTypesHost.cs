using SharpTicker.Caching.StackExchangeRedis.DependencyInjection;
using SharpTicker.DependencyInjection;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Interfaces.Managers;

namespace SharpTicker.Scenarios.Host;

/// <summary>A base type nothing tells the serializer how to read back: a value of it is written as an empty object.</summary>
public abstract class OwnShape
{
}

public sealed class OwnCircle : OwnShape
{
}

/// <summary>
/// A time ticker type of the application's own, as an application may write one: a list, and a property of an abstract
/// type.
/// </summary>
public sealed class OwnTimeTicker : TimeTickerEntity<OwnTimeTicker>
{
    public List<string> Tags { get; set; } = [];

    public OwnShape? Shape { get; set; }
}

public sealed class OwnCronTicker : CronTickerEntity
{
}

/// <summary>A job of <see cref="OwnTimeTicker"/> to seed.</summary>
public sealed class OwnSeed
{
    public string Function { get; set; } = ScenarioJobs.Record;
    public DateTime? ExecutionTime { get; set; }
    public List<string> Tags { get; set; } = [];
    public bool WithShape { get; set; }
}

/// <summary>
/// The scenario node with ticker types of its own (<c>SCENARIO_OWN_TYPES=1</c>): Redis as the job store, the same
/// scheduler timings and jobs as the default node, and one endpoint that adds jobs of the own type through the manager.
/// It sets no <c>JsonSerializerContext</c>, as an application that is neither trimmed nor AOT has no need to (#162).
/// No dashboard: the scenarios read the store.
/// </summary>
public static class OwnTypesHost
{
    public static void Run(string[] args, string connection, string node, string redisInstance)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(10));
        ScenarioJobs.Register(builder.Services);

        builder.Services.AddSharpTicker<OwnTimeTicker, OwnCronTicker>(options =>
        {
            options.ConfigureScheduler(s =>
            {
                s.NodeIdentifier = node;
                s.MinPollingInterval = TimeSpan.FromSeconds(1);
                s.FallbackIntervalChecker = TimeSpan.FromSeconds(2);
            });
            options.AddStackExchangeRedis(redis =>
            {
                redis.Configuration = connection;
                redis.InstanceName = redisInstance;
                redis.NodeHeartbeatInterval = TimeSpan.FromSeconds(5);
            });
        });

        var app = builder.Build();
        app.UseSharpTicker();

        var group = app.MapGroup("/scenario");
        group.MapGet("/health", () => Results.Ok(new { node = RunLog.Node, now = DateTimeOffset.UtcNow }));
        group.MapPost("/own/time-tickers", async (List<OwnSeed> seeds, ITimeTickerManager<OwnTimeTicker> manager, CancellationToken ct) =>
        {
            var ids = new List<object>();
            foreach (var seed in seeds)
            {
                var result = await manager.AddAsync(new OwnTimeTicker
                {
                    Function = seed.Function,
                    ExecutionTime = seed.ExecutionTime,
                    Tags = seed.Tags,
                    Shape = seed.WithShape ? new OwnCircle() : null
                }, ct);
                if (!result.IsSucceeded)
                    return Results.Problem(result.Exception?.ToString());
                ids.Add(new { id = result.Result.Id });
            }

            return Results.Ok(ids);
        });

        app.Run();
    }
}
