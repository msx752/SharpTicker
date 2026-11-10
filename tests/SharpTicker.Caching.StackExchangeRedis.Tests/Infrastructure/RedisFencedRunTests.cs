using NSubstitute;
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
/// The fenced writes of a run (#74) on Redis: a run writes only to the row it still holds, its claim marker in UpdatedAt and
/// this node's lock. Two provider instances (separate NodeIdentifier) over one real Redis, each with the real internal
/// manager, the way two nodes run. A node's rows are released the way the heartbeat does it (ReleaseDeadNode...Resources).
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class RedisFencedRunTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    // A tick count that does not end in 0, so the stored JSON text equals DateTime.ToString("O") and the compare-and-set in
    // QueueTimeTickers can match (see RedisClaimMultiNodeTests).
    private static readonly DateTime SeedUpdatedAt = T0.AddHours(-1).AddTicks(1234567);

    private readonly FakeTickerClock _clock = new(T0);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Node(TickerRedisPersistenceProvider<TimeTickerEntity, CronTickerEntity> Store, IInternalTickerManager Manager)
    {
        public ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> Provider => Store;
    }

    private Node CreateNode(string nodeIdentifier)
    {
        var store = fixture.CreateProvider(nodeIdentifier, _clock);
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
            UpdatedAt = SeedUpdatedAt,
            Request = [],
            Children = children.ToList()
        };
        await CreateNode("seed").Provider.AddTimeTickers([ticker], CancellationToken.None);
        return ticker;
    }

    private static TimeTickerEntity InlineChild() => new()
    {
        Id = Guid.NewGuid(),
        Function = "fenced-child",
        Status = TickerStatus.Idle,
        RunCondition = RunCondition.OnSuccess,
        CreatedAt = T0.AddHours(-2),
        UpdatedAt = SeedUpdatedAt,
        Request = []
    };

    // Queues the row on the node and claims it the way the scheduler does. Returns the context the run gets.
    private static async Task<InternalFunctionContext> ClaimTimeTicker(Node node, Guid id)
    {
        var window = await node.Provider.GetEarliestTimeTickers(CancellationToken.None);
        Assert.Single(await Drain(node.Provider.QueueTimeTickers(window.Where(x => x.Id == id).ToArray(), CancellationToken.None)));
        var context = new InternalFunctionContext { TickerId = id, FunctionName = "fenced-fn", Type = TickerType.TimeTicker };
        Assert.True(await node.Manager.TryClaimDueTicker(context, node.Manager.NextClaimMarker(), CancellationToken.None));
        context.RunMarker = context.ClaimMarker; // as the handler does when the run starts
        return context;
    }

    private static async Task<InternalFunctionContext> ClaimOccurrence(Node node)
    {
        var slot = (T0, new[] { new InternalManagerContext(Guid.NewGuid()) { FunctionName = "fenced-cron-fn", Expression = "* * * * *" } });
        var id = Assert.Single(await Drain(node.Provider.QueueCronTickerOccurrences(slot, CancellationToken.None))).Id;
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

    private async Task<TimeTickerEntity> Row(Guid id) => (await CreateNode("reader").Provider.GetTimeTickerById(id, CancellationToken.None))!;

    private async Task<CronTickerOccurrenceEntity<CronTickerEntity>> OccurrenceRow(Guid id)
        => (await CreateNode("reader").Provider.GetAllCronTickerOccurrences(x => x.Id == id, CancellationToken.None)).Single();

    // T6: the scheduler's due claim hands the run the marker it wrote, as the other claim paths do, so the run is fenced.
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

    // T1: node A's rows were released (its heartbeat was seen as dead) and node B claimed and ran the row. A's late write
    // must not overwrite B's run.
    [Fact]
    public async Task ALateWriteOfARunWhoseRowAnotherNodeTook_ChangesNothing()
    {
        var a = CreateNode("node-A");
        var b = CreateNode("node-B");
        var ticker = await SeedTimeTicker(T0);
        var runA = await ClaimTimeTicker(a, ticker.Id);
        await b.Provider.ReleaseDeadNodeTimeTickerResources("node-A", CancellationToken.None);
        var runB = await ClaimTimeTicker(b, ticker.Id);
        Assert.True(await b.Manager.UpdateTickerAsync(Outcome(runB, TickerStatus.Done, T0.AddMinutes(1))));

        var stored = await a.Manager.UpdateTickerAsync(Outcome(runA, TickerStatus.Failed, T0.AddMinutes(2)));

        Assert.False(stored);
        var row = await Row(ticker.Id);
        Assert.Equal(TickerStatus.Done, row.Status);
        Assert.Equal("node-B", row.LockHolder);
        Assert.Equal(T0.AddMinutes(1), row.ExecutedAt);
    }

    // The same for a cron occurrence; node B takes the freed occurrence through run-now.
    [Fact]
    public async Task ALateWriteOfACronRunWhoseRowAnotherNodeTook_ChangesNothing()
    {
        var a = CreateNode("node-A");
        var b = CreateNode("node-B");
        var runA = await ClaimOccurrence(a);
        await b.Provider.ReleaseDeadNodeOccurrenceResources("node-A", CancellationToken.None);
        Assert.Equal(TickerStatus.Idle, (await OccurrenceRow(runA.TickerId)).Status);
        var claimed = await b.Provider.AcquireImmediateCronOccurrencesAsync([runA.TickerId], CancellationToken.None);
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
        await a.Provider.ReleaseDeadNodeTimeTickerResources("node-A", CancellationToken.None);
        var second = await ClaimTimeTicker(a, ticker.Id);

        var stored = await a.Manager.UpdateTickerAsync(Outcome(first, TickerStatus.Done, T0.AddMinutes(1)));

        Assert.False(stored);
        var row = await Row(ticker.Id);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal(second.ClaimMarker, row.UpdatedAt);
    }

    // T4: a final write sent twice (it landed, but the caller could not tell) lands again on the same row.
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

    // T4: during a run every write keeps the claim marker in UpdatedAt, so the next write of the run still holds the row.
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

    // T9: an inline child carries this node's lock while it runs, so the heartbeat's release of a dead node's rows frees it
    // to Idle (this store releases running rows too) instead of leaving it InProgress for good; it has no time of its own,
    // so no claim path hands it out again.
    [Fact]
    public async Task AnInlineChildLeftByADeadNode_IsFreed_AndNeverHandedOutAgain()
    {
        var a = CreateNode("node-A");
        var child = InlineChild();
        await SeedTimeTicker(T0.AddHours(1), child);
        await a.Manager.RecordChildStartedAsync(new InternalFunctionContext { TickerId = child.Id, Type = TickerType.TimeTicker });

        var b = CreateNode("node-B");
        await b.Provider.ReleaseDeadNodeTimeTickerResources("node-A", CancellationToken.None);

        var row = await Row(child.Id);
        Assert.Equal(TickerStatus.Idle, row.Status);
        Assert.Null(row.LockHolder);
        _clock.UtcNow = T0.AddHours(2);
        Assert.DoesNotContain(await b.Provider.GetEarliestTimeTickers(CancellationToken.None), x => x.Id == child.Id);
        Assert.DoesNotContain(await Drain(b.Provider.QueueTimedOutTimeTickers(CancellationToken.None)), x => x.Id == child.Id);
        Assert.Empty(await b.Provider.AcquireImmediateTimeTickersAsync([child.Id], CancellationToken.None));
        Assert.Equal(TickerStatus.Idle, (await Row(child.Id)).Status);
    }

    // T10: an edit of a running job (a full-entity update, as UpdateAsync and the dashboard send it, with no run state)
    // writes its fields but keeps the run, so the run's final write still lands and the job does not run again.
    [Fact]
    public async Task EditingARunningJob_KeepsItsRun_AndItsOutcomeIsStillStored()
    {
        var a = CreateNode("node-A");
        var ticker = await SeedTimeTicker(T0);
        var run = await ClaimTimeTicker(a, ticker.Id);
        _clock.UtcNow = T0.AddMinutes(5); // the edit's own time is not the run's marker
        var edit = new TimeTickerEntity
        {
            Id = ticker.Id, Function = ticker.Function, ExecutionTime = T0.AddHours(3), Description = "edited",
            CreatedAt = ticker.CreatedAt, Request = []
        };

        await a.Provider.UpdateTimeTickers([edit], CancellationToken.None);

        var row = await Row(ticker.Id);
        Assert.Equal("edited", row.Description);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("node-A", row.LockHolder);
        Assert.Equal(run.ClaimMarker, row.UpdatedAt);
        Assert.True(await a.Manager.UpdateTickerAsync(Outcome(run, TickerStatus.Done, T0.AddMinutes(1))));
        Assert.Equal(TickerStatus.Done, (await Row(ticker.Id)).Status);
    }

    // T10: the same for a running inline chain child, edited with its parent.
    [Fact]
    public async Task EditingAChainWhileItsInlineChildRuns_KeepsTheChildsRun()
    {
        var a = CreateNode("node-A");
        var child = InlineChild();
        var parent = await SeedTimeTicker(T0, child);
        var childRun = new InternalFunctionContext { TickerId = child.Id, Type = TickerType.TimeTicker };
        await a.Manager.RecordChildStartedAsync(childRun);
        _clock.UtcNow = T0.AddMinutes(5); // the edit's own time is not the run's marker
        var edit = new TimeTickerEntity
        {
            Id = parent.Id, Function = parent.Function, ExecutionTime = T0, Description = "edited", CreatedAt = parent.CreatedAt, Request = [],
            Children = [new TimeTickerEntity { Id = child.Id, Function = child.Function, Description = "child edited", RunCondition = RunCondition.OnSuccess, Request = [] }]
        };

        await a.Provider.UpdateTimeTickers([edit], CancellationToken.None);

        var row = await Row(child.Id);
        Assert.Equal("child edited", row.Description);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal(childRun.RunMarker, row.UpdatedAt);
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

        await a.Provider.UpdateTimeTickers([readBefore], CancellationToken.None);

        var row = await Row(ticker.Id);
        Assert.Equal("edited", row.Description);
        Assert.Equal(TickerStatus.Done, row.Status);
        Assert.DoesNotContain(await a.Provider.GetEarliestTimeTickers(CancellationToken.None), x => x.Id == ticker.Id);
    }

    // T16: a body read while the job ran and sent after it ended must not bring the run back: the row stays Done, with the
    // lock its run left, not the one the body shows.
    [Fact]
    public async Task AnEditWithABodyReadWhileTheJobRan_DoesNotBringTheRunBack()
    {
        var a = CreateNode("node-A");
        var ticker = await SeedTimeTicker(T0);
        var run = await ClaimTimeTicker(a, ticker.Id);
        var readWhileRunning = await Row(ticker.Id);
        Assert.Equal(TickerStatus.InProgress, readWhileRunning.Status);
        Assert.True(await a.Manager.UpdateTickerAsync(Outcome(run, TickerStatus.Done, T0.AddMinutes(1))));
        var ended = await Row(ticker.Id);
        readWhileRunning.Description = "edited";
        readWhileRunning.LockHolder = "node-stale";

        await a.Provider.UpdateTimeTickers([readWhileRunning], CancellationToken.None);

        var row = await Row(ticker.Id);
        Assert.Equal("edited", row.Description);
        Assert.Equal(TickerStatus.Done, row.Status);
        Assert.Equal(ended.LockHolder, row.LockHolder);
        Assert.Equal(ended.LockedAt, row.LockedAt);
    }

    // T8 (store side): the write a cron edit makes to an occurrence lands only on one that has not started; a running
    // occurrence keeps its run, and its outcome is still stored.
    [Fact]
    public async Task TheNotStartedCronWrite_LeavesARunningOccurrenceAlone_AndWritesAPlannedOne()
    {
        var a = CreateNode("node-A");
        var writer = (IFencedRunWriter)(object)a.Store;
        var run = await ClaimOccurrence(a);
        var planned = Assert.Single(await Drain(a.Provider.QueueCronTickerOccurrences(
            (T0.AddMinutes(1), [new InternalManagerContext(Guid.NewGuid()) { FunctionName = "fenced-cron-fn", Expression = "* * * * *" }]),
            CancellationToken.None)));
        InternalFunctionContext Write(Guid id) => new InternalFunctionContext { TickerId = id, Type = TickerType.CronTickerOccurrence }
            .SetProperty(x => x.ExecutionTime, T0.AddHours(1))
            .SetProperty(x => x.RetryCount, 3);
        _clock.UtcNow = T0.AddSeconds(30);

        Assert.Equal(0, await writer.UpdateNotStartedCronOccurrenceAsync(Write(run.TickerId)));
        Assert.Equal(1, await writer.UpdateNotStartedCronOccurrenceAsync(Write(planned.Id)));

        var running = await OccurrenceRow(run.TickerId);
        Assert.Equal(TickerStatus.InProgress, running.Status);
        Assert.Equal(0, running.RetryCount);
        Assert.Equal(run.ClaimMarker, running.UpdatedAt);
        Assert.Equal(3, (await OccurrenceRow(planned.Id)).RetryCount);
        Assert.True(await a.Manager.UpdateTickerAsync(Outcome(run, TickerStatus.Done, T0.AddMinutes(1))));
    }

    // PR-C: the stop's Cancelled write (onlyWhileRunning) lands only on a row that is still running, not over an outcome the
    // run stored with the same marker (an outcome write keeps the lock and the marker, so the fence alone still holds).
    [Fact]
    public async Task OnlyWhileRunning_LeavesAStoredOutcomeAlone_AndWritesARunningRow()
    {
        var a = CreateNode("node-A");
        var done = await ClaimTimeTicker(a, (await SeedTimeTicker(T0)).Id);
        Assert.True(await a.Manager.UpdateTickerAsync(Outcome(done, TickerStatus.Done, T0.AddMinutes(1))));
        var doneOccurrence = await ClaimOccurrence(a);
        Assert.True(await a.Manager.UpdateTickerAsync(Outcome(doneOccurrence, TickerStatus.Done, T0.AddMinutes(1))));
        var running = await ClaimTimeTicker(a, (await SeedTimeTicker(T0)).Id);
        var writer = (IFencedRunWriter)(object)a.Store;

        Assert.Equal(0, await writer.UpdateTimeTickerFencedAsync(Stopped(done), done.RunMarker!.Value, onlyWhileRunning: true));
        Assert.Equal(0, await writer.UpdateCronOccurrenceFencedAsync(Stopped(doneOccurrence), doneOccurrence.RunMarker!.Value, onlyWhileRunning: true));
        Assert.Equal(1, await writer.UpdateTimeTickerFencedAsync(Stopped(running), running.RunMarker!.Value, onlyWhileRunning: true));

        Assert.Equal(TickerStatus.Done, (await Row(done.TickerId)).Status);
        Assert.Equal(TickerStatus.Done, (await OccurrenceRow(doneOccurrence.TickerId)).Status);
        var stopped = await Row(running.TickerId);
        Assert.Equal(TickerStatus.Cancelled, stopped.Status);
        Assert.Null(stopped.LockHolder);
    }

    private static InternalFunctionContext Stopped(InternalFunctionContext run)
        => new InternalFunctionContext { TickerId = run.TickerId, Type = run.Type }
            .SetProperty(x => x.Status, TickerStatus.Cancelled)
            .SetProperty(x => x.ReleaseLock, true);
}
