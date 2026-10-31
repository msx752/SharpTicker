using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SharpTicker.Utilities.Enums;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// The dashboard read API over HTTP, against the seeded store of the fixture it is run with, so the same tests run
/// against every store (see <see cref="DashboardApiFixture"/>).
/// Status numbers are TickerStatus: 0 Idle, 1 Queued, 2 InProgress, 3 Done, 4 DueDone, 5 Failed, 6 Cancelled, 7 Skipped.
/// </summary>
public abstract class DashboardApiReadTests<TFixture> : IClassFixture<TFixture> where TFixture : DashboardApiFixture
{
    private const int Queued = 1, Done = 3, Failed = 5;

    private readonly TFixture _api;

    protected DashboardApiReadTests(TFixture api) => _api = api;

    private static Guid[] Ids(JsonElement items) => items.EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray();

    private static Dictionary<int, int> Counts(JsonElement tuples) =>
        tuples.EnumerateArray().ToDictionary(t => t.GetProperty("item1").GetInt32(), t => t.GetProperty("item2").GetInt32());

    #region Options and host

    [Fact]
    public async Task Options_DescribeTheHost()
    {
        var options = await _api.GetAsync("/options");

        Assert.True(options.GetProperty("maxConcurrency").GetInt32() > 0);
        Assert.False(string.IsNullOrEmpty(options.GetProperty("currentMachine").GetString()));
        Assert.False(string.IsNullOrEmpty(options.GetProperty("schedulerTimeZone").GetString()));
    }

    [Fact]
    public async Task HostStatus_IsNotRunning_WhenTheSchedulerIsDisabled()
    {
        var status = await _api.GetAsync("/ticker-host/status");

        Assert.False(status.GetProperty("isRunning").GetBoolean());
    }

    [Fact]
    public async Task NextTicker_HasNoOccurrence_BeforeTheSchedulerHasPlannedAnything()
    {
        var next = await _api.GetAsync("/ticker-host/next-ticker");

        // The scheduler is disabled in this host, so nothing was ever planned.
        Assert.True(next.ValueKind == System.Text.Json.JsonValueKind.Object);
        Assert.True(!next.TryGetProperty("nextOccurrence", out var value) || value.ValueKind == System.Text.Json.JsonValueKind.Null, next.ToString());
    }

    [Fact]
    public async Task TickerFunctions_ListsTheRegisteredFunctions()
    {
        var names = (await _api.GetAsync("/ticker-functions")).EnumerateArray().Select(f => f.GetProperty("functionName").GetString()).ToArray();

        Assert.Contains("SeedJob", names);
        Assert.Contains("SeedCron", names);
        Assert.Contains(DashboardApiFixture.TypedFunction, names);
    }

    [Fact]
    public async Task TickerFunctions_GiveAnExampleRequestForAFunctionWithATypedRequest()
    {
        // The dashboard offers this text in the dialogs. An application that never configured a JSON context
        // still gets it, which a missing resolver used to prevent.
        var typed = (await _api.GetAsync("/ticker-functions")).EnumerateArray()
            .Single(f => f.GetProperty("functionName").GetString() == DashboardApiFixture.TypedFunction);

        Assert.Equal(typeof(DashboardApiFixture.ApiOrder).FullName, typed.GetProperty("functionRequestNamespace").GetString());
        using var example = JsonDocument.Parse(typed.GetProperty("functionRequestType").GetString()!);
        Assert.Equal(JsonValueKind.String, example.RootElement.GetProperty("OrderId").ValueKind);
        Assert.Equal(JsonValueKind.Number, example.RootElement.GetProperty("Amount").ValueKind);
    }

    [Fact]
    public async Task TickerFunctions_HaveNoExample_ForAFunctionWithoutARequest()
    {
        var plain = (await _api.GetAsync("/ticker-functions")).EnumerateArray()
            .Single(f => f.GetProperty("functionName").GetString() == "SeedJob");

        Assert.Equal(JsonValueKind.Null, plain.GetProperty("functionRequestType").ValueKind);
    }

