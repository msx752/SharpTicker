using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces.Managers;

namespace SharpTicker.Scenarios.Host;

/// <summary>A job to seed: a time ticker, optionally with children (a chain).</summary>
public sealed class TickerSeed
{
    public string Function { get; set; } = ScenarioJobs.Record;
    public DateTime? ExecutionTime { get; set; }
    public int Retries { get; set; }
    public int[]? RetryIntervals { get; set; }
    public WaitRequest? Request { get; set; }
    public RunCondition? RunCondition { get; set; }
    public List<TickerSeed> Children { get; set; } = new();
}

public sealed class CronSeed
{
    public string Function { get; set; } = ScenarioJobs.Record;
    public string Expression { get; set; } = "* * * * * *";
}

/// <summary>
/// Test-only endpoints under <c>/scenario</c>: they seed jobs through the managers, so the scenario tests never
/// write to a store directly.
/// </summary>
public static class ScenarioEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/scenario");

        group.MapGet("/health", () => Results.Ok(new { node = RunLog.Node, now = DateTimeOffset.UtcNow }));

        group.MapPost("/time-tickers", async (List<TickerSeed> seeds, ITimeTickerManager<TimeTickerEntity> manager, CancellationToken ct) =>
        {
            var entities = seeds.Select(ToEntity).ToList();
            var result = await manager.AddBatchAsync(entities, ct);
            return result.IsSucceeded
                ? Results.Ok(result.Result.Select(e => new { id = e.Id }))
                : Results.Problem(result.Exception?.ToString());
        });

        group.MapPost("/chains", async (TickerSeed root, ITimeTickerManager<TimeTickerEntity> manager, CancellationToken ct) =>
        {
            var result = await manager.AddAsync(ToEntity(root), ct);
            return result.IsSucceeded
                ? Results.Ok(new { id = result.Result.Id, tree = Ids(result.Result) })
                : Results.Problem(result.Exception?.ToString());
        });

        group.MapPost("/crons", async (CronSeed seed, ICronTickerManager<CronTickerEntity> manager, CancellationToken ct) =>
        {
            var result = await manager.AddAsync(new CronTickerEntity { Function = seed.Function, Expression = seed.Expression }, ct);
            return result.IsSucceeded
                ? Results.Ok(new { id = result.Result.Id })
                : Results.Problem(result.Exception?.ToString());
        });

        // Starves the .NET thread pool for the given seconds: it is capped at the processor count and every pool thread is
        // then blocked. The answer is sent before the pool is blocked; while it lasts the node cannot answer HTTP.
        group.MapPost("/starve-pool", (int seconds) =>
        {
            ThreadPool.GetMinThreads(out var minWorkers, out var minIo);
            ThreadPool.GetMaxThreads(out var maxWorkers, out var maxIo);
            var cap = Environment.ProcessorCount;
            if (!ThreadPool.SetMinThreads(Math.Min(minWorkers, cap), minIo) || !ThreadPool.SetMaxThreads(cap, maxIo))
                return Results.Problem($"The thread pool could not be capped at {cap} threads.", statusCode: 500);

            // Node clock: the starvation runs from 'from' to 'until'.
            var from = DateTime.UtcNow.AddMilliseconds(200);
            var until = from.AddSeconds(seconds);
            var starver = new Thread(() =>
            {
                var wait = from - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                    Thread.Sleep(wait);
                // More blocking items than pool threads, also when the pool had grown past the cap before.
                var blockers = Math.Max(cap, ThreadPool.ThreadCount) + cap;
                using var done = new CountdownEvent(blockers);
                for (var i = 0; i < blockers; i++)
                {
                    ThreadPool.UnsafeQueueUserWorkItem(_ =>
                    {
                        var left = until - DateTime.UtcNow;
                        if (left > TimeSpan.Zero)
                            Thread.Sleep(left);
                        done.Signal();
                    }, null);
                }

                done.Wait();
                ThreadPool.SetMaxThreads(maxWorkers, maxIo);
                ThreadPool.SetMinThreads(minWorkers, minIo);
                Console.Out.WriteLine("SCENARIO_POOL released");
            }) { IsBackground = true, Name = "Scenario.PoolStarver" };
            starver.Start();
            Console.Out.WriteLine($"SCENARIO_POOL starving {cap} threads from {from:O} until {until:O}");
            return Results.Ok(new { cap, from, until });
        });

        group.MapDelete("/time-tickers/{id:guid}", async (Guid id, ITimeTickerManager<TimeTickerEntity> manager, CancellationToken ct) =>
        {
            var result = await manager.DeleteAsync(id, ct);
            return result.IsSucceeded ? Results.Ok() : Results.Problem(result.Exception?.ToString());
        });
    }

    private static TimeTickerEntity ToEntity(TickerSeed seed)
    {
        var entity = new TimeTickerEntity
        {
            Function = seed.Function,
            ExecutionTime = seed.ExecutionTime,
            Retries = seed.Retries,
            RetryIntervals = seed.RetryIntervals,
            RunCondition = seed.RunCondition,
        };
        if (seed.Request != null)
            entity.Request = TickerHelper.CreateTickerRequest(seed.Request);
        foreach (var child in seed.Children)
            entity.Children.Add(ToEntity(child));
        return entity;
    }

    // The ids of the tree in depth-first order (root first), so a test can address every node it seeded.
    private static List<Guid> Ids(TimeTickerEntity entity)
    {
        var ids = new List<Guid> { entity.Id };
        foreach (var child in entity.Children)
            ids.AddRange(Ids(child));
        return ids;
    }
}
