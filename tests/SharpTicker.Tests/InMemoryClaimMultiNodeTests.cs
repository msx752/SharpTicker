using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SharpTicker.Provider;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Models;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// Two provider instances (separate NodeIdentifier) over the one process-wide in-memory store and a fake clock,
/// driving the fenced Queued to InProgress claim and the fallback claim the way two nodes would.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class InMemoryClaimMultiNodeTests : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FakeClock(DateTime start) : ITickerClock
    {
        public DateTime UtcNow { get; set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    private readonly FakeClock _clock = new(T0);
    private readonly List<Guid> _timeTickerIds = [];
    private readonly List<Guid> _occurrenceIds = [];

    public Task InitializeAsync() => Task.CompletedTask;

    // The in-memory store is process-wide, so every row a test made is removed again.
    public async Task DisposeAsync()
    {
        var node = CreateProvider("cleanup");
        await node.RemoveTimeTickers(_timeTickerIds.ToArray(), CancellationToken.None);
        await node.RemoveCronTickerOccurrences(_occurrenceIds.ToArray(), CancellationToken.None);
    }

    private TickerInMemoryPersistenceProvider<TimeTickerEntity, CronTickerEntity> CreateProvider(string nodeIdentifier, FakeClock? clock = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITickerClock>(clock ?? _clock);
        services.AddSingleton(new SchedulerOptionsBuilder { NodeIdentifier = nodeIdentifier });
        return new TickerInMemoryPersistenceProvider<TimeTickerEntity, CronTickerEntity>(services.BuildServiceProvider());
    }

    private static InternalFunctionContext StatusContext(TickerStatus status)
        => new InternalFunctionContext().SetProperty(x => x.Status, status);

    private static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    private async Task<Guid> SeedTimeTicker()
    {
        var ticker = new TimeTickerEntity
        {
            Id = Guid.NewGuid(),
            Function = "claim-fn",
            ExecutionTime = T0,
            Status = TickerStatus.Idle,
            CreatedAt = T0.AddHours(-2),
            UpdatedAt = T0.AddHours(-1),
            Request = []
        };
        _timeTickerIds.Add(ticker.Id);
        await CreateProvider("seed").AddTimeTickers([ticker], CancellationToken.None);
        return ticker.Id;
    }

    private async Task<Guid> QueueTimeTickerOn(TickerInMemoryPersistenceProvider<TimeTickerEntity, CronTickerEntity> node)
    {
        var id = await SeedTimeTicker();
        var window = await node.GetEarliestTimeTickers(CancellationToken.None);
        Assert.Single(await Drain(node.QueueTimeTickers(window.Where(x => x.Id == id).ToArray(), CancellationToken.None)));
        return id;
    }

    private async Task<Guid> QueueOccurrenceOn(TickerInMemoryPersistenceProvider<TimeTickerEntity, CronTickerEntity> node)
    {
        var slot = (T0, new[] { new InternalManagerContext(Guid.NewGuid()) { FunctionName = "claim-cron-fn", Expression = "* * * * *" } });
        var id = Assert.Single(await Drain(node.QueueCronTickerOccurrences(slot, CancellationToken.None))).Id;
        _occurrenceIds.Add(id);
        return id;
    }

    private static async Task<CronTickerOccurrenceEntity<CronTickerEntity>> Occurrence(
        TickerInMemoryPersistenceProvider<TimeTickerEntity, CronTickerEntity> provider, Guid id)
        => (await provider.GetAllCronTickerOccurrences(x => x.Id == id, CancellationToken.None)).Single();

    public static TheoryData<TickerStatus> NonQueuedStatuses => new()
    {
        TickerStatus.Idle, TickerStatus.InProgress, TickerStatus.Done, TickerStatus.DueDone,
        TickerStatus.Failed, TickerStatus.Cancelled, TickerStatus.Skipped
    };

    [Fact]
    public async Task TryMark_OwnQueuedRow_ReturnsTrueAndWritesInProgress_TimeTicker()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueTimeTickerOn(nodeA);
        _clock.Advance(TimeSpan.FromMilliseconds(300));

        Assert.True(await nodeA.TryMarkTimeTickerInProgress(id));

        var row = await nodeA.GetTimeTickerById(id, CancellationToken.None);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("node-A", row.LockHolder);
        Assert.Equal(_clock.UtcNow, row.UpdatedAt);
    }

    [Fact]
    public async Task TryMark_OwnQueuedRow_ReturnsTrueAndWritesInProgress_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueOccurrenceOn(nodeA);
        _clock.Advance(TimeSpan.FromMilliseconds(300));

        Assert.True(await nodeA.TryMarkCronOccurrenceInProgress(id));

        var row = await Occurrence(nodeA, id);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("node-A", row.LockHolder);
        Assert.Equal(_clock.UtcNow, row.UpdatedAt);
    }

    [Fact]
    public async Task TryMark_RowClaimedByFallbackOfAnotherNode_ReturnsFalseAndLeavesRow_TimeTicker()
    {
        var nodeA = CreateProvider("node-A");
        var nodeB = CreateProvider("node-B");
        var id = await QueueTimeTickerOn(nodeA);
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Contains(await Drain(nodeB.QueueTimedOutTimeTickers(CancellationToken.None)), x => x.Id == id);
        var before = await nodeA.GetTimeTickerById(id, CancellationToken.None);

        Assert.False(await nodeA.TryMarkTimeTickerInProgress(id));

        var after = await nodeA.GetTimeTickerById(id, CancellationToken.None);
        Assert.Equal("node-B", after.LockHolder);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task TryMark_RowClaimedByFallbackOfAnotherNode_ReturnsFalseAndLeavesRow_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var nodeB = CreateProvider("node-B");
        var id = await QueueOccurrenceOn(nodeA);
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Contains(await Drain(nodeB.QueueTimedOutCronTickerOccurrences(CancellationToken.None)), x => x.Id == id);
        var before = await Occurrence(nodeA, id);

        Assert.False(await nodeA.TryMarkCronOccurrenceInProgress(id));

        var after = await Occurrence(nodeA, id);
        Assert.Equal("node-B", after.LockHolder);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    // The status clause alone cannot tell these apart: the row is Queued again, but for another node.
    [Fact]
    public async Task TryMark_RowRequeuedByAnotherNode_ReturnsFalseUntilItsHolderMarks_TimeTicker()
    {
        var nodeA = CreateProvider("node-A");
        var nodeB = CreateProvider("node-B");
        var id = await QueueTimeTickerOn(nodeA);
        await nodeA.ReleaseAcquiredTimeTickers([id], CancellationToken.None);
        var window = await nodeB.GetEarliestTimeTickers(CancellationToken.None);
        Assert.Single(await Drain(nodeB.QueueTimeTickers(window.Where(x => x.Id == id).ToArray(), CancellationToken.None)));
        var before = await nodeA.GetTimeTickerById(id, CancellationToken.None);

        Assert.False(await nodeA.TryMarkTimeTickerInProgress(id));

        var untouched = await nodeA.GetTimeTickerById(id, CancellationToken.None);
        Assert.Equal(TickerStatus.Queued, untouched.Status);
        Assert.Equal("node-B", untouched.LockHolder);
        Assert.Equal(before.UpdatedAt, untouched.UpdatedAt);
        Assert.True(await nodeB.TryMarkTimeTickerInProgress(id));
        Assert.Equal(TickerStatus.InProgress, (await nodeA.GetTimeTickerById(id, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task TryMark_RowRequeuedByAnotherNode_ReturnsFalseUntilItsHolderMarks_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var nodeB = CreateProvider("node-B");
        var cronId = Guid.NewGuid();
        var slot = (T0, new[] { new InternalManagerContext(cronId) { FunctionName = "claim-cron-fn", Expression = "* * * * *" } });
        var id = Assert.Single(await Drain(nodeA.QueueCronTickerOccurrences(slot, CancellationToken.None))).Id;
        _occurrenceIds.Add(id);
        await nodeA.ReleaseAcquiredCronTickerOccurrences([id], CancellationToken.None);
        slot.Item2[0].NextCronOccurrence = new NextCronOccurrence(id, T0);
        Assert.Single(await Drain(nodeB.QueueCronTickerOccurrences(slot, CancellationToken.None)));
        var before = await Occurrence(nodeA, id);

        Assert.False(await nodeA.TryMarkCronOccurrenceInProgress(id));

        var untouched = await Occurrence(nodeA, id);
        Assert.Equal(TickerStatus.Queued, untouched.Status);
        Assert.Equal("node-B", untouched.LockHolder);
        Assert.Equal(before.UpdatedAt, untouched.UpdatedAt);
        Assert.True(await nodeB.TryMarkCronOccurrenceInProgress(id));
        Assert.Equal(TickerStatus.InProgress, (await Occurrence(nodeA, id)).Status);
    }

    // Finished rows keep the lock holder, so even the owning node must not move them back to InProgress.
    [Theory]
    [MemberData(nameof(NonQueuedStatuses))]
    public async Task TryMark_RowInStatus_ReturnsFalse_TimeTicker(TickerStatus status)
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueTimeTickerOn(nodeA);
        await nodeA.UpdateTimeTickersWithUnifiedContext([id], StatusContext(status), CancellationToken.None);
        var before = await nodeA.GetTimeTickerById(id, CancellationToken.None);

        Assert.False(await nodeA.TryMarkTimeTickerInProgress(id));

        var after = await nodeA.GetTimeTickerById(id, CancellationToken.None);
        Assert.Equal(status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Theory]
    [MemberData(nameof(NonQueuedStatuses))]
    public async Task TryMark_RowInStatus_ReturnsFalse_CronOccurrence(TickerStatus status)
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueOccurrenceOn(nodeA);
        await nodeA.UpdateCronTickerOccurrencesWithUnifiedContext([id], StatusContext(status), CancellationToken.None);
        var before = await Occurrence(nodeA, id);

        Assert.False(await nodeA.TryMarkCronOccurrenceInProgress(id));

        var after = await Occurrence(nodeA, id);
        Assert.Equal(status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task TryMark_MissingRow_ReturnsFalse()
    {
        var nodeA = CreateProvider("node-A");

        Assert.False(await nodeA.TryMarkTimeTickerInProgress(Guid.NewGuid()));
        Assert.False(await nodeA.TryMarkCronOccurrenceInProgress(Guid.NewGuid()));
    }

    // The fallback scans a snapshot, then re-reads each row. A row that another node moved to InProgress between the
    // two, on a clock that is behind, still passes the UpdatedAt comparison and would be claimed a second time.
    [Fact]
    public async Task Fallback_DoesNotClaimInProgressRow_WhenItsUpdatedAtIsEarlier_TimeTicker()
    {
        var nodeFallback = CreateProvider("node-fallback");
        var nodeBehind = CreateProvider("node-behind", new FakeClock(T0.AddHours(-2)));
        var first = await SeedTimeTicker();
        var second = await SeedTimeTicker();
        _clock.Advance(TimeSpan.FromSeconds(2));

        var claimed = new List<Guid>();
        await foreach (var row in nodeFallback.QueueTimedOutTimeTickers(CancellationToken.None))
        {
            claimed.Add(row.Id);
            if (claimed.Count != 1)
                continue;

            var other = claimed[0] == first ? second : first;
            await nodeBehind.UpdateTimeTickersWithUnifiedContext([other], StatusContext(TickerStatus.InProgress), CancellationToken.None);
        }

        var untouched = await nodeFallback.GetTimeTickerById(claimed[0] == first ? second : first, CancellationToken.None);
        Assert.Single(claimed);
        Assert.Equal(TickerStatus.InProgress, untouched.Status);
        Assert.NotEqual("node-fallback", untouched.LockHolder);
    }

    [Fact]
    public async Task Fallback_DoesNotClaimInProgressRow_WhenItsUpdatedAtIsEarlier_CronOccurrence()
    {
        var nodeFallback = CreateProvider("node-fallback");
        var nodeBehind = CreateProvider("node-behind", new FakeClock(T0.AddHours(-2)));
        var first = await QueueOccurrenceOn(nodeBehind);
        var second = await QueueOccurrenceOn(nodeBehind);
        _clock.Advance(TimeSpan.FromSeconds(2));

        var claimed = new List<Guid>();
        await foreach (var row in nodeFallback.QueueTimedOutCronTickerOccurrences(CancellationToken.None))
        {
            claimed.Add(row.Id);
            if (claimed.Count != 1)
                continue;

            var other = claimed[0] == first ? second : first;
            await nodeBehind.UpdateCronTickerOccurrencesWithUnifiedContext([other], StatusContext(TickerStatus.InProgress), CancellationToken.None);
        }

        var untouched = await Occurrence(nodeFallback, claimed[0] == first ? second : first);
        Assert.Single(claimed);
        Assert.Equal(TickerStatus.InProgress, untouched.Status);
        Assert.NotEqual("node-fallback", untouched.LockHolder);
    }

    // A provider written before the fenced claim exists still compiles and keeps the unfenced write.
    [Fact]
    public async Task CustomProvider_DefaultBody_WritesAndReturnsTrue()
    {
        var provider = new LegacyProvider();
        ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> contract = provider;
        var timeId = Guid.NewGuid();
        var occurrenceId = Guid.NewGuid();

        Assert.True(await contract.TryMarkTimeTickerInProgress(timeId));
        Assert.True(await contract.TryMarkCronOccurrenceInProgress(occurrenceId));

        var timeWrite = Assert.Single(provider.TimeTickerWrites);
        Assert.Equal([timeId], timeWrite.Ids);
        Assert.Equal(TickerStatus.InProgress, timeWrite.Status);
        var occurrenceWrite = Assert.Single(provider.OccurrenceWrites);
        Assert.Equal([occurrenceId], occurrenceWrite.Ids);
        Assert.Equal(TickerStatus.InProgress, occurrenceWrite.Status);
    }

    // Implements only the members that existed before the fenced claim, so the two new ones use their default bodies.
    private sealed class LegacyProvider : ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>
    {
        public List<(Guid[] Ids, TickerStatus Status)> TimeTickerWrites { get; } = [];

        public List<(Guid[] Ids, TickerStatus Status)> OccurrenceWrites { get; } = [];

        public IAsyncEnumerable<TimeTickerEntity> QueueTimeTickers(TimeTickerEntity[] timeTickers, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TimeTickerEntity> QueueTimedOutTimeTickers(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReleaseAcquiredTimeTickers(Guid[] timeTickerIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TimeTickerEntity[]> GetEarliestTimeTickers(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> UpdateTimeTicker(InternalFunctionContext functionContext, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> GetTimeTickerRequest(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateTimeTickersWithUnifiedContext(Guid[] timeTickerIds, InternalFunctionContext functionContext, CancellationToken cancellationToken = default)
        {
            TimeTickerWrites.Add((timeTickerIds, functionContext.Status));
            return Task.CompletedTask;
        }
        public Task<TimeTickerEntity[]> AcquireImmediateTimeTickersAsync(Guid[] ids, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MigrateDefinedCronTickers((string Function, string Expression)[] cronTickers, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CronTickerEntity[]> GetAllCronTickerExpressions(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReleaseDeadNodeTimeTickerResources(string instanceIdentifier, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CronTickerOccurrenceEntity<CronTickerEntity>> GetEarliestAvailableCronOccurrence(Guid[] ids, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<CronTickerOccurrenceEntity<CronTickerEntity>> QueueCronTickerOccurrences((DateTime Key, InternalManagerContext[] Items) cronTickerOccurrences, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<CronTickerOccurrenceEntity<CronTickerEntity>> QueueTimedOutCronTickerOccurrences(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateCronTickerOccurrence(InternalFunctionContext functionContext, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReleaseAcquiredCronTickerOccurrences(Guid[] occurrenceIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> GetCronTickerOccurrenceRequest(Guid tickerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateCronTickerOccurrencesWithUnifiedContext(Guid[] timeTickerIds, InternalFunctionContext functionContext, CancellationToken cancellationToken = default)
        {
            OccurrenceWrites.Add((timeTickerIds, functionContext.Status));
            return Task.CompletedTask;
        }
        public Task ReleaseDeadNodeOccurrenceResources(string instanceIdentifier, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ITickerQueryable<TimeTickerEntity> TimeTickersQuery() => throw new NotSupportedException();
        public ITickerQueryable<CronTickerEntity> CronTickersQuery() => throw new NotSupportedException();
        public ITickerQueryable<CronTickerOccurrenceEntity<CronTickerEntity>> CronTickerOccurrencesQuery() => throw new NotSupportedException();
        public Task<TimeTickerEntity> GetTimeTickerById(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TimeTickerEntity[]> GetTimeTickers(Expression<Func<TimeTickerEntity, bool>> predicate, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PaginationResult<TimeTickerEntity>> GetTimeTickersPaginated(Expression<Func<TimeTickerEntity, bool>> predicate, int pageNumber, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> AddTimeTickers(TimeTickerEntity[] tickers, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> UpdateTimeTickers(TimeTickerEntity[] tickers, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> RemoveTimeTickers(Guid[] tickerIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CronTickerEntity> GetCronTickerById(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CronTickerEntity[]> GetCronTickers(Expression<Func<CronTickerEntity, bool>> predicate, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PaginationResult<CronTickerEntity>> GetCronTickersPaginated(Expression<Func<CronTickerEntity, bool>> predicate, int pageNumber, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> InsertCronTickers(CronTickerEntity[] tickers, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> UpdateCronTickers(CronTickerEntity[] cronTicker, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> RemoveCronTickers(Guid[] cronTickerIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CronTickerOccurrenceEntity<CronTickerEntity>[]> GetAllCronTickerOccurrences(Expression<Func<CronTickerOccurrenceEntity<CronTickerEntity>, bool>> predicate, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PaginationResult<CronTickerOccurrenceEntity<CronTickerEntity>>> GetAllCronTickerOccurrencesPaginated(Expression<Func<CronTickerOccurrenceEntity<CronTickerEntity>, bool>> predicate, int pageNumber, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> InsertCronTickerOccurrences(CronTickerOccurrenceEntity<CronTickerEntity>[] cronTickerOccurrences, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> RemoveCronTickerOccurrences(Guid[] cronTickerOccurrences, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CronTickerOccurrenceEntity<CronTickerEntity>[]> AcquireImmediateCronOccurrencesAsync(Guid[] occurrenceIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