    [Fact]
    public async Task AuthInfo_SaysAuthenticationIsOff_ForADashboardWithoutAuth()
    {
        var info = await _api.GetAsync("/auth/info");

        Assert.Equal("none", info.GetProperty("mode").GetString());
        Assert.False(info.GetProperty("enabled").GetBoolean());
    }

    #endregion

    #region Time tickers

    [Fact]
    public async Task TimeTickers_ListsEveryTicker_NewestExecutionTimeFirst()
    {
        var tickers = await _api.GetAsync("/time-tickers");

        Assert.Equal(new[] { DashboardApiFixture.QueuedId, DashboardApiFixture.FailedId, DashboardApiFixture.DoneId }, Ids(tickers));
    }

    [Fact]
    public async Task TimeTickers_CarryTheirRequestAsText_AndNullWhenThereIsNone()
    {
        var tickers = (await _api.GetAsync("/time-tickers")).EnumerateArray().ToDictionary(t => t.GetProperty("id").GetGuid());

        Assert.Equal(DashboardApiFixture.DoneRequest, tickers[DashboardApiFixture.DoneId].GetProperty("request").GetString());
        Assert.Equal(JsonValueKind.Null, tickers[DashboardApiFixture.FailedId].GetProperty("request").ValueKind);
    }

    [Fact]
    public async Task TimeTickers_KeepRetriesAndIntervals()
    {
        var done = (await _api.GetAsync("/time-tickers")).EnumerateArray()
            .Single(t => t.GetProperty("id").GetGuid() == DashboardApiFixture.DoneId);

        Assert.Equal(2, done.GetProperty("retries").GetInt32());
        Assert.Equal(new[] { 30, 90 }, done.GetProperty("retryIntervals").EnumerateArray().Select(i => i.GetInt32()));
    }

    [Fact]
    public async Task TimeTickersPaginated_SplitsTheListIntoPages()
    {
        var first = await _api.GetAsync("/time-tickers/paginated?pageNumber=1&pageSize=2");
        var second = await _api.GetAsync("/time-tickers/paginated?pageNumber=2&pageSize=2");

        Assert.Equal(3, first.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, first.GetProperty("totalPages").GetInt32());
        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        Assert.False(first.GetProperty("hasPreviousPage").GetBoolean());
        Assert.True(first.GetProperty("hasNextPage").GetBoolean());
        Assert.Equal(1, first.GetProperty("firstItemIndex").GetInt32());
        Assert.Equal(2, first.GetProperty("lastItemIndex").GetInt32());

        Assert.Equal(new[] { DashboardApiFixture.DoneId }, Ids(second.GetProperty("items")));
        Assert.True(second.GetProperty("hasPreviousPage").GetBoolean());
        Assert.False(second.GetProperty("hasNextPage").GetBoolean());
    }

