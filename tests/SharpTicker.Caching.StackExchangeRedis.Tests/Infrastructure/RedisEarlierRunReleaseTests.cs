using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>
/// The release of what an earlier run of a node left locked (the lockedBefore overloads), over a real Redis. Two provider
/// instances with one NodeIdentifier stand for the earlier run and this run of a restarted node. Every call goes through
/// the provider interface, so a default body that ignores lockedBefore would be caught.
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class RedisEarlierRunReleaseTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private const string Node = "node-A";

    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    // A tick count that does not end in 0, so the stored JSON text equals DateTime.ToString("O") and the compare-and-set
    // in QueueTimeTickers can match.
    private static readonly DateTime SeedUpdatedAt = T0.AddHours(-1).AddTicks(1234567);

    private readonly FakeTickerClock _clock = new(T0);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> Run(string node = Node)
        => fixture.CreateProvider(node, _clock);

    private static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    // Queued by the node, as the scheduler loop leaves a row it is about to run.
    private async Task<Guid> QueueTimeTickerAsync(ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> run)
    {
        var ticker = new TimeTickerEntity
        {
            Id = Guid.NewGuid(), Function = "release-fn", ExecutionTime = _clock.UtcNow, Status = TickerStatus.Idle,
            CreatedAt = T0.AddHours(-2), UpdatedAt = SeedUpdatedAt, Request = []
        };
        await run.AddTimeTickers([ticker], CancellationToken.None);
        var window = await run.GetEarliestTimeTickers(CancellationToken.None);
        Assert.Single(await Drain(run.QueueTimeTickers(window.Where(x => x.Id == ticker.Id).ToArray(), CancellationToken.None)));
        return ticker.Id;
    }

    // Claimed and running, as a job that started.
    private async Task<Guid> StartTimeTickerAsync(ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> run)
    {
        var id = await QueueTimeTickerAsync(run);
        Assert.True(await run.TryMarkTimeTickerInProgress(id, _clock.NextClaimMarker()));
        return id;
    }

    private async Task<Guid> QueueOccurrenceAsync(ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> run)
    {
        var slot = (_clock.UtcNow, new[] { new InternalManagerContext(Guid.NewGuid()) { FunctionName = "release-cron-fn", Expression = "* * * * *" } });
        return Assert.Single(await Drain(run.QueueCronTickerOccurrences(slot, CancellationToken.None))).Id;
    }

    private async Task<Guid> StartOccurrenceAsync(ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> run)
    {
        var id = await QueueOccurrenceAsync(run);
        Assert.True(await run.TryMarkCronOccurrenceInProgress(id, _clock.NextClaimMarker()));
        return id;
    }

    private async Task<TimeTickerEntity> TimeTicker(Guid id)
        => (await Run("reader").GetTimeTickerById(id, CancellationToken.None))!;

    private async Task<CronTickerOccurrenceEntity<CronTickerEntity>> Occurrence(Guid id)
        => (await Run("reader").GetAllCronTickerOccurrences(x => x.Id == id, CancellationToken.None)).Single();

    // The restart: the earlier run is gone, time has passed, and this run begins now.
    private DateTime Restart()
    {
        _clock.Advance(TimeSpan.FromMinutes(1));
        return _clock.UtcNow;
    }

    [Fact]
    public async Task ATimeTickerTheEarlierRunLeftRunning_IsReleased_AndCanBeClaimedAgain()
    {
        var id = await StartTimeTickerAsync(Run());
        var runStartedAt = Restart();

        await Run().ReleaseDeadNodeTimeTickerResources(Node, runStartedAt, CancellationToken.None);

        var row = await TimeTicker(id);
        Assert.Equal(TickerStatus.Idle, row.Status);
        Assert.Null(row.LockHolder);
        Assert.Null(row.LockedAt);
        // Back in the pending index: another node's fallback takes it.
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Contains(await Drain(Run("node-B").QueueTimedOutTimeTickers(CancellationToken.None)), x => x.Id == id);
    }

    [Fact]
    public async Task AnOccurrenceTheEarlierRunLeftRunning_IsReleased()
    {
        var id = await StartOccurrenceAsync(Run());
        var runStartedAt = Restart();

        await Run().ReleaseDeadNodeOccurrenceResources(Node, runStartedAt, CancellationToken.None);

        var row = await Occurrence(id);
        Assert.Equal(TickerStatus.Idle, row.Status);
        Assert.Null(row.LockHolder);
        Assert.Null(row.LockedAt);
        // Back in the pending index: the fallback of another node finds it.
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Contains(await Drain(Run("node-B").QueueTimedOutCronTickerOccurrences(CancellationToken.None)), x => x.Id == id);
    }

    // Nothing says such a row is older than this run, so it is not taken for an earlier run's (the same rule as EF Core,
    // where a null LockedAt never compares as earlier).
    [Fact]
    public async Task ARowWithoutLockedAt_IsLeftAlone()
    {
        var ticker = new TimeTickerEntity
        {
            Id = Guid.NewGuid(), Function = "release-fn", ExecutionTime = T0, Status = TickerStatus.InProgress,
            LockHolder = Node, LockedAt = null, CreatedAt = T0.AddHours(-2), UpdatedAt = SeedUpdatedAt, Request = []
        };
        await Run().AddTimeTickers([ticker], CancellationToken.None);
        var runStartedAt = Restart();

        await Run().ReleaseDeadNodeTimeTickerResources(Node, runStartedAt, CancellationToken.None);

        var row = await TimeTicker(ticker.Id);
        Assert.Equal((TickerStatus.InProgress, Node), (row.Status, row.LockHolder));
    }

    [Fact]
    public async Task ATimeTickerTheEarlierRunLeftQueued_IsReleased()
    {
        var id = await QueueTimeTickerAsync(Run());
        var runStartedAt = Restart();

        await Run().ReleaseDeadNodeTimeTickerResources(Node, runStartedAt, CancellationToken.None);

        var row = await TimeTicker(id);
        Assert.Equal(TickerStatus.Idle, row.Status);
        Assert.Null(row.LockHolder);
    }

    // The duplicate-run guard: what this run claimed since it started is running now and must keep its lock.
    [Fact]
    public async Task WhatThisRunClaimed_IsKept_WhileTheEarlierRunsRowsAreReleased()
    {
        var earlierTicker = await StartTimeTickerAsync(Run());
        var earlierOccurrence = await StartOccurrenceAsync(Run());
        var runStartedAt = Restart();
        var thisRun = Run();
        var running = await StartTimeTickerAsync(thisRun);
        var queued = await QueueTimeTickerAsync(thisRun);
        var runningOccurrence = await StartOccurrenceAsync(thisRun);
        var queuedOccurrence = await QueueOccurrenceAsync(thisRun);

        await thisRun.ReleaseDeadNodeTimeTickerResources(Node, runStartedAt, CancellationToken.None);
        await thisRun.ReleaseDeadNodeOccurrenceResources(Node, runStartedAt, CancellationToken.None);

        Assert.Equal((TickerStatus.InProgress, Node), ((await TimeTicker(running)).Status, (await TimeTicker(running)).LockHolder));
        Assert.Equal((TickerStatus.Queued, Node), ((await TimeTicker(queued)).Status, (await TimeTicker(queued)).LockHolder));
        Assert.Equal((TickerStatus.InProgress, Node), ((await Occurrence(runningOccurrence)).Status, (await Occurrence(runningOccurrence)).LockHolder));
        Assert.Equal((TickerStatus.Queued, Node), ((await Occurrence(queuedOccurrence)).Status, (await Occurrence(queuedOccurrence)).LockHolder));
        Assert.Equal(TickerStatus.Idle, (await TimeTicker(earlierTicker)).Status);
        Assert.Equal(TickerStatus.Idle, (await Occurrence(earlierOccurrence)).Status);
    }

    // A fallback or run-now claim writes its claim marker as LockedAt, cut to the millisecond, while the start of the run
    // is not cut: a claim in the millisecond the run started would look earlier than the start.
    [Fact]
    public async Task AClaimInTheMillisecondTheRunStarted_IsKept()
    {
        var thisRun = Run();

        // Half a millisecond past a whole one; the first marker of that millisecond is the whole millisecond.
        _clock.UtcNow = T0.AddMinutes(1).AddTicks(5000);
        var runStartedAt = _clock.UtcNow;
        var ticker = await QueueTimeTickerAsync(thisRun);
        Assert.Single(await thisRun.AcquireImmediateTimeTickersAsync([ticker], CancellationToken.None));
        Assert.True((await TimeTicker(ticker)).LockedAt < runStartedAt, "the test needs a marker below the raw start");

        await thisRun.ReleaseDeadNodeTimeTickerResources(Node, runStartedAt, CancellationToken.None);

        Assert.Equal(TickerStatus.InProgress, (await TimeTicker(ticker)).Status);

        // The same for an occurrence, in a millisecond of its own (a second marker in one millisecond moves on by one).
        _clock.UtcNow = T0.AddMinutes(2).AddTicks(5000);
        runStartedAt = _clock.UtcNow;
        var occurrence = await QueueOccurrenceAsync(thisRun);
        Assert.Single(await thisRun.AcquireImmediateCronOccurrencesAsync([occurrence], CancellationToken.None));
        Assert.True((await Occurrence(occurrence)).LockedAt < runStartedAt, "the test needs a marker below the raw start");

        await thisRun.ReleaseDeadNodeOccurrenceResources(Node, runStartedAt, CancellationToken.None);

        Assert.Equal(TickerStatus.InProgress, (await Occurrence(occurrence)).Status);
    }

    [Fact]
    public async Task AnotherNodesRows_AndEndedRows_AreLeftAlone()
    {
        var others = await StartTimeTickerAsync(Run("node-B"));
        var othersOccurrence = await StartOccurrenceAsync(Run("node-B"));
        var ended = await StartTimeTickerAsync(Run());
        await Run().UpdateTimeTicker(new InternalFunctionContext { TickerId = ended, Type = TickerType.TimeTicker }
            .SetProperty(x => x.Status, TickerStatus.Done)
            .SetProperty(x => x.ExecutedAt, T0), CancellationToken.None);
        var runStartedAt = Restart();

        await Run().ReleaseDeadNodeTimeTickerResources(Node, runStartedAt, CancellationToken.None);
        await Run().ReleaseDeadNodeOccurrenceResources(Node, runStartedAt, CancellationToken.None);

        Assert.Equal((TickerStatus.InProgress, "node-B"), ((await TimeTicker(others)).Status, (await TimeTicker(others)).LockHolder));
        Assert.Equal((TickerStatus.InProgress, "node-B"), ((await Occurrence(othersOccurrence)).Status, (await Occurrence(othersOccurrence)).LockHolder));
        Assert.Equal(TickerStatus.Done, (await TimeTicker(ended)).Status);
    }

    // The earlier run left the row Queued; this run's fallback takes it (a Queued row of its own name) between the
    // release's read and its write. The release must see the new claim and leave the row to the job that now runs.
    [Fact]
    public async Task ARowThisRunClaimsWhileItIsBeingReleased_IsKept()
    {
        var id = await QueueTimeTickerAsync(Run());
        var runStartedAt = Restart();
        var thisRun = fixture.CreateProvider(Node, _clock);
        ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> store = thisRun;
        var claimed = false;
        thisRun.BeforeCompareAndSetHook = async key =>
        {
            if (claimed || !key.Contains(id.ToString()))
                return;
            claimed = true;
            Assert.Single(await store.AcquireImmediateTimeTickersAsync([id], CancellationToken.None));
        };

        await store.ReleaseDeadNodeTimeTickerResources(Node, runStartedAt, CancellationToken.None);

        Assert.True(claimed, "the release never reached the row");
        var row = await TimeTicker(id);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal(Node, row.LockHolder);
    }

    // The main loop of this run finds a row the earlier run left Queued under the same name, queues it again and starts
    // it. Queueing writes a new LockedAt, so the release leaves the running job alone.
    [Fact]
    public async Task ARowTheEarlierRunLeftQueued_ThatThisRunQueuedAgainAndStarted_IsKept()
    {
        var id = await QueueTimeTickerAsync(Run());
        _clock.Advance(TimeSpan.FromMilliseconds(200));
        var runStartedAt = _clock.UtcNow;
        var thisRun = Run();
        var window = await thisRun.GetEarliestTimeTickers(CancellationToken.None);
        Assert.Single(await Drain(thisRun.QueueTimeTickers(window.Where(x => x.Id == id).ToArray(), CancellationToken.None)));
        Assert.True(await thisRun.TryMarkTimeTickerInProgress(id, _clock.NextClaimMarker()));

        await thisRun.ReleaseDeadNodeTimeTickerResources(Node, runStartedAt, CancellationToken.None);

        var row = await TimeTicker(id);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal(Node, row.LockHolder);
    }

    // Held by the node without being queued or running (the rows RecoverDeadNode.lua also frees, and EF Core's release).
    [Fact]
    public async Task AnIdleRowTheEarlierRunStillHeld_IsReleased()
    {
        var ticker = new TimeTickerEntity
        {
            Id = Guid.NewGuid(), Function = "release-fn", ExecutionTime = T0, Status = TickerStatus.Idle,
            LockHolder = Node, LockedAt = T0, CreatedAt = T0.AddHours(-2), UpdatedAt = SeedUpdatedAt, Request = []
        };
        await Run().AddTimeTickers([ticker], CancellationToken.None);
        var runStartedAt = Restart();

        await Run().ReleaseDeadNodeTimeTickerResources(Node, runStartedAt, CancellationToken.None);

        var row = await TimeTicker(ticker.Id);
        Assert.Equal(TickerStatus.Idle, row.Status);
        Assert.Null(row.LockHolder);
    }

    // The fallback and the loop find rows through the pending index. A release that set the row Idle first and then failed
    // to write the index would leave a row nothing looks at, and the next pass would no longer see it as held. So the
    // index is written first: when the row's write then fails, the row is still held and already indexed, and the next
    // pass releases it.
    [Fact]
    public async Task ARowWhoseWriteFails_StaysHeldAndIsAlreadyIndexed_AndTheNextPassReleasesIt()
    {
        var id = await StartTimeTickerAsync(Run());
        var runStartedAt = Restart();
        var thisRun = fixture.CreateProvider(Node, _clock);
        ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> store = thisRun;
        Assert.Null(await fixture.Database.SortedSetScoreAsync("tq:tt:pending", id.ToString()));
        thisRun.BeforeCompareAndSetHook = key => key.Contains(id.ToString())
            ? throw new InvalidOperationException("The write of this row fails.")
            : Task.CompletedTask;

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReleaseDeadNodeTimeTickerResources(Node, runStartedAt, CancellationToken.None));

        var held = await TimeTicker(id);
        Assert.Equal((TickerStatus.InProgress, Node), (held.Status, held.LockHolder));
        Assert.NotNull(await fixture.Database.SortedSetScoreAsync("tq:tt:pending", id.ToString()));

        thisRun.BeforeCompareAndSetHook = null;
        await store.ReleaseDeadNodeTimeTickerResources(Node, runStartedAt, CancellationToken.None);

        Assert.Equal(TickerStatus.Idle, (await TimeTicker(id)).Status);
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Contains(await Drain(Run("node-B").QueueTimedOutTimeTickers(CancellationToken.None)), x => x.Id == id);
    }

    // One row that cannot be released must not keep the others held; the pass still fails, so the caller tries again.
    [Fact]
    public async Task ARowThatCannotBeReleased_DoesNotKeepTheOthersHeld_AndThePassFails()
    {
        var ids = new[] { await StartTimeTickerAsync(Run()), await StartTimeTickerAsync(Run()), await StartTimeTickerAsync(Run()) };
        var runStartedAt = Restart();
        var thisRun = fixture.CreateProvider(Node, _clock);
        ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> store = thisRun;
        var failing = ids[1];
        thisRun.BeforeCompareAndSetHook = key => key.Contains(failing.ToString())
            ? throw new InvalidOperationException("The write of this row fails.")
            : Task.CompletedTask;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReleaseDeadNodeTimeTickerResources(Node, runStartedAt, CancellationToken.None));

        Assert.Contains("1 row(s)", failure.Message);
        Assert.Equal(TickerStatus.InProgress, (await TimeTicker(failing)).Status);
        Assert.Equal(TickerStatus.Idle, (await TimeTicker(ids[0])).Status);
        Assert.Equal(TickerStatus.Idle, (await TimeTicker(ids[2])).Status);
    }
}
