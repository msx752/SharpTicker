using NSubstitute;
using SharpTicker.Caching.StackExchangeRedis.Helpers;
using SharpTicker.Caching.StackExchangeRedis.Infrastructure;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Managers;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>
/// The compare-and-set every C# read-modify-write of a run row goes through, raced deterministically: the provider's test
/// hook runs another node's write in the gap between the read and the set (or after a fenced write landed, before its index
/// update), where a plain GET then SET loses one of the two writes.
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class RedisCompareAndSetRaceTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SeedUpdatedAt = T0.AddHours(-1).AddTicks(1234567);

    private readonly FakeTickerClock _clock = new(T0);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private (TickerRedisPersistenceProvider<TimeTickerEntity, CronTickerEntity> Store, IInternalTickerManager Manager) CreateNode(string nodeIdentifier)
    {
        var store = fixture.CreateProvider(nodeIdentifier, _clock);
        return (store, new InternalTickerManager<TimeTickerEntity, CronTickerEntity>(store, _clock, Substitute.For<ISharpTickerNotificationHubSender>()));
    }

    private static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    private static TimeTickerEntity NewTicker() => new()
    {
        Id = Guid.NewGuid(),
        Function = "race-fn",
        ExecutionTime = T0,
        Status = TickerStatus.Idle,
        Description = "before",
        CreatedAt = T0.AddHours(-2),
        UpdatedAt = SeedUpdatedAt,
        Request = []
    };

    // A row node A claimed and runs, as the scheduler hands it out.
    private async Task<(TimeTickerEntity Ticker, InternalFunctionContext Run)> RunningOnA(
        (TickerRedisPersistenceProvider<TimeTickerEntity, CronTickerEntity> Store, IInternalTickerManager Manager) a)
    {
        var ticker = NewTicker();
        ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> provider = a.Store;
        await provider.AddTimeTickers([ticker], CancellationToken.None);
        var window = await provider.GetEarliestTimeTickers(CancellationToken.None);
        Assert.Single(await Drain(provider.QueueTimeTickers(window.Where(x => x.Id == ticker.Id).ToArray(), CancellationToken.None)));
        var run = new InternalFunctionContext { TickerId = ticker.Id, FunctionName = "race-fn", Type = TickerType.TimeTicker };
        Assert.True(await a.Manager.TryClaimDueTicker(run, a.Manager.NextClaimMarker(), CancellationToken.None));
        run.RunMarker = run.ClaimMarker;
        return (ticker, run);
    }

    private static TimeTickerEntity EditOf(TimeTickerEntity ticker) => new()
    {
        Id = ticker.Id, Function = ticker.Function, ExecutionTime = ticker.ExecutionTime, Description = "edited",
        CreatedAt = ticker.CreatedAt, Request = []
    };

    private static InternalFunctionContext Done(InternalFunctionContext run)
    {
        run.ResetUpdateProps()
            .SetProperty(x => x.Status, TickerStatus.Done)
            .SetProperty(x => x.ExecutedAt, T0.AddMinutes(1));
        return run;
    }

    private async Task<TimeTickerEntity> Row(Guid id)
        => (await ((ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>)fixture.CreateProvider("reader", _clock)).GetTimeTickerById(id, CancellationToken.None))!;

    // T12: an edit (WriteChainAsync) lands between the read and the set of the run's final write. Both land: the final write
    // sees the row changed, reads it again, and the edit kept the run's marker, so the fence still holds.
    [Fact]
    public async Task AnEditBetweenTheReadAndTheSetOfAFencedWrite_IsNotLost_AndTheOutcomeIsStored()
    {
        var a = CreateNode("node-A");
        var b = CreateNode("node-B");
        var (ticker, run) = await RunningOnA(a);
        var hooks = 0;
        a.Store.BeforeCompareAndSetHook = async _ =>
        {
            if (hooks++ == 0)
                await ((ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>)b.Store).UpdateTimeTickers([EditOf(ticker)], CancellationToken.None);
        };

        Assert.True(await a.Manager.UpdateTickerAsync(Done(run)));

        var row = await Row(ticker.Id);
        Assert.Equal("edited", row.Description);
        Assert.Equal(TickerStatus.Done, row.Status);
        Assert.Equal(2, hooks);
    }

    // T12: node A's rows are released and the freed row is edited between the read and the set of A's late final write. A's
    // write must change nothing: the row stays Idle with the edit, and its pending entry stays, so it is claimed again.
    [Fact]
    public async Task AReleaseAndEditBetweenTheReadAndTheSetOfAFencedWrite_KeepsTheClaimableRowAndItsPendingEntry()
    {
        var a = CreateNode("node-A");
        var b = CreateNode("node-B");
        var (ticker, run) = await RunningOnA(a);
        ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> providerB = b.Store;
        var hooks = 0;
        a.Store.BeforeCompareAndSetHook = async _ =>
        {
            if (hooks++ != 0) return;
            await providerB.ReleaseDeadNodeTimeTickerResources("node-A", CancellationToken.None);
            await providerB.UpdateTimeTickers([EditOf(ticker)], CancellationToken.None);
        };

        Assert.False(await a.Manager.UpdateTickerAsync(Done(run)));

        var row = await Row(ticker.Id);
        Assert.Equal(TickerStatus.Idle, row.Status);
        Assert.Null(row.LockHolder);
        Assert.Equal("edited", row.Description);
        Assert.NotNull(await fixture.Database.SortedSetScoreAsync(RedisKeyBuilder.TimeTickerPendingKey, ticker.Id.ToString()));
        Assert.Contains(await providerB.GetEarliestTimeTickers(CancellationToken.None), x => x.Id == ticker.Id);
    }

    // S2: the row is released (and indexed as pending) right after node A's write of its run landed, before A's index
    // update. That update only adds, so the pending entry of the claimable row stays.
    [Fact]
    public async Task AReleaseRightAfterAFencedWrite_KeepsThePendingEntry()
    {
        var a = CreateNode("node-A");
        var b = CreateNode("node-B");
        var (ticker, run) = await RunningOnA(a);
        ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> providerB = b.Store;
        a.Store.AfterFencedWriteHook = _ => providerB.ReleaseDeadNodeTimeTickerResources("node-A", CancellationToken.None);

        Assert.True(await a.Manager.UpdateTickerAsync(run.ResetUpdateProps().SetProperty(x => x.RetryCount, 1)));

        Assert.Equal(TickerStatus.Idle, (await Row(ticker.Id)).Status);
        Assert.NotNull(await fixture.Database.SortedSetScoreAsync(RedisKeyBuilder.TimeTickerPendingKey, ticker.Id.ToString()));
    }

    // T17 (R2): a row that changes on every round is not written, and the write fails loudly. A count of 0 would read as
    // "the row is gone or another run holds it", which a caller may drop.
    [Fact]
    public async Task ARowThatKeepsChanging_IsNotWritten_AndTheWriteThrows()
    {
        var a = CreateNode("node-A");
        var (ticker, run) = await RunningOnA(a);
        var hooks = 0;
        a.Store.BeforeCompareAndSetHook = async key =>
        {
            hooks++;
            await fixture.Database.StringAppendAsync(key, " ");
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => a.Manager.UpdateTickerAsync(Done(run)));

        Assert.Equal(5, hooks);
        Assert.Equal(TickerStatus.InProgress, (await Row(ticker.Id)).Status);
    }
}
