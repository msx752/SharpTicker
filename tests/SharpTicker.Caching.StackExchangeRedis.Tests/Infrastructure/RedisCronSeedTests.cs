using SharpTicker.Caching.StackExchangeRedis.Infrastructure;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>
/// The seed of code-defined crons at startup (MigrateDefinedCronTickers) on real Redis, when a function has more than one
/// stored cron: a user's cron next to the seeded one, or two seeded copies. Before #92's first step the seed put the crons
/// into a dictionary by function and threw on the second one, so the host failed to start on every start.
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class RedisCronSeedTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private const string Function = "cron-seed-fn";
    private const string Seeded = "MemoryTicker_Seeded_" + Function;
    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    public Task InitializeAsync()
    {
        // The seed removes the crons of functions that are not registered; this one must stay.
        TickerFunctionProvider.RegisterFunctions(
            new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>
            {
                [Function] = ("", TickerTaskPriority.Normal, (_, _, _) => Task.CompletedTask, 0)
            });
        TickerFunctionProvider.Build();
        return fixture.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private TickerRedisPersistenceProvider<TimeTickerEntity, CronTickerEntity> Provider()
        => fixture.CreateProvider("node-A", new FakeTickerClock(T0));

    private static CronTickerEntity Cron(string expression, string? initIdentifier = null)
        => new()
        {
            Id = Guid.NewGuid(), Function = Function, Expression = expression, InitIdentifier = initIdentifier,
            CreatedAt = T0.AddHours(-1), UpdatedAt = T0.AddHours(-1), Request = [], RetryIntervals = [],
        };

    private async Task<Dictionary<Guid, CronTickerEntity>> StoredAsync()
        => (await Provider().GetCronTickers(c => c.Function == Function, CancellationToken.None)).ToDictionary(c => c.Id);

    [Fact]
    public async Task ASeededCronAndAUsersCronOfOneFunction_TheNodeStarts_AndOnlyTheSeededOneTakesTheExpressionInCode()
    {
        var seeded = Cron("0 0 3 * * *", Seeded);
        var user = Cron("0 0 1 * * *");
        await Provider().InsertCronTickers([seeded, user], CancellationToken.None);

        await Provider().MigrateDefinedCronTickers([(Function, "0 0 4 * * *")], CancellationToken.None);

        var stored = await StoredAsync();
        Assert.Equal(2, stored.Count);
        Assert.Equal("0 0 1 * * *", stored[user.Id].Expression);
        Assert.Equal(T0.AddHours(-1), stored[user.Id].UpdatedAt);
        Assert.Equal("0 0 4 * * *", stored[seeded.Id].Expression);
    }

    [Fact]
    public async Task TwoSeededCopiesOfOneCron_TheNodeStarts_AndBothTakeTheExpressionInCode()
    {
        await Provider().InsertCronTickers([Cron("0 0 3 * * *", Seeded), Cron("0 0 3 * * *", Seeded)], CancellationToken.None);

        await Provider().MigrateDefinedCronTickers([(Function, "0 0 4 * * *")], CancellationToken.None);

        var stored = await StoredAsync();
        Assert.Equal(2, stored.Count);
        Assert.All(stored.Values, c => Assert.Equal("0 0 4 * * *", c.Expression));
    }

    [Fact]
    public async Task SeveralCronsOfOneFunction_NoneMarkedAsSeeded_TheNodeStarts_AndNoneIsChanged()
    {
        await Provider().InsertCronTickers([Cron("0 0 1 * * *"), Cron("0 0 2 * * *")], CancellationToken.None);

        await Provider().MigrateDefinedCronTickers([(Function, "0 0 4 * * *")], CancellationToken.None);

        var stored = await StoredAsync();
        Assert.Equal(new[] { "0 0 1 * * *", "0 0 2 * * *" }, stored.Values.Select(c => c.Expression).OrderBy(e => e));
    }

    // As before: one cron of the function, not marked (most likely the seeded cron edited on the dashboard), takes the
    // expression in code, and no second row is added.
    [Fact]
    public async Task OneUnmarkedCronOfTheFunction_TakesTheExpressionInCode()
    {
        var only = Cron("0 0 3 * * *");
        await Provider().InsertCronTickers([only], CancellationToken.None);

        await Provider().MigrateDefinedCronTickers([(Function, "0 0 4 * * *")], CancellationToken.None);

        var stored = Assert.Single(await StoredAsync());
        Assert.Equal(only.Id, stored.Key);
        Assert.Equal("0 0 4 * * *", stored.Value.Expression);
    }
}
