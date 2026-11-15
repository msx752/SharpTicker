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

namespace SharpTicker.EntityFrameworkCore.Tests.Infrastructure;

/// <summary>
/// The lease on InProgress rows (#24), on the store: the node that holds a row renews its <c>LockedAt</c>, and a node
/// releases the rows of other node identifiers whose <c>LockedAt</c> is older than the lease. Provider instances with
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

    private static void AssertReleased(TickerStatus status, string? lockHolder, DateTime? lockedAt, DateTime updatedAt)
    {
        Assert.Equal(TickerStatus.Idle, status);
        Assert.Null(lockHolder);
        Assert.Null(lockedAt);
        Assert.Equal(T0, updatedAt);
    }

    #region Release of other nodes' rows (T1, T4)

    // T1: a row of another node whose lock is older than the lease is released with the dead-node write; one whose lock
    // is younger stays.
    [Fact]
    public async Task Release_TimeJobOfAnotherNodeLockedLongerThanTheLease_IsReleased_AndAYoungerOneStays()
    {
        var expired = await SeedTimeTickerAsync(TickerStatus.InProgress, "pod-a", T0 - Lease - TimeSpan.FromSeconds(1));
        var young = await SeedTimeTickerAsync(TickerStatus.InProgress, "pod-a", T0 - Lease + TimeSpan.FromSeconds(10));

        var result = await Node("pod-b").ReleaseExpiredInProgressOfOtherNodesAsync(Lease);

        var released = await TimeTickerAsync(expired);
        AssertReleased(released.Status, released.LockHolder, released.LockedAt, released.UpdatedAt);
        var kept = await TimeTickerAsync(young);
        Assert.Equal(TickerStatus.InProgress, kept.Status);
        Assert.Equal("pod-a", kept.LockHolder);
        Assert.Equal(T0 - Lease + TimeSpan.FromSeconds(10), kept.LockedAt);
        Assert.Equal(1, result.Released);
        Assert.Equal(("pod-a", 1), Assert.Single(result.Nodes));
    }

    [Fact]
    public async Task Release_OccurrenceOfAnotherNodeLockedLongerThanTheLease_IsReleased_AndAYoungerOneStays()
    {
        var expired = await SeedOccurrenceAsync(TickerStatus.InProgress, "pod-a", T0 - Lease - TimeSpan.FromSeconds(1));
        var young = await SeedOccurrenceAsync(TickerStatus.InProgress, "pod-a", T0 - Lease + TimeSpan.FromSeconds(10));

        var result = await Node("pod-b").ReleaseExpiredInProgressOfOtherNodesAsync(Lease);

        var released = await OccurrenceAsync(expired);
        AssertReleased(released.Status, released.LockHolder, released.LockedAt, released.UpdatedAt);
        var kept = await OccurrenceAsync(young);
        Assert.Equal(TickerStatus.InProgress, kept.Status);
        Assert.Equal("pod-a", kept.LockHolder);
        Assert.Equal(1, result.Released);
        Assert.Equal(("pod-a", 1), Assert.Single(result.Nodes));
    }

    // Only InProgress rows: a row of another node that is Queued, or that ended and kept its lock holder, is not changed.
    [Fact]
    public async Task Release_RowsOfAnotherNodeThatAreNotInProgress_AreNotTouched()
    {
        var old = T0 - Lease - TimeSpan.FromHours(1);
        var queued = await SeedTimeTickerAsync(TickerStatus.Queued, "pod-a", old);
        var done = await SeedTimeTickerAsync(TickerStatus.Done, "pod-a", old);
        var doneOccurrence = await SeedOccurrenceAsync(TickerStatus.Done, "pod-a", old);

        var result = await Node("pod-b").ReleaseExpiredInProgressOfOtherNodesAsync(Lease);

        Assert.Equal(TickerStatus.Queued, (await TimeTickerAsync(queued)).Status);
        Assert.Equal("pod-a", (await TimeTickerAsync(queued)).LockHolder);
        Assert.Equal(TickerStatus.Done, (await TimeTickerAsync(done)).Status);
        Assert.Equal(T0.AddHours(-1), (await TimeTickerAsync(done)).UpdatedAt);
        Assert.Equal(TickerStatus.Done, (await OccurrenceAsync(doneOccurrence)).Status);
        Assert.Equal(0, result.Released);
        Assert.Empty(result.Nodes);
    }

    // T4: the release pass of a node leaves the rows of that node alone, however old their lock is.
    [Fact]
    public async Task Release_OwnRowsWithAnOldLock_AreNotTouched()
    {
        var old = T0 - Lease - TimeSpan.FromHours(1);
        var own = await SeedTimeTickerAsync(TickerStatus.InProgress, "pod-b", old);
        var ownOccurrence = await SeedOccurrenceAsync(TickerStatus.InProgress, "pod-b", old);

        var result = await Node("pod-b").ReleaseExpiredInProgressOfOtherNodesAsync(Lease);

        var row = await TimeTickerAsync(own);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("pod-b", row.LockHolder);
        Assert.Equal(old, row.LockedAt);
        var occurrence = await OccurrenceAsync(ownOccurrence);
        Assert.Equal(TickerStatus.InProgress, occurrence.Status);
        Assert.Equal("pod-b", occurrence.LockHolder);
        Assert.Equal(0, result.Released);
    }

    // Two nodes run the pass on the same rows: the second finds nothing left.
    [Fact]
    public async Task Release_RunByTwoNodes_ReleasesEachRowOnce()
    {
        var expired = await SeedTimeTickerAsync(TickerStatus.InProgress, "pod-a", T0 - Lease - TimeSpan.FromMinutes(1));

        var first = await Node("pod-b").ReleaseExpiredInProgressOfOtherNodesAsync(Lease);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var second = await Node("pod-c").ReleaseExpiredInProgressOfOtherNodesAsync(Lease);

        Assert.Equal(1, first.Released);
        Assert.Equal(0, second.Released);
        Assert.Equal(T0, (await TimeTickerAsync(expired)).UpdatedAt);
    }

    #endregion

    #region Renewal (T3, T5)

    // A running job of this run: renewal moves LockedAt to now and leaves UpdatedAt (the claim marker) as it is. So the
    // release pass of another node one lease after the claim does not take it.
    [Fact]
    public async Task Renew_OwnTimeJobOfThisRun_MovesLockedAt_KeepsUpdatedAt_AndAnotherNodeDoesNotReleaseIt()
    {
        var runStartedAt = T0.AddMinutes(-10);
        var marker = T0.AddMilliseconds(-3);
        var id = await SeedTimeTickerAsync(TickerStatus.InProgress, "pod-a", T0.AddMinutes(-4), marker);

        var renewed = await Node("pod-a").RenewInProgressLeaseAsync(runStartedAt);
        _clock.Advance(Lease - TimeSpan.FromSeconds(1));
        var result = await Node("pod-b").ReleaseExpiredInProgressOfOtherNodesAsync(Lease);

        var row = await TimeTickerAsync(id);
        Assert.Equal(1, renewed);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("pod-a", row.LockHolder);
        Assert.Equal(T0, row.LockedAt);
        Assert.Equal(marker, row.UpdatedAt);
        Assert.Equal(0, result.Released);
    }

    [Fact]
    public async Task Renew_OwnOccurrenceOfThisRun_MovesLockedAt_KeepsUpdatedAt_AndAnotherNodeDoesNotReleaseIt()
    {
        var runStartedAt = T0.AddMinutes(-10);
        var marker = T0.AddMilliseconds(-3);
        var id = await SeedOccurrenceAsync(TickerStatus.InProgress, "pod-a", T0.AddMinutes(-4), marker);

        var renewed = await Node("pod-a").RenewInProgressLeaseAsync(runStartedAt);
        _clock.Advance(Lease - TimeSpan.FromSeconds(1));
        var result = await Node("pod-b").ReleaseExpiredInProgressOfOtherNodesAsync(Lease);

        var row = await OccurrenceAsync(id);
        Assert.Equal(1, renewed);
        Assert.Equal(TickerStatus.InProgress, row.Status);
        Assert.Equal("pod-a", row.LockHolder);
        Assert.Equal(T0, row.LockedAt);
        Assert.Equal(marker, row.UpdatedAt);
        Assert.Equal(0, result.Released);
    }

    // The same row without the renewal: the other node releases it. The control for the two tests above.
    [Fact]
    public async Task WithoutRenewal_OwnRowOfThisRun_IsReleasedByAnotherNodeAfterTheLease()
    {
        var id = await SeedTimeTickerAsync(TickerStatus.InProgress, "pod-a", T0.AddMinutes(-4));

        _clock.Advance(Lease - TimeSpan.FromSeconds(1));
        var result = await Node("pod-b").ReleaseExpiredInProgressOfOtherNodesAsync(Lease);

        Assert.Equal(1, result.Released);
        Assert.Equal(TickerStatus.Idle, (await TimeTickerAsync(id)).Status);
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

    // T5: renewal does not take back a row that another node released or claimed since, and renews no row of another
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
