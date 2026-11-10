using MongoDB.Driver;
using NSubstitute;
using SharpTicker.MongoDB.Infrastructure;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Managers;
using SharpTicker.Utilities.Models;

namespace SharpTicker.MongoDB.Tests;

/// <summary>
/// The fenced writes of a run (#74) on MongoDB: a run writes only to the row it still holds, its claim marker in UpdatedAt
/// and this node's lock. Two provider instances (separate NodeIdentifier) over one MongoDB, each with the real internal
/// manager, the way two nodes run. A release of a node's rows is ReleaseDeadNode…Resources, what the heartbeat does.
/// </summary>
[Collection("Mongo")]
public class MongoFencedRunTests : IClassFixture<MongoTestFixture>, IAsyncLifetime
{
    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly MongoTestFixture _f;
    private readonly FakeClock _clock = new(T0);

    public MongoFencedRunTests(MongoTestFixture fixture) => _f = fixture;

    public Task InitializeAsync() => _f.DropAllAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class FakeClock(DateTime start) : ITickerClock
    {
        public DateTime UtcNow { get; set; } = start;
    }

    private sealed record Node(ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> Store, IInternalTickerManager Manager);

    private Node CreateNode(string nodeIdentifier)
    {
        var store = new TickerMongoPersistenceProvider<TimeTickerEntity, CronTickerEntity>(
            new TickerMongoContext<TimeTickerEntity, CronTickerEntity>(_f.Database, "ticker_"),
            _clock,
            new SchedulerOptionsBuilder { NodeIdentifier = nodeIdentifier });
        var manager = new InternalTickerManager<TimeTickerEntity, CronTickerEntity>(store, _clock, Substitute.For<ISharpTickerNotificationHubSender>());
        return new Node(store, manager);
    }

    private static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    private async Task<TimeTickerEntity> SeedTimeTicker(DateTime? executionTime, params TimeTickerEntity[] children)
    {
        var ticker = new TimeTickerEntity
        {
            Id = Guid.NewGuid(),
            Function = "fenced-fn",
            ExecutionTime = executionTime,
            Status = TickerStatus.Idle,
            Description = "before",
            CreatedAt = T0.AddHours(-2),
            UpdatedAt = T0.AddHours(-1),
            Request = [],
            Children = children.ToList()
        };
        await CreateNode("seed").Store.AddTimeTickers([ticker], CancellationToken.None);
        return ticker;
    }

    private static TimeTickerEntity InlineChild() => new()
    {
        Id = Guid.NewGuid(),
        Function = "fenced-child",
        Status = TickerStatus.Idle,
        RunCondition = RunCondition.OnSuccess,
        CreatedAt = T0.AddHours(-2),
        UpdatedAt = T0.AddHours(-1),
        Request = []
    };

    // Queues the row on the node and claims it the way the scheduler does. Returns the context the run gets.
    private static async Task<InternalFunctionContext> ClaimTimeTicker(Node node, Guid id)
    {
        var window = await node.Store.GetEarliestTimeTickers(CancellationToken.None);
        Assert.Single(await Drain(node.Store.QueueTimeTickers(window.Where(x => x.Id == id).ToArray(), CancellationToken.None)));
        var context = new InternalFunctionContext { TickerId = id, FunctionName = "fenced-fn", Type = TickerType.TimeTicker };
        Assert.True(await node.Manager.TryClaimDueTicker(context, node.Manager.NextClaimMarker(), CancellationToken.None));
        context.RunMarker = context.ClaimMarker; // as the handler does when the run starts
        return context;
    }

    private static async Task<InternalFunctionContext> ClaimOccurrence(Node node)
    {
        var slot = (T0, new[] { new InternalManagerContext(Guid.NewGuid()) { FunctionName = "fenced-cron-fn", Expression = "* * * * *" } });
        var id = Assert.Single(await Drain(node.Store.QueueCronTickerOccurrences(slot, CancellationToken.None))).Id;
        var context = new InternalFunctionContext { TickerId = id, FunctionName = "fenced-cron-fn", Type = TickerType.CronTickerOccurrence };
        Assert.True(await node.Manager.TryClaimDueTicker(context, node.Manager.NextClaimMarker(), CancellationToken.None));
        context.RunMarker = context.ClaimMarker;
        return context;
    }

