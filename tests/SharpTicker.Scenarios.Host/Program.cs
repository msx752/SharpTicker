using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SharpTicker.Caching.StackExchangeRedis.DependencyInjection;
using SharpTicker.Dashboard.DependencyInjection;
using SharpTicker.DependencyInjection;
using SharpTicker.EntityFrameworkCore.DbContextFactory;
using SharpTicker.EntityFrameworkCore.DependencyInjection;
using SharpTicker.MongoDB.DependencyInjection;
using SharpTicker.Scenarios.Host;
using EfStore = SharpTicker.EntityFrameworkCore.DependencyInjection.ServiceExtension;
using MongoStore = SharpTicker.MongoDB.DependencyInjection.ServiceExtension;

// The scenario node. Everything it needs comes from environment variables set by the scenario tests:
//   SCENARIO_STORE           efcore-postgres | mongodb | redis
//   SCENARIO_CONNECTION      the store's connection string
//   SCENARIO_DATABASE        the MongoDB database name
//   SCENARIO_NODE            the node identifier (also written in every run-log line)
//   SCENARIO_REDIS_INSTANCE  the Redis InstanceName (key prefix), unique per scenario
//   SCENARIO_CREATE_SCHEMA   1 = create the EF Core schema before starting (one node per scenario)
//   SCENARIO_HEARTBEAT_REDIS optional, with a store other than Redis (run with EF Core only so far): a Redis connection
//                            string; the node then uses Redis next to its store (node heartbeat, release of nodes whose
//                            heartbeat stopped)
//   SCENARIO_MAX_CONCURRENCY      optional: the scheduler's MaxConcurrency
//   SCENARIO_IDLE_WORKER_TIMEOUT  optional: the scheduler's IdleWorkerTimeOut, in seconds
//   SCENARIO_MIGRATIONS      optional, EF Core: documented | unedited = use that context and its migrations (the migration scenarios);
//                            the node applies them at every start
//   SCENARIO_MIGRATE_TO      optional, with SCENARIO_MIGRATIONS: apply the migrations up to this one, then exit
var store = Required("SCENARIO_STORE");
var connection = Required("SCENARIO_CONNECTION");
var node = Required("SCENARIO_NODE");
var migrations = Environment.GetEnvironmentVariable("SCENARIO_MIGRATIONS");
RunLog.Node = node;

var builder = WebApplication.CreateBuilder(args);

// Longer than the stop wait of the scheduler, shorter than the container stop timeout of the tests (20 s).
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(10));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

ScenarioJobs.Register(builder.Services);

builder.Services.AddSharpTicker(options =>
{
    options.ConfigureScheduler(s =>
    {
        s.NodeIdentifier = node;
        s.MinPollingInterval = TimeSpan.FromSeconds(1);
        s.FallbackIntervalChecker = TimeSpan.FromSeconds(2);
        if (int.TryParse(Environment.GetEnvironmentVariable("SCENARIO_MAX_CONCURRENCY"), out var maxConcurrency))
            s.MaxConcurrency = maxConcurrency;
        if (double.TryParse(Environment.GetEnvironmentVariable("SCENARIO_IDLE_WORKER_TIMEOUT"), System.Globalization.CultureInfo.InvariantCulture, out var idleSeconds))
            s.IdleWorkerTimeOut = TimeSpan.FromSeconds(idleSeconds);
    });

    switch (store)
    {
        case "efcore-postgres":
            EfStore.AddOperationalStore(options, ef =>
            {
                switch (migrations)
                {
                    case null or "":
                        ef.UseSharpTickerDbContext<SharpTickerDbContext>(db => db.UseNpgsql(connection));
                        break;
                    case "documented":
                        ef.UseSharpTickerDbContext<DocumentedContext>(db => db.UseNpgsql(connection));
                        break;
                    case "unedited":
                        ef.UseSharpTickerDbContext<UneditedContext>(db => db.UseNpgsql(connection));
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown SCENARIO_MIGRATIONS '{migrations}'.");
                }
            });
            break;
        case "mongodb":
            MongoStore.AddOperationalStore(options, mongo =>
                mongo.UseSharpTickerMongoClient(connection, Required("SCENARIO_DATABASE")));
            break;
        case "redis":
            AddRedis(connection);
            break;
        default:
            throw new InvalidOperationException($"Unknown SCENARIO_STORE '{store}'.");
    }

    // Redis next to another store: it is the job store only when no other store is registered.
    if (store != "redis" && Environment.GetEnvironmentVariable("SCENARIO_HEARTBEAT_REDIS") is { Length: > 0 } heartbeatRedis)
        AddRedis(heartbeatRedis);

    void AddRedis(string redisConnection) => options.AddStackExchangeRedis(redis =>
    {
        redis.Configuration = redisConnection;
        redis.InstanceName = Required("SCENARIO_REDIS_INSTANCE");
        redis.NodeHeartbeatInterval = TimeSpan.FromSeconds(5);
    });

    options.AddDashboard(dashboard => dashboard.WithNoAuth());
});

var app = builder.Build();

if (store == "efcore-postgres" && !string.IsNullOrEmpty(migrations))
{
    using var scope = app.Services.CreateScope();
    DbContext db = migrations == "documented"
        ? scope.ServiceProvider.GetRequiredService<DocumentedContext>()
        : scope.ServiceProvider.GetRequiredService<UneditedContext>();
    var migrateTo = Environment.GetEnvironmentVariable("SCENARIO_MIGRATE_TO");
    if (!string.IsNullOrEmpty(migrateTo))
    {
        // Apply up to the named migration, then stop: the scenario writes rows in that older schema.
        await db.GetService<IMigrator>().MigrateAsync(migrateTo);
        Console.Out.WriteLine($"SCENARIO_MIGRATED to {migrateTo}");
        return;
    }

    await db.Database.MigrateAsync();
}
else if (store == "efcore-postgres" && Environment.GetEnvironmentVariable("SCENARIO_CREATE_SCHEMA") == "1")
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<SharpTickerDbContext>().Database.EnsureCreated();
}

app.UseSharpTicker();
ScenarioEndpoints.Map(app);

app.Run();

static string Required(string name)
    => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"The environment variable {name} is not set.");
