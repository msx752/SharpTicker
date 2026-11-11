using MongoDB.Driver;
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
}
