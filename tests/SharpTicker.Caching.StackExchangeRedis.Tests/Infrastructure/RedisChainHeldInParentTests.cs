using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SharpTicker.Caching.StackExchangeRedis.Helpers;
using SharpTicker.Caching.StackExchangeRedis.Infrastructure;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Instrumentation;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Managers;
using SharpTicker.Utilities.Models;
using StackExchange.Redis;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>
/// A chain a version before 1.3.0 stored in Redis: one record, the children and grandchildren held inside the parent's row,
/// with no rows of their own. The rows are written here as the raw text such a version stored. The node that claims the
/// parent writes the nodes as rows of their own before the parent is handed out, so the chain then runs like one stored by
/// this version.
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class RedisChainHeldInParentTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private const string NodeA = "node-A";
    private const string NodeB = "node-B";

    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeTickerClock _clock = new(T0);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    public enum ClaimPath
    {
        Fallback,
        RunNow,
        Loop
    }

    #region The stored text and what is read back

    // parent -> child0 (OnSuccess) -> grand (OnSuccess); child1 (OnFailure); delayed (OnSuccess, a time of its own)
    private sealed record Chain(Guid Parent, Guid Child0, Guid Grand, Guid Child1, Guid Delayed)
    {
        public Guid[] Descendants => [Child0, Grand, Child1, Delayed];
    }

    private static string Q(DateTime time) => "\"" + time.ToString("O") + "\"";

    // One node as 1.2.3 stored it: a nested node has ParentId null and is Idle.
    private static string NodeText(Guid id, string function, string executionTime, int? runCondition, string children,
        int status = 0, string holder = "null", string executedAt = "null", string parentId = "null")
        => "{\"Status\":" + status + ",\"LockHolder\":" + holder + ",\"Request\":\"\",\"ExecutionTime\":" + executionTime
           + ",\"LockedAt\":null,\"ExecutedAt\":" + executedAt + ",\"ExceptionMessage\":null,\"SkippedReason\":null,\"ElapsedTime\":0,"
           + "\"Retries\":0,\"RetryCount\":0,\"RetryIntervals\":null,\"ParentId\":" + parentId + ",\"Children\":" + children
           + ",\"RunCondition\":" + (runCondition?.ToString() ?? "null") + ",\"Id\":\"" + id + "\",\"Function\":\"" + function + "\","
           + "\"Description\":null,\"InitIdentifier\":null,\"CreatedAt\":\"2025-06-15T10:00:00.3556448Z\",\"UpdatedAt\":\"2025-06-15T10:00:00.3556448Z\"}";

    // empty: how an empty Children collection of a leaf is written; a claim script before 1.3.0 wrote {}.
    private async Task<Chain> SeedAsync(DateTime parentTime, DateTime delayedTime, string empty = "[]", int parentStatus = 0,
        string parentHolder = "null", string parentExecutedAt = "null", bool pending = true)
    {
        var chain = new Chain(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var grand = NodeText(chain.Grand, "grand", "null", (int)RunCondition.OnSuccess, empty);
        var child0 = NodeText(chain.Child0, "child0", "null", (int)RunCondition.OnSuccess, "[" + grand + "]");
        var child1 = NodeText(chain.Child1, "child1", "null", (int)RunCondition.OnFailure, empty);
        var delayed = NodeText(chain.Delayed, "delayed", Q(delayedTime), (int)RunCondition.OnSuccess, empty);
        var parent = NodeText(chain.Parent, "parent", Q(parentTime), null, "[" + child0 + "," + child1 + "," + delayed + "]",
            parentStatus, parentHolder, parentExecutedAt);

        var db = fixture.Database;
        await db.StringSetAsync(RedisKeyBuilder.TimeTickerKey(chain.Parent), parent);
        await db.SetAddAsync(RedisKeyBuilder.TimeTickerIdsKey, chain.Parent.ToString());
        if (pending)
            await db.SortedSetAddAsync(RedisKeyBuilder.TimeTickerPendingKey, chain.Parent.ToString(), RedisKeyBuilder.ToScore(parentTime));
        return chain;
    }

    // A parent that is overdue, with a delayed child that is overdue too: the fallback's case.
    private Task<Chain> SeedOverdueAsync(string empty = "[]") => SeedAsync(T0.AddMinutes(-1), T0.AddSeconds(-30), empty);

    private async Task<string?> RawAsync(Guid id) => (string?)await fixture.Database.StringGetAsync(RedisKeyBuilder.TimeTickerKey(id));

    private async Task<JsonElement> StoredAsync(Guid id)
    {
        var raw = await RawAsync(id);
        Assert.True(raw != null, $"no row is stored for {id}");
        using var doc = JsonDocument.Parse(raw!);
        return doc.RootElement.Clone();
    }

    private async Task<TickerStatus> StatusAsync(Guid id) => (TickerStatus)(await StoredAsync(id)).GetProperty("Status").GetInt32();

    private static string? Text(JsonElement row, string name)
        => row.GetProperty(name).ValueKind == JsonValueKind.Null ? null : row.GetProperty(name).GetString();

    private string[] Keys()
        => fixture.Connection.GetServer(fixture.Connection.GetEndPoints()[0]).Keys(pattern: "tq:*").Select(k => (string)k!).Order().ToArray();

    private async Task<bool> IsPendingAsync(Guid id)
        => await fixture.Database.SortedSetScoreAsync(RedisKeyBuilder.TimeTickerPendingKey, id.ToString()) != null;

    private static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    private static bool Ended(TickerStatus status) => status is TickerStatus.Done or TickerStatus.DueDone;

    #endregion

    #region Running a due chain: the fallback's claim, the manager's contexts, the handler's run

    // One fallback pass of a node: what it claims is run by the real handler. Returns the ids whose function ran.
    private async Task<List<Guid>> RunPassAsync(string node = NodeA, IDatabase? database = null, Guid? failing = null)
    {
        var provider = fixture.CreateProvider(node, _clock, database);
        var manager = new InternalTickerManager<TimeTickerEntity, CronTickerEntity>(provider, _clock, Substitute.For<ISharpTickerNotificationHubSender>());
        var instrumentation = Substitute.For<ISharpTickerInstrumentation>();
        var services = new ServiceCollection();
        services.AddSingleton<IInternalTickerManager>(manager);
        services.AddSingleton(instrumentation);
        services.AddSingleton(Substitute.For<ISharpTickerDispatcher>());
        var handler = new TickerExecutionTaskHandler(services.BuildServiceProvider(), _clock, instrumentation, manager);

        var ran = new ConcurrentBag<Guid>();
        void Wire(InternalFunctionContext context)
        {
            context.CachedDelegate = (_, _, _) =>
            {
                ran.Add(context.TickerId);
                return context.TickerId == failing ? throw new InvalidOperationException("the job failed") : Task.CompletedTask;
            };
            foreach (var child in context.TimeTickerChildren)
                Wire(child);
        }

        foreach (var context in await manager.RunTimedOutTickers(CancellationToken.None))
        {
            Wire(context);
            await handler.ExecuteTaskAsync(context, isDue: true);
        }

        return ran.ToList();
    }

    // Several passes, each by a node that just started, the clock moved on between them: what a chain needs to run out.
    private async Task<List<Guid>> RunPassesAsync(int passes = 4, Guid? failing = null)
    {
        var ran = new List<Guid>();
        for (var pass = 0; pass < passes; pass++)
        {
            ran.AddRange(await RunPassAsync(failing: failing));
            _clock.Advance(TimeSpan.FromSeconds(40));
        }
        return ran;
    }

    private static void AssertRanOnceEach(IEnumerable<Guid> ran, params Guid[] expected)
        => Assert.Equal(expected.Order(), ran.Order());

    // The chain ran as its conditions say after a parent that succeeded, and is stored as rows.
    private async Task AssertRanOutAfterASuccessfulParentAsync(Chain chain, IEnumerable<Guid> ran)
    {
        AssertRanOnceEach(ran, chain.Parent, chain.Child0, chain.Grand, chain.Delayed);
        Assert.True(Ended(await StatusAsync(chain.Parent)));
        Assert.True(Ended(await StatusAsync(chain.Child0)));
        Assert.True(Ended(await StatusAsync(chain.Grand)));
        Assert.True(Ended(await StatusAsync(chain.Delayed)));
        Assert.Equal(TickerStatus.Skipped, await StatusAsync(chain.Child1));
        await AssertStoredAsRowsAsync(chain);
    }

    private async Task AssertStoredAsRowsAsync(Chain chain)
    {
        Assert.Equal(0, (await StoredAsync(chain.Parent)).GetProperty("Children").GetArrayLength());
        Assert.Equal(chain.Parent.ToString(), Text(await StoredAsync(chain.Child0), "ParentId"));
        Assert.Equal(chain.Parent.ToString(), Text(await StoredAsync(chain.Child1), "ParentId"));
        Assert.Equal(chain.Parent.ToString(), Text(await StoredAsync(chain.Delayed), "ParentId"));
        Assert.Equal(chain.Child0.ToString(), Text(await StoredAsync(chain.Grand), "ParentId"));
        Assert.Equal(0, (await StoredAsync(chain.Child0)).GetProperty("Children").GetArrayLength());
    }

    // Control: the same chain stored by this version (a row per node).
    [Fact]
    public async Task AChainStoredAsRows_RunsEachNodeOnce_ByItsCondition()
    {
        TimeTickerEntity Node(string function, DateTime? time, RunCondition? condition = null) => new()
        {
            Id = Guid.NewGuid(), Function = function, ExecutionTime = time, RunCondition = condition, Status = TickerStatus.Idle, Request = []
        };
        var grand = Node("grand", null, RunCondition.OnSuccess);
        var child0 = Node("child0", null, RunCondition.OnSuccess);
        child0.Children = [grand];
        var child1 = Node("child1", null, RunCondition.OnFailure);
        var delayed = Node("delayed", T0.AddSeconds(-30), RunCondition.OnSuccess);
        var parent = Node("parent", T0.AddMinutes(-1));
        parent.Children = [child0, child1, delayed];
        await fixture.CreateProvider("seed", _clock).AddTimeTickers([parent], CancellationToken.None);

        var ran = await RunPassesAsync();

        await AssertRanOutAfterASuccessfulParentAsync(new Chain(parent.Id, child0.Id, grand.Id, child1.Id, delayed.Id), ran);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    public async Task AChainHeldInsideItsParentsRow_RunsEachNodeOnce_ByItsCondition(string empty)
    {
        var chain = await SeedOverdueAsync(empty);

        var ran = await RunPassesAsync();

        await AssertRanOutAfterASuccessfulParentAsync(chain, ran);
    }

    [Fact]
    public async Task AChainHeldInsideItsParentsRow_WhoseParentFails_RunsOnlyTheChildThatWaitsForAFailure()
    {
        var chain = await SeedOverdueAsync();

        var ran = await RunPassesAsync(failing: chain.Parent);

        AssertRanOnceEach(ran, chain.Parent, chain.Child1);
        Assert.Equal(TickerStatus.Failed, await StatusAsync(chain.Parent));
        Assert.True(Ended(await StatusAsync(chain.Child1)));
        Assert.Equal(TickerStatus.Skipped, await StatusAsync(chain.Child0));
        Assert.Equal(TickerStatus.Skipped, await StatusAsync(chain.Grand));
        Assert.Equal(TickerStatus.Skipped, await StatusAsync(chain.Delayed));
    }

    // A nested child with a time of its own waits for that time once it is a row, as a delayed child of this version does.
    [Fact]
    public async Task ANestedChildWithATimeOfItsOwn_RunsAtThatTime_NotRightAfterItsParent()
    {
        var chain = await SeedAsync(T0.AddMinutes(-1), delayedTime: T0.AddMinutes(10));

        var ranFirst = await RunPassesAsync(passes: 3);

        AssertRanOnceEach(ranFirst, chain.Parent, chain.Child0, chain.Grand);
        Assert.Equal(TickerStatus.Idle, await StatusAsync(chain.Delayed));
        Assert.Equal(T0.AddMinutes(10), (await StoredAsync(chain.Delayed)).GetProperty("ExecutionTime").GetDateTime().ToUniversalTime());

        _clock.UtcNow = T0.AddMinutes(11);
        var ranLater = await RunPassesAsync(passes: 2);

        AssertRanOnceEach(ranLater, chain.Delayed);
    }

    // What the dashboard and GetTimeTickerById read, before the chain is claimed and after it ran.
    [Fact]
    public async Task AChainHeldInsideItsParentsRow_ReadsAsOneRootWithItsChildren_BeforeAndAfterItRan()
    {
        var chain = await SeedOverdueAsync();
        var provider = fixture.CreateProvider(NodeA, _clock);

        async Task AssertReadsAsATreeAsync()
        {
            var root = await provider.GetTimeTickerById(chain.Parent, CancellationToken.None);
            Assert.Equal([chain.Child0, chain.Child1, chain.Delayed], root.Children.Select(c => c.Id));
            Assert.Equal(chain.Grand, Assert.Single(root.Children.First().Children).Id);
            Assert.Equal(chain.Parent, Assert.Single(await provider.GetTimeTickers(null!, CancellationToken.None)).Id);
        }

        await AssertReadsAsATreeAsync();
        await RunPassesAsync();
        await AssertReadsAsATreeAsync();
    }

    #endregion

    #region What each claim path hands out

    // The children as the handler gets them: rows, linked to their parent, a delayed child with its time.
    private static void AssertHandedOutAsRows(Chain chain, TimeTickerEntity handedOut, DateTime delayedTime)
    {
        Assert.Equal(chain.Parent, handedOut.Id);
        Assert.Equal([chain.Child0, chain.Child1, chain.Delayed], handedOut.Children.Select(c => c.Id));
        Assert.All(handedOut.Children, c => Assert.Equal(chain.Parent, c.ParentId));
        var grand = Assert.Single(handedOut.Children.First().Children);
        Assert.Equal((chain.Grand, chain.Child0), (grand.Id, grand.ParentId!.Value));
        Assert.Null(handedOut.Children.First().ExecutionTime);
        Assert.Equal(delayedTime, handedOut.Children.Last().ExecutionTime);
        Assert.Equal(RunCondition.OnFailure, handedOut.Children.ElementAt(1).RunCondition);
    }

    private static async Task<List<TimeTickerEntity>> ClaimAsync(TickerRedisPersistenceProvider<TimeTickerEntity, CronTickerEntity> provider,
        ClaimPath path, Guid parentId)
        => path switch
        {
            ClaimPath.Fallback => await Drain(provider.QueueTimedOutTimeTickers(CancellationToken.None)),
            ClaimPath.RunNow => (await provider.AcquireImmediateTimeTickersAsync([parentId], CancellationToken.None)).ToList(),
            _ => await Drain(provider.QueueTimeTickers(await provider.GetEarliestTimeTickers(CancellationToken.None), CancellationToken.None))
        };

    // The loop takes a row that is due within the next second; the fallback and run-now take one that is overdue.
    private Task<Chain> SeedForAsync(ClaimPath path, string empty = "[]")
        => path == ClaimPath.Loop ? SeedAsync(T0, T0.AddSeconds(30), empty) : SeedOverdueAsync(empty);

    private static DateTime DelayedTimeFor(ClaimPath path) => path == ClaimPath.Loop ? T0.AddSeconds(30) : T0.AddSeconds(-30);

    [Theory]
    [InlineData(ClaimPath.Fallback, "[]")]
    [InlineData(ClaimPath.RunNow, "[]")]
    [InlineData(ClaimPath.Loop, "[]")]
    [InlineData(ClaimPath.Fallback, "{}")]
    [InlineData(ClaimPath.RunNow, "{}")]
    [InlineData(ClaimPath.Loop, "{}")]
    public async Task EveryClaimPath_WritesTheNestedNodesAsRows_AndHandsThemOutAsRows(ClaimPath path, string empty)
    {
        var chain = await SeedForAsync(path, empty);

        var handedOut = Assert.Single(await ClaimAsync(fixture.CreateProvider(NodeA, _clock), path, chain.Parent));

        AssertHandedOutAsRows(chain, handedOut, DelayedTimeFor(path));
        await AssertStoredAsRowsAsync(chain);
        var parent = await StoredAsync(chain.Parent);
        Assert.Equal(path == ClaimPath.Loop ? TickerStatus.Queued : TickerStatus.InProgress, (TickerStatus)parent.GetProperty("Status").GetInt32());
        Assert.Equal(NodeA, Text(parent, "LockHolder"));
        foreach (var id in chain.Descendants)
            Assert.Equal(TickerStatus.Idle, await StatusAsync(id));

        // The delayed child is pending like any row with a time; the children without one wait for their inline start.
        Assert.True(await IsPendingAsync(chain.Delayed));
        var waiting = (await fixture.Database.HashGetAllAsync(RedisKeyBuilder.TimeTickerWaitingChildrenKey)).ToDictionary(e => (string)e.Name!, e => (string)e.Value!);
        Assert.Equal(chain.Parent.ToString(), waiting[chain.Child0.ToString()]);
        Assert.Equal(chain.Parent.ToString(), waiting[chain.Child1.ToString()]);
        Assert.Equal(chain.Child0.ToString(), waiting[chain.Grand.ToString()]);
        Assert.DoesNotContain(chain.Delayed.ToString(), waiting.Keys);
        var ids = (await fixture.Database.SetMembersAsync(RedisKeyBuilder.TimeTickerIdsKey)).Select(m => (string)m!).ToHashSet();
        Assert.All(chain.Descendants, id => Assert.Contains(id.ToString(), ids));
    }

    // Two nodes read the same due chain; the one that queues it second holds a copy that is no longer current and gets
    // nothing, so the nodes are written once and one node holds the chain.
    [Fact]
    public async Task TheLoopOfASecondNode_ThatReadTheChainBeforeTheFirstQueuedIt_GetsNothing()
    {
        var chain = await SeedForAsync(ClaimPath.Loop);
        var a = fixture.CreateProvider(NodeA, _clock);
        var b = fixture.CreateProvider(NodeB, _clock);

        var readByB = await b.GetEarliestTimeTickers(CancellationToken.None);
        Assert.Equal(chain.Parent, Assert.Single(readByB).Id);
        var queuedByA = await Drain(a.QueueTimeTickers(await a.GetEarliestTimeTickers(CancellationToken.None), CancellationToken.None));
        var rowsAfterA = await Task.WhenAll(chain.Descendants.Select(RawAsync));
        var queuedByB = await Drain(b.QueueTimeTickers(readByB, CancellationToken.None));

        Assert.Single(queuedByA);
        Assert.All(rowsAfterA, Assert.NotNull);
        Assert.Empty(queuedByB);
        Assert.Equal(NodeA, Text(await StoredAsync(chain.Parent), "LockHolder"));
        Assert.Equal(rowsAfterA, await Task.WhenAll(chain.Descendants.Select(RawAsync)));
    }

    #endregion

    #region A chain that was written as rows only in part

    public enum HalfWay
    {
        OnlyTheFirstLink,
        TheGrandchildsRowButNotItsParents,
        EveryRowButTheParentStillHoldsItsChildren
    }

    // What a node that stopped while it wrote the rows left behind. The next claim finishes the work.
    [Theory]
    [InlineData(HalfWay.OnlyTheFirstLink)]
    [InlineData(HalfWay.TheGrandchildsRowButNotItsParents)]
    [InlineData(HalfWay.EveryRowButTheParentStillHoldsItsChildren)]
    public async Task AChainLeftHalfWrittenAsRows_IsFinishedByTheNextClaim_AndRunsEachNodeOnce(HalfWay state)
    {
        var chain = await SeedOverdueAsync();
        var db = fixture.Database;
        string Row(Guid id, string function, string time, RunCondition condition, Guid parentId)
            => NodeText(id, function, time, (int)condition, "[]", parentId: "\"" + parentId + "\"");

        await db.SortedSetAddAsync(RedisKeyBuilder.TimeTickerChildrenKey(chain.Parent), chain.Child0.ToString(), 0);
        if (state != HalfWay.OnlyTheFirstLink)
        {
            await db.SortedSetAddAsync(RedisKeyBuilder.TimeTickerChildrenKey(chain.Child0), chain.Grand.ToString(), 0);
            await db.StringSetAsync(RedisKeyBuilder.TimeTickerKey(chain.Grand), Row(chain.Grand, "grand", "null", RunCondition.OnSuccess, chain.Child0));
        }
        if (state == HalfWay.EveryRowButTheParentStillHoldsItsChildren)
        {
            await db.StringSetAsync(RedisKeyBuilder.TimeTickerKey(chain.Child0), Row(chain.Child0, "child0", "null", RunCondition.OnSuccess, chain.Parent));
            await db.SortedSetAddAsync(RedisKeyBuilder.TimeTickerChildrenKey(chain.Parent), chain.Child1.ToString(), 1);
            await db.StringSetAsync(RedisKeyBuilder.TimeTickerKey(chain.Child1), Row(chain.Child1, "child1", "null", RunCondition.OnFailure, chain.Parent));
            await db.SortedSetAddAsync(RedisKeyBuilder.TimeTickerChildrenKey(chain.Parent), chain.Delayed.ToString(), 2);
            await db.StringSetAsync(RedisKeyBuilder.TimeTickerKey(chain.Delayed), Row(chain.Delayed, "delayed", Q(T0.AddSeconds(-30)), RunCondition.OnSuccess, chain.Parent));
        }

        var ran = await RunPassesAsync();

        await AssertRanOutAfterASuccessfulParentAsync(chain, ran);
    }

    // A child's row that already exists is the child: it is not written again from the copy the parent still holds. Here
    // the row says the child ran, the copy inside the parent says it is Idle.
    [Fact]
    public async Task AChildsRowThatAlreadyExists_IsLeftAsItIs_AndTheChildDoesNotRunAgain()
    {
        var chain = await SeedOverdueAsync();
        var ranBefore = NodeText(chain.Child0, "child0", "null", (int)RunCondition.OnSuccess, "[]", status: (int)TickerStatus.Done,
            executedAt: Q(T0.AddMinutes(-2)), parentId: "\"" + chain.Parent + "\"");
        await fixture.Database.StringSetAsync(RedisKeyBuilder.TimeTickerKey(chain.Child0), ranBefore);

        var ran = await RunPassesAsync();

        Assert.Equal(ranBefore, await RawAsync(chain.Child0));
        Assert.DoesNotContain(chain.Child0, ran);
        Assert.Equal(1, ran.Count(id => id == chain.Parent));
        // Its own child was never started; the fallback starts it once, since the row of its parent says it succeeded.
        Assert.Equal(1, ran.Count(id => id == chain.Grand));
        Assert.Equal(ran.Count, ran.Distinct().Count());
    }

    #endregion

    #region Parents that are not claimed are not touched

    // An ended parent keeps its children inside its row: none of them is written as a row, so none of them runs.
    [Theory]
    [InlineData((int)TickerStatus.Done)]
    [InlineData((int)TickerStatus.Failed)]
    [InlineData((int)TickerStatus.Skipped)]
    public async Task AnEndedParent_KeepsItsChildrenInsideItsRow_AndNoneOfThemRuns(int status)
    {
        var chain = await SeedAsync(T0.AddMinutes(-1), T0.AddSeconds(-30), parentStatus: status, parentExecutedAt: Q(T0.AddHours(-1)));
        var before = await RawAsync(chain.Parent);
        var a = fixture.CreateProvider(NodeA, _clock);

        Assert.Empty(await a.AcquireImmediateTimeTickersAsync([chain.Parent], CancellationToken.None));
        Assert.Empty(await Drain(a.QueueTimeTickers(await a.GetEarliestTimeTickers(CancellationToken.None), CancellationToken.None)));
        var ran = await RunPassesAsync();

        Assert.Empty(ran);
        Assert.Equal(before, await RawAsync(chain.Parent));
        Assert.All(await Task.WhenAll(chain.Descendants.Select(RawAsync)), Assert.Null);
        Assert.DoesNotContain(Keys(), key => key.EndsWith(":children") || key == RedisKeyBuilder.TimeTickerWaitingChildrenKey);
    }

    // An update of an ended parent (what the dashboard's edit sends, with or without the children) keeps the children
    // inside its row as well. Written as rows they would be Idle children of an ended parent, and the fallback would run
    // the ones the parent's outcome lets run (#152).
    [Theory]
    [InlineData((int)TickerStatus.Done, false)]
    [InlineData((int)TickerStatus.Done, true)]
    [InlineData((int)TickerStatus.Failed, false)]
    [InlineData((int)TickerStatus.Failed, true)]
    [InlineData((int)TickerStatus.DueDone, true)]
    [InlineData((int)TickerStatus.Cancelled, true)]
    public async Task AnUpdateOfAnEndedParent_KeepsItsChildrenInsideItsRow_AndNoneOfThemRuns(int status, bool bodyCarriesChildren)
    {
        var chain = await SeedAsync(T0.AddHours(-2), T0.AddHours(-1), parentStatus: status, parentExecutedAt: Q(T0.AddHours(-2)));
        var provider = fixture.CreateProvider(NodeA, _clock);
        var body = await provider.GetTimeTickerById(chain.Parent, CancellationToken.None);
        Assert.Equal(3, body.Children.Count);
        body.Description = "edited";
        if (!bodyCarriesChildren)
            body.Children = new List<TimeTickerEntity>();

        await provider.UpdateTimeTickers([body], CancellationToken.None);
        var ran = await RunPassesAsync();

        Assert.Empty(ran);
        Assert.All(await Task.WhenAll(chain.Descendants.Select(RawAsync)), Assert.Null);
        var stored = await StoredAsync(chain.Parent);
        Assert.Equal("edited", Text(stored, "Description"));
        Assert.Equal(status, stored.GetProperty("Status").GetInt32());
        Assert.Equal(3, stored.GetProperty("Children").GetArrayLength());
        var read = await provider.GetTimeTickerById(chain.Parent, CancellationToken.None);
        Assert.Equal([chain.Child0, chain.Child1, chain.Delayed], read.Children.Select(c => c.Id));
    }

    // The control: an update of a parent that has not run yet still writes the children as rows of their own.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUpdateOfAParentThatHasNotRunYet_WritesItsChildrenAsRows(bool bodyCarriesChildren)
    {
        var chain = await SeedAsync(T0.AddHours(1), T0.AddHours(2));
        var provider = fixture.CreateProvider(NodeA, _clock);
        var body = await provider.GetTimeTickerById(chain.Parent, CancellationToken.None);
        if (!bodyCarriesChildren)
            body.Children = new List<TimeTickerEntity>();

        await provider.UpdateTimeTickers([body], CancellationToken.None);

        Assert.All(await Task.WhenAll(chain.Descendants.Select(RawAsync)), Assert.NotNull);
        Assert.Equal(0, (await StoredAsync(chain.Parent)).GetProperty("Children").GetArrayLength());
    }

    // A parent another node holds is not this node's to write: its row and its children stay as they are.
    [Theory]
    [InlineData((int)TickerStatus.InProgress)]
    [InlineData((int)TickerStatus.Queued)]
    public async Task AParentHeldByAnotherNode_IsLeftAsItIs(int status)
    {
        var chain = await SeedAsync(T0, T0.AddSeconds(30), parentStatus: status, parentHolder: "\"" + NodeB + "\"");
        var before = await RawAsync(chain.Parent);
        var a = fixture.CreateProvider(NodeA, _clock);
        var stale = new TimeTickerEntity { Id = chain.Parent, UpdatedAt = DateTime.Parse("2025-06-15T10:00:00.3556448Z").ToUniversalTime() };

        Assert.Empty(await a.AcquireImmediateTimeTickersAsync([chain.Parent], CancellationToken.None));
        Assert.Empty(await Drain(a.QueueTimeTickers([stale], CancellationToken.None)));
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(await Drain(a.QueueTimedOutTimeTickers(CancellationToken.None)));

        Assert.Equal(before, await RawAsync(chain.Parent));
        Assert.All(await Task.WhenAll(chain.Descendants.Select(RawAsync)), Assert.Null);
        Assert.DoesNotContain(Keys(), key => key.EndsWith(":children"));
    }

    #endregion

    #region Two nodes, a failing store, a claim lost on the way

    // Two nodes run their fallback over the same chain at the same time, many times over: each node of the chain starts once.
    [Fact]
    public async Task TwoNodesThatTakeTheSameChainAtTheSameTime_StartEachNodeOnce()
    {
        for (var round = 0; round < 15; round++)
        {
            await fixture.ResetAsync();
            _clock.UtcNow = T0;
            var chain = await SeedOverdueAsync();

            var ran = new List<Guid>();
            for (var pass = 0; pass < 4; pass++)
            {
                var both = await Task.WhenAll(Task.Run(() => RunPassAsync(NodeA)), Task.Run(() => RunPassAsync(NodeB)));
                ran.AddRange(both.SelectMany(r => r));
                _clock.Advance(TimeSpan.FromSeconds(40));
            }

            await AssertRanOutAfterASuccessfulParentAsync(chain, ran);
        }
    }

    // A write of the nodes as rows fails. The parent is not handed out without them: its claim is put back, the row still
    // holds its children, and it is still pending, so the next pass does the work again and the chain runs.
    [Theory]
    [InlineData(ClaimPath.Fallback, nameof(IDatabase.StringSetAsync), 1)]
    [InlineData(ClaimPath.Fallback, nameof(IDatabase.StringSetAsync), 2)]
    [InlineData(ClaimPath.Fallback, nameof(IDatabase.StringSetAsync), 3)]
    [InlineData(ClaimPath.Fallback, nameof(IDatabase.StringSetAsync), 4)]
    [InlineData(ClaimPath.Fallback, nameof(IDatabase.SortedSetAddAsync), 1)]
    [InlineData(ClaimPath.Fallback, nameof(IDatabase.SortedSetAddAsync), 2)]
    [InlineData(ClaimPath.Fallback, nameof(IDatabase.SortedSetAddAsync), 4)]
    [InlineData(ClaimPath.RunNow, nameof(IDatabase.StringSetAsync), 1)]
    [InlineData(ClaimPath.RunNow, nameof(IDatabase.StringSetAsync), 3)]
    [InlineData(ClaimPath.RunNow, nameof(IDatabase.SortedSetAddAsync), 2)]
    [InlineData(ClaimPath.Loop, nameof(IDatabase.StringSetAsync), 1)]
    [InlineData(ClaimPath.Loop, nameof(IDatabase.StringSetAsync), 3)]
    [InlineData(ClaimPath.Loop, nameof(IDatabase.SortedSetAddAsync), 2)]
    // The second script call of each path is the write that takes the children out of the parent's row (the first is the claim).
    [InlineData(ClaimPath.Fallback, nameof(IDatabase.ScriptEvaluateAsync), 2)]
    [InlineData(ClaimPath.RunNow, nameof(IDatabase.ScriptEvaluateAsync), 2)]
    [InlineData(ClaimPath.Loop, nameof(IDatabase.ScriptEvaluateAsync), 2)]
    public async Task WhenWritingTheRowsFails_TheParentIsPutBack_AndTheNextClaimRunsTheChain(ClaimPath path, string method, int failAt)
    {
        var chain = await SeedForAsync(path);
        var failingStore = RedisClaimMultiNodeTests.FailingDatabase.Create(fixture.Database, method, failAt);

        var handedOut = await ClaimAsync(fixture.CreateProvider(NodeA, _clock, failingStore), path, chain.Parent);

        Assert.Empty(handedOut);
        var parent = await StoredAsync(chain.Parent);
        Assert.Equal(TickerStatus.Idle, (TickerStatus)parent.GetProperty("Status").GetInt32());
        Assert.Null(Text(parent, "LockHolder"));
        Assert.Equal(3, parent.GetProperty("Children").GetArrayLength());
        Assert.True(await IsPendingAsync(chain.Parent));

        if (path == ClaimPath.Loop)
        {
            var again = Assert.Single(await ClaimAsync(fixture.CreateProvider(NodeA, _clock), path, chain.Parent));
            AssertHandedOutAsRows(chain, again, DelayedTimeFor(path));
            await AssertStoredAsRowsAsync(chain);
            return;
        }

        _clock.Advance(TimeSpan.FromSeconds(2));
        await AssertRanOutAfterASuccessfulParentAsync(chain, await RunPassesAsync());
    }

    // Between this node's claim and its write that takes the children out of the parent's row, the claim is released and
    // the parent is taken again: by another node, or by this node under a new claim. That claim stays as it is, and the
    // first one does not hand the parent out.
    [Theory]
    [InlineData(NodeB)]
    [InlineData(NodeA)]
    public async Task WhenTheClaimIsLostBeforeTheParentsRowIsWritten_TheLaterClaimStays_AndTheParentIsNotHandedOutTwice(string laterHolder)
    {
        var chain = await SeedOverdueAsync();
        var a = fixture.CreateProvider(NodeA, _clock);
        var aOther = fixture.CreateProvider(NodeA, _clock);
        var b = fixture.CreateProvider(laterHolder, _clock);
        var parentKey = RedisKeyBuilder.TimeTickerKey(chain.Parent);
        TimeTickerEntity[]? takenByB = null;
        string? afterB = null;
        a.BeforeCompareAndSetHook = async key =>
        {
            if (key != parentKey || takenByB != null) return;
            var held = await aOther.GetTimeTickerById(chain.Parent, CancellationToken.None);
            Assert.True(await aOther.TryReleaseTimeTickerClaim(chain.Parent, held.UpdatedAt, CancellationToken.None));
            takenByB = await b.AcquireImmediateTimeTickersAsync([chain.Parent], CancellationToken.None);
            afterB = await RawAsync(chain.Parent);
        };

        var handedOutByA = await a.AcquireImmediateTimeTickersAsync([chain.Parent], CancellationToken.None);

        Assert.True(takenByB != null, "the write of the parent's row was not reached");
        AssertHandedOutAsRows(chain, Assert.Single(takenByB!), T0.AddSeconds(-30));
        Assert.Empty(handedOutByA);
        Assert.Equal(afterB, await RawAsync(chain.Parent));
        Assert.Equal(laterHolder, Text(await StoredAsync(chain.Parent), "LockHolder"));
        await AssertStoredAsRowsAsync(chain);
    }

    #endregion
}
