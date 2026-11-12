using System.Reflection;
using SharpTicker.Caching.StackExchangeRedis.Helpers;
using SharpTicker.Caching.StackExchangeRedis.Infrastructure;
using StackExchange.Redis;
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
    private const string AnotherFunction = "cron-seed-another-fn";

    // The first 16 bytes of SHA-256("SharpTicker.SeededCron:cron-seed-fn"), computed outside .NET. The id is stored, so
    // how it is made is a contract.
    private static readonly Guid IdOfFunction = Guid.Parse("1810d369-8a39-7c24-16ee-042e48d95bdc");
    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    public Task InitializeAsync()
    {
        // The seed removes the crons of functions that are not registered; this one must stay.
        TickerFunctionProvider.RegisterFunctions(
            new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>
            {
                [Function] = ("", TickerTaskPriority.Normal, (_, _, _) => Task.CompletedTask, 0),
                [AnotherFunction] = ("", TickerTaskPriority.Normal, (_, _, _) => Task.CompletedTask, 0)
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

    // Two replicas of a new deployment: both read that the cron is missing, then both add it.
    [Fact]
    public async Task TwoNodesStartingTogether_SeedTheCronOnce()
    {
        (string, string)[] defined = [(Function, "0 0 4 * * *")];
        var hooked = DispatchProxy.Create<IDatabase, RedisClaimMultiNodeTests.HookedDatabase>();
        var hook = (RedisClaimMultiNodeTests.HookedDatabase)hooked;
        hook.Inner = fixture.Database;
        hook.HookOn = nameof(IDatabase.SetMembersAsync);
        var nodeBSeeded = false;
        // Node A has read which crons exist (none); node B's whole seed runs before node A goes on.
        hook.AfterNextCall = async () =>
        {
            await fixture.CreateProvider("node-B", new FakeTickerClock(T0)).MigrateDefinedCronTickers(defined, CancellationToken.None);
            nodeBSeeded = true;
        };

        await fixture.CreateProvider("node-A", new FakeTickerClock(T0), hooked).MigrateDefinedCronTickers(defined, CancellationToken.None);

        Assert.True(nodeBSeeded, "the other node did not seed between this node's read and its write");
        var stored = Assert.Single(await StoredAsync());
        Assert.Equal("0 0 4 * * *", stored.Value.Expression);
        Assert.Equal(1, await fixture.Database.SetLengthAsync(RedisKeyBuilder.CronIdsKey));
    }

    [Fact]
    public async Task ASeededCron_GetsTheIdOfItsFunctionName()
    {
        await Provider().MigrateDefinedCronTickers([(Function, "0 0 4 * * *")], CancellationToken.None);

        Assert.Equal(IdOfFunction, Assert.Single(await StoredAsync()).Key);
    }

    // A seeded cron whose function was changed keeps its id, the id of the old function name.
    [Fact]
    public async Task TheIdOfTheFunctionNameIsTakenByACronOfAnotherFunction_TheCronIsStillSeeded()
    {
        var holder = Cron("0 0 1 * * *");
        holder.Id = IdOfFunction;
        holder.Function = AnotherFunction;
        await Provider().InsertCronTickers([holder], CancellationToken.None);

        await Provider().MigrateDefinedCronTickers([(Function, "0 0 4 * * *")], CancellationToken.None);

        var stored = Assert.Single(await StoredAsync());
        Assert.NotEqual(IdOfFunction, stored.Key);
        Assert.Equal("0 0 4 * * *", stored.Value.Expression);
        var kept = Assert.Single(await Provider().GetCronTickers(c => c.Id == IdOfFunction, CancellationToken.None));
        Assert.Equal(AnotherFunction, kept.Function);
        Assert.Equal("0 0 1 * * *", kept.Expression);
    }

    // The node that created the row stopped before it added the row to the index of crons: no node sees the cron. With a
    // fixed id no later start could create it again, so the start that finds the row adds it to the index.
    [Fact]
    public async Task ASeededCronThatIsMissingFromTheIndex_IsSeenAgainAfterTheNextStart()
    {
        await Provider().MigrateDefinedCronTickers([(Function, "0 0 4 * * *")], CancellationToken.None);
        Assert.True(await fixture.Database.SetRemoveAsync(RedisKeyBuilder.CronIdsKey, IdOfFunction.ToString()));
        Assert.Empty(await StoredAsync());

        await Provider().MigrateDefinedCronTickers([(Function, "0 0 4 * * *")], CancellationToken.None);

        Assert.Equal(IdOfFunction, Assert.Single(await StoredAsync()).Key);
    }

    private static CronTickerOccurrenceEntity<CronTickerEntity> OccurrenceOf(CronTickerEntity cron, DateTime at)
        => new()
        {
            Id = Guid.NewGuid(), CronTickerId = cron.Id, Status = TickerStatus.Done, ExecutionTime = at, CreatedAt = at, UpdatedAt = at,
            CronTicker = cron,
        };

    // Seeded copies of one cron (nodes that seeded it at the same time) each run the job on every tick: the oldest is
    // kept and takes the expression in code, the others go with their occurrences. Both insertion orders.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SeededCopiesOfOneCron_TheOldestIsKept_TheOthersAreRemovedWithTheirOccurrences(bool oldestFirst)
    {
        var oldest = Cron("0 0 3 * * *", Seeded);
        oldest.CreatedAt = T0.AddHours(-2);
        var copy = Cron("0 0 3 * * *", Seeded);
        var third = Cron("0 0 3 * * *", Seeded);
        await Provider().InsertCronTickers(oldestFirst ? [oldest, copy, third] : [copy, third, oldest], CancellationToken.None);
        var kept = OccurrenceOf(oldest, T0.AddMinutes(-10));
        var removed = OccurrenceOf(copy, T0.AddMinutes(-20));
        await Provider().InsertCronTickerOccurrences([kept, removed], CancellationToken.None);

        await Provider().MigrateDefinedCronTickers([(Function, "0 0 4 * * *")], CancellationToken.None);

        var stored = Assert.Single(await StoredAsync());
        Assert.Equal(oldest.Id, stored.Key);
        Assert.Equal("0 0 4 * * *", stored.Value.Expression);
        var occurrences = await Provider().GetAllCronTickerOccurrences(_ => true, CancellationToken.None);
        Assert.Equal([kept.Id], occurrences.Select(o => o.Id));
        // Nothing of the copies is left in the store: the rows, their index entries and their occurrences.
        Assert.Equal(1, await fixture.Database.SetLengthAsync(RedisKeyBuilder.CronIdsKey));
        Assert.False(await fixture.Database.KeyExistsAsync(RedisKeyBuilder.CronKey(copy.Id)));
        Assert.False(await fixture.Database.KeyExistsAsync(RedisKeyBuilder.CronKey(third.Id)));
        Assert.False(await fixture.Database.KeyExistsAsync(RedisKeyBuilder.CronOccurrenceKey(removed.Id)));
    }

    // Created in the same instant: every node must keep the same row, so the id decides.
    [Fact]
    public async Task SeededCopiesCreatedAtTheSameTime_TheOneWithTheLowestIdIsKept()
    {
        var a = Cron("0 0 3 * * *", Seeded);
        var b = Cron("0 0 3 * * *", Seeded);
        await Provider().InsertCronTickers([a, b], CancellationToken.None);

        await Provider().MigrateDefinedCronTickers([(Function, "0 0 4 * * *")], CancellationToken.None);

        Assert.Equal(a.Id.CompareTo(b.Id) < 0 ? a.Id : b.Id, Assert.Single(await StoredAsync()).Key);
    }

    // A user's cron and a row with the marker of another function are not copies, whatever their number.
    [Fact]
    public async Task CronsWithoutTheSeedMarker_AreNotRemovedAsCopies()
    {
        CronTickerEntity[] rows = [Cron("0 0 3 * * *", Seeded), Cron("0 0 3 * * *"), Cron("0 0 3 * * *", "MemoryTicker_Seeded_another-fn")];
        await Provider().InsertCronTickers(rows, CancellationToken.None);

        await Provider().MigrateDefinedCronTickers([(Function, "0 0 4 * * *")], CancellationToken.None);

        Assert.Equal(rows.Select(r => r.Id).OrderBy(x => x), (await StoredAsync()).Keys.OrderBy(x => x));
    }
}