    private static InternalFunctionContext Outcome(InternalFunctionContext run, TickerStatus status, DateTime executedAt)
    {
        run.ResetUpdateProps()
            .SetProperty(x => x.Status, status)
            .SetProperty(x => x.ExecutedAt, executedAt);
        return run;
    }

    private Task<TimeTickerEntity> Row(Guid id) => _f.TimeTickers.Find(x => x.Id == id).SingleAsync();

    private Task<CronTickerOccurrenceEntity<CronTickerEntity>> OccurrenceRow(Guid id) => _f.CronTickerOccurrences.Find(x => x.Id == id).SingleAsync();

    // T6: the scheduler's due claim hands the run the marker it wrote, so the run is fenced.
    [Fact]
    public async Task TheDueClaim_GivesTheRunTheMarkerItWrote_ForATimeTickerAndACronOccurrence()
    {
        var node = CreateNode("node-A");
        var ticker = await SeedTimeTicker(T0);

        var timeRun = await ClaimTimeTicker(node, ticker.Id);
        var cronRun = await ClaimOccurrence(node);

        Assert.NotNull(timeRun.ClaimMarker);
        Assert.Equal((await Row(ticker.Id)).UpdatedAt, timeRun.ClaimMarker);
        Assert.NotNull(cronRun.ClaimMarker);
        Assert.Equal((await OccurrenceRow(cronRun.TickerId)).UpdatedAt, cronRun.ClaimMarker);
    }

    // T1: node A's run lost its row (released as for a dead node) and node B claimed and ran it. A's late write must not
    // overwrite B's run.
    [Fact]
    public async Task ALateWriteOfARunWhoseRowAnotherNodeTook_ChangesNothing()
    {
        var a = CreateNode("node-A");
        var b = CreateNode("node-B");
        var ticker = await SeedTimeTicker(T0);
        var runA = await ClaimTimeTicker(a, ticker.Id);
        await a.Store.ReleaseDeadNodeTimeTickerResources("node-A", CancellationToken.None);
        var runB = await ClaimTimeTicker(b, ticker.Id);
        Assert.True(await b.Manager.UpdateTickerAsync(Outcome(runB, TickerStatus.Done, T0.AddMinutes(1))));

        var stored = await a.Manager.UpdateTickerAsync(Outcome(runA, TickerStatus.Failed, T0.AddMinutes(2)));

        Assert.False(stored);
        var row = await Row(ticker.Id);
        Assert.Equal(TickerStatus.Done, row.Status);
        Assert.Equal("node-B", row.LockHolder);
        Assert.Equal(T0.AddMinutes(1), row.ExecutedAt);
    }

    // T1 for a cron occurrence.
    [Fact]
    public async Task ALateWriteOfACronRunWhoseRowAnotherNodeTook_ChangesNothing()
    {
        var a = CreateNode("node-A");
        var b = CreateNode("node-B");
        var runA = await ClaimOccurrence(a);
        await a.Store.ReleaseDeadNodeOccurrenceResources("node-A", CancellationToken.None);
        Assert.Equal(TickerStatus.Idle, (await OccurrenceRow(runA.TickerId)).Status);
        var claimed = await b.Store.AcquireImmediateCronOccurrencesAsync([runA.TickerId], CancellationToken.None);
        var runB = new InternalFunctionContext { TickerId = runA.TickerId, Type = TickerType.CronTickerOccurrence, RunMarker = Assert.Single(claimed).UpdatedAt };
        Assert.True(await b.Manager.UpdateTickerAsync(Outcome(runB, TickerStatus.Done, T0.AddMinutes(1))));

        var stored = await a.Manager.UpdateTickerAsync(Outcome(runA, TickerStatus.Failed, T0.AddMinutes(2)));

        Assert.False(stored);
        var row = await OccurrenceRow(runA.TickerId);
        Assert.Equal(TickerStatus.Done, row.Status);
        Assert.Equal("node-B", row.LockHolder);
        Assert.Equal(T0.AddMinutes(1), row.ExecutedAt);
    }

