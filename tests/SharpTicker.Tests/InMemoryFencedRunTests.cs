using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SharpTicker.Provider;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Managers;
using SharpTicker.Utilities.Models;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// The fenced writes of a run (#74): a run writes only to the row it still holds, its claim marker in UpdatedAt and this
/// node's lock. Two provider instances (separate NodeIdentifier) over the one process-wide in-memory store, each with the
/// real internal manager, the way two nodes run.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class InMemoryFencedRunTests : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FakeClock(DateTime start) : ITickerClock
    {
        public DateTime UtcNow { get; set; } = start;
    }

    private sealed record Node(TickerInMemoryPersistenceProvider<TimeTickerEntity, CronTickerEntity> Store, IInternalTickerManager Manager);

    private readonly FakeClock _clock = new(T0);
    private readonly List<Guid> _timeTickerIds = [];
    private readonly List<Guid> _occurrenceIds = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var node = CreateNode("cleanup");
        await node.Store.RemoveTimeTickers(_timeTickerIds.ToArray(), CancellationToken.None);
        await node.Store.RemoveCronTickerOccurrences(_occurrenceIds.ToArray(), CancellationToken.None);
    }

    private Node CreateNode(string nodeIdentifier, ISharpTickerNotificationHubSender? hub = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITickerClock>(_clock);
        services.AddSingleton(new SchedulerOptionsBuilder { NodeIdentifier = nodeIdentifier });
        var store = new TickerInMemoryPersistenceProvider<TimeTickerEntity, CronTickerEntity>(services.BuildServiceProvider());
        var manager = new InternalTickerManager<TimeTickerEntity, CronTickerEntity>(store, _clock, hub ?? Substitute.For<ISharpTickerNotificationHubSender>());
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
        _timeTickerIds.Add(ticker.Id);
        _timeTickerIds.AddRange(children.Select(c => c.Id));
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

    private static async Task<InternalFunctionContext> ClaimOccurrence(Node node, List<Guid> occurrenceIds)
    {
        var slot = (T0, new[] { new InternalManagerContext(Guid.NewGuid()) { FunctionName = "fenced-cron-fn", Expression = "* * * * *" } });
        var id = Assert.Single(await Drain(node.Store.QueueCronTickerOccurrences(slot, CancellationToken.None))).Id;
        occurrenceIds.Add(id);
        var context = new InternalFunctionContext { TickerId = id, FunctionName = "fenced-cron-fn", Type = TickerType.CronTickerOccurrence };
        Assert.True(await node.Manager.TryClaimDueTicker(context, node.Manager.NextClaimMarker(), CancellationToken.None));
        context.RunMarker = context.ClaimMarker;
        return context;
    }

    // Frees the row of a run back to Idle, as EF Core's startup release does with the rows of a node's earlier run, so it can
    // be claimed again. (This store's own release of a dead node's rows skips the running ones instead.)
    private static async Task ReleaseToIdle(Node node, InternalFunctionContext run)
        => Assert.True(await node.Store.TryReleaseTimeTickerClaim(run.TickerId, run.ClaimMarker!.Value, CancellationToken.None));

    private static InternalFunctionContext Outcome(InternalFunctionContext run, TickerStatus status, DateTime executedAt)
    {
        run.ResetUpdateProps()
            .SetProperty(x => x.Status, status)
            .SetProperty(x => x.ExecutedAt, executedAt);
        return run;
    }

    private Task<TimeTickerEntity> Row(Guid id) => CreateNode("reader").Store.GetTimeTickerById(id, CancellationToken.None);

    private async Task<CronTickerOccurrenceEntity<CronTickerEntity>> OccurrenceRow(Guid id)
        => (await CreateNode("reader").Store.GetAllCronTickerOccurrences(x => x.Id == id, CancellationToken.None)).Single();

    // T6: the scheduler's due claim hands the run the marker it wrote, as the other claim paths do, so the run is fenced.
    [Fact]
    public async Task TheDueClaim_GivesTheRunTheMarkerItWrote_ForATimeTickerAndACronOccurrence()
    {
        var node = CreateNode("node-A");
        var ticker = await SeedTimeTicker(T0);

        var timeRun = await ClaimTimeTicker(node, ticker.Id);
        var cronRun = await ClaimOccurrence(node, _occurrenceIds);

        Assert.Equal((await Row(ticker.Id)).UpdatedAt, timeRun.ClaimMarker);
        Assert.Equal((await OccurrenceRow(cronRun.TickerId)).UpdatedAt, cronRun.ClaimMarker);
    }

    // T1: node A's run lost its row (the row was released, here as for a dead node) and node B claimed and runs it. A's late
    // write must not overwrite B's run.
    [Fact]
    public async Task ALateWriteOfARunWhoseRowAnotherNodeTook_ChangesNothing()
    {
        var a = CreateNode("node-A");
        var b = CreateNode("node-B");
        var ticker = await SeedTimeTicker(T0);
        var runA = await ClaimTimeTicker(a, ticker.Id);
        await ReleaseToIdle(a, runA);
        var runB = await ClaimTimeTicker(b, ticker.Id);
        Assert.True(await b.Manager.UpdateTickerAsync(Outcome(runB, TickerStatus.Done, T0.AddMinutes(1))));

        var stored = await a.Manager.UpdateTickerAsync(Outcome(runA, TickerStatus.Failed, T0.AddMinutes(2)));

        Assert.False(stored);
        var row = await Row(ticker.Id);
        Assert.Equal(TickerStatus.Done, row.Status);
        Assert.Equal("node-B", row.LockHolder);
        Assert.Equal(T0.AddMinutes(1), row.ExecutedAt);
    }

    // The same for a cron occurrence.
    [Fact]
    public async Task ALateWriteOfACronRunWhoseRowAnotherNodeTook_ChangesNothing()
    {
        var a = CreateNode("node-A");
        var b = CreateNode("node-B");
        var runA = await ClaimOccurrence(a, _occurrenceIds);
        Assert.True(await a.Store.TryReleaseCronOccurrenceClaim(runA.TickerId, runA.ClaimMarker!.Value, CancellationToken.None));
        var occurrence = await OccurrenceRow(runA.TickerId);
        Assert.Equal(TickerStatus.Idle, occurrence.Status);
        var runB = new InternalFunctionContext { TickerId = runA.TickerId, Type = TickerType.CronTickerOccurrence };
        var claimed = await b.Store.AcquireImmediateCronOccurrencesAsync([runA.TickerId], CancellationToken.None);
        runB.RunMarker = Assert.Single(claimed).UpdatedAt;
        Assert.True(await b.Manager.UpdateTickerAsync(Outcome(runB, TickerStatus.Done, T0.AddMinutes(1))));

        var stored = await a.Manager.UpdateTickerAsync(Outcome(runA, TickerStatus.Failed, T0.AddMinutes(2)));

        Assert.False(stored);
        var row = await OccurrenceRow(runA.TickerId);
        Assert.Equal(TickerStatus.Done, row.Status);
        Assert.Equal("node-B", row.LockHolder);
    }

    // T2: the same node claimed the row again after it was released. The first run's late write must not land on the second.
    [Fact]
    public async Task ALateWriteOfAnEarlierRunOfTheSameNode_ChangesNothing()
    {
        var a = CreateNode("node-A");
        var ticker = await SeedTimeTicker(T0);
        var first = await ClaimTimeTicker(a, ticker.Id);
        await ReleaseToIdle(a, first);
        var second = await ClaimTimeTicker(a, ticker.Id);

        var stored = await a.Manager.UpdateTickerAsync(Outcome(first, TickerStatus.Done, T0.AddMinutes(1)));

        Assert.False(stored);
        var row = await Row(ticker.Id);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal(second.ClaimMarker, row.UpdatedAt);
    }

    // T13: the stop records a job that did not end in time as Cancelled and frees its lock. A write the run sends after
    // that changes nothing: the row stays Cancelled with no lock, so it is neither stuck nor taken back by the old run.
    [Fact]
    public async Task ALateWriteAfterTheStopRecordedTheJobAsCancelled_ChangesNothing()
    {
        var a = CreateNode("node-A");
        var ticker = await SeedTimeTicker(T0);
        var run = await ClaimTimeTicker(a, ticker.Id);
        var stop = new InternalFunctionContext { TickerId = ticker.Id, Type = TickerType.TimeTicker }
            .SetProperty(x => x.Status, TickerStatus.Cancelled)
            .SetProperty(x => x.ReleaseLock, true);
        Assert.True(await a.Manager.UpdateTickerAsync(stop));

        var stored = await a.Manager.UpdateTickerAsync(Outcome(run, TickerStatus.Done, T0.AddMinutes(1)));

        Assert.False(stored);
        var row = await Row(ticker.Id);
        Assert.Equal(TickerStatus.Cancelled, row.Status);
        Assert.Null(row.LockHolder);
    }

    // T4 (guard): a final write sent twice (it landed, but the caller could not tell) lands again on the same row.
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
    }

    // T9: an inline child carries this node's lock while it runs, so the release of a dead node's rows ends it (this store
    // records a running row of a dead node as Skipped) instead of leaving it InProgress for good; it has no time of its own,
    // so no claim path hands it out again.
    [Fact]
    public async Task AnInlineChildLeftByAStoppedNode_IsFreed_AndNeverHandedOutAgain()
    {
        var a = CreateNode("node-A");
        var child = InlineChild();
        await SeedTimeTicker(T0.AddHours(1), child);
        await a.Manager.RecordChildStartedAsync(new InternalFunctionContext { TickerId = child.Id, Type = TickerType.TimeTicker });

        await a.Store.ReleaseDeadNodeTimeTickerResources("node-A", CancellationToken.None);

        Assert.Equal(TickerStatus.Skipped, (await Row(child.Id)).Status);
        var b = CreateNode("node-B");
        Assert.DoesNotContain(await b.Store.GetEarliestTimeTickers(CancellationToken.None), x => x.Id == child.Id);
        Assert.DoesNotContain(await Drain(b.Store.QueueTimedOutTimeTickers(CancellationToken.None)), x => x.Id == child.Id);
    }

    // T10: an edit of a running job (a full-entity update, as UpdateAsync and the dashboard send it, with no run state)
    // writes its fields but keeps the run, so the run's final write still lands and the job does not run again.
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
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("node-A", row.LockHolder);
        Assert.True(await a.Manager.UpdateTickerAsync(Outcome(run, TickerStatus.Done, T0.AddMinutes(1))));
        Assert.Equal(TickerStatus.Done, (await Row(ticker.Id)).Status);
    }

    // The same for a running inline chain child, edited with its parent.
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
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.True(await a.Manager.UpdateTickerAsync(Outcome(childRun, TickerStatus.Done, T0.AddMinutes(1))));
    }

    // The dashboard's edit form sends a child on its own, with no ParentId and no RunCondition. The link is the store's: the
    // child stays in its chain and keeps its condition, whether it has started or not.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditingAChildWithABodyWithoutItsLink_KeepsItInItsChain(bool running)
    {
        var a = CreateNode("node-A");
        var child = InlineChild();
        var parent = await SeedTimeTicker(T0, child);
        var condition = (await Row(child.Id)).RunCondition;
        Assert.NotNull(condition);
        if (running)
            await a.Manager.RecordChildStartedAsync(new InternalFunctionContext { TickerId = child.Id, Type = TickerType.TimeTicker });
        var edit = new TimeTickerEntity { Id = child.Id, Function = child.Function, Description = "child edited", ExecutionTime = T0.AddHours(1), Request = [] };

        await a.Store.UpdateTimeTickers([edit], CancellationToken.None);

        var row = await Row(child.Id);
        Assert.Equal("child edited", row.Description);
        Assert.Equal(parent.Id, row.ParentId);
        Assert.Equal(condition, row.RunCondition);
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
        Assert.True(await a.Manager.UpdateTickerAsync(Outcome(run, TickerStatus.Done, T0.AddMinutes(1))));
        readWhileRunning.Description = "edited";

        await a.Store.UpdateTimeTickers([readWhileRunning], CancellationToken.None);

        var row = await Row(ticker.Id);
        Assert.Equal("edited", row.Description);
        Assert.Equal(TickerStatus.Done, row.Status);
    }

    // T8 (store side): the write a cron edit makes to move the planned occurrence lands only on one that has not started; a
    // running occurrence keeps its run, and its outcome is still stored.
    [Fact]
    public async Task MovingAPlannedOccurrence_LeavesARunningOneAlone()
    {
        var a = CreateNode("node-A");
        var run = await ClaimOccurrence(a, _occurrenceIds);
        var move = new InternalFunctionContext { TickerId = run.TickerId, Type = TickerType.CronTickerOccurrence }
            .SetProperty(x => x.ExecutionTime, T0.AddHours(1));

        var moved = await ((IFencedRunWriter)a.Store).UpdateNotStartedCronOccurrenceAsync(move);

        Assert.Equal(0, moved);
        Assert.Equal(run.ClaimMarker, (await OccurrenceRow(run.TickerId)).UpdatedAt);
        Assert.True(await a.Manager.UpdateTickerAsync(Outcome(run, TickerStatus.Done, T0.AddMinutes(1))));
    }

    // PR-C: a lost outcome is written again from a copy of the run's context. The copy must write the row exactly as the run's
    // own context does.
    [Fact]
    public async Task TheCopyOfAnOutcome_WritesTheSameRowAsTheRunsOwnContext()
    {
        var a = CreateNode("node-A");
        var first = await SeedTimeTicker(T0);
        var second = await SeedTimeTicker(T0);
        var original = FailedOutcome(await ClaimTimeTicker(a, first.Id));
        var copied = FailedOutcome(await ClaimTimeTicker(a, second.Id)).CopyForOutcomeRetry();

        Assert.True(await a.Manager.UpdateTickerAsync(original));
        Assert.True(await a.Manager.UpdateTickerAsync(copied));

        var fromOriginal = await Row(first.Id);
        var fromCopy = await Row(second.Id);
        Assert.Equal(TickerStatus.Failed, fromCopy.Status);
        Assert.Equal(original.RunMarker, fromOriginal.UpdatedAt);
        Assert.Equal(copied.RunMarker, fromCopy.UpdatedAt);
        Assert.Equal(
            (fromOriginal.Status, fromOriginal.ExecutedAt, fromOriginal.ElapsedTime, fromOriginal.ExceptionMessage, fromOriginal.RetryCount, fromOriginal.LockHolder),
            (fromCopy.Status, fromCopy.ExecutedAt, fromCopy.ElapsedTime, fromCopy.ExceptionMessage, fromCopy.RetryCount, fromCopy.LockHolder));

        static InternalFunctionContext FailedOutcome(InternalFunctionContext run)
            => run.ResetUpdateProps()
                .SetProperty(x => x.Status, TickerStatus.Failed)
                .SetProperty(x => x.ExecutedAt, T0.AddMinutes(3))
                .SetProperty(x => x.ElapsedTime, 1234L)
                .SetProperty(x => x.ExceptionDetails, "boom")
                .SetProperty(x => x.RetryCount, 2);
    }

    // PR-C: the stop's Cancelled write (onlyWhileRunning) lands only on a row that is still running, not over an outcome the
    // run stored with the same marker (an outcome write keeps the lock and the marker, so the fence alone still holds).
    [Fact]
    public async Task OnlyWhileRunning_LeavesAStoredOutcomeAlone_AndWritesARunningRow()
    {
        var a = CreateNode("node-A");
        var done = await ClaimTimeTicker(a, (await SeedTimeTicker(T0)).Id);
        Assert.True(await a.Manager.UpdateTickerAsync(Outcome(done, TickerStatus.Done, T0.AddMinutes(1))));
        var doneOccurrence = await ClaimOccurrence(a, _occurrenceIds);
        Assert.True(await a.Manager.UpdateTickerAsync(Outcome(doneOccurrence, TickerStatus.Done, T0.AddMinutes(1))));
        var running = await ClaimTimeTicker(a, (await SeedTimeTicker(T0)).Id);
        var writer = (IFencedRunWriter)a.Store;

        Assert.Equal(0, await writer.UpdateTimeTickerFencedAsync(Stopped(done), done.RunMarker!.Value, onlyWhileRunning: true));
        Assert.Equal(0, await writer.UpdateCronOccurrenceFencedAsync(Stopped(doneOccurrence), doneOccurrence.RunMarker!.Value, onlyWhileRunning: true));
        Assert.Equal(1, await writer.UpdateTimeTickerFencedAsync(Stopped(running), running.RunMarker!.Value, onlyWhileRunning: true));

        Assert.Equal(TickerStatus.Done, (await Row(done.TickerId)).Status);
        Assert.Equal(TickerStatus.Done, (await OccurrenceRow(doneOccurrence.TickerId)).Status);
        var stopped = await Row(running.TickerId);
        Assert.Equal(TickerStatus.Cancelled, stopped.Status);
        Assert.Null(stopped.LockHolder);
    }

    // PR-C: the manager writes a lost outcome again fenced, and reports a row that is no longer this run's as not stored.
    [Fact]
    public async Task StoreLostOutcome_WritesFenced_AndReportsARowThatIsNoLongerThisRun()
    {
        var a = CreateNode("node-A");
        var kept = await ClaimTimeTicker(a, (await SeedTimeTicker(T0)).Id);
        var released = await ClaimTimeTicker(a, (await SeedTimeTicker(T0)).Id);
        await ReleaseToIdle(a, released);

        Assert.True(a.Manager.FencesRuns);
        Assert.True(await a.Manager.StoreLostOutcomeAsync(Outcome(kept, TickerStatus.Done, T0.AddMinutes(1))));
        Assert.False(await a.Manager.StoreLostOutcomeAsync(Outcome(released, TickerStatus.Done, T0.AddMinutes(1))));

        Assert.Equal(TickerStatus.Done, (await Row(kept.TickerId)).Status);
        Assert.Equal(TickerStatus.Idle, (await Row(released.TickerId)).Status);
    }

    // PR-C: an outcome is never written again by Id: without a marker, or on a store that does not fence, the manager refuses,
    // as such a write could land on a later run of the row.
    [Fact]
    public async Task StoreLostOutcome_RefusesAWriteThatIsNotFenced()
    {
        var run = new InternalFunctionContext { TickerId = Guid.NewGuid(), FunctionName = "fenced-fn", Type = TickerType.TimeTicker }
            .SetProperty(x => x.Status, TickerStatus.Done);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateNode("node-A").Manager.StoreLostOutcomeAsync(run));

        var store = Substitute.For<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();
        var unfenced = new InternalTickerManager<TimeTickerEntity, CronTickerEntity>(store, _clock, Substitute.For<ISharpTickerNotificationHubSender>());
        run.RunMarker = T0;

        Assert.False(unfenced.FencesRuns);
        await Assert.ThrowsAsync<InvalidOperationException>(() => unfenced.StoreLostOutcomeAsync(run));
        await store.DidNotReceiveWithAnyArgs().UpdateTimeTicker(default!, default);
    }

    // PR-C: a stored outcome whose dashboard notification fails is still reported as stored, or it would be written again forever.
    [Fact]
    public async Task StoreLostOutcome_ReportsTheOutcomeAsStored_WhenTheNotificationFails()
    {
        var hub = Substitute.For<ISharpTickerNotificationHubSender>();
        hub.UpdateTimeTickerFromInternalFunctionContext<TimeTickerEntity>(Arg.Any<InternalFunctionContext>())
            .Returns(Task.FromException(new InvalidOperationException("hub down")));
        var a = CreateNode("node-A", hub);
        var run = await ClaimTimeTicker(a, (await SeedTimeTicker(T0)).Id);

        Assert.True(await a.Manager.StoreLostOutcomeAsync(Outcome(run, TickerStatus.Done, T0.AddMinutes(1))));

        Assert.Equal(TickerStatus.Done, (await Row(run.TickerId)).Status);
    }

    // PR-C: a stop's Cancelled write that landed is reported as written even when the notification fails, so the stop does not
    // log it as not recorded.
    [Fact]
    public async Task RecordStoppedRun_ReportsTheWriteAsWritten_WhenTheNotificationFails()
    {
        var hub = Substitute.For<ISharpTickerNotificationHubSender>();
        hub.UpdateTimeTickerFromInternalFunctionContext<TimeTickerEntity>(Arg.Any<InternalFunctionContext>())
            .Returns(Task.FromException(new InvalidOperationException("hub down")));
        var a = CreateNode("node-A", hub);
        var run = await ClaimTimeTicker(a, (await SeedTimeTicker(T0)).Id);
        var stopped = Stopped(run);
        stopped.RunMarker = run.RunMarker;

        Assert.True(await a.Manager.RecordStoppedRunAsync(stopped));

        Assert.Equal(TickerStatus.Cancelled, (await Row(run.TickerId)).Status);
    }

    // PR-C: the stop's write lands on the running row of this run, not on a row whose outcome the run stored.
    [Fact]
    public async Task RecordStoppedRun_WritesARunningRow_AndLeavesAStoredOutcomeAlone()
    {
        var a = CreateNode("node-A");
        var done = await ClaimTimeTicker(a, (await SeedTimeTicker(T0)).Id);
        Assert.True(await a.Manager.UpdateTickerAsync(Outcome(done, TickerStatus.Done, T0.AddMinutes(1))));
        var running = await ClaimTimeTicker(a, (await SeedTimeTicker(T0)).Id);

        Assert.False(await a.Manager.RecordStoppedRunAsync(StoppedWithMarker(done)));
        Assert.True(await a.Manager.RecordStoppedRunAsync(StoppedWithMarker(running)));

        Assert.Equal(TickerStatus.Done, (await Row(done.TickerId)).Status);
        var stopped = await Row(running.TickerId);
        Assert.Equal(TickerStatus.Cancelled, stopped.Status);
        Assert.Null(stopped.LockHolder);

        static InternalFunctionContext StoppedWithMarker(InternalFunctionContext run)
        {
            var stopped = Stopped(run);
            stopped.RunMarker = run.RunMarker;
            return stopped;
        }
    }

    private static InternalFunctionContext Stopped(InternalFunctionContext run)
        => new InternalFunctionContext { TickerId = run.TickerId, Type = run.Type }
            .SetProperty(x => x.Status, TickerStatus.Cancelled)
            .SetProperty(x => x.ReleaseLock, true);
}
