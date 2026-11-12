using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using SharpTicker.MongoDB.Infrastructure;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;

namespace SharpTicker.MongoDB.Tests;

/// <summary>
/// The seed of code-defined crons at startup (MigrateDefinedCronTickers) on MongoDB, when a function has more than one
/// stored cron: only the crons the seed made take the expression in code, never a user's cron of the same function.
/// </summary>
[Collection("Mongo")]
public class MongoCronSeedTests(MongoTestFixture fixture) : IClassFixture<MongoTestFixture>, IAsyncLifetime
{
    private const string Function = "cron-seed-fn";
    private const string Seeded = "MemoryTicker_Seeded_" + Function;

    public Task InitializeAsync()
    {
        // The seed removes the seeded crons of functions that are not registered; this one must stay.
        TickerFunctionProvider.RegisterFunctions(
            new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>
            {
                [Function] = ("", TickerTaskPriority.Normal, (_, _, _) => Task.CompletedTask, 0)
            });
        TickerFunctionProvider.Build();
        return fixture.DropAllAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private CronTickerEntity Cron(string expression, string? initIdentifier = null)
        => new()
        {
            Id = Guid.NewGuid(), Function = Function, Expression = expression, InitIdentifier = initIdentifier,
            CreatedAt = fixture.FixedNow.AddHours(-1), UpdatedAt = fixture.FixedNow.AddHours(-1), Request = [],
        };

    private async Task<Dictionary<Guid, CronTickerEntity>> StoredAsync()
        => (await fixture.CronTickers.Find(c => c.Function == Function).ToListAsync()).ToDictionary(c => c.Id);

    // Both insertion orders, since which row came first decided it before.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASeededCronAndAUsersCronOfOneFunction_OnlyTheSeededOneTakesTheExpressionInCode(bool userFirst)
    {
        var seeded = Cron("0 0 3 * * *", Seeded);
        var user = Cron("0 0 1 * * *");
        await fixture.CronTickers.InsertManyAsync(userFirst ? [user, seeded] : [seeded, user]);

        await fixture.Provider.MigrateDefinedCronTickers([(Function, "0 0 4 * * *")], CancellationToken.None);

        var stored = await StoredAsync();
        Assert.Equal(2, stored.Count);
        Assert.Equal("0 0 1 * * *", stored[user.Id].Expression);
        Assert.Equal(fixture.FixedNow.AddHours(-1), stored[user.Id].UpdatedAt);
        Assert.Equal("0 0 4 * * *", stored[seeded.Id].Expression);
    }

    [Fact]
    public async Task TwoSeededCopiesOfOneCron_BothTakeTheExpressionInCode()
    {
        await fixture.CronTickers.InsertManyAsync([Cron("0 0 3 * * *", Seeded), Cron("0 0 3 * * *", Seeded)]);

        await fixture.Provider.MigrateDefinedCronTickers([(Function, "0 0 4 * * *")], CancellationToken.None);

        var stored = await StoredAsync();
        Assert.Equal(2, stored.Count);
        Assert.All(stored.Values, c => Assert.Equal("0 0 4 * * *", c.Expression));
    }

    [Fact]
    public async Task SeveralCronsOfOneFunction_NoneMarkedAsSeeded_NoneIsChanged()
    {
        await fixture.CronTickers.InsertManyAsync([Cron("0 0 1 * * *"), Cron("0 0 2 * * *")]);

        await fixture.Provider.MigrateDefinedCronTickers([(Function, "0 0 4 * * *")], CancellationToken.None);

        Assert.Equal(new[] { "0 0 1 * * *", "0 0 2 * * *" }, (await StoredAsync()).Values.Select(c => c.Expression).OrderBy(e => e));
    }

    // As before: one cron of the function, not marked, takes the expression in code, and no second row is added.
    [Fact]
    public async Task OneUnmarkedCronOfTheFunction_TakesTheExpressionInCode()
    {
        var only = Cron("0 0 3 * * *");
        await fixture.CronTickers.InsertOneAsync(only);

        await fixture.Provider.MigrateDefinedCronTickers([(Function, "0 0 4 * * *")], CancellationToken.None);

        var stored = Assert.Single(await StoredAsync());
        Assert.Equal(only.Id, stored.Key);
        Assert.Equal("0 0 4 * * *", stored.Value.Expression);
    }

    /// <summary>
    /// A node of its own connection whose first insert of a cron is held until <paramref name="beforeFirstInsert"/> has
    /// run: what another node does between this node's read of the stored crons and its write.
    /// </summary>
    private TickerMongoPersistenceProvider<TimeTickerEntity, CronTickerEntity> NodeThatIsOvertaken(Func<Task> beforeFirstInsert)
    {
        var overtaken = 0;
        var settings = MongoClientSettings.FromConnectionString(fixture.Container.GetConnectionString());
        settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(started =>
        {
            if (started.CommandName == "insert" && Interlocked.Exchange(ref overtaken, 1) == 0)
                Task.Run(beforeFirstInsert).GetAwaiter().GetResult();
        });

        var context = new TickerMongoContext<TimeTickerEntity, CronTickerEntity>(
            new MongoClient(settings).GetDatabase(fixture.Database.DatabaseNamespace.DatabaseName), "ticker_");
        return new TickerMongoPersistenceProvider<TimeTickerEntity, CronTickerEntity>(
            context, fixture.Clock, new SchedulerOptionsBuilder { NodeIdentifier = "node-a" });
    }

    // Two replicas of a new deployment: both read that the cron is missing, then both insert it.
    [Fact]
    public async Task TwoNodesStartingTogether_SeedTheCronOnce()
    {
        (string, string)[] defined = [(Function, "0 0 4 * * *")];
        var nodeBSeeded = false;
        var nodeA = NodeThatIsOvertaken(async () =>
        {
            await fixture.Provider.MigrateDefinedCronTickers(defined, CancellationToken.None);
            nodeBSeeded = true;
        });

        await nodeA.MigrateDefinedCronTickers(defined, CancellationToken.None);

        Assert.True(nodeBSeeded, "the other node did not seed between this node's read and its write");
        var stored = Assert.Single(await StoredAsync());
        Assert.Equal("0 0 4 * * *", stored.Value.Expression);
        Assert.Equal(Seeded, stored.Value.InitIdentifier);
    }

    // The id is stored, so how it is made is a contract. The expected value was computed outside .NET: the first 16
    // bytes of SHA-256("SharpTicker.SeededCron:cron-seed-fn").
    [Fact]
    public async Task ASeededCron_GetsTheIdOfItsFunctionName()
    {
        await fixture.Provider.MigrateDefinedCronTickers([(Function, "0 0 4 * * *")], CancellationToken.None);

        var stored = Assert.Single(await StoredAsync());
        Assert.Equal(Guid.Parse("1810d369-8a39-7c24-16ee-042e48d95bdc"), stored.Key);
    }

    // A seeded cron whose function was changed keeps its id, the id of the old function name.
    [Fact]
    public async Task TheIdOfTheFunctionNameIsTakenByACronOfAnotherFunction_TheCronIsStillSeeded()
    {
        var holder = Cron("0 0 1 * * *");
        holder.Id = Guid.Parse("1810d369-8a39-7c24-16ee-042e48d95bdc");
        holder.Function = "another-fn";
        await fixture.CronTickers.InsertOneAsync(holder);

        await fixture.Provider.MigrateDefinedCronTickers([(Function, "0 0 4 * * *")], CancellationToken.None);

        var stored = Assert.Single(await StoredAsync());
        Assert.NotEqual(holder.Id, stored.Key);
        Assert.Equal("0 0 4 * * *", stored.Value.Expression);
        Assert.Equal("another-fn", (await fixture.CronTickers.Find(c => c.Id == holder.Id).SingleAsync()).Function);
    }
}
