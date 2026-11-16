using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SharpTicker.EntityFrameworkCore.Infrastructure;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Models;

namespace SharpTicker.EntityFrameworkCore.Tests.Infrastructure;

/// <summary>
/// The lease on InProgress rows (#24), on the store: the node that holds a row renews its <c>LockedAt</c>, and a node
/// records the rows of other node identifiers whose <c>LockedAt</c> is older than the lease as Cancelled (#211). Provider instances with
/// their own node identifier over one store and a fake clock; the same bodies run on SQLite and on a real PostgreSQL.
/// </summary>
public abstract class EfCoreInProgressLeaseTests : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private readonly FakeTickerClock _clock = new(T0);

    protected abstract Task InitializeStoreAsync();

    protected abstract Task DisposeStoreAsync();

    protected abstract TestSharpTickerDbContext CreateContext();

    internal abstract TestableProvider CreateProvider(string nodeIdentifier, ITickerClock clock);

    public Task InitializeAsync() => InitializeStoreAsync();

    public Task DisposeAsync() => DisposeStoreAsync();

    private IInProgressLease Node(string nodeIdentifier) => CreateProvider(nodeIdentifier, _clock);

    private async Task<Guid> SeedTimeTickerAsync(TickerStatus status, string? lockHolder, DateTime? lockedAt, DateTime? updatedAt = null)
    {
        var row = new TimeTickerEntity
        {
            Id = Guid.NewGuid(), Function = "lease-fn", ExecutionTime = T0.AddHours(-1), Status = status,
            LockHolder = lockHolder, LockedAt = lockedAt, CreatedAt = T0.AddHours(-2), UpdatedAt = updatedAt ?? T0.AddHours(-1), Request = []
        };
        await using var context = CreateContext();
        context.Set<TimeTickerEntity>().Add(row);
        await context.SaveChangesAsync();
        return row.Id;
    }

    private async Task<Guid> SeedOccurrenceAsync(TickerStatus status, string? lockHolder, DateTime? lockedAt, DateTime? updatedAt = null)
    {
        var cron = new CronTickerEntity
        {
            Id = Guid.NewGuid(), Function = "lease-cron-fn", Expression = "* * * * *", CreatedAt = T0.AddHours(-2), UpdatedAt = T0.AddHours(-2), Request = []
        };
        var occurrence = new CronTickerOccurrenceEntity<CronTickerEntity>
        {
            Id = Guid.NewGuid(), CronTickerId = cron.Id, ExecutionTime = T0.AddHours(-1), Status = status,
            LockHolder = lockHolder, LockedAt = lockedAt, CreatedAt = T0.AddHours(-2), UpdatedAt = updatedAt ?? T0.AddHours(-1)
        };
        await using var context = CreateContext();
        context.Set<CronTickerEntity>().Add(cron);
        context.Set<CronTickerOccurrenceEntity<CronTickerEntity>>().Add(occurrence);
        await context.SaveChangesAsync();
        return occurrence.Id;
    }

    private async Task<TimeTickerEntity> TimeTickerAsync(Guid id)
    {
        await using var context = CreateContext();
        return await context.Set<TimeTickerEntity>().AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private async Task<CronTickerOccurrenceEntity<CronTickerEntity>> OccurrenceAsync(Guid id)
    {
        await using var context = CreateContext();
        return await context.Set<CronTickerOccurrenceEntity<CronTickerEntity>>().AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private const string LeaseEndedReason = "The lock of the instance that ran the job was not renewed for the lease.";

    // What a graceful stop stores for a job it records as Cancelled, with the time of the pass: Cancelled, ExecutedAt and
    // UpdatedAt now, the reason in ExceptionMessage, no lock. Nothing else of the row changes.
    private static void AssertCancelled(TickerStatus status, DateTime? executedAt, string? exceptionMessage, string? skippedReason, long elapsedTime,
        string? lockHolder, DateTime? lockedAt, DateTime updatedAt)
    {
        Assert.Equal(TickerStatus.Cancelled, status);
        Assert.Equal(T0, executedAt);
        Assert.Equal(LeaseEndedReason, exceptionMessage);
        Assert.Null(skippedReason);
        Assert.Equal(0, elapsedTime);
        Assert.Null(lockHolder);
        Assert.Null(lockedAt);
        Assert.Equal(T0, updatedAt);
    }

    #region Cancel of other nodes' rows (T1, T4)

    // T1: a row of another node whose lock is older than the lease is recorded as Cancelled (#211; up to 1.6.1 it was
    // released to Idle and ran again); one whose lock is younger stays.
    [Fact]
    public async Task Cancel_TimeJobOfAnotherNodeLockedLongerThanTheLease_IsRecordedCancelled_AndAYoungerOneStays()
    {
        var expired = await SeedTimeTickerAsync(TickerStatus.InProgress, "pod-a", T0 - Lease - TimeSpan.FromSeconds(1));
        var young = await SeedTimeTickerAsync(TickerStatus.InProgress, "pod-a", T0 - Lease + TimeSpan.FromSeconds(10));

        var result = await Node("pod-b").CancelExpiredInProgressOfOtherNodesAsync(Lease);

        var cancelled = await TimeTickerAsync(expired);
        AssertCancelled(cancelled.Status, cancelled.ExecutedAt, cancelled.ExceptionMessage, cancelled.SkippedReason, cancelled.ElapsedTime,
            cancelled.LockHolder, cancelled.LockedAt, cancelled.UpdatedAt);
        var kept = await TimeTickerAsync(young);
        Assert.Equal(TickerStatus.InProgress, kept.Status);
        Assert.Equal("pod-a", kept.LockHolder);
        Assert.Equal(T0 - Lease + TimeSpan.FromSeconds(10), kept.LockedAt);
        Assert.Equal(1, result.Cancelled);
        Assert.Equal(("pod-a", 1), Assert.Single(result.Nodes));
    }

    [Fact]
    public async Task Cancel_OccurrenceOfAnotherNodeLockedLongerThanTheLease_IsRecordedCancelled_AndAYoungerOneStays()
    {
        var expired = await SeedOccurrenceAsync(TickerStatus.InProgress, "pod-a", T0 - Lease - TimeSpan.FromSeconds(1));
        var young = await SeedOccurrenceAsync(TickerStatus.InProgress, "pod-a", T0 - Lease + TimeSpan.FromSeconds(10));

        var result = await Node("pod-b").CancelExpiredInProgressOfOtherNodesAsync(Lease);

        var cancelled = await OccurrenceAsync(expired);
        AssertCancelled(cancelled.Status, cancelled.ExecutedAt, cancelled.ExceptionMessage, cancelled.SkippedReason, cancelled.ElapsedTime,
            cancelled.LockHolder, cancelled.LockedAt, cancelled.UpdatedAt);
        var kept = await OccurrenceAsync(young);
        Assert.Equal(TickerStatus.InProgress, kept.Status);
        Assert.Equal("pod-a", kept.LockHolder);
        Assert.Equal(1, result.Cancelled);
        Assert.Equal(("pod-a", 1), Assert.Single(result.Nodes));
    }

    // Only InProgress rows: a row of another node that is Queued, or that ended and kept its lock holder, is not changed.
    [Fact]
    public async Task Cancel_RowsOfAnotherNodeThatAreNotInProgress_AreNotTouched()
    {
        var old = T0 - Lease - TimeSpan.FromHours(1);
        var queued = await SeedTimeTickerAsync(TickerStatus.Queued, "pod-a", old);
        var done = await SeedTimeTickerAsync(TickerStatus.Done, "pod-a", old);
        var doneOccurrence = await SeedOccurrenceAsync(TickerStatus.Done, "pod-a", old);

        var result = await Node("pod-b").CancelExpiredInProgressOfOtherNodesAsync(Lease);

        Assert.Equal(TickerStatus.Queued, (await TimeTickerAsync(queued)).Status);
        Assert.Equal("pod-a", (await TimeTickerAsync(queued)).LockHolder);
        Assert.Equal(TickerStatus.Done, (await TimeTickerAsync(done)).Status);
        Assert.Equal(T0.AddHours(-1), (await TimeTickerAsync(done)).UpdatedAt);
        Assert.Equal(TickerStatus.Done, (await OccurrenceAsync(doneOccurrence)).Status);
        Assert.Equal(0, result.Cancelled);
        Assert.Empty(result.Nodes);
    }

    // T4: the pass of a node leaves the rows of that node alone, however old their lock is.
    [Fact]
    public async Task Cancel_OwnRowsWithAnOldLock_AreNotTouched()
    {
        var old = T0 - Lease - TimeSpan.FromHours(1);
        var own = await SeedTimeTickerAsync(TickerStatus.InProgress, "pod-b", old);
        var ownOccurrence = await SeedOccurrenceAsync(TickerStatus.InProgress, "pod-b", old);

        var result = await Node("pod-b").CancelExpiredInProgressOfOtherNodesAsync(Lease);

        var row = await TimeTickerAsync(own);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("pod-b", row.LockHolder);
        Assert.Equal(old, row.LockedAt);
        var occurrence = await OccurrenceAsync(ownOccurrence);
        Assert.Equal(TickerStatus.InProgress, occurrence.Status);
        Assert.Equal("pod-b", occurrence.LockHolder);
        Assert.Equal(0, result.Cancelled);
    }

    // Two nodes run the pass on the same rows: the second finds nothing left, and the row keeps the first one's write.
    [Fact]
    public async Task Cancel_RunByTwoNodes_CancelsEachRowOnce()
    {
        var expired = await SeedTimeTickerAsync(TickerStatus.InProgress, "pod-a", T0 - Lease - TimeSpan.FromMinutes(1));

        var first = await Node("pod-b").CancelExpiredInProgressOfOtherNodesAsync(Lease);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var second = await Node("pod-c").CancelExpiredInProgressOfOtherNodesAsync(Lease);

        Assert.Equal(1, first.Cancelled);
        Assert.Equal(0, second.Cancelled);
        var row = await TimeTickerAsync(expired);
        Assert.Equal(TickerStatus.Cancelled, row.Status);
        Assert.Equal(T0, row.ExecutedAt);
        Assert.Equal(T0, row.UpdatedAt);
    }

    // The run that still goes on at pod-a after its rows were cancelled: its fenced outcome write, with the claim marker of
    // that run, matches no row (the lock and the marker are gone), so the rows keep the cancel. This pins the fencing the
    // cancel relies on; it is not new behaviour of #211.
    [Fact]
    public async Task Cancel_TheLateFencedOutcomeOfTheRunThatHeldTheRows_WritesNothing()
    {
        var marker = T0.AddMinutes(-20).AddMilliseconds(-3);
        var lockedAt = T0 - Lease - TimeSpan.FromMinutes(1);
        var timeJob = await SeedTimeTickerAsync(TickerStatus.InProgress, "pod-a", lockedAt, marker);
        var occurrence = await SeedOccurrenceAsync(TickerStatus.InProgress, "pod-a", lockedAt, marker);

        var result = await Node("pod-b").CancelExpiredInProgressOfOtherNodesAsync(Lease);
        _clock.Advance(TimeSpan.FromSeconds(1));

        IFencedRunWriter podA = CreateProvider("pod-a", _clock);
        InternalFunctionContext Outcome(Guid id, TickerType type) => new InternalFunctionContext { TickerId = id, Type = type, RunMarker = marker }
            .SetProperty(x => x.Status, TickerStatus.Done)
            .SetProperty(x => x.ExecutedAt, _clock.UtcNow)
            .SetProperty(x => x.ElapsedTime, 1234L)
            .SetProperty(x => x.ReleaseLock, true);
        var timeJobWritten = await podA.UpdateTimeTickerFencedAsync(Outcome(timeJob, TickerType.TimeTicker), marker, onlyWhileRunning: false);
        var occurrenceWritten = await podA.UpdateCronOccurrenceFencedAsync(Outcome(occurrence, TickerType.CronTickerOccurrence), marker, onlyWhileRunning: false);

        Assert.Equal(2, result.Cancelled);
        Assert.Equal(0, timeJobWritten);
        Assert.Equal(0, occurrenceWritten);
        var row = await TimeTickerAsync(timeJob);
        AssertCancelled(row.Status, row.ExecutedAt, row.ExceptionMessage, row.SkippedReason, row.ElapsedTime, row.LockHolder, row.LockedAt, row.UpdatedAt);
        var cronRow = await OccurrenceAsync(occurrence);
        AssertCancelled(cronRow.Status, cronRow.ExecutedAt, cronRow.ExceptionMessage, cronRow.SkippedReason, cronRow.ElapsedTime, cronRow.LockHolder, cronRow.LockedAt, cronRow.UpdatedAt);
    }

    #endregion

    #region Renewal (T3, T5)

    // A running job of this run: renewal moves LockedAt to now and leaves UpdatedAt (the claim marker) as it is. So the
    // pass of another node one lease after the claim does not cancel it.
    [Fact]
    public async Task Renew_OwnTimeJobOfThisRun_MovesLockedAt_KeepsUpdatedAt_AndAnotherNodeDoesNotCancelIt()
    {
        var runStartedAt = T0.AddMinutes(-10);
        var marker = T0.AddMilliseconds(-3);
        var id = await SeedTimeTickerAsync(TickerStatus.InProgress, "pod-a", T0.AddMinutes(-4), marker);

        var renewed = await Node("pod-a").RenewInProgressLeaseAsync(runStartedAt);
        _clock.Advance(Lease - TimeSpan.FromSeconds(1));
        var result = await Node("pod-b").CancelExpiredInProgressOfOtherNodesAsync(Lease);

        var row = await TimeTickerAsync(id);
        Assert.Equal(1, renewed);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("pod-a", row.LockHolder);
        Assert.Equal(T0, row.LockedAt);
        Assert.Equal(marker, row.UpdatedAt);
        Assert.Equal(0, result.Cancelled);
    }

    [Fact]
    public async Task Renew_OwnOccurrenceOfThisRun_MovesLockedAt_KeepsUpdatedAt_AndAnotherNodeDoesNotCancelIt()
    {
        var runStartedAt = T0.AddMinutes(-10);
        var marker = T0.AddMilliseconds(-3);
        var id = await SeedOccurrenceAsync(TickerStatus.InProgress, "pod-a", T0.AddMinutes(-4), marker);

        var renewed = await Node("pod-a").RenewInProgressLeaseAsync(runStartedAt);
        _clock.Advance(Lease - TimeSpan.FromSeconds(1));
        var result = await Node("pod-b").CancelExpiredInProgressOfOtherNodesAsync(Lease);

        var row = await OccurrenceAsync(id);
        Assert.Equal(1, renewed);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("pod-a", row.LockHolder);
        Assert.Equal(T0, row.LockedAt);
        Assert.Equal(marker, row.UpdatedAt);
        Assert.Equal(0, result.Cancelled);
    }

    // The same row without the renewal: the other node records it as Cancelled. The control for the two tests above.
    [Fact]
    public async Task WithoutRenewal_OwnRowOfThisRun_IsRecordedCancelledByAnotherNodeAfterTheLease()
    {
        var id = await SeedTimeTickerAsync(TickerStatus.InProgress, "pod-a", T0.AddMinutes(-4));

        _clock.Advance(Lease - TimeSpan.FromSeconds(1));
        var result = await Node("pod-b").CancelExpiredInProgressOfOtherNodesAsync(Lease);

        Assert.Equal(1, result.Cancelled);
        Assert.Equal(TickerStatus.Cancelled, (await TimeTickerAsync(id)).Status);
    }

    // T3: a row an earlier run of this node left (locked before this run started) is not renewed, so the startup release,
    // which takes rows locked before the start of the run, still frees it when the renewal ran first.
    [Fact]
    public async Task Renew_OwnRowsOfAnEarlierRun_AreNotRenewed_AndTheStartupReleaseStillFreesThem()
    {
        var runStartedAt = T0.AddMinutes(-1);
        var earlier = runStartedAt.AddMinutes(-30);
        var timeJob = await SeedTimeTickerAsync(TickerStatus.InProgress, "pod-a", earlier);
        var occurrence = await SeedOccurrenceAsync(TickerStatus.InProgress, "pod-a", earlier);
        var node = CreateProvider("pod-a", _clock);

        var renewed = await ((IInProgressLease)node).RenewInProgressLeaseAsync(runStartedAt);

        Assert.Equal(0, renewed);
        Assert.Equal(earlier, (await TimeTickerAsync(timeJob)).LockedAt);
        Assert.Equal(earlier, (await OccurrenceAsync(occurrence)).LockedAt);

        await node.ReleaseDeadNodeTimeTickerResources("pod-a", runStartedAt, CancellationToken.None);
        await node.ReleaseDeadNodeOccurrenceResources("pod-a", runStartedAt, CancellationToken.None);

        Assert.Equal(TickerStatus.Idle, (await TimeTickerAsync(timeJob)).Status);
        Assert.Equal(TickerStatus.Idle, (await OccurrenceAsync(occurrence)).Status);
    }

    // T5: renewal does not take back a row that another node released, cancelled or claimed since, and renews no row of another
    // node.
    [Fact]
    public async Task Renew_LeavesRowsThatAnotherNodeReleasedOrHolds_AsTheyAre()
    {
        var runStartedAt = T0.AddMinutes(-10);
        var lockedAt = T0.AddMinutes(-2);
        var released = await SeedTimeTickerAsync(TickerStatus.Idle, null, null);
        var claimedByOther = await SeedTimeTickerAsync(TickerStatus.InProgress, "pod-c", lockedAt);
        var occurrenceOfOther = await SeedOccurrenceAsync(TickerStatus.InProgress, "pod-c", lockedAt);

        var renewed = await Node("pod-a").RenewInProgressLeaseAsync(runStartedAt);

        Assert.Equal(0, renewed);
        var idle = await TimeTickerAsync(released);
        Assert.Equal(TickerStatus.Idle, idle.Status);
        Assert.Null(idle.LockHolder);
        Assert.Null(idle.LockedAt);
        var other = await TimeTickerAsync(claimedByOther);
        Assert.Equal("pod-c", other.LockHolder);
        Assert.Equal(lockedAt, other.LockedAt);
        Assert.Equal(lockedAt, (await OccurrenceAsync(occurrenceOfOther)).LockedAt);
    }

    // Only InProgress rows are renewed: an own row that ended keeps its LockedAt.
    [Fact]
    public async Task Renew_OwnRowThatEnded_IsNotRenewed()
    {
        var runStartedAt = T0.AddMinutes(-10);
        var lockedAt = T0.AddMinutes(-2);
        var done = await SeedTimeTickerAsync(TickerStatus.Done, "pod-a", lockedAt);

        var renewed = await Node("pod-a").RenewInProgressLeaseAsync(runStartedAt);

        Assert.Equal(0, renewed);
        Assert.Equal(lockedAt, (await TimeTickerAsync(done)).LockedAt);
    }

    #endregion
}

public class EfCoreInProgressLeaseSqliteTests : EfCoreInProgressLeaseTests
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<TestSharpTickerDbContext> _options = null!;

    protected override async Task InitializeStoreAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<TestSharpTickerDbContext>().UseSqlite(_connection).Options;
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
    }

    protected override async Task DisposeStoreAsync() => await _connection.DisposeAsync();

    protected override TestSharpTickerDbContext CreateContext() => new(_options);

    internal override TestableProvider CreateProvider(string nodeIdentifier, ITickerClock clock)
    {
        var redisContext = Substitute.For<ISharpTickerRedisContext>();
        redisContext.HasRedisConnection.Returns(false);

        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<TestSharpTickerDbContext>>(new PooledDbContextFactory<TestSharpTickerDbContext>(_options));

        return new TestableProvider(services.BuildServiceProvider(), clock, new SchedulerOptionsBuilder { NodeIdentifier = nodeIdentifier }, redisContext);
    }
}

[Collection(PostgresTestFixture.CollectionName)]
public class EfCoreInProgressLeasePostgresTests(PostgresTestFixture fixture) : EfCoreInProgressLeaseTests
{
    protected override Task InitializeStoreAsync() => fixture.ResetAsync();

    protected override Task DisposeStoreAsync() => Task.CompletedTask;

    protected override TestSharpTickerDbContext CreateContext() => fixture.CreateContext();

    internal override TestableProvider CreateProvider(string nodeIdentifier, ITickerClock clock) => fixture.CreateProvider(nodeIdentifier, clock);
}