    // T2: the same node claimed the row again after it was released. The first run's late write must not land on the second.
    [Fact]
    public async Task ALateWriteOfAnEarlierRunOfTheSameNode_ChangesNothing()
    {
        var a = CreateNode("node-A");
        var ticker = await SeedTimeTicker(T0);
        var first = await ClaimTimeTicker(a, ticker.Id);
        await a.Store.ReleaseDeadNodeTimeTickerResources("node-A", CancellationToken.None);
        var second = await ClaimTimeTicker(a, ticker.Id);

        var stored = await a.Manager.UpdateTickerAsync(Outcome(first, TickerStatus.Done, T0.AddMinutes(1)));

        Assert.False(stored);
        var row = await Row(ticker.Id);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal(second.ClaimMarker, row.UpdatedAt);
    }

    // T4: a final write sent twice (it landed, but the caller could not tell) is stored again. The second write changes no
    // value, so this needs the matched count, not the modified count.
    [Fact]
    public async Task AFinalWriteSentAgain_IsStoredAgain()
    {
        var a = CreateNode("node-A");
        var ticker = await SeedTimeTicker(T0);
        var run = await ClaimTimeTicker(a, ticker.Id);
        Assert.True(await a.Manager.UpdateTickerAsync(Outcome(run, TickerStatus.Done, T0.AddMinutes(1))));

        Assert.True(await a.Manager.UpdateTickerAsync(Outcome(run, TickerStatus.Done, T0.AddMinutes(1))));
        Assert.Equal(TickerStatus.Done, (await Row(ticker.Id)).Status);
    }

    // T4 for a cron occurrence.
    [Fact]
    public async Task AFinalCronWriteSentAgain_IsStoredAgain()
    {
        var a = CreateNode("node-A");
        var run = await ClaimOccurrence(a);
        Assert.True(await a.Manager.UpdateTickerAsync(Outcome(run, TickerStatus.Done, T0.AddMinutes(1))));

        Assert.True(await a.Manager.UpdateTickerAsync(Outcome(run, TickerStatus.Done, T0.AddMinutes(1))));
        Assert.Equal(TickerStatus.Done, (await OccurrenceRow(run.TickerId)).Status);
    }

    // During a run every write keeps the claim marker in UpdatedAt, so the next write of the run still holds the row.
    [Fact]
    public async Task TheWritesOfARun_KeepItsMarker_SoEachNextWriteStillLands()
    {
        var a = CreateNode("node-A");
        var ticker = await SeedTimeTicker(T0);
        var run = await ClaimTimeTicker(a, ticker.Id);
        _clock.UtcNow = T0.AddMinutes(5);

        Assert.True(await a.Manager.UpdateTickerAsync(run.ResetUpdateProps().SetProperty(x => x.RetryCount, 1)));
        Assert.True(await a.Manager.UpdateTickerAsync(Outcome(run, TickerStatus.Done, T0.AddMinutes(6))));

        var row = await Row(ticker.Id);
        Assert.Equal(TickerStatus.Done, row.Status);
        Assert.Equal(1, row.RetryCount);
        Assert.Equal(run.ClaimMarker, row.UpdatedAt);
    }

    // T3: an inline child started by two runs of its parent (node A's run was released and node B ran the parent again).
    // The child's row belongs to the last start: A's late write changes nothing.
    [Fact]
    public async Task ALateWriteOfAnInlineChildThatAnotherRunStartedAgain_ChangesNothing()
    {
        var a = CreateNode("node-A");
        var b = CreateNode("node-B");
        var child = InlineChild();
        await SeedTimeTicker(T0, child);
        var childOfA = new InternalFunctionContext { TickerId = child.Id, Type = TickerType.TimeTicker };
        var childOfB = new InternalFunctionContext { TickerId = child.Id, Type = TickerType.TimeTicker };
        await a.Manager.RecordChildStartedAsync(childOfA);
        await b.Manager.RecordChildStartedAsync(childOfB);
        Assert.NotNull(childOfA.RunMarker);

        var stored = await a.Manager.UpdateTickerAsync(Outcome(childOfA, TickerStatus.Done, T0.AddMinutes(1)));

        Assert.False(stored);
        var row = await Row(child.Id);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("node-B", row.LockHolder);
        Assert.Equal(childOfB.RunMarker, row.UpdatedAt);
    }

