using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using static SharpTicker.Tests.VisibilityFixtureSetup;

namespace SharpTicker.Tests;

/// <summary>
/// The dashboard API with a visibility rule: a user only gets what the rule lets them see, from every list, page, chart
/// and count. The rule here is a team prefix on the function name; admins see everything. The same tests run on each
/// store, because the rule is applied inside the query and each store has its own way of running one.
/// Status numbers are TickerStatus: 3 Done, 5 Failed.
/// </summary>
public abstract class DashboardVisibilityApiTests<TFixture> : IClassFixture<TFixture> where TFixture : DashboardApiFixture
{
    private const int Done = 3, Failed = 5;

    private readonly TFixture _api;

    protected DashboardVisibilityApiTests(TFixture api) => _api = api;

    private static Guid[] Ids(JsonElement items) => items.EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray();

    private static Dictionary<int, int> Counts(JsonElement tuples) =>
        tuples.EnumerateArray().ToDictionary(t => t.GetProperty("item1").GetInt32(), t => t.GetProperty("item2").GetInt32());

    /// <summary>These endpoints list every status, with 0 for the ones that did not occur; only the others matter here.</summary>
    private static Dictionary<int, int> NonZero(Dictionary<int, int> counts) => counts.Where(c => c.Value > 0).ToDictionary(c => c.Key, c => c.Value);

    private static int Total(JsonElement days) =>
        days.EnumerateArray().Sum(day => day.GetProperty("results").EnumerateArray().Sum(r => r.GetProperty("item2").GetInt32()));

    #region Who gets in

    [Fact]
    public async Task ARequestThatIsNotSignedIn_IsRefused()
    {
        var (status, _) = await _api.SendAsync(null, false, HttpMethod.Get, "/time-tickers");

        Assert.Equal(401, status);
    }

    #endregion

    #region Lists and pages

    [Fact]
    public async Task TimeTickers_ListsOnlyTheTickersOfTheUsersTeam()
    {
        var billing = Ids(await _api.GetAsAsync(Billing, "/time-tickers"));
        var ops = Ids(await _api.GetAsAsync(Ops, "/time-tickers"));

        Assert.Equal(new[] { BillingInvoicesId, BillingRemindersId }.OrderBy(x => x), billing.OrderBy(x => x));
        Assert.Equal(new[] { OpsCleanupId }, ops);
    }

    [Fact]
    public async Task AnAdmin_SeesEveryTimeTicker()
    {
        var all = Ids(await _api.GetAsAsync(null, "/time-tickers", admin: true));

        Assert.Contains(BillingInvoicesId, all);
        Assert.Contains(OpsCleanupId, all);
        Assert.Contains(DashboardApiFixture.QueuedId, all); // seeded by the standard fixture, in no team
    }

    [Fact]
    public async Task TimeTickersPaginated_CountsAndPagesOnlyWhatTheUserMaySee()
    {
        var first = await _api.GetAsAsync(Billing, "/time-tickers/paginated?pageNumber=1&pageSize=1");
        var second = await _api.GetAsAsync(Billing, "/time-tickers/paginated?pageNumber=2&pageSize=1");

        Assert.Equal(2, first.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, first.GetProperty("totalPages").GetInt32());
        var seen = Ids(first.GetProperty("items")).Concat(Ids(second.GetProperty("items"))).ToArray();
        Assert.Equal(new[] { BillingInvoicesId, BillingRemindersId }.OrderBy(x => x), seen.OrderBy(x => x));
    }

