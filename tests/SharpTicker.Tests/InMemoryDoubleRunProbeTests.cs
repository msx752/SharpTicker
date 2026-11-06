using System;
using System.Collections.Generic;
using System.Linq;
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
/// Probes on the in-memory store: each one drives provider instances the way the scheduler loops do and asserts what
/// happens TODAY, bugs included, so they pass. When a fix closes a finding its probe is deleted and a regression
/// test that asserts the corrected behaviour stays.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class InMemoryDoubleRunProbeTests : IAsyncLifetime
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

    // The in-memory store is process-wide, so every row a probe made is removed again.
    public async Task DisposeAsync()
    {
        var node = CreateProvider("cleanup");
        await node.RemoveTimeTickers(_timeTickerIds.ToArray(), CancellationToken.None);
        await node.RemoveCronTickerOccurrences(_occurrenceIds.ToArray(), CancellationToken.None);
    }

    private TickerInMemoryPersistenceProvider<TimeTickerEntity, CronTickerEntity> CreateProvider(string nodeIdentifier)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITickerClock>(_clock);
        services.AddSingleton(new SchedulerOptionsBuilder { NodeIdentifier = nodeIdentifier });
        return new TickerInMemoryPersistenceProvider<TimeTickerEntity, CronTickerEntity>(services.BuildServiceProvider());
    }

    private static InternalFunctionContext InProgressContext()
        => new InternalFunctionContext().SetProperty(x => x.Status, TickerStatus.InProgress);

    private static InternalFunctionContext CancelledContext(Guid id)
        => new InternalFunctionContext { TickerId = id }.SetProperty(x => x.Status, TickerStatus.Cancelled);

    private static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    private async Task<Guid> SeedTimeTicker(TickerStatus status = TickerStatus.Idle)
    {
        var ticker = new TimeTickerEntity
        {
            Id = Guid.NewGuid(),
            Function = "probe-fn",
            ExecutionTime = T0,
            Status = status,
            CreatedAt = T0.AddHours(-2),
            UpdatedAt = T0.AddHours(-1),
            Request = []
        };
        _timeTickerIds.Add(ticker.Id);
        await CreateProvider("seed").AddTimeTickers([ticker], CancellationToken.None);
        return ticker.Id;
    }

    private static (DateTime Key, InternalManagerContext[] Items) Slot()
        => (T0, [new InternalManagerContext(Guid.NewGuid()) { FunctionName = "probe-cron-fn", Expression = "* * * * *" }]);

    private static async Task<CronTickerOccurrenceEntity<CronTickerEntity>> Occurrence(
        TickerInMemoryPersistenceProvider<TimeTickerEntity, CronTickerEntity> provider, Guid id)
        => (await provider.GetAllCronTickerOccurrences(x => x.Id == id, CancellationToken.None)).Single();

    // R1: the loop that queued a row wakes late, the fallback has already taken it, and the loop's InProgress write
    // is unconditional, so it lands over the claim and the row is handed to a worker a second time.
    [Fact]
    public async Task Probe_LateLoopOverwritesFallbackClaim()
    {
        var nodeA = CreateProvider("node-A");
        var nodeB = CreateProvider("node-B");
        var id = await SeedTimeTicker();

        var window = await nodeA.GetEarliestTimeTickers(CancellationToken.None);
        var queuedByA = await Drain(nodeA.QueueTimeTickers(window.Where(x => x.Id == id).ToArray(), CancellationToken.None));

        _clock.Advance(TimeSpan.FromSeconds(2));
        var claimedByB = await Drain(nodeB.QueueTimedOutTimeTickers(CancellationToken.None));
        var afterClaim = await nodeA.GetTimeTickerById(id, CancellationToken.None);

        _clock.Advance(TimeSpan.FromMilliseconds(500));
        await nodeA.UpdateTimeTickersWithUnifiedContext([id], InProgressContext(), CancellationToken.None);
        var afterLateWrite = await nodeA.GetTimeTickerById(id, CancellationToken.None);

        Assert.Equal(id, Assert.Single(queuedByA).Id);
        Assert.Contains(claimedByB, row => row.Id == id);
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
        var nodeA = CreateProvider("node-A");
        var nodeB = CreateProvider("node-B");

        var occurrenceId = Assert.Single(await Drain(nodeA.QueueCronTickerOccurrences(Slot(), CancellationToken.None))).Id;
        _occurrenceIds.Add(occurrenceId);

        _clock.Advance(TimeSpan.FromSeconds(2));
        var claimedByB = await Drain(nodeB.QueueTimedOutCronTickerOccurrences(CancellationToken.None));
        var afterClaim = await Occurrence(nodeA, occurrenceId);

        _clock.Advance(TimeSpan.FromMilliseconds(500));
        await nodeA.UpdateCronTickerOccurrencesWithUnifiedContext([occurrenceId], InProgressContext(), CancellationToken.None);
        var afterLateWrite = await Occurrence(nodeA, occurrenceId);

        Assert.Contains(claimedByB, row => row.Id == occurrenceId);
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
        var nodeA = CreateProvider("node-A");
        var id = await SeedTimeTicker();

        var window = await nodeA.GetEarliestTimeTickers(CancellationToken.None);
        Assert.Single(await Drain(nodeA.QueueTimeTickers(window.Where(x => x.Id == id).ToArray(), CancellationToken.None)));

        Assert.Equal(1, await nodeA.UpdateTimeTicker(CancelledContext(id), CancellationToken.None));
        Assert.Equal(TickerStatus.Cancelled, (await nodeA.GetTimeTickerById(id, CancellationToken.None)).Status);

        await nodeA.UpdateTimeTickersWithUnifiedContext([id], InProgressContext(), CancellationToken.None);

        Assert.Equal(TickerStatus.InProgress, (await nodeA.GetTimeTickerById(id, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Probe_LateLoopRevivesCancelledRow_CronOccurrence()
    {
        var nodeA = CreateProvider("node-A");

        var occurrenceId = Assert.Single(await Drain(nodeA.QueueCronTickerOccurrences(Slot(), CancellationToken.None))).Id;
        _occurrenceIds.Add(occurrenceId);

        await nodeA.UpdateCronTickerOccurrence(CancelledContext(occurrenceId), CancellationToken.None);
        Assert.Equal(TickerStatus.Cancelled, (await Occurrence(nodeA, occurrenceId)).Status);

        await nodeA.UpdateCronTickerOccurrencesWithUnifiedContext([occurrenceId], InProgressContext(), CancellationToken.None);

        Assert.Equal(TickerStatus.InProgress, (await Occurrence(nodeA, occurrenceId)).Status);
    }
}