    // T9: an inline child carries this node's lock while it runs, so the release of the node's rows frees it (this store
    // releases a running row of the node to Idle) instead of leaving it InProgress for good. It has no time of its own, so
    // no claim path hands it out again.
    [Fact]
    public async Task AnInlineChildLeftByAStoppedNode_IsFreed_AndNeverHandedOutAgain()
    {
        var a = CreateNode("node-A");
        var child = InlineChild();
        await SeedTimeTicker(T0.AddHours(1), child);
        await a.Manager.RecordChildStartedAsync(new InternalFunctionContext { TickerId = child.Id, Type = TickerType.TimeTicker });
        Assert.Equal("node-A", (await Row(child.Id)).LockHolder);

        await a.Store.ReleaseDeadNodeTimeTickerResources("node-A", CancellationToken.None);

        var row = await Row(child.Id);
        Assert.Equal(TickerStatus.Idle, row.Status);
        Assert.Null(row.LockHolder);
        var b = CreateNode("node-B");
        _clock.UtcNow = T0.AddHours(2);
        Assert.DoesNotContain(await b.Store.GetEarliestTimeTickers(CancellationToken.None), x => x.Id == child.Id);
        Assert.DoesNotContain(await Drain(b.Store.QueueTimedOutTimeTickers(CancellationToken.None)), x => x.Id == child.Id);
        Assert.Equal(TickerStatus.Idle, (await Row(child.Id)).Status);
    }

    // T10: an edit of a running job (a full-entity update, as UpdateAsync and the dashboard send it, with no run state)
    // writes its fields but keeps the run, so the run's final write still lands and the job is not run again.
    [Fact]
    public async Task EditingARunningJob_KeepsItsRun_AndItsOutcomeIsStillStored()
    {
        var a = CreateNode("node-A");
        var ticker = await SeedTimeTicker(T0);
        var run = await ClaimTimeTicker(a, ticker.Id);
        var edit = new TimeTickerEntity
        {
            Id = ticker.Id, Function = ticker.Function, ExecutionTime = T0.AddHours(3), Description = "edited",
            CreatedAt = ticker.CreatedAt, Request = []
        };

        await a.Store.UpdateTimeTickers([edit], CancellationToken.None);

        var row = await Row(ticker.Id);
        Assert.Equal("edited", row.Description);
        Assert.Equal(T0.AddHours(3), row.ExecutionTime);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("node-A", row.LockHolder);
        Assert.Equal(run.ClaimMarker, row.UpdatedAt);
        Assert.True(await a.Manager.UpdateTickerAsync(Outcome(run, TickerStatus.Done, T0.AddMinutes(1))));
        Assert.Equal(TickerStatus.Done, (await Row(ticker.Id)).Status);
    }

    // T10 for a running inline chain child, edited with its parent.
    [Fact]
    public async Task EditingAChainWhileItsInlineChildRuns_KeepsTheChildsRun()
    {
        var a = CreateNode("node-A");
        var child = InlineChild();
        var parent = await SeedTimeTicker(T0, child);
        var childRun = new InternalFunctionContext { TickerId = child.Id, Type = TickerType.TimeTicker };
        await a.Manager.RecordChildStartedAsync(childRun);
        var edit = new TimeTickerEntity
        {
            Id = parent.Id, Function = parent.Function, ExecutionTime = T0, Description = "edited", CreatedAt = parent.CreatedAt, Request = [],
            Children = [new TimeTickerEntity { Id = child.Id, Function = child.Function, Description = "child edited", RunCondition = RunCondition.OnSuccess, Request = [] }]
        };

        await a.Store.UpdateTimeTickers([edit], CancellationToken.None);

        var row = await Row(child.Id);
        Assert.Equal("child edited", row.Description);
        Assert.Equal(parent.Id, row.ParentId);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("node-A", row.LockHolder);
        Assert.True(await a.Manager.UpdateTickerAsync(Outcome(childRun, TickerStatus.Done, T0.AddMinutes(1))));
        Assert.Equal(TickerStatus.Done, (await Row(child.Id)).Status);
    }

    // A body read before the job ran (a dialog opened before, saved after) must not write the ended job back as not started:
    // the row stays Done and is not handed out again.
    [Fact]
    public async Task AnEditWithABodyReadBeforeTheJobRan_DoesNotRunItAgain()
    {
        var a = CreateNode("node-A");
        var ticker = await SeedTimeTicker(T0);
        var readBefore = await Row(ticker.Id);
        var run = await ClaimTimeTicker(a, ticker.Id);
        Assert.True(await a.Manager.UpdateTickerAsync(Outcome(run, TickerStatus.Done, T0.AddMinutes(1))));
        readBefore.Description = "edited";

        await a.Store.UpdateTimeTickers([readBefore], CancellationToken.None);

        var row = await Row(ticker.Id);
        Assert.Equal("edited", row.Description);
        Assert.Equal(TickerStatus.Done, row.Status);
        Assert.DoesNotContain(await a.Store.GetEarliestTimeTickers(CancellationToken.None), x => x.Id == ticker.Id);
    }

