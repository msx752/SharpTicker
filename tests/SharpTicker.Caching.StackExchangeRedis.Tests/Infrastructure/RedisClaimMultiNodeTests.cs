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

    // Redis keeps the children inside the parent's record; they are never rows of their own.
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
