using System;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using SharpTicker.Utilities.Enums;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// The dashboard write API over HTTP. Each test gets its own application and data, because these change it.
/// </summary>
public abstract class DashboardApiWriteTests<TFixture> : IAsyncLifetime where TFixture : DashboardApiFixture, new()
{
    protected readonly TFixture _api = new();

    public Task InitializeAsync() => _api.InitializeAsync();
    public Task DisposeAsync() => _api.DisposeAsync();

    private static string InTwoDays => DateTime.UtcNow.AddDays(2).ToString("yyyy-MM-ddTHH:mm:ss'Z'");

    /// <summary>
    /// A time read back from a relational store has an unspecified kind and is written without a "Z", where the
    /// in-memory store writes one. The dashboard treats both as UTC, so read both that way.
    /// </summary>
    private static DateTime AsUtc(JsonElement time)
        => DateTime.Parse(time.GetString()!, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

    private async Task<JsonElement[]> TimeTickersAsync() => (await _api.GetAsync("/time-tickers")).EnumerateArray().ToArray();
    private async Task<JsonElement[]> CronTickersAsync() => (await _api.GetAsync("/cron-tickers")).EnumerateArray().ToArray();

    private async Task<Guid> AddTimeTickerAsync(string json, string query = "")
    {
        var (status, body) = await _api.SendAsync(HttpMethod.Post, "/time-ticker/add" + query, json);
        Assert.Equal(200, status);
        Assert.True(body!.Value.GetProperty("success").GetBoolean(), body.ToString());
        return body.Value.GetProperty("tickerId").GetGuid();
    }

    #region Adding time tickers

    [Fact]
    public async Task AddTimeTicker_StoresItIdleUntilTheSchedulerPicksItUp_WithTheFunctionRequestRetriesAndDescription()
    {
        var id = await AddTimeTickerAsync(
            $$"""{"function":"WriteJob","executionTime":"{{InTwoDays}}","retries":1,"description":"added","request":"{\"a\":1}"}""");

        var added = (await TimeTickersAsync()).Single(t => t.GetProperty("id").GetGuid() == id);
        Assert.Equal("WriteJob", added.GetProperty("function").GetString());
        Assert.Equal(0, added.GetProperty("status").GetInt32()); // Idle. The scheduler marks it Queued when it takes it.
        Assert.Equal(1, added.GetProperty("retries").GetInt32());
        Assert.Equal("added", added.GetProperty("description").GetString());
        Assert.Equal("{\"a\":1}", added.GetProperty("request").GetString());
    }

    [Fact]
    public async Task AddTimeTicker_ReadsTheExecutionTimeInTheGivenTimeZone_AndStoresUtc()
    {
        // Istanbul is UTC+3 all year, so noon there is 09:00 UTC.
        var id = await AddTimeTickerAsync(
            """{"function":"TzJob","executionTime":"2030-06-01T12:00:00"}""", "?timeZoneId=Europe/Istanbul");

        var added = (await TimeTickersAsync()).Single(t => t.GetProperty("id").GetGuid() == id);
        Assert.Equal(new DateTime(2030, 6, 1, 9, 0, 0, DateTimeKind.Utc), AsUtc(added.GetProperty("executionTime")));
    }

    [Fact]
    public async Task AddTimeTicker_StoresTheRetryIntervals()
    {
        var id = await AddTimeTickerAsync(
            $$"""{"function":"WriteJob","executionTime":"{{InTwoDays}}","retries":2,"retryIntervals":[30,90]}""");

        var added = (await TimeTickersAsync()).Single(t => t.GetProperty("id").GetGuid() == id);
        Assert.Equal(new[] { 30, 90 }, added.GetProperty("retryIntervals").EnumerateArray().Select(i => i.GetInt32()));
    }

    /// <summary>The body the dashboard sends carries the intervals as "intervals", not "retryIntervals".</summary>
    protected async Task AssertTheRetryIntervalsTheDashboardSendsAreStoredAsync()
    {
        var id = await AddTimeTickerAsync(
            $$"""{"function":"WriteJob","executionTime":"{{InTwoDays}}","retries":2,"intervals":[30,90]}""");

        var added = (await TimeTickersAsync()).Single(t => t.GetProperty("id").GetGuid() == id);
        Assert.Equal(new[] { 30, 90 }, added.GetProperty("retryIntervals").EnumerateArray().Select(i => i.GetInt32()));
    }

    [Fact]
    public async Task AddTimeTicker_IsRefused_ForAFunctionNobodyRegistered()
    {
        var (status, body) = await _api.SendAsync(HttpMethod.Post, "/time-ticker/add",
            $$"""{"function":"NoSuchFunction","executionTime":"{{InTwoDays}}"}""");

        Assert.Equal(200, status);
        Assert.False(body!.Value.GetProperty("success").GetBoolean());
        Assert.DoesNotContain(await TimeTickersAsync(), t => t.GetProperty("function").GetString() == "NoSuchFunction");
    }

    #endregion

    #region Updating and deleting time tickers

    [Fact]
    public async Task UpdateTimeTicker_ChangesTheQueuedTicker()
    {
        var (status, body) = await _api.SendAsync(HttpMethod.Put, $"/time-ticker/update?id={DashboardApiFixture.QueuedId}",
            $$"""{"function":"OtherJob","executionTime":"{{InTwoDays}}","description":"changed","retries":5}""");

        Assert.Equal(200, status);
        Assert.True(body!.Value.GetProperty("success").GetBoolean(), body.ToString());
        var queued = (await TimeTickersAsync()).Single(t => t.GetProperty("id").GetGuid() == DashboardApiFixture.QueuedId);
        Assert.Equal("changed", queued.GetProperty("description").GetString());
        Assert.Equal(5, queued.GetProperty("retries").GetInt32());
    }

    /// <summary>Updating a ticker that does not exist is a failure and creates nothing.</summary>
    protected async Task AssertUpdatingAMissingTickerFailsAndCreatesNothingAsync()
    {
        var (_, body) = await _api.SendAsync(HttpMethod.Put, $"/time-ticker/update?id={Guid.NewGuid()}",
            $$"""{"function":"OtherJob","executionTime":"{{InTwoDays}}"}""");

        Assert.False(body!.Value.GetProperty("success").GetBoolean());
        Assert.Equal(3, (await TimeTickersAsync()).Length);
    }

    [Fact]
    public async Task DeleteTimeTicker_RemovesIt()
    {
        var (status, body) = await _api.SendAsync(HttpMethod.Delete, $"/time-ticker/delete?id={DashboardApiFixture.DoneId}");

        Assert.Equal(200, status);
        Assert.True(body!.Value.GetProperty("success").GetBoolean());
        Assert.DoesNotContain(await TimeTickersAsync(), t => t.GetProperty("id").GetGuid() == DashboardApiFixture.DoneId);
        Assert.Equal(2, (await TimeTickersAsync()).Length);
    }

    [Fact]
    public async Task DeleteTimeTicker_IsHarmless_ForATickerThatIsAlreadyGone()
    {
        var (status, _) = await _api.SendAsync(HttpMethod.Delete, $"/time-ticker/delete?id={Guid.NewGuid()}");

        Assert.Equal(200, status);
        Assert.Equal(3, (await TimeTickersAsync()).Length);
    }

    [Fact]
    public async Task DeleteTimeTickersBatch_RemovesEveryListedTicker_AndOnlyThose()
    {
        var (status, body) = await _api.SendAsync(HttpMethod.Delete, "/time-ticker/delete-batch",
            $"[\"{DashboardApiFixture.FailedId}\",\"{DashboardApiFixture.QueuedId}\"]");

        Assert.Equal(200, status);
        Assert.True(body!.Value.GetProperty("success").GetBoolean());
        Assert.Equal(new[] { DashboardApiFixture.DoneId }, (await TimeTickersAsync()).Select(t => t.GetProperty("id").GetGuid()));
    }

    #endregion

    #region Cron tickers

    [Fact]
    public async Task AddCronTicker_StoresItEnabled()
    {
        var (status, body) = await _api.SendAsync(HttpMethod.Post, "/cron-ticker/add",
            """{"function":"NewCron","expression":"0 0 * * * *","retries":0,"description":"hourly"}""");

        Assert.Equal(200, status);
        Assert.True(body!.Value.GetProperty("success").GetBoolean(), body.ToString());
        var added = (await CronTickersAsync()).Single(c => c.GetProperty("id").GetGuid() == body.Value.GetProperty("tickerId").GetGuid());
        Assert.Equal("NewCron", added.GetProperty("function").GetString());
        Assert.Equal("0 0 * * * *", added.GetProperty("expression").GetString());
        Assert.True(added.GetProperty("isEnabled").GetBoolean());
    }

    [Theory]
    [InlineData("0 0 9 * * MON-FRI")]
    [InlineData("0 30 8 * * mon,wed,fri")]
    [InlineData("0 0 0 1 Jan,Jul *")]
    [InlineData("0 0 9 * * Monday")]
    [InlineData("0 0 0 1 Sept *")]
    public async Task AddCronTicker_StoresAndReturnsAnExpressionWithNamesExactlyAsWritten(string expression)
    {
        var (status, body) = await _api.SendAsync(HttpMethod.Post, "/cron-ticker/add",
            $$"""{"function":"NewCron","expression":"{{expression}}"}""");

        Assert.Equal(200, status);
        Assert.True(body!.Value.GetProperty("success").GetBoolean(), body.ToString());
        var added = (await CronTickersAsync()).Single(c => c.GetProperty("id").GetGuid() == body.Value.GetProperty("tickerId").GetGuid());
        Assert.Equal(expression, added.GetProperty("expression").GetString());
    }

    [Fact]
    public async Task UpdateCronTicker_CanChangeTheExpressionToOneWithNames()
    {
        var (_, body) = await _api.SendAsync(HttpMethod.Put, $"/cron-ticker/update?id={DashboardApiFixture.CronId}",
            """{"function":"SeedCron","expression":"0 0 9 * * MON-FRI"}""");

        Assert.True(body!.Value.GetProperty("success").GetBoolean(), body.ToString());
        var cron = (await CronTickersAsync()).Single(c => c.GetProperty("id").GetGuid() == DashboardApiFixture.CronId);
        Assert.Equal("0 0 9 * * MON-FRI", cron.GetProperty("expression").GetString());
    }

    [Theory]
    [InlineData("0 0 22-2 * * *")]
    [InlineData("0 0 0 * * FRI-MON")]
    public async Task AddCronTicker_IsRefused_ForABackwardsRange_AndSaysWhy(string expression)
    {
        var (_, body) = await _api.SendAsync(HttpMethod.Post, "/cron-ticker/add",
            $$"""{"function":"NewCron","expression":"{{expression}}"}""");

        Assert.False(body!.Value.GetProperty("success").GetBoolean());
        var message = body.Value.GetProperty("message").GetString();
        Assert.StartsWith("Failed to add cron ticker", message);
        Assert.Contains("backwards", message);
        Assert.Contains(expression, message);
        Assert.Equal(2, (await CronTickersAsync()).Length);
    }

    [Fact]
    public async Task UpdateCronTicker_IsRefused_ForABackwardsRange_AndKeepsTheStoredExpression()
    {
        var (_, body) = await _api.SendAsync(HttpMethod.Put, $"/cron-ticker/update?id={DashboardApiFixture.CronId}",
            """{"function":"SeedCron","expression":"0 0 22-2 * * *"}""");

        Assert.False(body!.Value.GetProperty("success").GetBoolean());
        Assert.StartsWith("Failed to update cron ticker", body.Value.GetProperty("message").GetString());
        Assert.Contains("backwards", body.Value.GetProperty("message").GetString());
        var cron = (await CronTickersAsync()).Single(c => c.GetProperty("id").GetGuid() == DashboardApiFixture.CronId);
        Assert.Equal("0 * * * * *", cron.GetProperty("expression").GetString());
    }

    [Fact]
    public async Task ARefusedCronTicker_IsLoggedAsAWarningWithTheReason_AndNoException()
    {
        await _api.SendAsync(HttpMethod.Post, "/cron-ticker/add",
            """{"function":"NewCron","expression":"0 0 22-2 * * *"}""");

        var entry = Assert.Single(_api.Logs.Entries, e => e.Category.EndsWith("DashboardEndpoints") && e.Level == LogLevel.Warning);
        Assert.Contains("Failed to add cron ticker", entry.Message);
        Assert.Contains("backwards", entry.Message);
        Assert.Null(entry.Exception);
    }

    /// <summary>
    /// The store fails (adding a cron with an id that exists breaks the primary key). The response says only that the
    /// action failed, so no table or constraint name reaches the browser, and the details go to the server log.
    /// </summary>
    protected async Task AssertAFailureOfTheStoreIsLoggedButNotReturnedAsync()
    {
        var (status, body) = await _api.SendAsync(HttpMethod.Post, "/cron-ticker/add",
            $$"""{"id":"{{DashboardApiFixture.CronId}}","function":"NewCron","expression":"0 0 * * * *"}""");

        Assert.Equal(200, status);
        Assert.False(body!.Value.GetProperty("success").GetBoolean());
        Assert.Equal("Failed to add cron ticker", body.Value.GetProperty("message").GetString());

        var entry = Assert.Single(_api.Logs.Entries, e => e.Category.EndsWith("DashboardEndpoints") && e.Level == LogLevel.Error);
        Assert.Contains("Failed to add cron ticker", entry.Message);
        Assert.NotNull(entry.Exception);
        Assert.Contains("constraint", entry.Exception!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddCronTicker_IsRefused_ForAnInvalidExpression()
    {
        var (_, body) = await _api.SendAsync(HttpMethod.Post, "/cron-ticker/add",
            """{"function":"NewCron","expression":"not a cron expression"}""");

        Assert.False(body!.Value.GetProperty("success").GetBoolean());
        Assert.Contains("not a cron expression", body.Value.GetProperty("message").GetString());
        Assert.Equal(2, (await CronTickersAsync()).Length);
    }

    [Fact]
    public async Task UpdateCronTicker_ChangesTheExpressionAndDescription()
    {
        var (_, body) = await _api.SendAsync(HttpMethod.Put, $"/cron-ticker/update?id={DashboardApiFixture.CronId}",
            """{"function":"SeedCron","expression":"0 30 * * * *","description":"half past","retries":3}""");

        Assert.True(body!.Value.GetProperty("success").GetBoolean(), body.ToString());
        var cron = (await CronTickersAsync()).Single(c => c.GetProperty("id").GetGuid() == DashboardApiFixture.CronId);
        Assert.Equal("0 30 * * * *", cron.GetProperty("expression").GetString());
        Assert.Equal("half past", cron.GetProperty("description").GetString());
        Assert.Equal(3, cron.GetProperty("retries").GetInt32());
    }

    [Fact]
    public async Task ToggleCronTicker_DisablesAndEnablesIt()
    {
        await _api.SendAsync(HttpMethod.Put, $"/cron-ticker/toggle?id={DashboardApiFixture.CronId}&isEnabled=false");
        var disabled = (await CronTickersAsync()).Single(c => c.GetProperty("id").GetGuid() == DashboardApiFixture.CronId);

        await _api.SendAsync(HttpMethod.Put, $"/cron-ticker/toggle?id={DashboardApiFixture.PausedCronId}&isEnabled=true");
        var enabled = (await CronTickersAsync()).Single(c => c.GetProperty("id").GetGuid() == DashboardApiFixture.PausedCronId);

        Assert.False(disabled.GetProperty("isEnabled").GetBoolean());
        Assert.True(enabled.GetProperty("isEnabled").GetBoolean());
    }

    [Fact]
    public async Task DeleteCronTicker_RemovesIt()
    {
        var (_, body) = await _api.SendAsync(HttpMethod.Delete, $"/cron-ticker/delete?id={DashboardApiFixture.PausedCronId}");

        Assert.True(body!.Value.GetProperty("success").GetBoolean());
        Assert.Equal(new[] { DashboardApiFixture.CronId }, (await CronTickersAsync()).Select(c => c.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task DeleteCronTickerOccurrence_RemovesOnlyThatOccurrence()
    {
        var (status, _) = await _api.SendAsync(HttpMethod.Delete, $"/cron-ticker-occurrence/delete?id={DashboardApiFixture.OccurrenceDoneId}");

        Assert.Equal(200, status);
        var remaining = (await _api.GetAsync($"/cron-ticker-occurrences/{DashboardApiFixture.CronId}")).EnumerateArray()
            .Select(o => o.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal(2, remaining.Length);
        Assert.DoesNotContain(DashboardApiFixture.OccurrenceDoneId, remaining);
    }

    [Fact]
    public async Task RunCronTickerOnDemand_IsAccepted()
    {
        var (status, _) = await _api.SendAsync(HttpMethod.Post, $"/cron-ticker/run?id={DashboardApiFixture.CronId}");

        Assert.Equal(200, status);
    }

    #endregion

    #region Statistics with every status

    private async Task AddOneTickerInEveryOtherStatusAsync()
    {
        // The seed has Done, Failed and Queued. Add the five others, all scheduled for today.
        await _api.Store.AddTimeTickers(new[]
        {
            _api.Time(Guid.NewGuid(), "SeedJob", TickerStatus.Idle, _api.Today),
            _api.Time(Guid.NewGuid(), "SeedJob", TickerStatus.InProgress, _api.Today),
            _api.Time(Guid.NewGuid(), "SeedJob", TickerStatus.DueDone, _api.Today),
            _api.Time(Guid.NewGuid(), "SeedJob", TickerStatus.Cancelled, _api.Today),
            _api.Time(Guid.NewGuid(), "SeedJob", TickerStatus.Skipped, _api.Today),
        });
    }

    [Fact]
    public async Task JobStatuses_CountEveryStatusSeparately()
    {
        await AddOneTickerInEveryOtherStatusAsync();

        var counts = (await _api.GetAsync("/ticker/statuses/get")).EnumerateArray()
            .ToDictionary(s => s.GetProperty("item1").GetInt32(), s => s.GetProperty("item2").GetInt32());

        // 0 Idle, 1 Queued, 2 InProgress, 3 Done, 4 DueDone, 5 Failed, 6 Cancelled, 7 Skipped. The queued, done and
        // failed ones are counted for the time tickers and for the cron occurrences.
        Assert.Equal(new System.Collections.Generic.Dictionary<int, int>
        {
            [0] = 1, [1] = 2, [2] = 1, [3] = 2, [4] = 1, [5] = 2, [6] = 1, [7] = 1,
        }, counts);
    }

    [Fact]
    public async Task LastWeekJobStatuses_CountDoneAndDueDoneAsSuccessful_AndEveryStatusInTheTotal()
    {
        await AddOneTickerInEveryOtherStatusAsync();

        var counts = (await _api.GetAsync("/ticker/statuses/get-last-week")).EnumerateArray()
            .ToDictionary(s => s.GetProperty("item1").GetInt32(), s => s.GetProperty("item2").GetInt32());

        // Successful: Done yesterday (ticker and occurrence) and the new DueDone. Failed: today's ticker and occurrence.
        // Total: the four seeded ones in the window plus the five added, but not tomorrow's Queued ones.
        Assert.Equal(3, counts[0]);
        Assert.Equal(2, counts[1]);
        Assert.Equal(9, counts[2]);
    }

    #endregion

    #region Cancel and host control

    [Fact]
    public async Task CancelTicker_IsRefused_ForATickerThatIsNotRunning()
    {
        var (status, _) = await _api.SendAsync(HttpMethod.Post, $"/ticker/cancel?id={DashboardApiFixture.QueuedId}");

        Assert.Equal(400, status);
        var queued = (await TimeTickersAsync()).Single(t => t.GetProperty("id").GetGuid() == DashboardApiFixture.QueuedId);
        Assert.Equal(1, queued.GetProperty("status").GetInt32()); // still Queued
    }

    [Theory]
    [InlineData("/ticker-host/stop")]
    [InlineData("/ticker-host/start")]
    [InlineData("/ticker-host/restart")]
    public async Task HostControl_IsAccepted(string path)
    {
        var (status, _) = await _api.SendAsync(HttpMethod.Post, path);

        Assert.Equal(200, status);
    }

    #endregion
}

[Collection(SharedStaticStateCollection.Name)]
public class InMemoryDashboardApiWriteTests : DashboardApiWriteTests<InMemoryDashboardApiFixture>
{
    [Fact(Skip = "Known bug: the dashboard sends the retry intervals as \"intervals\", but the body is read straight into the ticker, whose property is \"retryIntervals\", so the intervals are dropped. Needs a decision: accept \"intervals\" on the server or send \"retryIntervals\" from the dashboard.")]
    public Task AddTimeTicker_StoresTheRetryIntervalsTheDashboardSends() => AssertTheRetryIntervalsTheDashboardSendsAreStoredAsync();

    [Fact(Skip = "Known inconsistency: with the in-memory store, updating a ticker that does not exist creates it (UpdateTickerWithChildren adds it when it is not found) and reports success, while the EF Core store fails the update. The expected behavior is a failure and no new ticker. Needs a decision, then unskip.")]
    public Task UpdateTimeTicker_ReportsFailureAndCreatesNothing_ForATickerThatDoesNotExist() => AssertUpdatingAMissingTickerFailsAndCreatesNothingAsync();
}

[Collection(SharedStaticStateCollection.Name)]
public class EfCoreDashboardApiWriteTests : DashboardApiWriteTests<EfCoreDashboardApiFixture>
{
    [Fact(Skip = "Known bug: the dashboard sends the retry intervals as \"intervals\", but the body is read straight into the ticker, whose property is \"retryIntervals\", so the intervals are dropped. Needs a decision: accept \"intervals\" on the server or send \"retryIntervals\" from the dashboard.")]
    public Task AddTimeTicker_StoresTheRetryIntervalsTheDashboardSends() => AssertTheRetryIntervalsTheDashboardSendsAreStoredAsync();

    [Fact]
    public Task UpdateTimeTicker_ReportsFailureAndCreatesNothing_ForATickerThatDoesNotExist() => AssertUpdatingAMissingTickerFailsAndCreatesNothingAsync();

    [Fact]
    public Task AFailureOfTheStore_IsLoggedOnTheServer_ButNotReturnedToTheBrowser() => AssertAFailureOfTheStoreIsLoggedButNotReturnedAsync();
}