    // T16: a body read while the job ran and sent after it ended must not bring the run back: the row stays Done.
    [Fact]
    public async Task AnEditWithABodyReadWhileTheJobRan_DoesNotBringTheRunBack()
    {
        var a = CreateNode("node-A");
        var ticker = await SeedTimeTicker(T0);
        var run = await ClaimTimeTicker(a, ticker.Id);
        var readWhileRunning = await Row(ticker.Id);
        Assert.Equal(TickerStatus.InProgress, readWhileRunning.Status);
        Assert.True(await a.Manager.UpdateTickerAsync(Outcome(run, TickerStatus.Done, T0.AddMinutes(1))));
        readWhileRunning.Description = "edited";
        readWhileRunning.LockHolder = "stale-node";

        await a.Store.UpdateTimeTickers([readWhileRunning], CancellationToken.None);

        var row = await Row(ticker.Id);
        Assert.Equal("edited", row.Description);
        Assert.Equal(TickerStatus.Done, row.Status);
        Assert.Equal("node-A", row.LockHolder);
    }

    // R1 when there is no row: an upsert of a body that shows a run creates the row Idle, with no lock.
    [Fact]
    public async Task AnEditThatCreatesItsRow_NeverCreatesItRunning()
    {
        var a = CreateNode("node-A");
        var body = new TimeTickerEntity
        {
            Id = Guid.NewGuid(), Function = "fenced-fn", ExecutionTime = T0, Status = TickerStatus.InProgress,
            LockHolder = "stale-node", LockedAt = T0, CreatedAt = T0, UpdatedAt = T0, Request = []
        };

        Assert.Equal(1, await a.Store.UpdateTimeTickers([body], CancellationToken.None));

        var row = await Row(body.Id);
        Assert.Equal(TickerStatus.Idle, row.Status);
        Assert.Null(row.LockHolder);
        Assert.Null(row.LockedAt);
        Assert.Equal(TickerStatus.InProgress, body.Status); // the caller's body is not changed
    }

    // T8 (store side): the write a cron edit makes to move the planned occurrence lands only on one that has not started; a
    // running occurrence keeps its run, and its outcome is still stored.
    [Fact]
    public async Task MovingAPlannedOccurrence_LeavesARunningOneAlone()
    {
        var a = CreateNode("node-A");
        var run = await ClaimOccurrence(a);
        var move = new InternalFunctionContext { TickerId = run.TickerId, Type = TickerType.CronTickerOccurrence }
            .SetProperty(x => x.ExecutionTime, T0.AddHours(1));

        var moved = await ((IFencedRunWriter)a.Store).UpdateNotStartedCronOccurrenceAsync(move);

        Assert.Equal(0, moved);
        var row = await OccurrenceRow(run.TickerId);
        Assert.Equal(T0, row.ExecutionTime);
        Assert.Equal(run.ClaimMarker, row.UpdatedAt);
        Assert.True(await a.Manager.UpdateTickerAsync(Outcome(run, TickerStatus.Done, T0.AddMinutes(1))));
    }

    // The same write moves an occurrence that has not started.
    [Fact]
    public async Task MovingAPlannedOccurrence_MovesOneThatHasNotStarted()
    {
        var a = CreateNode("node-A");
        var slot = (T0, new[] { new InternalManagerContext(Guid.NewGuid()) { FunctionName = "fenced-cron-fn", Expression = "* * * * *" } });
        var id = Assert.Single(await Drain(a.Store.QueueCronTickerOccurrences(slot, CancellationToken.None))).Id;
        var move = new InternalFunctionContext { TickerId = id, Type = TickerType.CronTickerOccurrence }
            .SetProperty(x => x.ExecutionTime, T0.AddHours(1));

        Assert.Equal(1, await ((IFencedRunWriter)a.Store).UpdateNotStartedCronOccurrenceAsync(move));
        Assert.Equal(T0.AddHours(1), (await OccurrenceRow(id)).ExecutionTime);
    }
}
