using Microsoft.EntityFrameworkCore;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Models;

namespace SharpTicker.EntityFrameworkCore.Tests.Infrastructure;

/// <summary>
/// Probes on a real PostgreSQL: each one drives provider instances the way the scheduler loops do and asserts what
/// happens TODAY, bugs included, so they pass. When a fix closes a finding its probe is deleted and a regression
/// test that asserts the corrected behaviour stays.
/// </summary>
[Collection(PostgresTestFixture.CollectionName)]
public class PostgresDoubleRunProbeTests(PostgresTestFixture fixture) : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static InternalFunctionContext InProgressContext(Guid? id = null)
        => new InternalFunctionContext { TickerId = id ?? Guid.Empty }.SetProperty(x => x.Status, TickerStatus.InProgress);

    private static InternalFunctionContext CancelledContext(Guid id)
        => new InternalFunctionContext { TickerId = id }.SetProperty(x => x.Status, TickerStatus.Cancelled);

    private static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    private async Task<Guid> SeedTimeTicker(
        TickerStatus status = TickerStatus.Idle,
        DateTime? executionTime = null,
        DateTime? updatedAt = null)
    {
        var ticker = new TimeTickerEntity
        {
            Id = Guid.NewGuid(),
            Function = "probe-fn",
            ExecutionTime = executionTime ?? T0,
            Status = status,
            CreatedAt = T0.AddHours(-2),
            UpdatedAt = updatedAt ?? T0.AddHours(-1),
            Request = []
        };

        await using var context = fixture.CreateContext();
        context.Set<TimeTickerEntity>().Add(ticker);
        await context.SaveChangesAsync();
        return ticker.Id;
    }

    private async Task<Guid> SeedCronTicker()
    {
        var cron = new CronTickerEntity
        {
            Id = Guid.NewGuid(),
            Function = "probe-cron-fn",
            Expression = "* * * * *",
            CreatedAt = T0.AddHours(-2),
            UpdatedAt = T0.AddHours(-2),
            Request = []
        };

        await using var context = fixture.CreateContext();
        context.Set<CronTickerEntity>().Add(cron);
        await context.SaveChangesAsync();
        return cron.Id;
    }

    private async Task<TimeTickerEntity> ReadTimeTicker(Guid id)
    {
        await using var context = fixture.CreateContext();
        return await context.Set<TimeTickerEntity>().AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private async Task<CronTickerOccurrenceEntity<CronTickerEntity>> ReadOccurrence(Guid id)
    {
        await using var context = fixture.CreateContext();
        return await context.Set<CronTickerOccurrenceEntity<CronTickerEntity>>().AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private static (DateTime Key, InternalManagerContext[] Items) Slot(Guid cronId)
        => (T0, [new InternalManagerContext(cronId) { FunctionName = "probe-cron-fn", Expression = "* * * * *" }]);

    // R1: the loop that queued a row wakes late, the fallback has already taken it, and the loop's InProgress write
    // is unconditional, so it lands over the claim and the row is handed to a worker a second time.
    [Fact]
    public async Task Probe_LateLoopOverwritesFallbackClaim()
    {
        var clock = new FakeTickerClock(T0);
        var nodeA = fixture.CreateProvider("node-A", clock);
        var nodeB = fixture.CreateProvider("node-B", clock);
        var id = await SeedTimeTicker();

        var window = await nodeA.GetEarliestTimeTickers(CancellationToken.None);
        var queuedByA = await Drain(nodeA.QueueTimeTickers(window, CancellationToken.None));

        clock.Advance(TimeSpan.FromSeconds(2));
        var claimedByB = await Drain(nodeB.QueueTimedOutTimeTickers(CancellationToken.None));
        var afterClaim = await ReadTimeTicker(id);

        clock.Advance(TimeSpan.FromMilliseconds(500));
        await nodeA.UpdateTimeTickersWithUnifiedContext([id], InProgressContext(), CancellationToken.None);
        var afterLateWrite = await ReadTimeTicker(id);

        // Both nodes hold the same row to run.
        Assert.Equal(id, Assert.Single(queuedByA).Id);
        Assert.Equal(id, Assert.Single(claimedByB).Id);
        Assert.Equal("node-B", afterClaim.LockHolder);
        Assert.Equal(T0.AddSeconds(2), afterClaim.UpdatedAt);
        // The late write was not refused: it moved UpdatedAt, though the row belongs to node B.
        Assert.Equal("node-B", afterLateWrite.LockHolder);
        Assert.Equal(TickerStatus.InProgress, afterLateWrite.Status);
        Assert.Equal(T0.AddSeconds(2.5), afterLateWrite.UpdatedAt);
    }

    [Fact]
    public async Task Probe_LateLoopOverwritesFallbackClaim_CronOccurrence()
    {
        var clock = new FakeTickerClock(T0);
        var nodeA = fixture.CreateProvider("node-A", clock);
        var nodeB = fixture.CreateProvider("node-B", clock);
        var cronId = await SeedCronTicker();

        var queuedByA = await Drain(nodeA.QueueCronTickerOccurrences(Slot(cronId), CancellationToken.None));
        var occurrenceId = Assert.Single(queuedByA).Id;

        clock.Advance(TimeSpan.FromSeconds(2));
        var claimedByB = await Drain(nodeB.QueueTimedOutCronTickerOccurrences(CancellationToken.None));
        var afterClaim = await ReadOccurrence(occurrenceId);

        clock.Advance(TimeSpan.FromMilliseconds(500));
        await nodeA.UpdateCronTickerOccurrencesWithUnifiedContext([occurrenceId], InProgressContext(), CancellationToken.None);
        var afterLateWrite = await ReadOccurrence(occurrenceId);

        Assert.Equal(occurrenceId, Assert.Single(claimedByB).Id);
        Assert.Equal("node-B", afterClaim.LockHolder);
        Assert.Equal(T0.AddSeconds(2), afterClaim.UpdatedAt);
        Assert.Equal("node-B", afterLateWrite.LockHolder);
        Assert.Equal(TickerStatus.InProgress, afterLateWrite.Status);
        Assert.Equal(T0.AddSeconds(2.5), afterLateWrite.UpdatedAt);
    }

    // R1: the same unconditional write also turns a finished or cancelled row back into InProgress.
    [Fact]
    public async Task Probe_LateLoopRevivesCancelledRow()
    {
        var clock = new FakeTickerClock(T0);
        var nodeA = fixture.CreateProvider("node-A", clock);
        var id = await SeedTimeTicker();

        var window = await nodeA.GetEarliestTimeTickers(CancellationToken.None);
        Assert.Single(await Drain(nodeA.QueueTimeTickers(window, CancellationToken.None)));

        Assert.Equal(1, await nodeA.UpdateTimeTicker(CancelledContext(id), CancellationToken.None));
        Assert.Equal(TickerStatus.Cancelled, (await ReadTimeTicker(id)).Status);

        await nodeA.UpdateTimeTickersWithUnifiedContext([id], InProgressContext(), CancellationToken.None);

        Assert.Equal(TickerStatus.InProgress, (await ReadTimeTicker(id)).Status);
    }

    [Fact]
    public async Task Probe_LateLoopRevivesCancelledRow_CronOccurrence()
    {
        var clock = new FakeTickerClock(T0);
        var nodeA = fixture.CreateProvider("node-A", clock);
        var cronId = await SeedCronTicker();

        var occurrenceId = Assert.Single(await Drain(nodeA.QueueCronTickerOccurrences(Slot(cronId), CancellationToken.None))).Id;

        await nodeA.UpdateCronTickerOccurrence(CancelledContext(occurrenceId), CancellationToken.None);
        Assert.Equal(TickerStatus.Cancelled, (await ReadOccurrence(occurrenceId)).Status);

        await nodeA.UpdateCronTickerOccurrencesWithUnifiedContext([occurrenceId], InProgressContext(), CancellationToken.None);

        Assert.Equal(TickerStatus.InProgress, (await ReadOccurrence(occurrenceId)).Status);
    }

    // R2: the fallback's compare-and-set is "UpdatedAt <= value read", with no status check. Row y is read by node A,
    // claimed by node B whose clock is behind (so it writes an earlier UpdatedAt), and node A's claim still goes through.
    [Fact]
    public async Task Probe_FallbackClaimsInProgressRow_WhenClockIsBehind()
    {
        var aheadClock = new FakeTickerClock(T0.AddSeconds(10));
        var behindClock = new FakeTickerClock(T0);
        var nodeA = fixture.CreateProvider("node-A", aheadClock);
        var nodeB = fixture.CreateProvider("node-B", behindClock);

        // Both rows were last written by a node whose clock is ahead of node B's.
        var written = T0.AddSeconds(10);
        var x = await SeedTimeTicker(executionTime: T0.AddSeconds(-5), updatedAt: written);
        var y = await SeedTimeTicker(executionTime: T0.AddSeconds(-5), updatedAt: written);

        await using var aEnumerator = nodeA.QueueTimedOutTimeTickers(CancellationToken.None).GetAsyncEnumerator();
        // Node A reads both candidates and claims the first one.
        Assert.True(await aEnumerator.MoveNextAsync());
        var firstOfA = aEnumerator.Current.Id;
        var second = firstOfA == x ? y : x;

        // Node B now runs a full pass: it sees only the second row (the first is InProgress) and claims it.
        var claimedByB = await Drain(nodeB.QueueTimedOutTimeTickers(CancellationToken.None));
        Assert.Equal(second, Assert.Single(claimedByB).Id);
        var afterB = await ReadTimeTicker(second);
        Assert.Equal(TickerStatus.InProgress, afterB.Status);
        Assert.Equal("node-B", afterB.LockHolder);

        // Node A carries on with its stale read: its claim of the InProgress row succeeds too.
        Assert.True(await aEnumerator.MoveNextAsync());
        Assert.Equal(second, aEnumerator.Current.Id);
        Assert.Equal("node-A", (await ReadTimeTicker(second)).LockHolder);
    }

    // R6: a batch "run now" acquire returns every row of the batch that is InProgress for this node, including a row
    // this node's fallback took earlier, so that row is handed to a worker a second time.
    [Fact]
    public async Task Probe_ImmediateBatchReturnsRowTheFallbackTook()
    {
        var clock = new FakeTickerClock(T0);
        var node = fixture.CreateProvider("node-A", clock);
        var takenByFallback = await SeedTimeTicker(executionTime: T0.AddSeconds(-5));
        var fresh = await SeedTimeTicker(executionTime: T0.AddHours(1));

        var fallback = await Drain(node.QueueTimedOutTimeTickers(CancellationToken.None));
        Assert.Equal(takenByFallback, Assert.Single(fallback).Id);

        var acquired = await node.AcquireImmediateTimeTickersAsync([takenByFallback, fresh], CancellationToken.None);

        // Only the fresh row was acquired by this call, yet both come back.
        Assert.Contains(acquired, row => row.Id == fresh);
        Assert.Contains(acquired, row => row.Id == takenByFallback);
    }
}
