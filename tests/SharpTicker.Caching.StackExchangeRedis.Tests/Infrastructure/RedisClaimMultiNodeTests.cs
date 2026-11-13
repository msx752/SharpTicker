using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using StackExchange.Redis;
using SharpTicker.Caching.StackExchangeRedis.Helpers;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>
/// Provider instances (separate NodeIdentifier) over one real Redis and fake clocks, driving the fenced Queued to
/// InProgress claim and the fallback claim the way several nodes would. Every call goes through the provider
/// interface, so a custom default body would be caught. The Redis fallback (Acquire.lua) never takes a row another
/// holder owns, so the late-loop cases that need a claim use the fallback of the same node.
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class RedisClaimMultiNodeTests(RedisTestFixture fixture, Xunit.Abstractions.ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    // A tick count that does not end in 0, so the stored JSON text equals DateTime.ToString("O") and the
    // compare-and-set in QueueTimeTickers can match (see Probe_RedisQueueCasFailsOnTrailingZeroTimestamp).
    private static readonly DateTime SeedUpdatedAt = T0.AddHours(-1).AddTicks(1234567);

    private readonly FakeTickerClock _clock = new(T0);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> CreateProvider(string nodeIdentifier, ITickerClock? clock = null)
        => fixture.CreateProvider(nodeIdentifier, clock ?? _clock);

    private static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    private static TimeTickerEntity NewTimeTicker(
        TickerStatus status = TickerStatus.Idle,
        string? lockHolder = null,
        DateTime? executionTime = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Function = "claim-fn",
            ExecutionTime = executionTime ?? T0,
            Status = status,
            LockHolder = lockHolder,
            LockedAt = lockHolder is null ? null : T0.AddHours(-1),
            CreatedAt = T0.AddHours(-2),
            UpdatedAt = SeedUpdatedAt,
            Request = []
        };

    private async Task<Guid> SeedTimeTicker(
        ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> via,
        TickerStatus status = TickerStatus.Idle,
        string? lockHolder = null,
        DateTime? executionTime = null)
    {
        var ticker = NewTimeTicker(status, lockHolder, executionTime);
        await via.AddTimeTickers([ticker], CancellationToken.None);
        return ticker.Id;
    }

    private static (DateTime Key, InternalManagerContext[] Items) Slot(Guid cronId)
        => (T0, [new InternalManagerContext(cronId) { FunctionName = "claim-cron-fn", Expression = "* * * * *" }]);

    // Queues a new occurrence for the T0 slot on the given node.
    private static async Task<Guid> QueueOccurrenceOn(ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> node)
        => Assert.Single(await Drain(node.QueueCronTickerOccurrences(Slot(Guid.NewGuid()), CancellationToken.None))).Id;

    private static async Task<TimeTickerEntity> ReadTimeTicker(ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> via, Guid id)
        => (await via.GetTimeTickerById(id, CancellationToken.None))!;

    private static async Task<CronTickerOccurrenceEntity<CronTickerEntity>> ReadOccurrence(ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> via, Guid id)
        => (await via.GetAllCronTickerOccurrences(x => x.Id == id, CancellationToken.None)).Single();

    // The main loop path: add the row, read the window, queue it.
    private async Task<Guid> QueueTimeTickerOn(ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> node, DateTime? executionTime = null)
    {
        var id = await SeedTimeTicker(node, executionTime: executionTime);
        var window = await node.GetEarliestTimeTickers(CancellationToken.None);
        Assert.Single(await Drain(node.QueueTimeTickers(window.Where(x => x.Id == id).ToArray(), CancellationToken.None)));
        return id;
    }

    private static InternalFunctionContext StatusContext(Guid id, TickerStatus status)
        => new InternalFunctionContext { TickerId = id }.SetProperty(x => x.Status, status);

    public static TheoryData<TickerStatus> NonQueuedStatuses => new()
    {
        TickerStatus.Idle, TickerStatus.InProgress, TickerStatus.Done, TickerStatus.DueDone,
        TickerStatus.Failed, TickerStatus.Cancelled, TickerStatus.Skipped
    };

    #region TryMark

    [Fact]
    public async Task TryMark_OwnQueuedRow_ReturnsTrueAndWritesInProgress_TimeTicker()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueTimeTickerOn(nodeA);
        _clock.Advance(TimeSpan.FromMilliseconds(300));

        Assert.True(await nodeA.TryMarkTimeTickerInProgress(id, _clock.NextClaimMarker()));

        var row = await ReadTimeTicker(nodeA, id);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("node-A", row.LockHolder);
        Assert.Equal(T0, row.LockedAt);
        Assert.Equal(_clock.UtcNow, row.UpdatedAt);
    }

    [Fact]
    public async Task TryMark_OwnQueuedRow_ReturnsTrueAndWritesInProgress_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueOccurrenceOn(nodeA);
        _clock.Advance(TimeSpan.FromMilliseconds(300));

        Assert.True(await nodeA.TryMarkCronOccurrenceInProgress(id, _clock.NextClaimMarker()));

        var row = await ReadOccurrence(nodeA, id);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("node-A", row.LockHolder);
        Assert.Equal(T0, row.LockedAt);
        Assert.Equal(_clock.UtcNow, row.UpdatedAt);
    }

    // The claimed row is no longer pending: neither the window read nor the fallback offers it again.
    [Fact]
    public async Task TryMark_OnSuccess_RemovesRowFromPendingIndex()
    {
        var nodeA = CreateProvider("node-A");
        var timeId = await QueueTimeTickerOn(nodeA);
        var cronId = Guid.NewGuid();
        var occurrenceId = Assert.Single(await Drain(nodeA.QueueCronTickerOccurrences(Slot(cronId), CancellationToken.None))).Id;
        Assert.NotNull(await fixture.Database.SortedSetScoreAsync(RedisKeyBuilder.TimeTickerPendingKey, timeId.ToString()));
        Assert.NotNull(await fixture.Database.SortedSetScoreAsync(RedisKeyBuilder.CronOccurrencePendingKey, occurrenceId.ToString()));

        Assert.True(await nodeA.TryMarkTimeTickerInProgress(timeId, _clock.NextClaimMarker()));
        Assert.True(await nodeA.TryMarkCronOccurrenceInProgress(occurrenceId, _clock.NextClaimMarker()));

        Assert.Null(await fixture.Database.SortedSetScoreAsync(RedisKeyBuilder.TimeTickerPendingKey, timeId.ToString()));
        Assert.Null(await fixture.Database.SortedSetScoreAsync(RedisKeyBuilder.CronOccurrencePendingKey, occurrenceId.ToString()));
        Assert.Empty(await nodeA.GetEarliestTimeTickers(CancellationToken.None));
        Assert.Null(await nodeA.GetEarliestAvailableCronOccurrence([cronId], CancellationToken.None));
    }

    // The status clause alone cannot tell these apart: the row is Queued again, but for another node. Only the
    // holder clause stops the first node's late write, and the new holder's own write must still go through.
    [Fact]
    public async Task TryMark_RowRequeuedByAnotherNode_ReturnsFalseUntilItsHolderMarks_TimeTicker()
    {
        var nodeA = CreateProvider("node-A");
        var nodeB = CreateProvider("node-B");
        var id = await QueueTimeTickerOn(nodeA);
        await nodeA.ReleaseAcquiredTimeTickers([id], CancellationToken.None);
        var window = await nodeB.GetEarliestTimeTickers(CancellationToken.None);
        Assert.Single(await Drain(nodeB.QueueTimeTickers(window.Where(x => x.Id == id).ToArray(), CancellationToken.None)));
        var before = await ReadTimeTicker(nodeA, id);

        Assert.False(await nodeA.TryMarkTimeTickerInProgress(id, _clock.NextClaimMarker()));

        var untouched = await ReadTimeTicker(nodeA, id);
        Assert.Equal(TickerStatus.Queued, untouched.Status);
        Assert.Equal("node-B", untouched.LockHolder);
        Assert.Equal(before.UpdatedAt, untouched.UpdatedAt);
        Assert.True(await nodeB.TryMarkTimeTickerInProgress(id, _clock.NextClaimMarker()));
        Assert.Equal(TickerStatus.InProgress, (await ReadTimeTicker(nodeA, id)).Status);
    }

    [Fact]
    public async Task TryMark_RowRequeuedByAnotherNode_ReturnsFalseUntilItsHolderMarks_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var nodeB = CreateProvider("node-B");
        var cronId = Guid.NewGuid();
        var id = Assert.Single(await Drain(nodeA.QueueCronTickerOccurrences(Slot(cronId), CancellationToken.None))).Id;
        await nodeA.ReleaseAcquiredCronTickerOccurrences([id], CancellationToken.None);
        var requeue = Slot(cronId);
        requeue.Items[0].NextCronOccurrence = new NextCronOccurrence(id, T0);
        Assert.Single(await Drain(nodeB.QueueCronTickerOccurrences(requeue, CancellationToken.None)));
        var before = await ReadOccurrence(nodeA, id);

        Assert.False(await nodeA.TryMarkCronOccurrenceInProgress(id, _clock.NextClaimMarker()));

        var untouched = await ReadOccurrence(nodeA, id);
        Assert.Equal(TickerStatus.Queued, untouched.Status);
        Assert.Equal("node-B", untouched.LockHolder);
        Assert.Equal(before.UpdatedAt, untouched.UpdatedAt);
        Assert.True(await nodeB.TryMarkCronOccurrenceInProgress(id, _clock.NextClaimMarker()));
        Assert.Equal(TickerStatus.InProgress, (await ReadOccurrence(nodeA, id)).Status);
    }

    // Finished rows keep the lock holder, so even the owning node must not move them back to InProgress.
    [Theory]
    [MemberData(nameof(NonQueuedStatuses))]
    public async Task TryMark_RowInStatus_ReturnsFalse_TimeTicker(TickerStatus status)
    {
        var nodeA = CreateProvider("node-A");
        var id = await SeedTimeTicker(nodeA, status, lockHolder: "node-A");
        var before = await ReadTimeTicker(nodeA, id);

        Assert.False(await nodeA.TryMarkTimeTickerInProgress(id, _clock.NextClaimMarker()));

        var after = await ReadTimeTicker(nodeA, id);
        Assert.Equal(status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Theory]
    [MemberData(nameof(NonQueuedStatuses))]
    public async Task TryMark_RowInStatus_ReturnsFalse_CronOccurrence(TickerStatus status)
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueOccurrenceOn(nodeA);
        await nodeA.UpdateCronTickerOccurrence(StatusContext(id, status), CancellationToken.None);
        var before = await ReadOccurrence(nodeA, id);

        Assert.False(await nodeA.TryMarkCronOccurrenceInProgress(id, _clock.NextClaimMarker()));

        var after = await ReadOccurrence(nodeA, id);
        Assert.Equal(status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task TryMark_MissingRow_ReturnsFalse()
    {
        var nodeA = CreateProvider("node-A");

        Assert.False(await nodeA.TryMarkTimeTickerInProgress(Guid.NewGuid(), _clock.NextClaimMarker()));
        Assert.False(await nodeA.TryMarkCronOccurrenceInProgress(Guid.NewGuid(), _clock.NextClaimMarker()));
    }

    // The script re-encodes the row with cjson: whatever the row carries must come back unchanged except Status and
    // UpdatedAt, and the row must stay readable (empty collections stay arrays, nested children and the cron ticker
    // survive, numbers and escaped text keep their value).
    [Fact]
    public async Task MarkInProgress_KeepsEveryFieldReadable_TimeTicker()
    {
        var nodeA = CreateProvider("node-A");
        var ticker = NewTimeTicker(TickerStatus.Queued, "node-A");
        ticker.Request = [1, 2, 3, 250];
        ticker.Description = "d \"Children\":{} \\ / é";
        ticker.InitIdentifier = "init";
        ticker.Retries = 3;
        ticker.RetryIntervals = [5, 10];
        ticker.RetryCount = 2;
        ticker.ElapsedTime = 987654321;
        ticker.ExceptionMessage = "boom \"RetryIntervals\":{}";
        ticker.SkippedReason = "skipped";
        ticker.ExecutedAt = T0.AddMinutes(-5).AddTicks(7);
        ticker.ParentId = Guid.NewGuid();
        ticker.Children =
        [
            new TimeTickerEntity
            {
                Id = Guid.NewGuid(), Function = "child", Retries = 1, RetryIntervals = [], RunCondition = RunCondition.OnSuccess,
                Children = [new TimeTickerEntity { Id = Guid.NewGuid(), Function = "grandchild", RetryIntervals = [1], Children = [] }]
            }
        ];
        await nodeA.AddTimeTickers([ticker], CancellationToken.None);
        var before = await RawTimeTicker(ticker.Id);
        _clock.Advance(TimeSpan.FromMilliseconds(300));

        Assert.True(await nodeA.TryMarkTimeTickerInProgress(ticker.Id, _clock.NextClaimMarker()));

        var after = await RawTimeTicker(ticker.Id);
        Assert.Equal((int)TickerStatus.InProgress, (int)after["Status"]!);
        AssertSameExceptStatusAndUpdatedAt(before, after);
        var read = await ReadTimeTicker(nodeA, ticker.Id);
        Assert.Equal(ticker.Request, read.Request);
        Assert.Equal(ticker.Description, read.Description);
        Assert.Equal(ticker.ElapsedTime, read.ElapsedTime);
        Assert.Equal(ticker.RetryIntervals, read.RetryIntervals);
        Assert.Equal("grandchild", read.Children.Single().Children.Single().Function);
        Assert.Empty(read.Children.Single().RetryIntervals!);
        Assert.Empty(read.Children.Single().Children.Single().Children);
    }

    [Fact]
    public async Task MarkInProgress_KeepsEveryFieldReadable_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var occurrence = new CronTickerOccurrenceEntity<CronTickerEntity>
        {
            Id = Guid.NewGuid(),
            Status = TickerStatus.Queued,
            LockHolder = "node-A",
            LockedAt = T0.AddHours(-1),
            ExecutionTime = T0,
            CronTickerId = Guid.NewGuid(),
            ExceptionMessage = "boom \"Children\":{}",
            SkippedReason = "skipped",
            ElapsedTime = 987654321,
            RetryCount = 2,
            CreatedAt = T0.AddHours(-2),
            UpdatedAt = SeedUpdatedAt,
            CronTicker = new CronTickerEntity
            {
                Id = Guid.NewGuid(), Function = "claim-cron-fn", Expression = "* * * * *", Request = [9, 8, 7],
                Retries = 2, RetryIntervals = [], IsEnabled = true
            }
        };
        await nodeA.InsertCronTickerOccurrences([occurrence], CancellationToken.None);
        var before = JsonNode.Parse((string)(await fixture.Database.StringGetAsync(RedisKeyBuilder.CronOccurrenceKey(occurrence.Id)))!)!.AsObject();
        _clock.Advance(TimeSpan.FromMilliseconds(300));

        Assert.True(await nodeA.TryMarkCronOccurrenceInProgress(occurrence.Id, _clock.NextClaimMarker()));

        var after = JsonNode.Parse((string)(await fixture.Database.StringGetAsync(RedisKeyBuilder.CronOccurrenceKey(occurrence.Id)))!)!.AsObject();
        Assert.Equal((int)TickerStatus.InProgress, (int)after["Status"]!);
        AssertSameExceptStatusAndUpdatedAt(before, after);
        var read = await ReadOccurrence(nodeA, occurrence.Id);
        Assert.Equal(new byte[] { 9, 8, 7 }, read.CronTicker.Request);
        Assert.Empty(read.CronTicker.RetryIntervals!);
        Assert.Equal(occurrence.ElapsedTime, read.ElapsedTime);
    }

    private async Task<JsonObject> RawTimeTicker(Guid id)
        => JsonNode.Parse((string)(await fixture.Database.StringGetAsync(RedisKeyBuilder.TimeTickerKey(id)))!)!.AsObject();

    private static void AssertSameExceptStatusAndUpdatedAt(JsonObject before, JsonObject after)
    {
        var expected = (JsonObject)before.DeepClone();
        var actual = (JsonObject)after.DeepClone();
        foreach (var node in new[] { expected, actual })
        {
            node.Remove("Status");
            node.Remove("UpdatedAt");
        }
        Assert.True(JsonNode.DeepEquals(expected, actual), $"expected {expected.ToJsonString()}{Environment.NewLine}actual   {actual.ToJsonString()}");
    }

    #endregion

    #region Children

    // Each chain node is a row of its own, linked to its parent.
    private async Task<(Guid ParentId, Guid ChildId)> SeedParentWithChild(ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> via)
    {
        var child = NewTimeTicker();
        child.ExecutionTime = null;
        child.Function = "claim-child-fn";
        child.RunCondition = RunCondition.OnSuccess;
        var parent = NewTimeTicker();
        parent.Children = [child];
        await via.AddTimeTickers([parent], CancellationToken.None);
        return (parent.Id, child.Id);
    }

    // A child runs only inside its parent. No claim path hands it out as a row of its own, and only the claim that won
    // the parent carries it, so a late loop cannot run it a second time. The Redis fallback never takes a row another
    // holder owns, so the late loop loses to the fallback of its own node.
    [Fact]
    public async Task Child_IsCarriedOnlyByTheClaimThatWonItsParent()
    {
        var nodeA = CreateProvider("node-A");
        var (parentId, childId) = await SeedParentWithChild(nodeA);

        var window = await nodeA.GetEarliestTimeTickers(CancellationToken.None);
        Assert.DoesNotContain(window, x => x.Id == childId);
        var parent = Assert.Single(window, x => x.Id == parentId);
        Assert.Equal([childId], parent.Children.Select(x => x.Id));
        Assert.Single(await Drain(nodeA.QueueTimeTickers([parent], CancellationToken.None)));

        _clock.Advance(TimeSpan.FromSeconds(2));
        var fallback = await Drain(nodeA.QueueTimedOutTimeTickers(CancellationToken.None));

        Assert.DoesNotContain(fallback, x => x.Id == childId);
        var taken = Assert.Single(fallback, x => x.Id == parentId);
        Assert.Equal([childId], taken.Children.Select(x => x.Id));
        Assert.False(await nodeA.TryMarkTimeTickerInProgress(parentId, _clock.NextClaimMarker()));
        Assert.False(await nodeA.TryMarkTimeTickerInProgress(childId, _clock.NextClaimMarker()));
        Assert.Empty(await Drain(nodeA.QueueTimedOutTimeTickers(CancellationToken.None)));
        Assert.Empty(await Drain(CreateProvider("node-B").QueueTimedOutTimeTickers(CancellationToken.None)));
    }

    // The handler writes a child's status by the child's id, while it runs and when it ends. The write must land, and
    // the parent read for the dashboard must show it.
    [Fact]
    public async Task AChildsStatusWrites_AreStored_AndShownUnderItsParent()
    {
        var nodeA = CreateProvider("node-A");
        var (parentId, childId) = await SeedParentWithChild(nodeA);

        Assert.Equal(1, await nodeA.UpdateTimeTicker(StatusContext(childId, TickerStatus.InProgress), CancellationToken.None));
        Assert.Equal(TickerStatus.InProgress, Assert.Single((await ReadTimeTicker(nodeA, parentId)).Children).Status);

        await nodeA.UpdateTimeTickersWithUnifiedContext([childId], StatusContext(childId, TickerStatus.Done), CancellationToken.None);

        Assert.Equal(TickerStatus.Done, Assert.Single((await ReadTimeTicker(nodeA, parentId)).Children).Status);
        Assert.Equal(TickerStatus.Done, Assert.Single(await nodeA.GetTimeTickers(x => x.Id == parentId, CancellationToken.None)).Children.Single().Status);
    }

    private static (TimeTickerEntity Parent, TimeTickerEntity Child, TimeTickerEntity Grandchild) NewChain()
    {
        var grandchild = NewTimeTicker();
        grandchild.ExecutionTime = null;
        grandchild.Function = "chain-grandchild";
        var child = NewTimeTicker();
        child.ExecutionTime = null;
        child.Function = "chain-child";
        child.RunCondition = RunCondition.OnSuccess;
        child.Children = [grandchild];
        var parent = NewTimeTicker();
        parent.Children = [child];
        return (parent, child, grandchild);
    }

    // A chain stored by an older version: the children nested in the parent's row, no rows or links of their own.
    private async Task<(TimeTickerEntity Parent, TimeTickerEntity Child, TimeTickerEntity Grandchild)> SeedOldFormatChain()
    {
        var chain = NewChain();
        var db = fixture.Database;
        await db.StringSetAsync(RedisKeyBuilder.TimeTickerKey(chain.Parent.Id), System.Text.Json.JsonSerializer.Serialize(chain.Parent));
        await db.SetAddAsync(RedisKeyBuilder.TimeTickerIdsKey, chain.Parent.Id.ToString());
        await db.SortedSetAddAsync(RedisKeyBuilder.TimeTickerPendingKey, chain.Parent.Id.ToString(), RedisKeyBuilder.ToScore(chain.Parent.ExecutionTime!.Value));
        return chain;
    }

    private static void AssertCarriesTheChain(TimeTickerEntity root, Guid childId, Guid grandchildId)
    {
        var child = Assert.Single(root.Children);
        Assert.Equal(childId, child.Id);
        Assert.Equal(RunCondition.OnSuccess, child.RunCondition);
        Assert.Equal(grandchildId, Assert.Single(child.Children).Id);
    }

    [Fact]
    public async Task AChain_IsStoredAsOneRowPerNode_AndReadBackWhole()
    {
        var nodeA = CreateProvider("node-A");
        var (parent, child, grandchild) = NewChain();
        await nodeA.AddTimeTickers([parent], CancellationToken.None);

        // The caller's entity keeps its children.
        Assert.Equal(child.Id, Assert.Single(parent.Children).Id);
        Assert.Contains("\"Children\":[]", (string)(await fixture.Database.StringGetAsync(RedisKeyBuilder.TimeTickerKey(parent.Id)))!);

        var childRow = await ReadTimeTicker(nodeA, child.Id);
        Assert.Equal(parent.Id, childRow.ParentId);
        Assert.Equal(grandchild.Id, Assert.Single(childRow.Children).Id);
        Assert.Equal(child.Id, (await ReadTimeTicker(nodeA, grandchild.Id)).ParentId);

        AssertCarriesTheChain(await ReadTimeTicker(nodeA, parent.Id), child.Id, grandchild.Id);
        AssertCarriesTheChain(Assert.Single(await nodeA.GetTimeTickers(null!, CancellationToken.None)), child.Id, grandchild.Id);
        Assert.Single(await nodeA.GetTimeTickers(x => x.Children.Any(), CancellationToken.None));

        // The dashboard lists a node's children through the queryable.
        Assert.Equal(child.Id, Assert.Single(await nodeA.TimeTickersQuery().Where(x => x.ParentId == parent.Id).ToArrayAsync()).Id);
        Assert.Equal(3, (await nodeA.TimeTickersQuery().ToArrayAsync()).Length);

        // Only the root is pending; a child runs inside its parent's run.
        Assert.Equal([parent.Id.ToString()], (await fixture.Database.SortedSetRangeByRankAsync(RedisKeyBuilder.TimeTickerPendingKey)).Select(x => x.ToString()));
    }

    [Fact]
    public async Task EveryClaimPath_CarriesTheChainReadFromTheChildRows()
    {
        var nodeA = CreateProvider("node-A");
        var (parent, child, grandchild) = NewChain();
        await nodeA.AddTimeTickers([parent], CancellationToken.None);

        AssertCarriesTheChain(Assert.Single(await nodeA.GetEarliestTimeTickers(CancellationToken.None)), child.Id, grandchild.Id);
        AssertCarriesTheChain(Assert.Single(await nodeA.AcquireImmediateTimeTickersAsync([parent.Id], CancellationToken.None)), child.Id, grandchild.Id);

        var (other, otherChild, otherGrandchild) = NewChain();
        await nodeA.AddTimeTickers([other], CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(2));
        AssertCarriesTheChain(Assert.Single(await Drain(nodeA.QueueTimedOutTimeTickers(CancellationToken.None))), otherChild.Id, otherGrandchild.Id);
    }

    [Fact]
    public async Task AnOldFormatChain_IsReadAndCarriedAsStored()
    {
        var nodeA = CreateProvider("node-A");
        var (parent, child, grandchild) = await SeedOldFormatChain();

        AssertCarriesTheChain(await ReadTimeTicker(nodeA, parent.Id), child.Id, grandchild.Id);
        AssertCarriesTheChain(Assert.Single(await nodeA.GetTimeTickers(null!, CancellationToken.None)), child.Id, grandchild.Id);
        AssertCarriesTheChain(Assert.Single(await nodeA.GetEarliestTimeTickers(CancellationToken.None)), child.Id, grandchild.Id);

        _clock.Advance(TimeSpan.FromSeconds(2));
        AssertCarriesTheChain(Assert.Single(await Drain(nodeA.QueueTimedOutTimeTickers(CancellationToken.None))), child.Id, grandchild.Id);

        // The claim wrote the nodes as rows of their own; the chain reads as before.
        AssertCarriesTheChain(await ReadTimeTicker(nodeA, parent.Id), child.Id, grandchild.Id);
    }

    [Fact]
    public async Task UpdatingAnOldFormatChain_StoresEachNodeAsARowOfItsOwn()
    {
        var nodeA = CreateProvider("node-A");
        var (parent, child, grandchild) = await SeedOldFormatChain();

        Assert.Equal(1, await nodeA.UpdateTimeTickers([await ReadTimeTicker(nodeA, parent.Id)], CancellationToken.None));

        Assert.Contains("\"Children\":[]", (string)(await fixture.Database.StringGetAsync(RedisKeyBuilder.TimeTickerKey(parent.Id)))!);
        Assert.Equal(parent.Id, (await ReadTimeTicker(nodeA, child.Id)).ParentId);
        Assert.Equal(1, await nodeA.UpdateTimeTicker(StatusContext(grandchild.Id, TickerStatus.Done), CancellationToken.None));

        var read = await ReadTimeTicker(nodeA, parent.Id);
        AssertCarriesTheChain(read, child.Id, grandchild.Id);
        Assert.Equal(TickerStatus.Done, read.Children.Single().Children.Single().Status);
    }

    [Fact]
    public async Task RemovingAParent_RemovesItsWholeChain()
    {
        var nodeA = CreateProvider("node-A");
        var (parent, child, grandchild) = NewChain();
        await nodeA.AddTimeTickers([parent], CancellationToken.None);

        Assert.Equal(1, await nodeA.RemoveTimeTickers([parent.Id], CancellationToken.None));

        var db = fixture.Database;
        foreach (var id in new[] { parent.Id, child.Id, grandchild.Id })
        {
            Assert.False(await db.KeyExistsAsync(RedisKeyBuilder.TimeTickerKey(id)));
            Assert.False(await db.KeyExistsAsync(RedisKeyBuilder.TimeTickerChildrenKey(id)));
        }
        Assert.Empty(await db.SetMembersAsync(RedisKeyBuilder.TimeTickerIdsKey));
        Assert.Empty(await db.SortedSetRangeByRankAsync(RedisKeyBuilder.TimeTickerPendingKey));
    }

    [Fact]
    public async Task RemovingAChild_RemovesItsGrandchildren_AndUnlinksItFromItsParent()
    {
        var nodeA = CreateProvider("node-A");
        var (parent, child, grandchild) = NewChain();
        await nodeA.AddTimeTickers([parent], CancellationToken.None);

        Assert.Equal(1, await nodeA.RemoveTimeTickers([child.Id], CancellationToken.None));

        Assert.Empty((await ReadTimeTicker(nodeA, parent.Id)).Children);
        Assert.False(await fixture.Database.KeyExistsAsync(RedisKeyBuilder.TimeTickerKey(grandchild.Id)));
        Assert.Equal([parent.Id.ToString()], (await fixture.Database.SetMembersAsync(RedisKeyBuilder.TimeTickerIdsKey)).Select(x => x.ToString()));
    }

    // An update carries a copy of the chain read earlier. A child's run (written by id) since then must survive it, while
    // the definition the update carries is applied.
    [Fact]
    public async Task UpdatingAChain_KeepsTheRunStateOfItsChildren_AndAppliesTheirDefinition()
    {
        var nodeA = CreateProvider("node-A");
        var (parent, child, grandchild) = NewChain();
        await nodeA.AddTimeTickers([parent], CancellationToken.None);
        var stale = await ReadTimeTicker(nodeA, parent.Id);

        var done = StatusContext(child.Id, TickerStatus.Done).SetProperty(x => x.ExecutedAt, T0).SetProperty(x => x.ElapsedTime, 42L);
        Assert.Equal(1, await nodeA.UpdateTimeTicker(done, CancellationToken.None));

        stale.Children.Single().Description = "edited";
        Assert.Equal(1, await nodeA.UpdateTimeTickers([stale], CancellationToken.None));

        var row = await ReadTimeTicker(nodeA, child.Id);
        Assert.Equal(TickerStatus.Done, row.Status);
        Assert.Equal(T0, row.ExecutedAt);
        Assert.Equal(42L, row.ElapsedTime);
        Assert.Equal("edited", row.Description);
        Assert.Equal(grandchild.Id, Assert.Single(row.Children).Id);
    }

    // The dashboard's edit form sends a child without its ParentId and RunCondition, and with an execution time. The
    // child must stay in its chain: linked, with its condition, and not handed out on its own.
    [Fact]
    public async Task EditingAChildAsAStandaloneTicker_KeepsItInItsChain()
    {
        var nodeA = CreateProvider("node-A");
        var (parent, child, grandchild) = NewChain();
        await nodeA.AddTimeTickers([parent], CancellationToken.None);

        var edit = NewTimeTicker();
        edit.Id = child.Id;
        edit.Function = "edited-child";
        Assert.Null(edit.ParentId);
        Assert.Null(edit.RunCondition);
        Assert.Equal(1, await nodeA.UpdateTimeTickers([edit], CancellationToken.None));

        var read = await ReadTimeTicker(nodeA, parent.Id);
        var edited = Assert.Single(read.Children);
        Assert.Equal("edited-child", edited.Function);
        Assert.Equal(RunCondition.OnSuccess, edited.RunCondition);
        Assert.Equal(grandchild.Id, Assert.Single(edited.Children).Id);
        Assert.Single(await nodeA.GetTimeTickers(null!, CancellationToken.None));

        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal([parent.Id], (await Drain(nodeA.QueueTimedOutTimeTickers(CancellationToken.None))).Select(x => x.Id));
    }

    // The dashboard's edit form sends a root without its children. An older chain keeps its children nested in the
    // root's row, so such an edit must not drop them.
    [Fact]
    public async Task EditingAnOldFormatRootWithoutItsChildren_KeepsTheChain()
    {
        var nodeA = CreateProvider("node-A");
        var (parent, child, grandchild) = await SeedOldFormatChain();

        var edit = NewTimeTicker();
        edit.Id = parent.Id;
        edit.Description = "edited";
        Assert.Equal(1, await nodeA.UpdateTimeTickers([edit], CancellationToken.None));

        var read = await ReadTimeTicker(nodeA, parent.Id);
        Assert.Equal("edited", read.Description);
        AssertCarriesTheChain(read, child.Id, grandchild.Id);
        Assert.Equal(parent.Id, (await ReadTimeTicker(nodeA, child.Id)).ParentId);
        Assert.Contains("\"Children\":[]", (string)(await fixture.Database.StringGetAsync(RedisKeyBuilder.TimeTickerKey(parent.Id)))!);
    }

    // Children come back in the order the chain was built, also when they share their CreatedAt.
    [Fact]
    public async Task Children_ComeBackInTheOrderTheChainWasBuilt()
    {
        var nodeA = CreateProvider("node-A");
        var parent = NewTimeTicker();
        var children = Enumerable.Range(0, 5).Select(i =>
        {
            var c = NewTimeTicker();
            c.ExecutionTime = null;
            c.Function = $"child-{i}";
            return c;
        }).ToList();
        parent.Children = children;
        await nodeA.AddTimeTickers([parent], CancellationToken.None);

        var expected = children.Select(c => c.Id).ToArray();
        Assert.Equal(expected, (await ReadTimeTicker(nodeA, parent.Id)).Children.Select(c => c.Id));
        Assert.Equal(expected, Assert.Single(await nodeA.GetTimeTickers(null!, CancellationToken.None)).Children.Select(c => c.Id));
        Assert.Equal(expected, Assert.Single(await nodeA.GetEarliestTimeTickers(CancellationToken.None)).Children.Select(c => c.Id));
    }

    // A write that stopped after linking a child but before storing its row leaves a link without a row.
    [Fact]
    public async Task AChildLinkWithoutItsRow_IsSkippedOnRead()
    {
        var nodeA = CreateProvider("node-A");
        var (parent, child, _) = NewChain();
        await nodeA.AddTimeTickers([parent], CancellationToken.None);
        await fixture.Database.SortedSetAddAsync(RedisKeyBuilder.TimeTickerChildrenKey(parent.Id), Guid.NewGuid().ToString(), -1);

        Assert.Equal(child.Id, Assert.Single((await ReadTimeTicker(nodeA, parent.Id)).Children).Id);
        Assert.Equal(child.Id, Assert.Single(Assert.Single(await nodeA.GetEarliestTimeTickers(CancellationToken.None)).Children).Id);
    }

    #endregion

    #region Delayed children

    // A delayed child (a child with an execution time of its own) is carried with its time, so the handler leaves it to
    // the store instead of running it inside its parent's run.
    [Fact]
    public async Task ADelayedChild_IsCarriedWithItsTime()
    {
        var nodeA = CreateProvider("node-A");
        var grandchild = NewTimeTicker(executionTime: T0.AddHours(3));
        grandchild.RunCondition = RunCondition.OnFailure;
        var child = NewTimeTicker(executionTime: T0.AddHours(2));
        child.RunCondition = RunCondition.OnSuccess;
        child.Children = [grandchild];
        var parent = NewTimeTicker();
        parent.Children = [child];
        await nodeA.AddTimeTickers([parent], CancellationToken.None);

        var payload = Assert.Single(await nodeA.GetEarliestTimeTickers(CancellationToken.None));

        var carried = Assert.Single(payload.Children);
        Assert.Equal(T0.AddHours(2), carried.ExecutionTime);
        Assert.Equal(RunCondition.OnSuccess, carried.RunCondition);
        var carriedGrandchild = Assert.Single(carried.Children);
        Assert.Equal(T0.AddHours(3), carriedGrandchild.ExecutionTime);
        Assert.Equal(RunCondition.OnFailure, carriedGrandchild.RunCondition);
    }

    // A chain stored by an older version keeps its children nested in the parent's row. Read before its claim, a delayed
    // one is carried without its time: it has no row yet. The claim writes the nodes as rows of their own, so the parent
    // is handed out with the delayed child's time, and the store hands that child out at its time.
    [Fact]
    public async Task ADelayedChildOfAnOldFormatChain_IsCarriedWithItsTimeOnceItsParentIsClaimed()
    {
        var chain = NewChain();
        chain.Child.ExecutionTime = T0.AddHours(2);
        chain.Grandchild.ExecutionTime = T0.AddHours(3);
        var db = fixture.Database;
        await db.StringSetAsync(RedisKeyBuilder.TimeTickerKey(chain.Parent.Id), System.Text.Json.JsonSerializer.Serialize(chain.Parent));
        await db.SetAddAsync(RedisKeyBuilder.TimeTickerIdsKey, chain.Parent.Id.ToString());
        await db.SortedSetAddAsync(RedisKeyBuilder.TimeTickerPendingKey, chain.Parent.Id.ToString(), RedisKeyBuilder.ToScore(chain.Parent.ExecutionTime!.Value));
        var nodeA = CreateProvider("node-A");

        var carried = Assert.Single(Assert.Single(await nodeA.GetEarliestTimeTickers(CancellationToken.None)).Children);
        Assert.Null(carried.ExecutionTime);
        Assert.Null(Assert.Single(carried.Children).ExecutionTime);

        _clock.Advance(TimeSpan.FromSeconds(2));
        carried = Assert.Single(Assert.Single(await nodeA.AcquireImmediateTimeTickersAsync([chain.Parent.Id], CancellationToken.None)).Children);
        Assert.Equal(T0.AddHours(2), carried.ExecutionTime);
        Assert.Equal(T0.AddHours(3), Assert.Single(carried.Children).ExecutionTime);
    }

    // A parent row (not due, or already run) with a delayed child.
    private async Task<(Guid ParentId, Guid ChildId)> SeedDelayedChild(TickerStatus parentStatus, RunCondition? condition, DateTime childTime, TimeTickerEntity? grandchild = null)
    {
        var child = NewTimeTicker(executionTime: childTime);
        child.Function = "delayed-child-fn";
        child.RunCondition = condition;
        if (grandchild != null) child.Children = [grandchild];
        var parent = NewTimeTicker(parentStatus, executionTime: T0.AddHours(-1));
        parent.Children = [child];
        await CreateProvider("seed").AddTimeTickers([parent], CancellationToken.None);
        return (parent.Id, child.Id);
    }

    public static IEnumerable<object?[]> EveryConditionAndParentStatus()
        => from condition in Enum.GetValues<RunCondition>().Select(c => (RunCondition?)c).Append(null)
           from status in Enum.GetValues<TickerStatus>()
           select new object?[] { condition, status };

    [Theory]
    [MemberData(nameof(EveryConditionAndParentStatus))]
    public async Task DelayedChild_IsHandedOutExactlyWhenItsParentsStatusLetsItRun(RunCondition? condition, TickerStatus parentStatus)
    {
        var (_, childId) = await SeedDelayedChild(parentStatus, condition, T0.AddSeconds(30));
        var nodeA = CreateProvider("node-A");

        var claimed = await nodeA.AcquireImmediateTimeTickersAsync([childId], CancellationToken.None);

        Assert.Equal(ChainRunGate.Allows(condition, parentStatus), claimed.Any(x => x.Id == childId));
        if (!claimed.Any(x => x.Id == childId))
            Assert.Equal(TickerStatus.Idle, (await ReadTimeTicker(nodeA, childId)).Status);
    }

    [Fact]
    public async Task RunNow_ClaimsTheRowsWhoseGateIsOpen_AndLeavesAWaitingDelayedChild()
    {
        var (_, waitingChildId) = await SeedDelayedChild(TickerStatus.InProgress, RunCondition.OnSuccess, T0.AddSeconds(30));
        var (_, readyChildId) = await SeedDelayedChild(TickerStatus.Done, RunCondition.OnSuccess, T0.AddSeconds(30));
        var nodeA = CreateProvider("node-A");
        var plainId = await SeedTimeTicker(nodeA, executionTime: T0.AddSeconds(30));

        var claimed = await nodeA.AcquireImmediateTimeTickersAsync([waitingChildId, readyChildId, plainId], CancellationToken.None);

        Assert.Equal(new[] { readyChildId, plainId }.OrderBy(x => x), claimed.Select(x => x.Id).OrderBy(x => x));
        Assert.Equal(TickerStatus.Idle, (await ReadTimeTicker(nodeA, waitingChildId)).Status);
    }

    [Theory]
    [MemberData(nameof(EveryConditionAndParentStatus))]
    public async Task DelayedChild_IsInTheWindowExactlyWhenItsParentsStatusLetsItRun(RunCondition? condition, TickerStatus parentStatus)
    {
        var (_, childId) = await SeedDelayedChild(parentStatus, condition, T0.AddSeconds(30));

        var window = await CreateProvider("node-A").GetEarliestTimeTickers(CancellationToken.None);

        Assert.Equal(ChainRunGate.Allows(condition, parentStatus), window.Any(x => x.Id == childId));
    }

    [Theory]
    [MemberData(nameof(EveryConditionAndParentStatus))]
    public async Task OverdueDelayedChild_IsClaimedSkippedOrLeftByTheFallback_ExactlyAsChainRunGateSays(RunCondition? condition, TickerStatus parentStatus)
    {
        var (_, childId) = await SeedDelayedChild(parentStatus, condition, T0.AddSeconds(-30));
        var nodeA = CreateProvider("node-A");

        var fallback = await Drain(nodeA.QueueTimedOutTimeTickers(CancellationToken.None));

        var claimed = fallback.Any(x => x.Id == childId);
        var row = await ReadTimeTicker(nodeA, childId);
        if (ChainRunGate.Allows(condition, parentStatus))
            Assert.True(claimed, "a child its parent's status lets run was not claimed");
        else if (ChainRunGate.IsFinal(parentStatus))
        {
            Assert.True(!claimed && row.Status == TickerStatus.Skipped, $"claimed={claimed}, status={row.Status}; expected Skipped");
            Assert.Null(await fixture.Database.SortedSetScoreAsync(RedisKeyBuilder.TimeTickerPendingKey, childId.ToString()));
        }
        else
            Assert.True(!claimed && row.Status == TickerStatus.Idle, $"claimed={claimed}, status={row.Status}; expected it to wait");
    }

    [Theory]
    [InlineData(TickerStatus.Idle, 30)]
    [InlineData(TickerStatus.Queued, 30)]
    [InlineData(TickerStatus.InProgress, 30)]
    [InlineData(TickerStatus.InProgress, -30)]
    public async Task DelayedChild_IsNotHandedOutByTheWindowOrTheFallback_WhileItsParentHasNotEnded(TickerStatus parentStatus, int childDueInSeconds)
    {
        var (_, childId) = await SeedDelayedChild(parentStatus, RunCondition.OnSuccess, T0.AddSeconds(childDueInSeconds));
        var node = CreateProvider("node-A");

        Assert.DoesNotContain(await node.GetEarliestTimeTickers(CancellationToken.None), x => x.Id == childId);
        Assert.DoesNotContain(await Drain(node.QueueTimedOutTimeTickers(CancellationToken.None)), x => x.Id == childId);
        Assert.Equal(TickerStatus.Idle, (await ReadTimeTicker(node, childId)).Status);
    }

    [Fact]
    public async Task DelayedChild_IsHandedOutByTheWindowAtItsTime_OnceItsParentSucceeded()
    {
        var (_, childId) = await SeedDelayedChild(TickerStatus.Done, RunCondition.OnSuccess, T0.AddSeconds(30));
        var node = CreateProvider("node-A");

        var window = await node.GetEarliestTimeTickers(CancellationToken.None);

        Assert.Equal([childId], window.Select(x => x.Id));
        Assert.Single(await Drain(node.QueueTimeTickers(window, CancellationToken.None)));
    }

    [Fact]
    public async Task DelayedChild_IsHandedOutByTheFallback_WhenItsTimePassedBeforeItsParentSucceeded()
    {
        var grandchild = NewTimeTicker(executionTime: T0.AddHours(1));
        var (_, childId) = await SeedDelayedChild(TickerStatus.Done, RunCondition.OnSuccess, T0.AddSeconds(-30), grandchild);

        var fallback = await Drain(CreateProvider("node-A").QueueTimedOutTimeTickers(CancellationToken.None));

        // The claimed child carries its own delayed child, with its time.
        var claimed = Assert.Single(fallback, x => x.Id == childId);
        Assert.Equal(T0.AddHours(1), Assert.Single(claimed.Children).ExecutionTime);
    }

    // A waiting delayed child due first must not hide the rows due after it.
    [Fact]
    public async Task AWaitingDelayedChildDueFirst_DoesNotHideTheJobsDueAfterIt()
    {
        await SeedDelayedChild(TickerStatus.InProgress, RunCondition.OnSuccess, T0.AddSeconds(10));
        var nodeA = CreateProvider("node-A");
        var laterId = await SeedTimeTicker(nodeA, executionTime: T0.AddSeconds(20));

        var window = await nodeA.GetEarliestTimeTickers(CancellationToken.None);

        Assert.Equal([laterId], window.Select(x => x.Id));
    }

    // Many waiting delayed children due first: the window reads past them in pages.
    [Fact]
    public async Task ManyWaitingDelayedChildrenDueFirst_DoNotHideTheJobsDueAfterThem()
    {
        var children = Enumerable.Range(0, 150).Select(i =>
        {
            var c = NewTimeTicker(executionTime: T0.AddSeconds(10).AddMilliseconds(i));
            c.RunCondition = RunCondition.OnSuccess;
            return c;
        }).ToList();
        var parent = NewTimeTicker(TickerStatus.InProgress, executionTime: T0.AddHours(-1));
        parent.Children = children;
        var nodeA = CreateProvider("node-A");
        await nodeA.AddTimeTickers([parent], CancellationToken.None);
        var laterId = await SeedTimeTicker(nodeA, executionTime: T0.AddSeconds(20));

        Assert.Equal([laterId], (await nodeA.GetEarliestTimeTickers(CancellationToken.None)).Select(x => x.Id));
    }

    // A waiting delayed child in the same second as a row that is due must not go out with it.
    [Fact]
    public async Task AWaitingDelayedChild_IsLeftOutOfTheWindowItsSecondOpens()
    {
        await SeedDelayedChild(TickerStatus.InProgress, RunCondition.OnSuccess, T0.AddSeconds(10).AddMilliseconds(100));
        var nodeA = CreateProvider("node-A");
        var dueId = await SeedTimeTicker(nodeA, executionTime: T0.AddSeconds(10).AddMilliseconds(200));

        var window = await nodeA.GetEarliestTimeTickers(CancellationToken.None);

        Assert.Equal([dueId], window.Select(x => x.Id));
    }

    // The parent's run skips a child its outcome does not let run; the fallback does it for a node that stopped first,
    // and the skip reaches a delayed grandchild on a later pass.
    [Fact]
    public async Task TheFallback_SkipsADelayedChildWhoseParentsOutcomeDoesNotMatch_AndThenItsDelayedGrandchild()
    {
        var grandchild = NewTimeTicker(executionTime: T0.AddSeconds(-20));
        grandchild.Function = "delayed-grandchild-fn";
        var (_, childId) = await SeedDelayedChild(TickerStatus.Failed, RunCondition.OnSuccess, T0.AddSeconds(-30), grandchild);
        var node = CreateProvider("node-A");

        Assert.Empty(await Drain(node.QueueTimedOutTimeTickers(CancellationToken.None)));
        Assert.Equal(TickerStatus.Idle, (await ReadTimeTicker(node, grandchild.Id)).Status);
        Assert.Empty(await Drain(node.QueueTimedOutTimeTickers(CancellationToken.None)));

        var skipped = await ReadTimeTicker(node, childId);
        Assert.Equal(TickerStatus.Skipped, skipped.Status);
        Assert.Equal(ChainRunGate.NotMatchedReason, skipped.SkippedReason);
        Assert.Equal(T0, skipped.ExecutedAt);
        Assert.Null(skipped.LockHolder);
        Assert.Equal(TickerStatus.Skipped, (await ReadTimeTicker(node, grandchild.Id)).Status);
    }

    // A row whose parent is not found waits: it is not handed out, not skipped, and does not hide the rows due after it.
    [Fact]
    public async Task ADelayedChildWhoseParentIsMissing_Waits_AndDoesNotHideTheJobsDueAfterIt()
    {
        var overdueOrphan = NewTimeTicker(executionTime: T0.AddSeconds(-30));
        overdueOrphan.ParentId = Guid.NewGuid();
        var orphan = NewTimeTicker(executionTime: T0.AddSeconds(10));
        orphan.ParentId = Guid.NewGuid();
        var node = CreateProvider("node-A");
        await node.AddTimeTickers([overdueOrphan, orphan], CancellationToken.None);
        var laterId = await SeedTimeTicker(node, executionTime: T0.AddSeconds(20));

        Assert.Equal([laterId], (await node.GetEarliestTimeTickers(CancellationToken.None)).Select(x => x.Id));
        Assert.Empty(await Drain(node.QueueTimedOutTimeTickers(CancellationToken.None)));
        Assert.Empty(await node.AcquireImmediateTimeTickersAsync([overdueOrphan.Id, orphan.Id], CancellationToken.None));
        Assert.Equal(TickerStatus.Idle, (await ReadTimeTicker(node, overdueOrphan.Id)).Status);
        Assert.Equal(TickerStatus.Idle, (await ReadTimeTicker(node, orphan.Id)).Status);
    }

    // The fallback reads the parents' statuses before it claims: a parent it takes in the same pass has not ended, so its
    // overdue delayed child waits for the parent's run.
    [Fact]
    public async Task TheFallback_TakesAnOverdueParent_ButNotItsOverdueDelayedChild_InTheSamePass()
    {
        var child = NewTimeTicker(executionTime: T0.AddSeconds(-30));
        child.Function = "delayed-child-fn";
        child.RunCondition = RunCondition.OnSuccess;
        var parent = NewTimeTicker(executionTime: T0.AddSeconds(-60));
        parent.Children = [child];
        var nodeA = CreateProvider("node-A");
        await nodeA.AddTimeTickers([parent], CancellationToken.None);

        var fallback = await Drain(nodeA.QueueTimedOutTimeTickers(CancellationToken.None));

        Assert.Equal([parent.Id], fallback.Select(x => x.Id));
        Assert.Equal(TickerStatus.Idle, (await ReadTimeTicker(nodeA, child.Id)).Status);
    }

    // A skip whose index write never landed (the node stopped in between) leaves a pending entry for a row that will
    // never run; the fallback drops it instead of trying the row again on every pass.
    [Fact]
    public async Task TheFallback_DropsAPendingEntryLeftForARowThatEnded()
    {
        var (_, childId) = await SeedDelayedChild(TickerStatus.Failed, RunCondition.OnSuccess, T0.AddSeconds(-30));
        var node = CreateProvider("node-A");
        Assert.Empty(await Drain(node.QueueTimedOutTimeTickers(CancellationToken.None)));
        Assert.Equal(TickerStatus.Skipped, (await ReadTimeTicker(node, childId)).Status);
        await fixture.Database.SortedSetAddAsync(RedisKeyBuilder.TimeTickerPendingKey, childId.ToString(), RedisKeyBuilder.ToScore(T0.AddSeconds(-30)));

        Assert.Empty(await Drain(node.QueueTimedOutTimeTickers(CancellationToken.None)));

        Assert.Null(await fixture.Database.SortedSetScoreAsync(RedisKeyBuilder.TimeTickerPendingKey, childId.ToString()));
    }

    // A stored row that does not deserialize goes to the claim script, which rewrites it in a readable form. A delayed
    // child among them must still wait for its parent: it is put back, not run.
    [Fact]
    public async Task AnUnreadableDelayedChild_IsPutBack_WhileItsParentHasNotEnded()
    {
        var (_, childId) = await SeedDelayedChild(TickerStatus.InProgress, RunCondition.OnSuccess, T0.AddSeconds(-30));
        var key = RedisKeyBuilder.TimeTickerKey(childId);
        var json = (string)(await fixture.Database.StringGetAsync(key))!;
        await fixture.Database.StringSetAsync(key, json.Replace("\"Children\":[]", "\"Children\":{}"));
        var node = CreateProvider("node-A");
        var broken = json.Replace("\"Children\":[]", "\"Children\":{}");

        Assert.Empty(await node.AcquireImmediateTimeTickersAsync([childId], CancellationToken.None));
        Assert.Equal(TickerStatus.Idle, (await ReadTimeTicker(node, childId)).Status);

        await fixture.Database.StringSetAsync(key, broken);
        Assert.Empty(await Drain(node.QueueTimedOutTimeTickers(CancellationToken.None)));

        var row = await ReadTimeTicker(node, childId);
        Assert.Equal(TickerStatus.Idle, row.Status);
        Assert.Null(row.LockHolder);
        Assert.NotNull(await fixture.Database.SortedSetScoreAsync(RedisKeyBuilder.TimeTickerPendingKey, childId.ToString()));
    }

    // The fallback reads the overdue rows a page at a time and still takes every one.
    [Fact]
    public async Task TheFallback_TakesEveryOverdueRow_AcrossPages()
    {
        var node = CreateProvider("node-A");
        var ids = new List<Guid>();
        for (var i = 0; i < 300; i++)
            ids.Add(await SeedTimeTicker(node, executionTime: T0.AddSeconds(-30).AddMilliseconds(i)));

        var fallback = await Drain(node.QueueTimedOutTimeTickers(CancellationToken.None));

        Assert.Equal(ids.OrderBy(x => x), fallback.Select(x => x.Id).OrderBy(x => x));
    }

    // A child without a time of its own normally runs inside its parent's run. Run-now (a claim by id) hands it out only once
    // its parent has ended with an outcome its condition accepts: that is how it follows a parent outcome that was stored
    // late. Never an InProgress child, which starts with its parent, and never by the window or the fallback.
    [Theory]
    [MemberData(nameof(EveryConditionAndParentStatus))]
    public async Task AChildWithoutATime_IsHandedOutByRunNow_OnlyOnceItsParentEndedWithAMatchingOutcome(RunCondition? condition, TickerStatus parentStatus)
    {
        var child = NewTimeTicker();
        child.ExecutionTime = null;
        child.RunCondition = condition;
        var parent = NewTimeTicker(parentStatus, executionTime: T0.AddHours(-1));
        parent.Children = [child];
        var nodeA = CreateProvider("node-A");
        await nodeA.AddTimeTickers([parent], CancellationToken.None);

        Assert.DoesNotContain(await nodeA.GetEarliestTimeTickers(CancellationToken.None), x => x.Id == child.Id);

        var claimed = await nodeA.AcquireImmediateTimeTickersAsync([child.Id], CancellationToken.None);

        var expected = condition != RunCondition.InProgress && ChainRunGate.Allows(condition, parentStatus);
        Assert.Equal(expected, claimed.Any(x => x.Id == child.Id));
        Assert.Equal(expected ? TickerStatus.InProgress : TickerStatus.Idle, (await ReadTimeTicker(nodeA, child.Id)).Status);
        Assert.Empty(await CreateProvider("node-B").AcquireImmediateTimeTickersAsync([child.Id], CancellationToken.None));
    }

    #region The fallback and a child without a time of its own (#84)

    // A parent (not due, so the fallback never takes the parent itself) with a child without a time that was never started.
    private async Task<Guid> SeedLeftWaiting(TickerStatus parentStatus, DateTime? parentEndedAt, RunCondition? condition = RunCondition.OnSuccess,
        Action<TimeTickerEntity>? shapeChild = null)
    {
        var child = NewTimeTicker();
        child.ExecutionTime = null;
        child.RunCondition = condition;
        shapeChild?.Invoke(child);
        var parent = NewTimeTicker(parentStatus, executionTime: T0.AddHours(1));
        parent.ExecutedAt = parentEndedAt;
        parent.Children = [child];
        await CreateProvider("seed").AddTimeTickers([parent], CancellationToken.None);
        return child.Id;
    }

    private Task<List<TimeTickerEntity>> Fallback(string node = "node-A") => Drain(CreateProvider(node).QueueTimedOutTimeTickers(CancellationToken.None));

    private Task<bool> HasWaitingEntry(Guid childId) => fixture.Database.HashExistsAsync(RedisKeyBuilder.TimeTickerWaitingChildrenKey, childId.ToString());

    // The parent's run starts such a child right after the parent ends. One that still waits 30 s later was never
    // started: the fallback claims it as a job of its own, with what it carries.
    [Fact]
    public async Task TheFallback_TakesAChildWithoutATime_ThatStillWaits30SecondsAfterItsParentEnded()
    {
        var grandchild = NewTimeTicker();
        grandchild.ExecutionTime = null;
        grandchild.RunCondition = RunCondition.OnSuccess;
        var childId = await SeedLeftWaiting(TickerStatus.Done, T0.AddSeconds(-30), shapeChild: c => c.Children = [grandchild]);
        var reader = CreateProvider("reader");

        var taken = Assert.Single(await Fallback());

        Assert.Equal(childId, taken.Id);
        Assert.Equal([grandchild.Id], taken.Children.Select(x => x.Id));
        var row = await ReadTimeTicker(reader, childId);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("node-A", row.LockHolder);
        Assert.Equal(T0, row.LockedAt);
        Assert.Null(row.ExecutionTime);
        Assert.Equal(row.UpdatedAt, taken.UpdatedAt);
        Assert.Equal(TickerStatus.Idle, (await ReadTimeTicker(reader, grandchild.Id)).Status);
        Assert.False(await HasWaitingEntry(childId));
        Assert.True(await HasWaitingEntry(grandchild.Id));
        Assert.Empty(await Fallback("node-B"));
    }

    // A final parent without an end time counts as ended long ago: its child must not wait forever.
    [Fact]
    public async Task TheFallback_TakesAChildWithoutATime_WhoseEndedParentHasNoEndTime()
    {
        var childId = await SeedLeftWaiting(TickerStatus.Done, null);

        Assert.Equal([childId], (await Fallback()).Select(x => x.Id));
    }

    // Within the grace time the parent's run may still be about to start the child; a parent that has not ended starts
    // it itself.
    [Theory]
    [InlineData(TickerStatus.Done, 29)]
    [InlineData(TickerStatus.Failed, 29)]
    [InlineData(TickerStatus.Idle, 3600)]
    [InlineData(TickerStatus.Queued, 3600)]
    [InlineData(TickerStatus.InProgress, 3600)]
    public async Task TheFallback_LeavesAChildWithoutATime_WhoseParentHasNotEndedLongEnoughAgo(TickerStatus parentStatus, int endedSecondsAgo)
    {
        var childId = await SeedLeftWaiting(parentStatus, T0.AddSeconds(-endedSecondsAgo));

        Assert.Empty(await Fallback());

        Assert.Equal(TickerStatus.Idle, (await ReadTimeTicker(CreateProvider("reader"), childId)).Status);
        Assert.True(await HasWaitingEntry(childId));
    }

    // A row another start holds, or one that has run, is not a child left waiting.
    [Theory]
    [InlineData(TickerStatus.Idle, "node-B")]
    [InlineData(TickerStatus.InProgress, "node-B")]
    [InlineData(TickerStatus.Queued, null)]
    [InlineData(TickerStatus.Done, null)]
    public async Task TheFallback_LeavesAChildWithoutATime_ThatDoesNotWaitForItsStart(TickerStatus childStatus, string? lockHolder)
    {
        var childId = await SeedLeftWaiting(TickerStatus.Done, T0.AddMinutes(-5), shapeChild: c =>
        {
            c.Status = childStatus;
            c.LockHolder = lockHolder;
        });
        // Also with an index entry that is behind its row: the row decides.
        await fixture.Database.HashSetAsync(RedisKeyBuilder.TimeTickerWaitingChildrenKey, childId.ToString(), Guid.NewGuid().ToString());

        Assert.Empty(await Fallback());

        Assert.Equal(childStatus, (await ReadTimeTicker(CreateProvider("reader"), childId)).Status);
    }

    // The outcome does not let the child run, or the child was to start with its parent (InProgress) and the parent is
    // over: it never runs. What it carries follows on a later pass, as for a delayed child.
    [Theory]
    [InlineData(TickerStatus.Failed, RunCondition.OnSuccess)]
    [InlineData(TickerStatus.Skipped, RunCondition.OnAnyCompletedStatus)]
    [InlineData(TickerStatus.Skipped, null)]
    [InlineData(TickerStatus.Done, RunCondition.InProgress)]
    public async Task TheFallback_SkipsAChildWithoutATime_ThatItsEndedParentDoesNotLetRun(TickerStatus parentStatus, RunCondition? condition)
    {
        var childId = await SeedLeftWaiting(parentStatus, T0.AddSeconds(-30), condition);

        Assert.Empty(await Fallback());

        var row = await ReadTimeTicker(CreateProvider("reader"), childId);
        Assert.Equal(TickerStatus.Skipped, row.Status);
        Assert.Equal(ChainRunGate.NotMatchedReason, row.SkippedReason);
        Assert.Equal(T0, row.ExecutedAt);
        Assert.False(await HasWaitingEntry(childId));
    }

    [Fact]
    public async Task TheFallback_DoesNotSkipAChildWithoutATime_WithinTheGraceTime()
    {
        var childId = await SeedLeftWaiting(TickerStatus.Failed, T0.AddSeconds(-29));

        Assert.Empty(await Fallback());

        Assert.Equal(TickerStatus.Idle, (await ReadTimeTicker(CreateProvider("reader"), childId)).Status);
    }

    // One start per child: the inline start of the parent's run and the fallback are claims on the same row. The entry of
    // a child an inline start took stays until a fallback pass sees its row.
    [Fact]
    public async Task AChildTheFallbackTook_IsRefusedToAnInlineStart_AndOneAnInlineStartTook_IsNotTakenByTheFallback()
    {
        var takenByFallback = await SeedLeftWaiting(TickerStatus.Done, T0.AddMinutes(-1));
        var startedInline = await SeedLeftWaiting(TickerStatus.Done, T0.AddMinutes(-1));
        var b = (IFencedRunWriter)CreateProvider("node-B");
        Assert.Equal(1, await b.StartInlineChildAsync(startedInline, T0.AddMilliseconds(500), CancellationToken.None));

        Assert.Equal([takenByFallback], (await Fallback()).Select(x => x.Id));

        Assert.Equal(0, await b.StartInlineChildAsync(takenByFallback, T0.AddMilliseconds(501), CancellationToken.None));
        Assert.False(await HasWaitingEntry(startedInline));
    }

    // A child that is started after the fallback read it is not claimed by the fallback's write.
    [Fact]
    public async Task TheFallbacksClaim_IsRefused_WhenTheChildWasStartedAfterItWasRead()
    {
        var first = await SeedLeftWaiting(TickerStatus.Done, T0.AddMinutes(-1));
        var second = await SeedLeftWaiting(TickerStatus.Done, T0.AddMinutes(-1));
        // Both parents are on one page, so both children are read before the first is handed out.
        var reader = CreateProvider("reader");
        var b = (IFencedRunWriter)CreateProvider("node-B");
        var taken = new List<Guid>();

        await foreach (var row in CreateProvider("node-A").QueueTimedOutTimeTickers(CancellationToken.None))
        {
            taken.Add(row.Id);
            var other = row.Id == first ? second : first;
            Assert.Equal(1, await b.StartInlineChildAsync(other, T0.AddMilliseconds(500), CancellationToken.None));
        }

        Assert.Single(taken);
        var startedByB = taken[0] == first ? second : first;
        Assert.Equal("node-B", (await ReadTimeTicker(reader, startedByB)).LockHolder);
    }

    // The fallback finds such children through the waiting-children index. A row stored by a version before 1.4.0 has no
    // entry (and an index write can fail): the first fallback pass of a process gives every waiting child its entry.
    [Fact]
    public async Task AChildStoredWithoutAnIndexEntry_IsIndexedByTheFirstFallbackPassOfAProcess_AndOnlyByThat()
    {
        var before = await SeedLeftWaiting(TickerStatus.Done, T0.AddMinutes(-1));
        Assert.True(await fixture.Database.HashDeleteAsync(RedisKeyBuilder.TimeTickerWaitingChildrenKey, before.ToString()));
        var process = CreateProvider("node-A");

        Assert.Equal([before], (await Drain(process.QueueTimedOutTimeTickers(CancellationToken.None))).Select(x => x.Id));

        var after = await SeedLeftWaiting(TickerStatus.Done, T0.AddMinutes(-1));
        Assert.True(await fixture.Database.HashDeleteAsync(RedisKeyBuilder.TimeTickerWaitingChildrenKey, after.ToString()));
        Assert.Empty(await Drain(process.QueueTimedOutTimeTickers(CancellationToken.None)));
        Assert.Equal([after], (await Fallback("node-B")).Select(x => x.Id));
    }

    // A chain added while the process runs is indexed by the write that stores it, not by the first pass.
    [Fact]
    public async Task AChildStoredAfterAProcessesFirstFallbackPass_IsFoundThroughTheIndexItsWriteMade()
    {
        var process = CreateProvider("node-A");
        Assert.Empty(await Drain(process.QueueTimedOutTimeTickers(CancellationToken.None)));

        var childId = await SeedLeftWaiting(TickerStatus.Done, T0.AddMinutes(-1));

        Assert.Equal([childId], (await Drain(process.QueueTimedOutTimeTickers(CancellationToken.None))).Select(x => x.Id));
    }

    // The index holds a child only while it waits: not once it has a time, and not after its chain is removed.
    [Fact]
    public async Task TheWaitingEntryOfAChild_IsDropped_WhenItIsGivenATime_AndWhenItsChainIsRemoved()
    {
        var givenATime = await SeedLeftWaiting(TickerStatus.Idle, null);
        var removed = await SeedLeftWaiting(TickerStatus.Idle, null);
        var node = CreateProvider("node-A");
        Assert.True(await HasWaitingEntry(givenATime));
        Assert.True(await HasWaitingEntry(removed));

        var edit = await ReadTimeTicker(node, givenATime);
        edit.ExecutionTime = T0.AddHours(2);
        await node.UpdateTimeTickers([edit], CancellationToken.None);
        await node.RemoveTimeTickers([(await ReadTimeTicker(node, removed)).ParentId!.Value], CancellationToken.None);

        Assert.False(await HasWaitingEntry(givenATime));
        Assert.False(await HasWaitingEntry(removed));
    }

    #endregion

    // The handler skips a child its parent's outcome does not let run; only a row that has not started is written, so a
    // delayed child the store already handed out keeps the status of its run.
    [Fact]
    public async Task SkippingChildren_WritesOnlyTheRowsThatHaveNotStarted()
    {
        var nodeA = CreateProvider("node-A");
        var idle = NewTimeTicker(executionTime: T0.AddSeconds(30));
        var queued = NewTimeTicker(TickerStatus.Queued, "node-A", T0.AddSeconds(30));
        var running = NewTimeTicker(TickerStatus.InProgress, "node-B", T0.AddSeconds(30));
        var done = NewTimeTicker(TickerStatus.Done, executionTime: T0.AddSeconds(30));
        var parent = NewTimeTicker(TickerStatus.Failed, executionTime: T0.AddHours(-1));
        parent.Children = [idle, queued, running, done];
        await nodeA.AddTimeTickers([parent], CancellationToken.None);

        var skip = new InternalFunctionContext()
            .SetProperty(x => x.Status, TickerStatus.Skipped)
            .SetProperty(x => x.ExecutedAt, T0)
            .SetProperty(x => x.ExceptionDetails, ChainRunGate.NotMatchedReason);
        await ((IChildSkipWriter)nodeA).SkipNotStartedTimeTickersAsync([idle.Id, queued.Id, running.Id, done.Id, Guid.NewGuid()], skip);

        foreach (var id in new[] { idle.Id, queued.Id })
        {
            var row = await ReadTimeTicker(nodeA, id);
            Assert.Equal(TickerStatus.Skipped, row.Status);
            Assert.Equal(ChainRunGate.NotMatchedReason, row.SkippedReason);
            Assert.Equal(T0, row.ExecutedAt);
            Assert.Null(row.LockHolder);
            Assert.Null(await fixture.Database.SortedSetScoreAsync(RedisKeyBuilder.TimeTickerPendingKey, id.ToString()));
        }
        Assert.Equal(TickerStatus.InProgress, (await ReadTimeTicker(nodeA, running.Id)).Status);
        Assert.Equal("node-B", (await ReadTimeTicker(nodeA, running.Id)).LockHolder);
        Assert.Equal(TickerStatus.Done, (await ReadTimeTicker(nodeA, done.Id)).Status);
    }

    #endregion

    [Fact]
    public async Task ClaimFanOut_TakesConcurrentClaims()
    {
        var fanOut = (IClaimFanOut)CreateProvider("node-A");

        Assert.Equal(16, await fanOut.GetMaxConcurrentClaimsAsync());
    }

    #region Putting back a claim whose job never started

    [Fact]
    public async Task ReleaseClaim_OwnClaim_PutsTheRowBackForTheFallback_TimeTicker()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueTimeTickerOn(nodeA);
        var marker = _clock.NextClaimMarker();
        Assert.True(await nodeA.TryMarkTimeTickerInProgress(id, marker));
        _clock.Advance(TimeSpan.FromMilliseconds(40));

        Assert.True(await nodeA.TryReleaseTimeTickerClaim(id, marker));

        var row = await ReadTimeTicker(nodeA, id);
        Assert.Equal(TickerStatus.Idle, row.Status);
        Assert.Null(row.LockHolder);
        Assert.Null(row.LockedAt);
        Assert.Equal(_clock.UtcNow, row.UpdatedAt);
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Contains(await Drain(CreateProvider("node-B").QueueTimedOutTimeTickers(CancellationToken.None)), x => x.Id == id);
    }

    [Fact]
    public async Task ReleaseClaim_OwnClaim_PutsTheRowBackForTheFallback_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueOccurrenceOn(nodeA);
        var marker = _clock.NextClaimMarker();
        Assert.True(await nodeA.TryMarkCronOccurrenceInProgress(id, marker));

        Assert.True(await nodeA.TryReleaseCronOccurrenceClaim(id, marker));

        var row = await ReadOccurrence(nodeA, id);
        Assert.Equal(TickerStatus.Idle, row.Status);
        Assert.Null(row.LockHolder);
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Contains(await Drain(CreateProvider("node-B").QueueTimedOutCronTickerOccurrences(CancellationToken.None)), x => x.Id == id);
    }

    [Fact]
    public async Task ReleaseClaim_RowTakenByTheSameNodesFallback_IsLeftRunning_TimeTicker()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueTimeTickerOn(nodeA);
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Contains(await Drain(nodeA.QueueTimedOutTimeTickers(CancellationToken.None)), x => x.Id == id);
        var loopMarker = _clock.NextClaimMarker();
        Assert.False(await nodeA.TryMarkTimeTickerInProgress(id, loopMarker));

        Assert.False(await nodeA.TryReleaseTimeTickerClaim(id, loopMarker));

        var row = await ReadTimeTicker(nodeA, id);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("node-A", row.LockHolder);
    }

    [Fact]
    public async Task ReleaseClaim_RowTakenByTheSameNodesFallback_IsLeftRunning_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueOccurrenceOn(nodeA);
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Contains(await Drain(nodeA.QueueTimedOutCronTickerOccurrences(CancellationToken.None)), x => x.Id == id);
        var loopMarker = _clock.NextClaimMarker();
        Assert.False(await nodeA.TryMarkCronOccurrenceInProgress(id, loopMarker));

        Assert.False(await nodeA.TryReleaseCronOccurrenceClaim(id, loopMarker));

        Assert.Equal(TickerStatus.InProgress, (await ReadOccurrence(nodeA, id)).Status);
    }

    // The fallback and run-now hand back each row they claimed with the marker their write gave it, so a job that never
    // starts can be put back with that marker.
    [Fact]
    public async Task FallbackClaim_ReturnsItsMarker_SoItCanBePutBack_TimeTicker()
    {
        var nodeB = CreateProvider("node-B");
        var id = await SeedTimeTicker(nodeB);
        _clock.Advance(TimeSpan.FromSeconds(2));

        var taken = Assert.Single(await Drain(nodeB.QueueTimedOutTimeTickers(CancellationToken.None)), x => x.Id == id);

        Assert.Equal((await ReadTimeTicker(nodeB, id)).UpdatedAt, taken.UpdatedAt);
        Assert.True(await nodeB.TryReleaseTimeTickerClaim(id, taken.UpdatedAt));
        Assert.Equal(TickerStatus.Idle, (await ReadTimeTicker(nodeB, id)).Status);
    }

    [Fact]
    public async Task FallbackClaim_ReturnsItsMarker_SoItCanBePutBack_CronOccurrence()
    {
        // Here the fallback does not take another holder's Queued row, so it takes a row of its own node.
        var nodeB = CreateProvider("node-B");
        var id = await QueueOccurrenceOn(nodeB);
        _clock.Advance(TimeSpan.FromSeconds(2));

        var taken = Assert.Single(await Drain(nodeB.QueueTimedOutCronTickerOccurrences(CancellationToken.None)), x => x.Id == id);

        Assert.Equal((await ReadOccurrence(nodeB, id)).UpdatedAt, taken.UpdatedAt);
        Assert.True(await nodeB.TryReleaseCronOccurrenceClaim(id, taken.UpdatedAt));
        Assert.Equal(TickerStatus.Idle, (await ReadOccurrence(nodeB, id)).Status);
    }

    [Fact]
    public async Task RunNowClaim_ReturnsItsMarker_SoItCanBePutBack_TimeTicker()
    {
        var nodeA = CreateProvider("node-A");
        var id = await SeedTimeTicker(nodeA);

        var taken = Assert.Single(await nodeA.AcquireImmediateTimeTickersAsync([id], CancellationToken.None));

        Assert.Equal((await ReadTimeTicker(nodeA, id)).UpdatedAt, taken.UpdatedAt);
        Assert.True(await nodeA.TryReleaseTimeTickerClaim(id, taken.UpdatedAt));
        Assert.Equal(TickerStatus.Idle, (await ReadTimeTicker(nodeA, id)).Status);
    }

    [Fact]
    public async Task RunNowClaim_ReturnsItsMarker_SoItCanBePutBack_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueOccurrenceOn(nodeA);
        await nodeA.ReleaseAcquiredCronTickerOccurrences([id], CancellationToken.None);

        var taken = Assert.Single(await nodeA.AcquireImmediateCronOccurrencesAsync([id], CancellationToken.None));

        Assert.Equal((await ReadOccurrence(nodeA, id)).UpdatedAt, taken.UpdatedAt);
        Assert.True(await nodeA.TryReleaseCronOccurrenceClaim(id, taken.UpdatedAt));
        Assert.Equal(TickerStatus.Idle, (await ReadOccurrence(nodeA, id)).Status);
    }

    // Run-now returns only the rows its own call claimed. A row this node's fallback holds is already running; handing it
    // out again would run it twice, and its put-back would undo the fallback's claim.
    [Fact]
    public async Task RunNowClaim_DoesNotReturnARowThisNodesFallbackHolds_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var heldByFallback = await QueueOccurrenceOn(nodeA);
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Contains(await Drain(nodeA.QueueTimedOutCronTickerOccurrences(CancellationToken.None)), x => x.Id == heldByFallback);
        var idle = await QueueOccurrenceOn(nodeA);
        await nodeA.ReleaseAcquiredCronTickerOccurrences([idle], CancellationToken.None);

        var taken = await nodeA.AcquireImmediateCronOccurrencesAsync([heldByFallback, idle], CancellationToken.None);

        Assert.Equal([idle], taken.Select(x => x.Id));
    }

    [Fact]
    public async Task ReleaseClaim_WithAnotherMarkerOrNode_LeavesTheRow()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueTimeTickerOn(nodeA);
        var marker = _clock.NextClaimMarker();
        Assert.True(await nodeA.TryMarkTimeTickerInProgress(id, marker));

        Assert.False(await nodeA.TryReleaseTimeTickerClaim(id, marker.AddMilliseconds(1)));
        Assert.False(await CreateProvider("node-B").TryReleaseTimeTickerClaim(id, marker));

        var row = await ReadTimeTicker(nodeA, id);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("node-A", row.LockHolder);
    }

    // Only the status is changed in the stored JSON, so UpdatedAt keeps the marker text: the status alone must stop it.
    [Theory]
    [InlineData(TickerStatus.Done)]
    [InlineData(TickerStatus.Failed)]
    [InlineData(TickerStatus.Cancelled)]
    [InlineData(TickerStatus.Skipped)]
    public async Task ReleaseClaim_RowNoLongerInProgress_LeavesTheRow(TickerStatus status)
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueTimeTickerOn(nodeA);
        var marker = _clock.NextClaimMarker();
        Assert.True(await nodeA.TryMarkTimeTickerInProgress(id, marker));
        var json = JsonNode.Parse((string)(await fixture.Database.StringGetAsync(RedisKeyBuilder.TimeTickerKey(id)))!)!;
        json["Status"] = (int)status;
        await fixture.Database.StringSetAsync(RedisKeyBuilder.TimeTickerKey(id), json.ToJsonString());

        Assert.False(await nodeA.TryReleaseTimeTickerClaim(id, marker));

        Assert.Equal(status, (await ReadTimeTicker(nodeA, id)).Status);
    }

    [Fact]
    public async Task ReleaseClaim_MissingRow_ReturnsFalse()
    {
        var nodeA = CreateProvider("node-A");

        Assert.False(await nodeA.TryReleaseTimeTickerClaim(Guid.NewGuid(), _clock.NextClaimMarker()));
        Assert.False(await nodeA.TryReleaseCronOccurrenceClaim(Guid.NewGuid(), _clock.NextClaimMarker()));
    }

    #endregion

    #region Late loop, clock skew, shared identifier

    // R1: the loop that queued a row wakes late; this node's own fallback has already claimed it.
    [Fact]
    public async Task LateLoop_LosesToFallback_JobClaimedOnce()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueTimeTickerOn(nodeA);

        _clock.Advance(TimeSpan.FromSeconds(2));
        var claimedByFallback = await Drain(nodeA.QueueTimedOutTimeTickers(CancellationToken.None));
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        var lateLoopWon = await nodeA.TryMarkTimeTickerInProgress(id, _clock.NextClaimMarker());

        Assert.Equal(id, Assert.Single(claimedByFallback).Id);
        Assert.False(lateLoopWon);
        var row = await ReadTimeTicker(nodeA, id);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("node-A", row.LockHolder);
        Assert.Equal(T0.AddSeconds(2), row.UpdatedAt);
    }

    [Fact]
    public async Task LateLoop_LosesToFallback_JobClaimedOnce_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueOccurrenceOn(nodeA);

        _clock.Advance(TimeSpan.FromSeconds(2));
        var claimedByFallback = await Drain(nodeA.QueueTimedOutCronTickerOccurrences(CancellationToken.None));
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        var lateLoopWon = await nodeA.TryMarkCronOccurrenceInProgress(id, _clock.NextClaimMarker());

        Assert.Equal(id, Assert.Single(claimedByFallback).Id);
        Assert.False(lateLoopWon);
        var row = await ReadOccurrence(nodeA, id);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("node-A", row.LockHolder);
    }

    // Unlike the other providers, the Redis fallback leaves a row another holder queued alone, however late it is,
    // so the owner's loop still wins. Both guards are in Acquire.lua; this keeps them from going away unseen.
    [Fact]
    public async Task LateLoop_FallbackOfAnotherNodeLeavesRow_OwnerStillWins_TimeTicker()
    {
        var nodeA = CreateProvider("node-A");
        var nodeB = CreateProvider("node-B");
        var id = await QueueTimeTickerOn(nodeA);

        _clock.Advance(TimeSpan.FromSeconds(2));
        var claimedByB = await Drain(nodeB.QueueTimedOutTimeTickers(CancellationToken.None));
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        var ownerWon = await nodeA.TryMarkTimeTickerInProgress(id, _clock.NextClaimMarker());

        Assert.Empty(claimedByB);
        Assert.True(ownerWon);
        var row = await ReadTimeTicker(nodeA, id);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("node-A", row.LockHolder);
    }

    [Fact]
    public async Task LateLoop_FallbackOfAnotherNodeLeavesRow_OwnerStillWins_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var nodeB = CreateProvider("node-B");
        var id = await QueueOccurrenceOn(nodeA);

        _clock.Advance(TimeSpan.FromSeconds(2));
        var claimedByB = await Drain(nodeB.QueueTimedOutCronTickerOccurrences(CancellationToken.None));
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        var ownerWon = await nodeA.TryMarkCronOccurrenceInProgress(id, _clock.NextClaimMarker());

        Assert.Empty(claimedByB);
        Assert.True(ownerWon);
        Assert.Equal("node-A", (await ReadOccurrence(nodeA, id)).LockHolder);
    }

    // R3: a pod whose clock runs ahead sees the row as overdue, but the fallback does not take another holder's row.
    [Fact]
    public async Task ClockSkew_FallbackOfFasterNodeLeavesRow_SlowOwnerStillWins()
    {
        var slowClock = new FakeTickerClock(T0);
        var fastClock = new FakeTickerClock(T0.AddSeconds(2));
        var owner = CreateProvider("node-slow", slowClock);
        var faster = CreateProvider("node-fast", fastClock);
        var id = await QueueTimeTickerOn(owner);

        var claimedByFaster = await Drain(faster.QueueTimedOutTimeTickers(CancellationToken.None));
        slowClock.Advance(TimeSpan.FromMilliseconds(300));
        var ownerWon = await owner.TryMarkTimeTickerInProgress(id, _clock.NextClaimMarker());

        Assert.Empty(claimedByFaster);
        Assert.True(ownerWon);
        Assert.Equal("node-slow", (await ReadTimeTicker(owner, id)).LockHolder);
    }

    // Two pods that start with the same identifier: Status = Queued is the token, so only one of them gets a row.
    [Fact]
    public async Task TwoNodesWithSameIdentifier_OneWins()
    {
        var first = CreateProvider("node-X");
        var twin = CreateProvider("node-X");
        var markedFirst = await QueueTimeTickerOn(first);
        var fallbackFirst = await QueueTimeTickerOn(first);
        _clock.Advance(TimeSpan.FromSeconds(2));

        // Row 1: one TryMark wins, the twin's fallback then finds nothing to claim for it.
        var tryMarkWon = await first.TryMarkTimeTickerInProgress(markedFirst, _clock.NextClaimMarker());
        var twinTryMarkWon = await twin.TryMarkTimeTickerInProgress(markedFirst, _clock.NextClaimMarker());
        // Row 2: the twin's fallback wins, the other's TryMark then loses.
        var claimedByTwin = await Drain(twin.QueueTimedOutTimeTickers(CancellationToken.None));
        var lateTryMarkWon = await first.TryMarkTimeTickerInProgress(fallbackFirst, _clock.NextClaimMarker());

        Assert.True(tryMarkWon);
        Assert.False(twinTryMarkWon);
        Assert.DoesNotContain(claimedByTwin, x => x.Id == markedFirst);
        Assert.Equal(fallbackFirst, Assert.Single(claimedByTwin).Id);
        Assert.False(lateTryMarkWon);
        Assert.Equal(TickerStatus.InProgress, (await ReadTimeTicker(first, markedFirst)).Status);
        Assert.Equal(TickerStatus.InProgress, (await ReadTimeTicker(first, fallbackFirst)).Status);
    }

    #endregion

    #region Fallback threshold (R8)

    // The window read covers the last second and the fallback starts at one second late. With the old 100 ms
    // threshold the fallback took a row the main loop still had a second to queue.
    [Fact]
    public async Task Fallback_LeavesRowAlone_BeforeOneSecondLate()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueTimeTickerOn(nodeA);

        _clock.Advance(TimeSpan.FromMilliseconds(999));
        var claimed = await Drain(nodeA.QueueTimedOutTimeTickers(CancellationToken.None));

        Assert.Empty(claimed);
        Assert.Equal(TickerStatus.Queued, (await ReadTimeTicker(nodeA, id)).Status);
        Assert.Contains(await nodeA.GetEarliestTimeTickers(CancellationToken.None), x => x.Id == id);
    }

    [Fact]
    public async Task Fallback_ClaimsRow_AfterOneSecondLate()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueTimeTickerOn(nodeA);

        _clock.Advance(TimeSpan.FromMilliseconds(1001));
        var claimed = await Drain(nodeA.QueueTimedOutTimeTickers(CancellationToken.None));

        Assert.Equal(id, Assert.Single(claimed).Id);
        Assert.Equal(TickerStatus.InProgress, (await ReadTimeTicker(nodeA, id)).Status);
        Assert.DoesNotContain(await nodeA.GetEarliestTimeTickers(CancellationToken.None), x => x.Id == id);
    }

    // Exactly one second late both ranges include the row: no gap between the window read and the fallback.
    [Fact]
    public async Task Fallback_AtExactlyOneSecondLate_LeavesNoGapToTheWindowRead()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueTimeTickerOn(nodeA);

        _clock.Advance(TimeSpan.FromSeconds(1));
        var inWindow = await nodeA.GetEarliestTimeTickers(CancellationToken.None);
        var claimed = await Drain(nodeA.QueueTimedOutTimeTickers(CancellationToken.None));

        Assert.Contains(inWindow, x => x.Id == id);
        Assert.Equal(id, Assert.Single(claimed).Id);
    }

    [Fact]
    public async Task Fallback_LeavesRowAlone_BeforeOneSecondLate_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueOccurrenceOn(nodeA);

        _clock.Advance(TimeSpan.FromMilliseconds(999));
        var claimed = await Drain(nodeA.QueueTimedOutCronTickerOccurrences(CancellationToken.None));

        Assert.Empty(claimed);
        Assert.Equal(TickerStatus.Queued, (await ReadOccurrence(nodeA, id)).Status);
    }

    [Fact]
    public async Task Fallback_ClaimsRow_AfterOneSecondLate_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueOccurrenceOn(nodeA);

        _clock.Advance(TimeSpan.FromMilliseconds(1001));
        var claimed = await Drain(nodeA.QueueTimedOutCronTickerOccurrences(CancellationToken.None));

        Assert.Equal(id, Assert.Single(claimed).Id);
        Assert.Equal(TickerStatus.InProgress, (await ReadOccurrence(nodeA, id)).Status);
    }

    #endregion

    #region Queue cron occurrence does not overwrite a claim (R5)

    /// <summary>Forwards every call to the real database and runs a callback once after the next call of one method returns.</summary>
    public class HookedDatabase : DispatchProxy
    {
        public IDatabase Inner { get; set; } = null!;

        public string HookOn { get; set; } = nameof(IDatabase.ScriptEvaluateAsync);

        public Func<Task>? AfterNextCall { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            object? result;
            try
            {
                result = targetMethod!.Invoke(Inner, args);
            }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                throw e.InnerException;
            }

            if (targetMethod.Name == HookOn && AfterNextCall is not null && result is Task task && task.GetType().IsGenericType)
            {
                return typeof(HookedDatabase).GetMethod(nameof(Run), BindingFlags.Instance | BindingFlags.NonPublic)!
                    .MakeGenericMethod(task.GetType().GetGenericArguments()[0])
                    .Invoke(this, [task]);
            }
            return result;
        }

        private async Task<T> Run<T>(Task<T> call)
        {
            var result = await call.ConfigureAwait(false);
            var hook = AfterNextCall;
            AfterNextCall = null;
            if (hook is not null)
                await hook().ConfigureAwait(false);
            return result;
        }
    }

    // UpdateCronTickerOccurrence / UpdateTimeTicker with a context that applies nothing (the manager sends only
    // ExecutionTime when a cron is edited) used to read the row and write it back. A claim made between the read and
    // the write was replaced by the older Queued copy. Now nothing is read or written. The claim is made right after
    // the read when there is one, otherwise after the call; either way the stored JSON must equal the claimed row.
    [Fact]
    public async Task UpdateWithoutApplicableProps_DoesNotWriteTheRow_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueOccurrenceOn(nodeA);
        var hooked = DispatchProxy.Create<IDatabase, HookedDatabase>();
        var hook = (HookedDatabase)hooked;
        hook.Inner = fixture.Database;
        hook.HookOn = nameof(IDatabase.StringGetAsync);
        var loop = fixture.CreateProvider("node-A", _clock, hooked);
        string? claimedJson = null;
        hook.AfterNextCall = async () =>
        {
            Assert.True(await nodeA.TryMarkCronOccurrenceInProgress(id, _clock.NextClaimMarker()));
            claimedJson = await RawOccurrence(id);
        };

        await loop.UpdateCronTickerOccurrence(
            new InternalFunctionContext { TickerId = id }.SetProperty(x => x.ExecutionTime, T0.AddMinutes(5)), CancellationToken.None);
        if (claimedJson is null)
        {
            Assert.True(await nodeA.TryMarkCronOccurrenceInProgress(id, _clock.NextClaimMarker()));
            claimedJson = await RawOccurrence(id);
        }

        Assert.Equal(claimedJson, await RawOccurrence(id));
        Assert.Equal(TickerStatus.InProgress, (await ReadOccurrence(nodeA, id)).Status);
        Assert.Null(await fixture.Database.SortedSetScoreAsync(RedisKeyBuilder.CronOccurrencePendingKey, id.ToString()));
    }

    [Fact]
    public async Task UpdateWithoutApplicableProps_DoesNotWriteTheRow_TimeTicker()
    {
        var nodeA = CreateProvider("node-A");
        var id = await QueueTimeTickerOn(nodeA);
        var hooked = DispatchProxy.Create<IDatabase, HookedDatabase>();
        var hook = (HookedDatabase)hooked;
        hook.Inner = fixture.Database;
        hook.HookOn = nameof(IDatabase.StringGetAsync);
        var loop = fixture.CreateProvider("node-A", _clock, hooked);
        string? claimedJson = null;
        hook.AfterNextCall = async () =>
        {
            Assert.True(await nodeA.TryMarkTimeTickerInProgress(id, _clock.NextClaimMarker()));
            claimedJson = await RawTimeTickerJson(id);
        };

        await loop.UpdateTimeTicker(
            new InternalFunctionContext { TickerId = id }.SetProperty(x => x.ExecutionTime, T0.AddMinutes(5)), CancellationToken.None);
        if (claimedJson is null)
        {
            Assert.True(await nodeA.TryMarkTimeTickerInProgress(id, _clock.NextClaimMarker()));
            claimedJson = await RawTimeTickerJson(id);
        }

        Assert.Equal(claimedJson, await RawTimeTickerJson(id));
        Assert.Equal(TickerStatus.InProgress, (await ReadTimeTicker(nodeA, id)).Status);
    }

    // The caller's token fires between two row acquires. The rows won so far are InProgress and must come back to be
    // dispatched; the rest must stay Idle. The hooked database cancels right after the first acquire script returns.
    [Fact]
    public async Task AcquireImmediate_CancelledMidBatch_ReturnsWonRowsAndLeavesTheRestIdle_CronOccurrence()
    {
        var seeder = CreateProvider("seed");
        var ids = new[] { await QueueOccurrenceOn(seeder), await QueueOccurrenceOn(seeder), await QueueOccurrenceOn(seeder) };
        await seeder.ReleaseAcquiredCronTickerOccurrences(ids, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var hooked = DispatchProxy.Create<IDatabase, HookedDatabase>();
        var hook = (HookedDatabase)hooked;
        hook.Inner = fixture.Database;
        hook.AfterNextCall = () =>
        {
            cts.Cancel();
            return Task.CompletedTask;
        };
        var node = fixture.CreateProvider("node-A", _clock, hooked);

        var acquired = await node.AcquireImmediateCronOccurrencesAsync(ids, cts.Token);

        Assert.Equal(ids[0], Assert.Single(acquired).Id);
        Assert.Equal(TickerStatus.InProgress, (await ReadOccurrence(seeder, ids[0])).Status);
        foreach (var id in ids.Skip(1))
        {
            var row = await ReadOccurrence(seeder, id);
            Assert.Equal(TickerStatus.Idle, row.Status);
            Assert.Null(row.LockHolder);
        }
    }

    [Fact]
    public async Task AcquireImmediate_CancelledMidBatch_ReturnsWonRowsAndLeavesTheRestIdle()
    {
        var seeder = CreateProvider("seed");
        var ids = new[] { await SeedTimeTicker(seeder), await SeedTimeTicker(seeder), await SeedTimeTicker(seeder) };
        using var cts = new CancellationTokenSource();
        var hooked = DispatchProxy.Create<IDatabase, HookedDatabase>();
        var hook = (HookedDatabase)hooked;
        hook.Inner = fixture.Database;
        hook.AfterNextCall = () =>
        {
            cts.Cancel();
            return Task.CompletedTask;
        };
        var node = fixture.CreateProvider("node-A", _clock, hooked);

        var acquired = await node.AcquireImmediateTimeTickersAsync(ids, cts.Token);

        Assert.Equal(ids[0], Assert.Single(acquired).Id);
        Assert.Equal(TickerStatus.InProgress, (await ReadTimeTicker(seeder, ids[0])).Status);
        foreach (var id in ids.Skip(1))
        {
            var row = await ReadTimeTicker(seeder, id);
            Assert.Equal(TickerStatus.Idle, row.Status);
            Assert.Null(row.LockHolder);
        }
    }

    // The store fails once after the call has won a row: the acquire script of the next row throws. Nobody gets the
    // won row to run, so it must be put back (Idle, no holder) for the fallback, and the call must throw.
    [Fact]
    public async Task AcquireImmediate_StoreFailsAfterAWin_PutsTheWonRowsBackAndThrows()
    {
        var seeder = CreateProvider("seed");
        var ids = new[] { await SeedTimeTicker(seeder), await SeedTimeTicker(seeder), await SeedTimeTicker(seeder) };
        var node = fixture.CreateProvider("node-A", _clock, FailingDatabase.Create(fixture.Database, nameof(IDatabase.ScriptEvaluateAsync), 2));

        await Assert.ThrowsAsync<RedisConnectionException>(() => node.AcquireImmediateTimeTickersAsync(ids, CancellationToken.None));

        foreach (var id in ids)
        {
            var row = await ReadTimeTicker(seeder, id);
            Assert.Equal(TickerStatus.Idle, row.Status);
            Assert.Null(row.LockHolder);
        }
    }

    [Fact]
    public async Task AcquireImmediate_StoreFailsAfterAWin_PutsTheWonRowsBackAndThrows_CronOccurrence()
    {
        var seeder = CreateProvider("seed");
        var ids = new[] { await QueueOccurrenceOn(seeder), await QueueOccurrenceOn(seeder), await QueueOccurrenceOn(seeder) };
        await seeder.ReleaseAcquiredCronTickerOccurrences(ids, CancellationToken.None);
        var node = fixture.CreateProvider("node-A", _clock, FailingDatabase.Create(fixture.Database, nameof(IDatabase.ScriptEvaluateAsync), 2));

        await Assert.ThrowsAsync<RedisConnectionException>(() => node.AcquireImmediateCronOccurrencesAsync(ids, CancellationToken.None));

        foreach (var id in ids)
        {
            var row = await ReadOccurrence(seeder, id);
            Assert.Equal(TickerStatus.Idle, row.Status);
            Assert.Null(row.LockHolder);
        }
    }

    // An occurrence stored without its cron: after the claim is won, the cron is read separately, and that read fails. The
    // won row must be put back, not left InProgress.
    [Fact]
    public async Task AcquireImmediate_CronReadFailsAfterTheWin_PutsTheRowBackAndThrows()
    {
        var seeder = CreateProvider("seed");
        var cron = new CronTickerEntity { Id = Guid.NewGuid(), Function = "claim-cron-fn", Expression = "* * * * *", CreatedAt = T0, UpdatedAt = T0, Request = [] };
        await seeder.InsertCronTickers([cron], CancellationToken.None);
        var occurrence = new CronTickerOccurrenceEntity<CronTickerEntity>
        {
            Id = Guid.NewGuid(), CronTickerId = cron.Id, Status = TickerStatus.Idle, ExecutionTime = T0, CreatedAt = T0, UpdatedAt = T0
        };
        await seeder.InsertCronTickerOccurrences([occurrence], CancellationToken.None);
        var node = fixture.CreateProvider("node-A", _clock, FailingDatabase.Create(fixture.Database, nameof(IDatabase.StringGetAsync), 1));

        await Assert.ThrowsAsync<RedisConnectionException>(() => node.AcquireImmediateCronOccurrencesAsync([occurrence.Id], CancellationToken.None));

        var row = await ReadOccurrence(seeder, occurrence.Id);
        Assert.Equal(TickerStatus.Idle, row.Status);
        Assert.Null(row.LockHolder);
    }

    // A claim's script runs and lands, then its reply is lost (the call throws). The row is InProgress for this node with
    // nothing to run it, so it must be put back like a won row. Covers run-now and the fallback, on time tickers and cron
    // occurrences.
    [Theory]
    [InlineData("immediate-time")]
    [InlineData("immediate-cron")]
    [InlineData("fallback-time")]
    [InlineData("fallback-cron")]
    public async Task AClaimWhoseReplyIsLost_IsPutBack(string path)
    {
        var cron = path.EndsWith("cron");
        var seeder = CreateProvider("seed");
        Guid id;
        if (cron)
        {
            id = await QueueOccurrenceOn(seeder);
            await seeder.ReleaseAcquiredCronTickerOccurrences([id], CancellationToken.None);
        }
        else
        {
            id = await SeedTimeTicker(seeder);
        }
        _clock.Advance(TimeSpan.FromSeconds(2));
        var node = fixture.CreateProvider("node-A", _clock, FailingDatabase.Create(fixture.Database, nameof(IDatabase.ScriptEvaluateAsync), 1, landFirst: true));

        Func<Task> claim = path switch
        {
            "immediate-time" => () => node.AcquireImmediateTimeTickersAsync([id], CancellationToken.None),
            "immediate-cron" => () => node.AcquireImmediateCronOccurrencesAsync([id], CancellationToken.None),
            "fallback-time" => () => Drain(node.QueueTimedOutTimeTickers(CancellationToken.None)),
            _ => () => Drain(node.QueueTimedOutCronTickerOccurrences(CancellationToken.None))
        };
        await Assert.ThrowsAsync<RedisConnectionException>(claim);

        if (cron)
        {
            var row = await ReadOccurrence(seeder, id);
            Assert.Equal(TickerStatus.Idle, row.Status);
            Assert.Null(row.LockHolder);
        }
        else
        {
            var row = await ReadTimeTicker(seeder, id);
            Assert.Equal(TickerStatus.Idle, row.Status);
            Assert.Null(row.LockHolder);
        }
    }

    // As above, and the store's own put-back (the next script) fails too. The row stays InProgress for this node; the
    // exception names it with the marker of the claim and its type, and a put-back with that marker frees it once the store
    // answers, which is what the scheduler loop does with it (candidate row 50).
    [Theory]
    [InlineData("immediate-time")]
    [InlineData("immediate-cron")]
    [InlineData("fallback-time")]
    [InlineData("fallback-cron")]
    public async Task AClaimWhoseReplyIsLost_AndItsPutBackFails_NamesTheRowSoItCanBePutBackLater(string path)
    {
        var cron = path.EndsWith("cron");
        var seeder = CreateProvider("seed");
        Guid id;
        if (cron)
        {
            id = await QueueOccurrenceOn(seeder);
            await seeder.ReleaseAcquiredCronTickerOccurrences([id], CancellationToken.None);
        }
        else
        {
            id = await SeedTimeTicker(seeder);
        }
        _clock.Advance(TimeSpan.FromSeconds(2));
        var node = fixture.CreateProvider("node-A", _clock,
            FailingDatabase.Create(fixture.Database, nameof(IDatabase.ScriptEvaluateAsync), 1, landFirst: true, failNextToo: true));

        Func<Task> claim = path switch
        {
            "immediate-time" => () => node.AcquireImmediateTimeTickersAsync([id], CancellationToken.None),
            "immediate-cron" => () => node.AcquireImmediateCronOccurrencesAsync([id], CancellationToken.None),
            "fallback-time" => () => Drain(node.QueueTimedOutTimeTickers(CancellationToken.None)),
            _ => () => Drain(node.QueueTimedOutCronTickerOccurrences(CancellationToken.None))
        };
        var thrown = await Assert.ThrowsAnyAsync<Exception>(claim);

        var failed = Assert.Single(ClaimPutBackFailedException.FindIn(thrown));
        var row = Assert.Single(failed.NotPutBack);
        Assert.Equal(id, row.Id);
        Assert.Equal(cron ? TickerType.CronTickerOccurrence : TickerType.TimeTicker, row.Type);

        var healthy = CreateProvider("node-A");
        if (cron)
        {
            Assert.Equal(TickerStatus.InProgress, (await ReadOccurrence(seeder, id)).Status);
            Assert.True(await healthy.TryReleaseCronOccurrenceClaim(id, row.ClaimMarker, CancellationToken.None));
            Assert.Equal(TickerStatus.Idle, (await ReadOccurrence(seeder, id)).Status);
        }
        else
        {
            Assert.Equal(TickerStatus.InProgress, (await ReadTimeTicker(seeder, id)).Status);
            Assert.True(await healthy.TryReleaseTimeTickerClaim(id, row.ClaimMarker, CancellationToken.None));
            Assert.Equal(TickerStatus.Idle, (await ReadTimeTicker(seeder, id)).Status);
        }
    }

    // Throws on the n-th call of one method, once; every other call goes through. By default the call fails before it
    // reaches Redis; with landFirst (ScriptEvaluateAsync only) it runs and lands, then its reply is lost. With failNextToo
    // the call after it fails as well, before it reaches Redis.
    public class FailingDatabase : DispatchProxy
    {
        private IDatabase _inner = null!;
        private string _method = "";
        private int _failAt;
        private int _seen;
        private bool _landFirst;
        private bool _failNextToo;

        public static IDatabase Create(IDatabase inner, string method, int failAt, bool landFirst = false, bool failNextToo = false)
        {
            var proxy = DispatchProxy.Create<IDatabase, FailingDatabase>();
            var failing = (FailingDatabase)(object)proxy;
            failing._inner = inner;
            failing._method = method;
            failing._failAt = failAt;
            failing._landFirst = landFirst;
            failing._failNextToo = failNextToo;
            return proxy;
        }

        private static async Task<RedisResult> LandThenFail(Task<RedisResult> call)
        {
            await call.ConfigureAwait(false);
            throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "the reply was lost");
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == _method && ++_seen == _failAt)
            {
                if (_landFirst)
                    return LandThenFail((Task<RedisResult>)targetMethod.Invoke(_inner, args)!);
                throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "store failed");
            }
            if (_failNextToo && targetMethod.Name == _method && _seen == _failAt + 1)
                throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "store failed again");

            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                throw e.InnerException;
            }
        }
    }

    private async Task<string> RawOccurrence(Guid id)
        => (string)(await fixture.Database.StringGetAsync(RedisKeyBuilder.CronOccurrenceKey(id)))!;

    private async Task<string> RawTimeTickerJson(Guid id)
        => (string)(await fixture.Database.StringGetAsync(RedisKeyBuilder.TimeTickerKey(id)))!;

    // The main loop re-queues a stored occurrence. Between its acquire script and the end of the call this node's
    // fallback claims the same row; the loop must not write its older copy back over that claim.
    [Fact]
    public async Task QueueCronOccurrence_DoesNotOverwriteFallbackClaim()
    {
        var nodeA = CreateProvider("node-A");
        var cronId = Guid.NewGuid();
        var occurrenceId = Assert.Single(await Drain(nodeA.QueueCronTickerOccurrences(Slot(cronId), CancellationToken.None))).Id;
        _clock.Advance(TimeSpan.FromSeconds(2));
        var hooked = DispatchProxy.Create<IDatabase, HookedDatabase>();
        ((HookedDatabase)hooked).Inner = fixture.Database;
        var loop = fixture.CreateProvider("node-A", _clock, hooked);
        var claimed = new List<CronTickerOccurrenceEntity<CronTickerEntity>>();
        ((HookedDatabase)hooked).AfterNextCall = async () =>
            claimed.AddRange(await Drain(nodeA.QueueTimedOutCronTickerOccurrences(CancellationToken.None)));
        var requeue = Slot(cronId);
        requeue.Items[0].NextCronOccurrence = new NextCronOccurrence(occurrenceId, T0);

        var queued = await Drain(loop.QueueCronTickerOccurrences(requeue, CancellationToken.None));

        Assert.Equal(occurrenceId, Assert.Single(claimed).Id);
        Assert.Single(queued);
        var row = await ReadOccurrence(nodeA, occurrenceId);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal(T0.AddSeconds(2), row.UpdatedAt);
    }

    #endregion

    #region Racing

    // Eight nodes run both the fenced claim and the fallback against every row at once. Each row is queued by one
    // node (the others cannot take it, in the fallback or in TryMark), whose fallback and loop race for it. Odd nodes
    // have a clock ten seconds behind. Whatever the interleaving, each row has exactly one winner.
    [Fact]
    public async Task Claim_ManyNodesRacing_EachRowHasExactlyOneWinner()
    {
        const int nodeCount = 8;
        const int rowCount = 200;
        var providers = Enumerable.Range(0, nodeCount)
            .Select(i => CreateProvider($"node-{i}", new FakeTickerClock(i % 2 == 0 ? T0 : T0.AddSeconds(-10))))
            .ToArray();
        var rows = Enumerable.Range(0, rowCount)
            .Select(i => NewTimeTicker(TickerStatus.Queued, lockHolder: $"node-{i % nodeCount}", executionTime: T0.AddSeconds(-20)))
            .ToArray();
        // Each row goes in through its holder, so the pending index lists it for that node's fallback too.
        for (var i = 0; i < rowCount; i++)
            await providers[i % nodeCount].AddTimeTickers([rows[i]], CancellationToken.None);
        var ids = rows.Select(x => x.Id).ToArray();
        var winners = new ConcurrentBag<Guid>();
        var fallbackWins = 0;
        using var start = new ManualResetEventSlim(false);

        async Task Fallback(ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> node)
        {
            start.Wait();
            await foreach (var row in node.QueueTimedOutTimeTickers(CancellationToken.None))
            {
                winners.Add(row.Id);
                Interlocked.Increment(ref fallbackWins);
            }
        }

        async Task TryMark(ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> node)
        {
            start.Wait();
            foreach (var id in ids)
                if (await node.TryMarkTimeTickerInProgress(id, _clock.NextClaimMarker()))
                    winners.Add(id);
        }

        var tasks = providers.SelectMany(node => new[] { Task.Run(() => Fallback(node)), Task.Run(() => TryMark(node)) }).ToArray();
        start.Set();
        await Task.WhenAll(tasks);

        output.WriteLine($"fallback wins: {fallbackWins} of {rowCount}");
        var perRow = winners.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(rowCount, perRow.Count);
        Assert.All(perRow, pair => Assert.Equal(1, pair.Value));
        var all = await providers[0].GetTimeTickers(null!, CancellationToken.None);
        Assert.Equal(rowCount, all.Count(x => x.Status == TickerStatus.InProgress));
    }

    #endregion

    #region CronSlot

    [Fact]
    public void SlotId_IsStableAcrossNodes()
    {
        var cronId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var slot = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        var id = RedisKeyBuilder.SlotId(cronId, slot);

        // Pinned: a change of the hash input layout would split the nodes of a mixed-version cluster.
        Assert.Equal(Guid.Parse("9c2c48e9-8454-52c5-1b0d-16d82bef0f58"), id);
        Assert.Equal(id, RedisKeyBuilder.SlotId(cronId, slot));
        Assert.Equal(id, RedisKeyBuilder.SlotId(cronId, DateTime.SpecifyKind(slot, DateTimeKind.Unspecified)));
        Assert.Equal(id, RedisKeyBuilder.SlotId(cronId, DateTime.SpecifyKind(slot, DateTimeKind.Local)));
        Assert.NotEqual(id, RedisKeyBuilder.SlotId(cronId, slot.AddTicks(1)));
        Assert.NotEqual(id, RedisKeyBuilder.SlotId(Guid.NewGuid(), slot));
    }

    [Fact]
    public async Task CronSlot_TwoNodes_OneOccurrence()
    {
        var nodeA = CreateProvider("node-A");
        var nodeB = CreateProvider("node-B");
        var cronId = Guid.NewGuid();

        var byA = Assert.Single(await Drain(nodeA.QueueCronTickerOccurrences(Slot(cronId), CancellationToken.None)));
        var byB = await Drain(nodeB.QueueCronTickerOccurrences(Slot(cronId), CancellationToken.None));

        Assert.Empty(byB);
        Assert.Equal(RedisKeyBuilder.SlotId(cronId, T0), byA.Id);
        var stored = await nodeA.GetAllCronTickerOccurrences(x => x.CronTickerId == cronId, CancellationToken.None);
        var row = Assert.Single(stored);
        Assert.Equal("node-A", row.LockHolder);
        Assert.Equal(TickerStatus.Queued, row.Status);
        Assert.Equal(1, await fixture.Database.SortedSetLengthAsync(RedisKeyBuilder.CronOccurrencePendingKey));
    }

    [Fact]
    public async Task CronSlot_ManyNodesRacing_OneWinner()
    {
        const int nodeCount = 8;
        var providers = Enumerable.Range(0, nodeCount).Select(i => CreateProvider($"node-{i}")).ToArray();
        var cronId = Guid.NewGuid();
        var yielded = new ConcurrentBag<Guid>();
        var winner = string.Empty;
        using var start = new ManualResetEventSlim(false);

        async Task Queue(int index)
        {
            start.Wait();
            foreach (var row in await Drain(providers[index].QueueCronTickerOccurrences(Slot(cronId), CancellationToken.None)))
            {
                yielded.Add(row.Id);
                winner = $"node-{index}";
            }
        }

        var tasks = Enumerable.Range(0, nodeCount).Select(i => Task.Run(() => Queue(i))).ToArray();
        start.Set();
        await Task.WhenAll(tasks);

        var winnerId = Assert.Single(yielded);
        var stored = Assert.Single(await providers[0].GetAllCronTickerOccurrences(x => x.CronTickerId == cronId, CancellationToken.None));
        Assert.Equal(winnerId, stored.Id);
        Assert.Equal(winner, stored.LockHolder);
    }

    [Fact]
    public async Task CronSlot_DifferentSlotsOfOneCron_BothCreated()
    {
        var node = CreateProvider("node-A");
        var cronId = Guid.NewGuid();
        var later = (T0.AddMinutes(1), Slot(cronId).Items);

        Assert.Single(await Drain(node.QueueCronTickerOccurrences(Slot(cronId), CancellationToken.None)));
        Assert.Single(await Drain(node.QueueCronTickerOccurrences(later, CancellationToken.None)));

        Assert.Equal(2, (await node.GetAllCronTickerOccurrences(x => x.CronTickerId == cronId, CancellationToken.None)).Length);
    }

    [Fact]
    public async Task CronSlot_RowDeleted_SameSlotCanBeCreatedAgain()
    {
        var nodeA = CreateProvider("node-A");
        var nodeB = CreateProvider("node-B");
        var cronId = Guid.NewGuid();
        var first = Assert.Single(await Drain(nodeA.QueueCronTickerOccurrences(Slot(cronId), CancellationToken.None)));

        await nodeA.RemoveCronTickerOccurrences([first.Id], CancellationToken.None);
        var again = Assert.Single(await Drain(nodeB.QueueCronTickerOccurrences(Slot(cronId), CancellationToken.None)));

        Assert.Equal(first.Id, again.Id);
        Assert.Equal("node-B", (await ReadOccurrence(nodeB, again.Id)).LockHolder);
    }

    // Simulates a failure after SET NX: the row key exists, the index batch never ran.
    private async Task<Guid> QueueOrphanSlotRow(ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> holder, Guid cronId)
    {
        var id = Assert.Single(await Drain(holder.QueueCronTickerOccurrences(Slot(cronId), CancellationToken.None))).Id;
        var db = fixture.Database;
        await db.SetRemoveAsync(RedisKeyBuilder.CronOccurrenceIdsKey, id.ToString());
        await db.SetRemoveAsync(RedisKeyBuilder.CronOccurrencesByCronKey(cronId), id.ToString());
        await db.SortedSetRemoveAsync(RedisKeyBuilder.CronOccurrencePendingKey, id.ToString());
        return id;
    }

    [Fact]
    public async Task OrphanSlotRow_OtherNodeQueues_IsIndexedAndRecoverable()
    {
        var nodeA = CreateProvider("node-A");
        var nodeB = CreateProvider("node-B");
        var nodeC = CreateProvider("node-C");
        var cronId = Guid.NewGuid();
        var id = await QueueOrphanSlotRow(nodeA, cronId);

        Assert.Empty(await Drain(nodeB.QueueCronTickerOccurrences(Slot(cronId), CancellationToken.None)));

        var db = fixture.Database;
        Assert.True(await db.SetContainsAsync(RedisKeyBuilder.CronOccurrenceIdsKey, id.ToString()));
        Assert.True(await db.SetContainsAsync(RedisKeyBuilder.CronOccurrencesByCronKey(cronId), id.ToString()));
        // Held by node-A, so it is not pending for B yet.
        Assert.Null(await db.SortedSetScoreAsync(RedisKeyBuilder.CronOccurrencePendingKey, id.ToString()));

        await nodeB.ReleaseDeadNodeOccurrenceResources("node-A", CancellationToken.None);

        Assert.Equal(TickerStatus.Idle, (await ReadOccurrence(nodeB, id)).Status);
        Assert.NotNull(await db.SortedSetScoreAsync(RedisKeyBuilder.CronOccurrencePendingKey, id.ToString()));

        var found = await nodeB.GetEarliestAvailableCronOccurrence([cronId], CancellationToken.None);
        Assert.Equal(id, found.Id);
        var wins = 0;
        async Task Claim(ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> node)
        {
            var requeue = Slot(cronId);
            requeue.Items[0].NextCronOccurrence = new NextCronOccurrence(id, T0);
            if ((await Drain(node.QueueCronTickerOccurrences(requeue, CancellationToken.None))).Count == 1)
                Interlocked.Increment(ref wins);
        }

        await Task.WhenAll(Task.Run(() => Claim(nodeB)), Task.Run(() => Claim(nodeC)));
        Assert.Equal(1, wins);
    }

    [Fact]
    public async Task OrphanSlotRow_HolderRetries_BecomesReachable()
    {
        var nodeA = CreateProvider("node-A");
        var cronId = Guid.NewGuid();
        var id = await QueueOrphanSlotRow(nodeA, cronId);

        Assert.Empty(await Drain(nodeA.QueueCronTickerOccurrences(Slot(cronId), CancellationToken.None)));

        Assert.NotNull(await fixture.Database.SortedSetScoreAsync(RedisKeyBuilder.CronOccurrencePendingKey, id.ToString()));
        var found = await nodeA.GetEarliestAvailableCronOccurrence([cronId], CancellationToken.None);
        Assert.Equal(id, found.Id);
        var requeue = Slot(cronId);
        requeue.Items[0].NextCronOccurrence = new NextCronOccurrence(found.Id, found.CreatedAt);
        Assert.Equal(id, Assert.Single(await Drain(nodeA.QueueCronTickerOccurrences(requeue, CancellationToken.None))).Id);
    }

    // The holder's index batch landed only in part: the id sets were written, the pending entry was not. Being in the
    // id set must not count as fully indexed, or the fallback, which reads only the pending set, never finds the row.
    [Fact]
    public async Task PartlyIndexedSlotRow_HolderRetries_BecomesReachable()
    {
        var nodeA = CreateProvider("node-A");
        var cronId = Guid.NewGuid();
        var id = Assert.Single(await Drain(nodeA.QueueCronTickerOccurrences(Slot(cronId), CancellationToken.None))).Id;
        await fixture.Database.SortedSetRemoveAsync(RedisKeyBuilder.CronOccurrencePendingKey, id.ToString());

        Assert.Empty(await Drain(nodeA.QueueCronTickerOccurrences(Slot(cronId), CancellationToken.None)));

        Assert.NotNull(await fixture.Database.SortedSetScoreAsync(RedisKeyBuilder.CronOccurrencePendingKey, id.ToString()));
        Assert.Equal(id, (await nodeA.GetEarliestAvailableCronOccurrence([cronId], CancellationToken.None)).Id);
    }

    // Same partial index, the per-cron set missing too: the heal restores it, so deleting the cron still finds the row.
    [Fact]
    public async Task PartlyIndexedSlotRow_HolderRetries_RestoresThePerCronEntry()
    {
        var nodeA = CreateProvider("node-A");
        var cronId = Guid.NewGuid();
        var id = Assert.Single(await Drain(nodeA.QueueCronTickerOccurrences(Slot(cronId), CancellationToken.None))).Id;
        await fixture.Database.SortedSetRemoveAsync(RedisKeyBuilder.CronOccurrencePendingKey, id.ToString());
        await fixture.Database.SetRemoveAsync(RedisKeyBuilder.CronOccurrencesByCronKey(cronId), id.ToString());

        Assert.Empty(await Drain(nodeA.QueueCronTickerOccurrences(Slot(cronId), CancellationToken.None)));

        Assert.True(await fixture.Database.SetContainsAsync(RedisKeyBuilder.CronOccurrencesByCronKey(cronId), id.ToString()));
    }

    [Fact]
    public async Task SlotLoser_DoesNotRemoveHoldersPendingEntry()
    {
        var nodeA = CreateProvider("node-A");
        var nodeB = CreateProvider("node-B");
        var cronId = Guid.NewGuid();
        var id = Assert.Single(await Drain(nodeA.QueueCronTickerOccurrences(Slot(cronId), CancellationToken.None))).Id;

        Assert.Empty(await Drain(nodeB.QueueCronTickerOccurrences(Slot(cronId), CancellationToken.None)));

        Assert.NotNull(await fixture.Database.SortedSetScoreAsync(RedisKeyBuilder.CronOccurrencePendingKey, id.ToString()));
        Assert.Equal("node-A", (await ReadOccurrence(nodeA, id)).LockHolder);
    }

    [Fact]
    public async Task TerminalSlotRow_IsNotRecreated()
    {
        var nodeA = CreateProvider("node-A");
        var nodeB = CreateProvider("node-B");
        var cronId = Guid.NewGuid();
        var id = Assert.Single(await Drain(nodeA.QueueCronTickerOccurrences(Slot(cronId), CancellationToken.None))).Id;
        await nodeA.UpdateCronTickerOccurrence(StatusContext(id, TickerStatus.Done), CancellationToken.None);
        var before = await ReadOccurrence(nodeA, id);

        Assert.Empty(await Drain(nodeB.QueueCronTickerOccurrences(Slot(cronId), CancellationToken.None)));

        var after = await ReadOccurrence(nodeA, id);
        Assert.Equal(TickerStatus.Done, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.LockHolder, after.LockHolder);
        Assert.Single(await nodeA.GetAllCronTickerOccurrences(x => x.CronTickerId == cronId, CancellationToken.None));
    }

    #endregion
}