    [Fact]
    public async Task TimeTickersPaginated_KeepsTheStatusAndSearchFilters_InsideTheTeam()
    {
        var failed = await _api.GetAsAsync(Billing, $"/time-tickers/paginated?pageNumber=1&pageSize=20&status={Failed}");
        var searched = await _api.GetAsAsync(Billing, "/time-tickers/paginated?pageNumber=1&pageSize=20&search=Cleanup");

        Assert.Equal(new[] { BillingRemindersId }, Ids(failed.GetProperty("items")));
        // "Cleanup" exists, but in the ops team
        Assert.Equal(0, searched.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task CronTickers_ListsOnlyTheCronsOfTheUsersTeam()
    {
        var billing = Ids(await _api.GetAsAsync(Billing, "/cron-tickers"));
        var ops = Ids(await _api.GetAsAsync(Ops, "/cron-tickers"));
        var admin = Ids(await _api.GetAsAsync(null, "/cron-tickers", admin: true));

        Assert.Equal(new[] { BillingCronId }, billing);
        Assert.Equal(new[] { OpsCronId }, ops);
        Assert.Contains(BillingCronId, admin);
        Assert.Contains(OpsCronId, admin);
        Assert.Contains(DashboardApiFixture.CronId, admin);
    }

    [Fact]
    public async Task CronTickersPaginated_CountsOnlyWhatTheUserMaySee()
    {
        var page = await _api.GetAsAsync(Billing, "/cron-tickers/paginated?pageNumber=1&pageSize=20");

        Assert.Equal(1, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(new[] { BillingCronId }, Ids(page.GetProperty("items")));
    }

    [Fact]
    public async Task ATeamWithoutTickers_SeesEmptyListsAndNotAnError()
    {
        var times = await _api.GetAsAsync("nobody", "/time-tickers");
        var crons = await _api.GetAsAsync("nobody", "/cron-tickers/paginated?pageNumber=1&pageSize=20");

        Assert.Empty(times.EnumerateArray());
        Assert.Equal(0, crons.GetProperty("totalCount").GetInt32());
    }

    #endregion

    #region Charts and counts

    [Fact]
    public async Task TimeTickerStatusCounts_IncludeOnlyVisibleTickers()
    {
        var billing = NonZero(Counts(await _api.GetAsAsync(Billing, "/time-tickers/graph-data")));
        var ops = NonZero(Counts(await _api.GetAsAsync(Ops, "/time-tickers/graph-data")));

        Assert.Equal(new Dictionary<int, int> { [Done] = 1, [Failed] = 1 }, billing);
        Assert.Equal(new Dictionary<int, int> { [Done] = 1 }, ops);
    }

    [Fact]
    public async Task CronStatusCounts_IncludeOnlyTheOccurrencesOfVisibleCrons()
    {
        var billing = NonZero(Counts(await _api.GetAsAsync(Billing, "/cron-tickers/graph-data")));
        var ops = NonZero(Counts(await _api.GetAsAsync(Ops, "/cron-tickers/graph-data")));

        Assert.Equal(new Dictionary<int, int> { [Done] = 1, [Failed] = 1 }, billing);
        Assert.Equal(new Dictionary<int, int> { [Done] = 1 }, ops);
    }

    [Fact]
    public async Task TheChartsOverTime_IncludeOnlyVisibleTickers()
    {
        var times = await _api.GetAsAsync(Billing, "/time-tickers/graph-data-range?pastDays=-3&futureDays=3");
        var crons = await _api.GetAsAsync(Billing, "/cron-tickers/graph-data-range?pastDays=-3&futureDays=3");

        Assert.Equal(2, Total(times));
        Assert.Equal(2, Total(crons));
    }

    [Fact]
    public async Task TheOverallAndLastWeekStatistics_IncludeOnlyVisibleTickers()
    {
        var overall = Counts(await _api.GetAsAsync(Billing, "/ticker/statuses/get"));
        var lastWeek = Counts(await _api.GetAsAsync(Billing, "/ticker/statuses/get-last-week"));
        var admin = Counts(await _api.GetAsAsync(null, "/ticker/statuses/get", admin: true));

        // One done and one failed time ticker plus one done and one failed cron occurrence
        Assert.Equal(2, overall[Done]);
        Assert.Equal(2, overall[Failed]);
        Assert.Equal(4, lastWeek[2]); // item1 2 is every status
        Assert.True(admin[Done] > overall[Done], "an admin sees more than one team");
    }

    #endregion
}

[Collection(SharedStaticStateCollection.Name)]
public class InMemoryDashboardVisibilityApiTests : DashboardVisibilityApiTests<InMemoryVisibilityApiFixture>
{
    public InMemoryDashboardVisibilityApiTests(InMemoryVisibilityApiFixture api) : base(api) { }
}

[Collection(SharedStaticStateCollection.Name)]
public class EfCoreDashboardVisibilityApiTests : DashboardVisibilityApiTests<EfCoreVisibilityApiFixture>
{
    public EfCoreDashboardVisibilityApiTests(EfCoreVisibilityApiFixture api) : base(api) { }
}
