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

    #region Reads and actions on one ticker

    private static string InTwoDays => DateTime.UtcNow.AddDays(2).ToString("O");

    private async Task<Guid[]> AllTimeTickerIdsAsync() => Ids(await _api.GetAsAsync(null, "/time-tickers", admin: true));

    private async Task<JsonElement> CronAsync(Guid id)
        => (await _api.GetAsAsync(null, "/cron-tickers", admin: true)).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == id);

    [Fact]
    public async Task TheOccurrencesOfACronOfAnotherTeam_AreAsIfTheCronDidNotExist()
    {
        var list = await _api.GetAsAsync(Ops, $"/cron-ticker-occurrences/{BillingCronId}");
        var page = await _api.GetAsAsync(Ops, $"/cron-ticker-occurrences/{BillingCronId}/paginated?pageNumber=1&pageSize=20");
        var graph = await _api.GetAsAsync(Ops, $"/cron-ticker-occurrences/{BillingCronId}/graph-data");
        var range = await _api.GetAsAsync(Ops, $"/cron-tickers/graph-data-range-id?id={BillingCronId}&pastDays=-3&futureDays=3");

        Assert.Empty(list.EnumerateArray());
        Assert.Equal(0, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, Total(graph));
        Assert.Equal(0, Total(range));
    }

    [Fact]
    public async Task TheOccurrencesOfACronOfTheUsersTeam_AreReturned()
    {
        var list = await _api.GetAsAsync(Billing, $"/cron-ticker-occurrences/{BillingCronId}");
        var page = await _api.GetAsAsync(Billing, $"/cron-ticker-occurrences/{BillingCronId}/paginated?pageNumber=1&pageSize=20");

        Assert.Equal(2, list.GetArrayLength());
        Assert.Equal(2, page.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task TheRunSummary_LeavesOutTheCronsOfAnotherTeam()
    {
        var summary = await _api.GetAsAsync(Billing, $"/cron-tickers/run-summary?ids={BillingCronId},{OpsCronId}");

        Assert.Equal(new[] { BillingCronId }, Ids(summary));
    }

    [Fact]
    public async Task TheRequestOfATickerOfAnotherTeam_IsNotReturned()
    {
        var own = await _api.GetAsAsync(Billing, $"/ticker-request/{BillingInvoicesId}?tickerType=TimeTicker");
        var other = await _api.GetAsAsync(Ops, $"/ticker-request/{BillingInvoicesId}?tickerType=TimeTicker");

        Assert.Contains("acme", own.GetProperty("result").GetString());
        Assert.Equal(string.Empty, other.GetProperty("result").GetString());
    }

    [Fact]
    public async Task TheRegisteredFunctions_ListOnlyTheOnesTheUserMayUse()
    {
        var billing = (await _api.GetAsAsync(Billing, "/ticker-functions")).EnumerateArray().Select(f => f.GetProperty("functionName").GetString()).ToArray();

        Assert.NotEmpty(billing);
        Assert.All(billing, name => Assert.StartsWith("billing.", name));
        Assert.DoesNotContain("ops.Cleanup", billing);
    }

    [Fact]
    public async Task ATimeTickerOfAnotherTeam_CannotBeDeleted_Alone_OrInABatch()
    {
        await _api.SendAsync(Billing, false, HttpMethod.Delete, $"/time-ticker/delete?id={OpsCleanupId}");
        await _api.SendAsync(Billing, false, HttpMethod.Delete, "/time-ticker/delete-batch", $"[\"{OpsCleanupId}\"]");

        Assert.Contains(OpsCleanupId, await AllTimeTickerIdsAsync());
    }

    [Fact]
    public async Task ATimeTickerOfTheUsersTeam_CanBeAddedUpdatedAndDeleted()
    {
        var (_, added) = await _api.SendAsync(Billing, false, HttpMethod.Post, "/time-ticker/add", $$"""{"function":"billing.Invoices","executionTime":"{{InTwoDays}}"}""");
        Assert.True(added!.Value.GetProperty("success").GetBoolean(), added.ToString());
        var id = added.Value.GetProperty("tickerId").GetGuid();

        var (_, updated) = await _api.SendAsync(Billing, false, HttpMethod.Put, $"/time-ticker/update?id={id}", $$"""{"function":"billing.Reminders","executionTime":"{{InTwoDays}}"}""");
        Assert.True(updated!.Value.GetProperty("success").GetBoolean(), updated.ToString());

        var (_, deleted) = await _api.SendAsync(Billing, false, HttpMethod.Delete, $"/time-ticker/delete?id={id}");
        Assert.True(deleted!.Value.GetProperty("success").GetBoolean(), deleted.ToString());
        Assert.DoesNotContain(id, await AllTimeTickerIdsAsync());
    }

    [Fact]
    public async Task ATimeTickerOfAFunctionTheUserMayNotUse_CannotBeAdded_AlsoAsAChild()
    {
        var before = (await AllTimeTickerIdsAsync()).Length;

        var (_, root) = await _api.SendAsync(Billing, false, HttpMethod.Post, "/time-ticker/add", $$"""{"function":"ops.Cleanup","executionTime":"{{InTwoDays}}"}""");
        var (_, child) = await _api.SendAsync(Billing, false, HttpMethod.Post, "/time-ticker/add",
            $$"""{"function":"billing.Invoices","executionTime":"{{InTwoDays}}","children":[{"function":"ops.Cleanup","runCondition":0}]}""");

        Assert.False(root!.Value.GetProperty("success").GetBoolean());
        Assert.Contains("not allowed", root.Value.GetProperty("message").GetString());
        Assert.False(child!.Value.GetProperty("success").GetBoolean());
        Assert.Contains("not allowed", child.Value.GetProperty("message").GetString());
        Assert.Equal(before, (await AllTimeTickerIdsAsync()).Length);
    }

    [Fact]
    public async Task ATimeTickerOfAnotherTeam_CannotBeUpdated_AndAnOwnOneCannotMoveToAFunctionOfAnotherTeam()
    {
        var (_, other) = await _api.SendAsync(Billing, false, HttpMethod.Put, $"/time-ticker/update?id={OpsCleanupId}", $$"""{"function":"billing.Invoices","executionTime":"{{InTwoDays}}","description":"taken"}""");
        var (_, moved) = await _api.SendAsync(Billing, false, HttpMethod.Put, $"/time-ticker/update?id={BillingRemindersId}", $$"""{"function":"ops.Cleanup","executionTime":"{{InTwoDays}}"}""");

        Assert.False(other!.Value.GetProperty("success").GetBoolean());
        Assert.False(moved!.Value.GetProperty("success").GetBoolean());
        var ops = (await _api.GetAsAsync(Ops, "/time-tickers")).EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == OpsCleanupId);
        Assert.Equal("ops.Cleanup", ops.GetProperty("function").GetString());
        var reminders = (await _api.GetAsAsync(Billing, "/time-tickers")).EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == BillingRemindersId);
        Assert.Equal("billing.Reminders", reminders.GetProperty("function").GetString());
    }

    [Fact]
    public async Task ACronOfTheUsersTeam_CanBeAddedUpdatedToggledAndDeleted()
    {
        var (_, added) = await _api.SendAsync(Billing, false, HttpMethod.Post, "/cron-ticker/add", """{"function":"billing.Nightly","expression":"0 0 4 * * *"}""");
        Assert.True(added!.Value.GetProperty("success").GetBoolean(), added.ToString());
        var id = added.Value.GetProperty("tickerId").GetGuid();

        var (_, updated) = await _api.SendAsync(Billing, false, HttpMethod.Put, $"/cron-ticker/update?id={id}", """{"function":"billing.Nightly","expression":"0 0 5 * * *"}""");
        var (_, toggled) = await _api.SendAsync(Billing, false, HttpMethod.Put, $"/cron-ticker/toggle?id={id}&isEnabled=false");
        var (_, deleted) = await _api.SendAsync(Billing, false, HttpMethod.Delete, $"/cron-ticker/delete?id={id}");

        Assert.True(updated!.Value.GetProperty("success").GetBoolean(), updated.ToString());
        Assert.True(toggled!.Value.GetProperty("success").GetBoolean(), toggled.ToString());
        Assert.True(deleted!.Value.GetProperty("success").GetBoolean(), deleted.ToString());
    }

    [Fact]
    public async Task ACronOfAFunctionTheUserMayNotUse_CannotBeAdded_OrMovedToThatFunction()
    {
        var (_, added) = await _api.SendAsync(Billing, false, HttpMethod.Post, "/cron-ticker/add", """{"function":"ops.Nightly","expression":"0 0 4 * * *"}""");
        var (_, moved) = await _api.SendAsync(Billing, false, HttpMethod.Put, $"/cron-ticker/update?id={BillingCronId}", """{"function":"ops.Nightly","expression":"0 0 2 * * *"}""");

        Assert.False(added!.Value.GetProperty("success").GetBoolean());
        Assert.Contains("not allowed", added.Value.GetProperty("message").GetString());
        Assert.False(moved!.Value.GetProperty("success").GetBoolean());
        Assert.Equal("billing.Nightly", (await CronAsync(BillingCronId)).GetProperty("function").GetString());
    }

    [Fact]
    public async Task ACronOfAnotherTeam_CannotBeUpdatedToggledRunOrDeleted()
    {
        var (_, updated) = await _api.SendAsync(Billing, false, HttpMethod.Put, $"/cron-ticker/update?id={OpsCronId}", """{"function":"billing.Nightly","expression":"0 0 9 * * *"}""");
        var (_, toggled) = await _api.SendAsync(Billing, false, HttpMethod.Put, $"/cron-ticker/toggle?id={OpsCronId}&isEnabled=false");
        var (run, _) = await _api.SendAsync(Billing, false, HttpMethod.Post, $"/cron-ticker/run?id={OpsCronId}");
        await _api.SendAsync(Billing, false, HttpMethod.Delete, $"/cron-ticker/delete?id={OpsCronId}");

        Assert.False(updated!.Value.GetProperty("success").GetBoolean());
        Assert.False(toggled!.Value.GetProperty("success").GetBoolean());
        Assert.Equal(404, run);
        var cron = await CronAsync(OpsCronId);
        Assert.Equal("ops.Nightly", cron.GetProperty("function").GetString());
        Assert.Equal("0 0 3 * * *", cron.GetProperty("expression").GetString());
        Assert.True(cron.GetProperty("isEnabled").GetBoolean());
        var occurrences = await _api.GetAsAsync(Ops, $"/cron-ticker-occurrences/{OpsCronId}");
        Assert.Equal(1, occurrences.GetArrayLength()); // nothing was queued by the refused run
    }

    [Fact]
    public async Task AnOccurrenceOfACronOfAnotherTeam_CannotBeDeleted()
    {
        await _api.SendAsync(Billing, false, HttpMethod.Delete, $"/cron-ticker-occurrence/delete?id={OpsOccurrenceDoneId}");

        var occurrences = await _api.GetAsAsync(Ops, $"/cron-ticker-occurrences/{OpsCronId}");
        Assert.Equal(1, occurrences.GetArrayLength());
    }

    [Fact]
    public async Task ATickerOfAnotherTeam_CannotBeCancelled_ButTheUsersOwnCan()
    {
        using var others = new System.Threading.CancellationTokenSource();
        using var own = new System.Threading.CancellationTokenSource();
        Register(OpsCleanupId, others);
        Register(BillingInvoicesId, own);
        try
        {
            var (hidden, body) = await _api.SendAsync(Billing, false, HttpMethod.Post, $"/ticker/cancel?id={OpsCleanupId}");
            var (allowed, _) = await _api.SendAsync(Billing, false, HttpMethod.Post, $"/ticker/cancel?id={BillingInvoicesId}");

            Assert.Equal(400, hidden);
            Assert.Equal("not-running-on-this-node", body!.Value.GetProperty("reason").GetString());
            Assert.False(others.IsCancellationRequested, "the job of another team was cancelled");
            Assert.Equal(200, allowed);
            Assert.True(own.IsCancellationRequested);
        }
        finally
        {
            SharpTicker.Utilities.TickerCancellationTokenManager.RemoveTickerCancellationToken(OpsCleanupId);
            SharpTicker.Utilities.TickerCancellationTokenManager.RemoveTickerCancellationToken(BillingInvoicesId);
        }

        static void Register(Guid id, System.Threading.CancellationTokenSource source)
            => SharpTicker.Utilities.TickerCancellationTokenManager.AddTickerCancellationToken(source,
                new SharpTicker.Utilities.Models.InternalFunctionContext { TickerId = id, FunctionName = "Test", Type = SharpTicker.Utilities.Enums.TickerType.TimeTicker }, isDue: false);
    }

    [Fact]
    public async Task UpdatingATicker_CannotWriteToATickerOfAnotherTeamThroughItsChildren()
    {
        var (_, added) = await _api.SendAsync(Billing, false, HttpMethod.Post, "/time-ticker/add", $$"""{"function":"billing.Reminders","executionTime":"{{InTwoDays}}"}""");
        var own = added!.Value.GetProperty("tickerId").GetGuid();

        var (_, body) = await _api.SendAsync(Billing, false, HttpMethod.Put, $"/time-ticker/update?id={own}",
            $$"""{"function":"billing.Reminders","executionTime":"{{InTwoDays}}","children":[{"id":"{{OpsCleanupId}}","function":"billing.Invoices","executionTime":"{{InTwoDays}}","description":"taken"}]}""");

        Assert.True(body!.Value.GetProperty("success").GetBoolean(), body.ToString());
        var ops = (await _api.GetAsAsync(Ops, "/time-tickers")).EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == OpsCleanupId);
        Assert.Equal("ops.Cleanup", ops.GetProperty("function").GetString());
        Assert.NotEqual("taken", ops.GetProperty("description").GetString());
        await _api.SendAsync(Billing, false, HttpMethod.Delete, $"/time-ticker/delete?id={own}");
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