    [Theory]
    [InlineData("")]
    [InlineData("?pageNumber=0&pageSize=0")]
    [InlineData("?pageNumber=-4&pageSize=-1")]
    [InlineData("?pageNumber=abc&pageSize=xyz")]
    public async Task TimeTickersPaginated_FallsBackToTheFirstPageOfTwenty_ForMissingOrInvalidParameters(string query)
    {
        var page = await _api.GetAsync("/time-tickers/paginated" + query);

        Assert.Equal(1, page.GetProperty("pageNumber").GetInt32());
        Assert.Equal(20, page.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, page.GetProperty("items").GetArrayLength());
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("failed")]
    [InlineData("FAILED")]
    public async Task TimeTickersPaginated_FiltersByStatus_IgnoringCase(string status)
    {
        var page = await _api.GetAsync($"/time-tickers/paginated?status={status}");

        Assert.Equal(new[] { DashboardApiFixture.FailedId }, Ids(page.GetProperty("items")));
        Assert.Equal(1, page.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task TimeTickersPaginated_IgnoresAnUnknownStatus()
    {
        var page = await _api.GetAsync("/time-tickers/paginated?status=NoSuchStatus");

        Assert.Equal(3, page.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task TimeTickersPaginated_SearchesTheFunctionName()
    {
        var page = await _api.GetAsync("/time-tickers/paginated?search=Other");

        Assert.Equal(new[] { DashboardApiFixture.QueuedId }, Ids(page.GetProperty("items")));
    }

    [Fact]
    public async Task TimeTickersPaginated_SearchesTheErrorMessage()
    {
        var page = await _api.GetAsync("/time-tickers/paginated?search=rejected");

        Assert.Equal(new[] { DashboardApiFixture.FailedId }, Ids(page.GetProperty("items")));
    }

    [Fact]
    public async Task TimeTickersPaginated_CombinesStatusAndSearch()
    {
        var match = await _api.GetAsync("/time-tickers/paginated?status=Failed&search=SeedJob");
        var noMatch = await _api.GetAsync("/time-tickers/paginated?status=Done&search=OtherJob");

        Assert.Equal(1, match.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, noMatch.GetProperty("totalCount").GetInt32());
    }

    #endregion

    #region Time ticker charts

    [Fact]
    public async Task TimeTickersGraphDataRange_DefaultsToThreeDaysEitherSideOfToday_OneEntryPerDay()
    {
        var days = (await _api.GetAsync("/time-tickers/graph-data-range")).EnumerateArray().ToArray();

        Assert.Equal(7, days.Length);
        var expected = Enumerable.Range(-3, 7).Select(offset => _api.Today.AddDays(offset).ToString("yyyy-MM-dd")).ToArray();
        Assert.Equal(expected, days.Select(d => d.GetProperty("date").GetString()![..10]));
    }

    [Fact]
    public async Task TimeTickersGraphDataRange_ListsEveryStatusOnEveryDay_WithZerosWhereNothingRan()
    {
        var days = (await _api.GetAsync("/time-tickers/graph-data-range")).EnumerateArray().ToArray();

        foreach (var day in days)
        {
            var statuses = day.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("item1").GetInt32()).ToArray();
            Assert.Equal(Enumerable.Range(0, 8), statuses);
        }
    }

    [Fact]
    public async Task TimeTickersGraphDataRange_CountsEachTickerOnTheDayItIsScheduledFor()
    {
        var days = (await _api.GetAsync("/time-tickers/graph-data-range")).EnumerateArray().ToArray();

        // Days are Today-3 .. Today+3, so yesterday is index 2, today 3 and tomorrow 4.
        var yesterday = Counts(days[2].GetProperty("results"));
        var today = Counts(days[3].GetProperty("results"));
        var tomorrow = Counts(days[4].GetProperty("results"));

        Assert.Equal(1, yesterday[Done]);
        Assert.Equal(1, today[Failed]);
        Assert.Equal(1, tomorrow[Queued]);

        var everythingElse = days.Select((day, index) => (day, index)).SelectMany(x => Counts(x.day.GetProperty("results")).Values).Sum();
        Assert.Equal(3, everythingElse);
    }

    [Fact]
    public async Task TimeTickersGraphDataRange_HonoursACustomRange()
    {
        var days = (await _api.GetAsync("/time-tickers/graph-data-range?pastDays=-1&futureDays=0")).EnumerateArray().ToArray();

        Assert.Equal(2, days.Length);
        Assert.Equal(_api.Yesterday.ToString("yyyy-MM-dd"), days[0].GetProperty("date").GetString()![..10]);
        Assert.Equal(_api.Today.ToString("yyyy-MM-dd"), days[1].GetProperty("date").GetString()![..10]);
    }

    [Fact]
    public async Task TimeTickersGraphData_CountsTickersPerStatus()
    {
        var counts = Counts(await _api.GetAsync("/time-tickers/graph-data"));

        Assert.Equal(8, counts.Count);
        Assert.Equal(1, counts[Queued]);
        Assert.Equal(1, counts[Done]);
        Assert.Equal(1, counts[Failed]);
        Assert.Equal(3, counts.Values.Sum());
    }

    #endregion

    #region Cron tickers and occurrences

    [Fact]
    public async Task CronTickers_ListsEveryCronTicker_WithItsEnabledFlag()
    {
        var crons = (await _api.GetAsync("/cron-tickers")).EnumerateArray().ToDictionary(c => c.GetProperty("id").GetGuid());

        Assert.Equal(2, crons.Count);
        Assert.True(crons[DashboardApiFixture.CronId].GetProperty("isEnabled").GetBoolean());
        Assert.False(crons[DashboardApiFixture.PausedCronId].GetProperty("isEnabled").GetBoolean());
        Assert.Equal("0 * * * * *", crons[DashboardApiFixture.CronId].GetProperty("expression").GetString());
    }

    [Fact]
    public async Task CronTickersPaginated_SplitsTheListIntoPages()
    {
        var first = await _api.GetAsync("/cron-tickers/paginated?pageNumber=1&pageSize=1");
        var second = await _api.GetAsync("/cron-tickers/paginated?pageNumber=2&pageSize=1");

        Assert.Equal(2, first.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, first.GetProperty("totalPages").GetInt32());
        Assert.Single(first.GetProperty("items").EnumerateArray());
        Assert.Single(second.GetProperty("items").EnumerateArray());
        Assert.NotEqual(Ids(first.GetProperty("items")), Ids(second.GetProperty("items")));
    }

    [Fact]
    public async Task CronTickersGraphDataRange_CountsOccurrencesOnTheDayTheyAreScheduledFor()
    {
        var days = (await _api.GetAsync("/cron-tickers/graph-data-range")).EnumerateArray().ToArray();

        Assert.Equal(7, days.Length);
        Assert.Equal(1, Counts(days[2].GetProperty("results"))[Done]);
        Assert.Equal(1, Counts(days[3].GetProperty("results"))[Failed]);
        Assert.Equal(1, Counts(days[4].GetProperty("results"))[Queued]);
    }

    [Fact]
    public async Task CronTickersGraphDataRangeById_CountsOnlyThatCronTickersOccurrences()
    {
        var own = (await _api.GetAsync($"/cron-tickers/graph-data-range-id?id={DashboardApiFixture.CronId}")).EnumerateArray().ToArray();
        var other = (await _api.GetAsync($"/cron-tickers/graph-data-range-id?id={DashboardApiFixture.PausedCronId}")).EnumerateArray().ToArray();

        Assert.Equal(3, own.SelectMany(d => Counts(d.GetProperty("results")).Values).Sum());
        Assert.Equal(0, other.SelectMany(d => Counts(d.GetProperty("results")).Values).Sum());
    }

    [Fact]
    public async Task CronTickersGraphData_CountsOccurrencesPerStatus()
    {
        var counts = Counts(await _api.GetAsync("/cron-tickers/graph-data"));

        Assert.Equal(1, counts[Queued]);
        Assert.Equal(1, counts[Done]);
        Assert.Equal(1, counts[Failed]);
        Assert.Equal(3, counts.Values.Sum());
    }

    [Fact]
    public async Task CronTickerOccurrences_ListsTheOccurrencesOfOneCronTicker()
    {
        var own = await _api.GetAsync($"/cron-ticker-occurrences/{DashboardApiFixture.CronId}");
        var other = await _api.GetAsync($"/cron-ticker-occurrences/{DashboardApiFixture.PausedCronId}");

        Assert.Equal(3, own.GetArrayLength());
        Assert.Equal(0, other.GetArrayLength());
    }

    [Fact]
    public async Task CronTickerOccurrencesPaginated_SplitsTheOccurrencesIntoPages()
    {
        var page = await _api.GetAsync($"/cron-ticker-occurrences/{DashboardApiFixture.CronId}/paginated?pageNumber=1&pageSize=2");

        Assert.Equal(3, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());
        Assert.True(page.GetProperty("hasNextPage").GetBoolean());
    }

    [Fact]
    public async Task CronTickerOccurrencesGraphData_IsOneEntryPerDayForFourteenDaysStartingAWeekAgo()
    {
        var days = (await _api.GetAsync($"/cron-ticker-occurrences/{DashboardApiFixture.CronId}/graph-data")).EnumerateArray().ToArray();

        // The repository caps the chart at 14 days in total: seven back, today and six ahead.
        Assert.Equal(14, days.Length);
        var expected = Enumerable.Range(-7, 14).Select(offset => _api.Today.AddDays(offset).ToString("yyyy-MM-dd")).ToArray();
        Assert.Equal(expected, days.Select(d => d.GetProperty("date").GetString()![..10]));
    }

    [Fact]
    public async Task CronTickerOccurrencesGraphData_ListsOnlyTheStatusesThatOccurredOnEachDay()
    {
        // Unlike the range endpoints, which list every status with zeros, this one leaves empty days empty.
        var days = (await _api.GetAsync($"/cron-ticker-occurrences/{DashboardApiFixture.CronId}/graph-data")).EnumerateArray().ToArray();

        // Days are Today-7 .. Today+7, so yesterday is index 6, today 7 and tomorrow 8.
        Assert.Equal(new Dictionary<int, int> { [Done] = 1 }, Counts(days[6].GetProperty("results")));
        Assert.Equal(new Dictionary<int, int> { [Failed] = 1 }, Counts(days[7].GetProperty("results")));
        Assert.Equal(new Dictionary<int, int> { [Queued] = 1 }, Counts(days[8].GetProperty("results")));
        Assert.Equal(11, days.Count(d => d.GetProperty("results").GetArrayLength() == 0));
    }

    #endregion

    #region Requests, statuses and machines

    [Fact]
    public async Task TickerRequest_ReturnsTheStoredRequestText()
    {
        var response = await _api.GetAsync($"/ticker-request/{DashboardApiFixture.DoneId}?tickerType=1");

        Assert.Equal(DashboardApiFixture.DoneRequest, response.GetProperty("result").GetString());
    }

    [Fact]
    public async Task JobStatuses_CountEveryTickerAndOccurrenceByStatus()
    {
        var statuses = (await _api.GetAsync("/ticker/statuses/get")).EnumerateArray()
            .Select(s => (Status: s.GetProperty("item1").GetInt32(), Count: s.GetProperty("item2").GetInt32()))
            .ToDictionary(s => s.Status, s => s.Count);

        Assert.Equal(2, statuses[Queued]);
        Assert.Equal(2, statuses[Done]);
        Assert.Equal(2, statuses[Failed]);
    }

    [Fact]
    public async Task LastWeekJobStatuses_ReportDone_Failed_AndTheTotalUpToToday()
    {
        // Entry 0 is Done and DueDone, entry 1 is Failed, entry 2 is every status. Tomorrow's Queued ones are not
        // in the last week, so they are not in the total.
        var counts = Counts(await _api.GetAsync("/ticker/statuses/get-last-week"));

        Assert.Equal(2, counts[0]);
        Assert.Equal(2, counts[1]);
        Assert.Equal(4, counts[2]);
    }

    [Fact]
    public async Task MachineJobs_IsEmpty_WhenNothingIsLockedByAMachine()
    {
        var jobs = await _api.GetAsync("/ticker/machine/jobs");

        Assert.Empty(jobs.EnumerateArray());
    }

    [Fact]
    public async Task MachineJobs_ShowALockedJobWithoutAHolderAsUnknown_InsteadOfFailing()
    {
        var ticker = _api.Time(Guid.NewGuid(), "SeedJob", TickerStatus.InProgress, _api.Today);
        ticker.LockedAt = DateTime.UtcNow;
        ticker.LockHolder = null;
        await _api.Store.AddTimeTickers(new[] { ticker });
        try
        {
            var jobs = await _api.GetAsync("/ticker/machine/jobs");

            var machine = Assert.Single(jobs.EnumerateArray());
            Assert.Contains("unknown", machine.GetRawText());
        }
        finally
        {
            await _api.Store.RemoveTimeTickers(new[] { ticker.Id });
        }
    }

    #endregion
}

/// <summary>The dashboard API read tests on the in-memory store.</summary>
[Collection(SharedStaticStateCollection.Name)]
public class InMemoryDashboardApiReadTests : DashboardApiReadTests<InMemoryDashboardApiFixture>
{
    public InMemoryDashboardApiReadTests(InMemoryDashboardApiFixture api) : base(api) { }
}

/// <summary>The dashboard API read tests on EF Core with SQLite, so the same answers are checked on a relational store.</summary>
[Collection(SharedStaticStateCollection.Name)]
public class EfCoreDashboardApiReadTests : DashboardApiReadTests<EfCoreDashboardApiFixture>
{
    public EfCoreDashboardApiReadTests(EfCoreDashboardApiFixture api) : base(api) { }
}
