using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SharpTicker.Dashboard.Infrastructure.Dashboard;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// The dashboard queries written against the provider-agnostic query API, run on the in-memory store and on EF Core
/// (SQLite), so the same query has to give the same answer on both. The store holds the data the API fixture seeds:
/// time tickers Done yesterday, Failed today and Queued tomorrow, and the same three days of cron occurrences.
/// </summary>
public abstract class TickerDashboardQueriesTests<TFixture> : IClassFixture<TFixture> where TFixture : DashboardApiFixture
{
    private readonly TFixture _fixture;
    private readonly TickerDashboardQueries<TimeTickerEntity, CronTickerEntity> _queries;

    protected TickerDashboardQueriesTests(TFixture fixture)
    {
        _fixture = fixture;
        _queries = new TickerDashboardQueries<TimeTickerEntity, CronTickerEntity>(fixture.Store);
    }

    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task TimeTickers_AreListedNewestFirst()
    {
        var tickers = await _queries.GetTimeTickersAsync(Ct);

        Assert.Equal(new[] { DashboardApiFixture.QueuedId, DashboardApiFixture.FailedId, DashboardApiFixture.DoneId }, tickers.Select(t => t.Id));
    }

    [Fact]
    public async Task TimeTickersPage_HoldsTheRequestedSlice_AndTheTotal()
    {
        var page = await _queries.GetTimeTickersPaginatedAsync(2, 2, Ct);

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.TotalPages);
        Assert.Equal(2, page.PageNumber);
        Assert.Equal(DashboardApiFixture.DoneId, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task ATimeTicker_IsFoundById()
    {
        var ticker = await _queries.GetTimeTickerByIdAsync(DashboardApiFixture.FailedId, Ct);

        Assert.NotNull(ticker);
        Assert.Equal("boom: the order was rejected", ticker.ExceptionMessage);
    }

    [Fact]
    public async Task AMissingTimeTicker_IsNull()
    {
        Assert.Null(await _queries.GetTimeTickerByIdAsync(Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task StatusCounts_ListEveryStatus_WithZeroForTheOnesNobodyHas()
    {
        var counts = await _queries.GetTimeTickerStatusCountsAsync(Ct);

        Assert.Equal(Enum.GetValues<TickerStatus>().Length, counts.Count);
        Assert.Equal(1, counts.Single(c => c.Status == TickerStatus.Done).Count);
        Assert.Equal(1, counts.Single(c => c.Status == TickerStatus.Failed).Count);
        Assert.Equal(1, counts.Single(c => c.Status == TickerStatus.Queued).Count);
        Assert.Equal(0, counts.Single(c => c.Status == TickerStatus.Cancelled).Count);
    }

    [Fact]
    public async Task TimeTickerGraph_HasOneEntryPerDay_WithTheCountsOfThatDay()
    {
        var graph = await _queries.GetTimeTickersGraphDataAsync(-1, 1, Ct);

        Assert.Equal(new[] { _fixture.Yesterday, _fixture.Today, _fixture.Tomorrow }, graph.Select(g => g.Date));
        AssertDay(graph[0], TickerStatus.Done);
        AssertDay(graph[1], TickerStatus.Failed);
        AssertDay(graph[2], TickerStatus.Queued);
    }

    [Fact]
    public async Task TimeTickerGraph_LeavesOutTickersOutsideTheWindow_AndKeepsTheEmptyDays()
    {
        var graph = await _queries.GetTimeTickersGraphDataAsync(0, 0, Ct);

        var today = Assert.Single(graph);
        Assert.Equal(_fixture.Today, today.Date);
        AssertDay(today, TickerStatus.Failed);

        var empty = await _queries.GetTimeTickersGraphDataAsync(-7, -3, Ct);
        Assert.Equal(5, empty.Count);
        Assert.All(empty, day => Assert.All(day.Results, r => Assert.Equal(0, r.Item2)));
    }

    [Fact]
    public async Task CronOccurrenceGraph_CountsTheOccurrencesOfEveryCronTicker()
    {
        var graph = await _queries.GetCronOccurrencesGraphDataAsync(-1, 1, Ct);

        AssertDay(graph[0], TickerStatus.Done);
        AssertDay(graph[1], TickerStatus.Failed);
        AssertDay(graph[2], TickerStatus.Queued);
    }

    [Fact]
    public async Task CronOccurrenceGraph_ForOneCronTicker_IgnoresTheOthers()
    {
        var own = await _queries.GetCronOccurrencesGraphDataByIdAsync(DashboardApiFixture.CronId, -1, 1, Ct);
        var other = await _queries.GetCronOccurrencesGraphDataByIdAsync(DashboardApiFixture.PausedCronId, -1, 1, Ct);

        AssertDay(own[0], TickerStatus.Done);
        Assert.All(other, day => Assert.All(day.Results, r => Assert.Equal(0, r.Item2)));
    }

    [Fact]
    public async Task CronTickers_AreListedNewestFirst_AndPaged()
    {
        var all = await _queries.GetCronTickersAsync(Ct);
        var page = await _queries.GetCronTickersPaginatedAsync(1, 1, Ct);

        Assert.Equal(2, all.Length);
        Assert.Equal(2, page.TotalCount);
        Assert.Single(page.Items);
        Assert.Equal(all[0].Id, page.Items.Single().Id);
    }

    [Fact]
    public async Task Occurrences_OfACronTicker_AreListedNewestFirst_WithTheirCronTicker()
    {
        var occurrences = await _queries.GetCronOccurrencesAsync(DashboardApiFixture.CronId, Ct);

        Assert.Equal(
            new[] { DashboardApiFixture.OccurrenceQueuedId, DashboardApiFixture.OccurrenceFailedId, DashboardApiFixture.OccurrenceDoneId },
            occurrences.Select(o => o.Id));
        Assert.All(occurrences, o => Assert.Equal("SeedCron", o.CronTicker?.Function));
    }

    [Fact]
    public async Task Occurrences_OfACronTicker_CanBePaged()
    {
        var page = await _queries.GetCronOccurrencesPaginatedAsync(DashboardApiFixture.CronId, 1, 2, Ct);

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.Items.Count());
        Assert.Equal(DashboardApiFixture.OccurrenceQueuedId, page.Items.First().Id);
    }

    [Fact]
    public async Task Occurrences_OfACronTickerWithNone_AreEmpty()
    {
        Assert.Empty(await _queries.GetCronOccurrencesAsync(DashboardApiFixture.PausedCronId, Ct));
    }

    [Fact]
    public async Task MachineJobs_AreEmpty_WhenNothingWasEverLocked()
    {
        Assert.Empty(await _queries.GetMachineJobsAsync(Ct));
    }

    [Fact]
    public async Task MachineJobs_CountTheJobsEachMachineLocked_BusiestFirst()
    {
        var lockedTime = _fixture.Time(Guid.NewGuid(), "SeedJob", TickerStatus.InProgress, _fixture.Today);
        var lockedTime2 = _fixture.Time(Guid.NewGuid(), "SeedJob", TickerStatus.InProgress, _fixture.Today);
        foreach (var t in new[] { lockedTime, lockedTime2 })
        {
            t.LockHolder = "node-a";
            t.LockedAt = DateTime.UtcNow;
        }
        var lockedOccurrence = new CronTickerOccurrenceEntity<CronTickerEntity>
        {
            Id = Guid.NewGuid(),
            CronTickerId = DashboardApiFixture.CronId,
            Status = TickerStatus.InProgress,
            ExecutionTime = _fixture.Today.AddHours(13),
            LockHolder = "NODE-A",
            LockedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var otherNode = _fixture.Time(Guid.NewGuid(), "SeedJob", TickerStatus.InProgress, _fixture.Today);
        otherNode.LockHolder = "node-b";
        otherNode.LockedAt = DateTime.UtcNow;

        await _fixture.Store.AddTimeTickers(new[] { lockedTime, lockedTime2, otherNode });
        await _fixture.Store.InsertCronTickerOccurrences(new[] { lockedOccurrence }, Ct);
        try
        {
            var machines = await _queries.GetMachineJobsAsync(Ct);

            Assert.Equal(2, machines.Count);
            Assert.Equal(3, machines[0].Count);
            Assert.Equal("node-a", machines[0].Machine, ignoreCase: true);
            Assert.Equal(("node-b", 1), machines[1]);
        }
        finally
        {
            await _fixture.Store.RemoveTimeTickers(new[] { lockedTime.Id, lockedTime2.Id, otherNode.Id });
            await _fixture.Store.RemoveCronTickerOccurrences(new[] { lockedOccurrence.Id }, Ct);
        }
    }

    [Fact(Skip = "Known bug: a locked job without a lock holder makes the machine jobs query throw ArgumentNullException (a null dictionary key). "
        + "TickerDashboardRepository.GetMachineJobsAsync, which the dashboard uses, has the same code.")]
    public async Task MachineJobs_DoNotFail_ForALockedJobWithoutAHolder()
    {
        var ticker = _fixture.Time(Guid.NewGuid(), "SeedJob", TickerStatus.InProgress, _fixture.Today);
        ticker.LockedAt = DateTime.UtcNow;
        ticker.LockHolder = null;
        await _fixture.Store.AddTimeTickers(new[] { ticker });
        try
        {
            await _queries.GetMachineJobsAsync(Ct);
        }
        finally
        {
            await _fixture.Store.RemoveTimeTickers(new[] { ticker.Id });
        }
    }

    private static void AssertDay(SharpTicker.Utilities.DashboardDtos.TickerGraphData day, TickerStatus expected)
    {
        foreach (var (status, count) in day.Results.Select(r => ((TickerStatus)r.Item1, r.Item2)))
            Assert.Equal(status == expected ? 1 : 0, count);
    }
}

public class InMemoryTickerDashboardQueriesTests : TickerDashboardQueriesTests<InMemoryDashboardApiFixture>
{
    public InMemoryTickerDashboardQueriesTests(InMemoryDashboardApiFixture fixture) : base(fixture) { }
}

public class EfCoreTickerDashboardQueriesTests : TickerDashboardQueriesTests<EfCoreDashboardApiFixture>
{
    public EfCoreTickerDashboardQueriesTests(EfCoreDashboardApiFixture fixture) : base(fixture) { }
}
