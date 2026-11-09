using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR.Client;
using SharpTicker.Scenarios.Harness;
using Xunit.Abstractions;

namespace SharpTicker.Scenarios;

/// <summary>Dashboard statistics, the delete notification, and the caching of the page.</summary>
public class DashboardScenarios(ITestOutputHelper output)
{
    private const string Base = "/ticker/dashboard";

    // #32: get-last-week carries the Cancelled count as entry 3 (entries 0 to 2 unchanged: Done/DueDone, Failed, total);
    // get gives all-time counts per status. The week counts jobs by the UTC date of their ExecutionTime.
    [Theory]
    [InlineData(Store.EfCorePostgres)]
    [InlineData(Store.MongoDb)]
    [InlineData(Store.Redis)]
    public async Task TheStatisticsCountCancelledAndFailed_ThisWeekAndAllTime(Store store)
    {
        var (week, overall) = await RunTheStatisticsJobs("statistics", store);

        // Week: Failed 2, Cancelled 2 (entry 3, missing before #32).
        Assert.Equal(2, week.GetValueOrDefault(1));
        Assert.Equal(2, week.GetValueOrDefault(3));

        // All time: the same two Failed and two Cancelled (the old jobs are Done).
        Assert.Equal(2, overall.GetValueOrDefault(TickerStatus.Failed));
        Assert.Equal(2, overall.GetValueOrDefault(TickerStatus.Cancelled));
    }

    // Three Done, two Failed, two Cancelled, a chain with an inline child, two jobs 30 days old and a run-now occurrence;
    // returns get-last-week (entry -> count) and get (status -> count) once all are final.
    private async Task<(Dictionary<int, int> Week, Dictionary<TickerStatus, int> Overall)> RunTheStatisticsJobs(string scenario, Store store)
    {
        // Within 10 minutes of midnight UTC the week window could move during the test: wait until it is past.
        var now = DateTime.UtcNow;
        if (now.TimeOfDay > TimeSpan.FromHours(24) - TimeSpan.FromMinutes(10) || now.TimeOfDay < TimeSpan.FromMinutes(10))
        {
            var wait = now.Date.AddDays(now.TimeOfDay < TimeSpan.FromMinutes(10) ? 0 : 1).AddMinutes(10) - now;
            output.WriteLine($"Near midnight UTC: waiting {wait.TotalMinutes:0} min before starting.");
            await Task.Delay(wait);
            now = DateTime.UtcNow;
        }

        await using var env = await ScenarioEnvironment.StartAsync(scenario, store, nodes: 1);
        var node = env.Nodes[0];

        var recent = await node.Api.AddTimeTickersAsync(
            new TickerSeed { Function = Functions.Record, ExecutionTime = now },
            new TickerSeed { Function = Functions.Record, ExecutionTime = now },
            new TickerSeed { Function = Functions.Record, ExecutionTime = now },
            new TickerSeed { Function = Functions.Fail, ExecutionTime = now },
            new TickerSeed { Function = Functions.Fail, ExecutionTime = now });
        var toCancel = await node.Api.AddTimeTickersAsync(
            new TickerSeed { Function = Functions.Slow, ExecutionTime = now, Request = new WaitRequest { Seconds = 60 } },
            new TickerSeed { Function = Functions.Slow, ExecutionTime = now, Request = new WaitRequest { Seconds = 60 } });
        var (_, chain) = await node.Api.AddChainAsync(new TickerSeed
        {
            Function = Functions.Record, ExecutionTime = now,
            Children = [new TickerSeed { Function = Functions.Record, RunCondition = RunCondition.OnSuccess }],
        });
        // 30 days back: in the all-time counts, outside the week.
        var old = await node.Api.AddTimeTickersAsync(
            new TickerSeed { Function = Functions.Record, ExecutionTime = now.AddDays(-30) },
            new TickerSeed { Function = Functions.Record, ExecutionTime = now.AddDays(-30) });
        var cronId = await node.Api.AddCronAsync(Functions.Record, Crons.Far);
        await node.Dashboard.RunCronNowAsync(cronId);

        await env.UntilAsync(() => Task.FromResult(toCancel.All(id => env.Runs.Starts(id) == 1)), () => "the jobs to cancel did not start");
        foreach (var id in toCancel)
        {
            var (status, body) = await node.Dashboard.CancelAsync(id);
            Assert.True(status == HttpStatusCode.OK, $"cancel {id}: {(int)status} {body}");
        }

        var all = recent.Concat(toCancel).Concat(chain).Concat(old).ToList();
        IReadOnlyList<TickerView> tickers = [];
        IReadOnlyList<OccurrenceView> occurrences = [];
        await env.UntilAsync(async () =>
        {
            tickers = await node.Dashboard.GetAllTimeTickersAsync();
            occurrences = await node.Dashboard.GetOccurrencesAsync(cronId);
            // The run-now occurrence is the one due now; the cron's next regular one (about 180 days ahead, Crons.Far) is listed too.
            var runNow = occurrences.Where(o => o.ExecutionTime < DateTime.UtcNow.AddDays(1)).ToList();
            return all.All(id => tickers.Any(t => t.Id == id && IsFinal(t.Status)))
                   && runNow.Count == 1 && IsFinal(runNow[0].Status);
        }, () => $"{all.Count(id => !tickers.Any(t => t.Id == id && IsFinal(t.Status)))} jobs not final, occurrences {occurrences.Count}");

        var week = (await node.Dashboard.GetJsonAsync<List<Pair<int>>>($"{Base}/api/ticker/statuses/get-last-week"))!
            .ToDictionary(p => p.Item1, p => p.Item2);
        var overall = (await node.Dashboard.GetJsonAsync<List<Pair<TickerStatus>>>($"{Base}/api/ticker/statuses/get"))!
            .ToDictionary(p => p.Item1, p => p.Item2);
        output.WriteLine($"week: {string.Join(", ", week.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"))}");
        return (week, overall);
    }

    // #29: a deleted time ticker is announced to the dashboard (RemoveTimeTickerNotification); before the fix the open
    // dashboard kept showing it.
    [Fact]
    public async Task ADeletedTicker_IsAnnouncedToTheDashboard()
    {
        await using var env = await ScenarioEnvironment.StartAsync("delete-notification", Store.EfCorePostgres, nodes: 1);
        var node = env.Nodes[0];
        var id = await node.Api.AddTimeTickerAsync(new TickerSeed { ExecutionTime = DateTime.UtcNow.AddHours(1) });

        var announced = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(node.BaseAddress, $"{Base}/ticker-notification-hub"))
            .Build();
        hub.On<Guid>("RemoveTimeTickerNotification", removed => announced.TrySetResult(removed));
        await hub.StartAsync();

        var (status, body) = await node.Dashboard.DeleteTimeTickerAsync(id);
        Assert.True(status == HttpStatusCode.OK, $"delete: {(int)status} {body}");

        var winner = await Task.WhenAny(announced.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(winner == announced.Task, "no RemoveTimeTickerNotification within 10 s");
        Assert.Equal(id, await announced.Task);
    }

    // #39, #41: no cache may store the page or its configuration script, and the preload script travels inside the
    // configuration script; before the fixes a proxy or browser that kept an old preload script loaded the old dashboard
    // after an upgrade.
    [Fact]
    public async Task ThePageIsNeverCached()
    {
        await using var env = await ScenarioEnvironment.StartAsync("page-cache", Store.EfCorePostgres, nodes: 1);
        using var http = new HttpClient { BaseAddress = env.Nodes[0].BaseAddress };

        var page = await http.GetAsync($"{Base}/");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("no-cache", page.Headers.CacheControl?.ToString());
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<script src=\"__sharpticker-config.js\"></script>", html);
        Assert.DoesNotMatch(new Regex(@"<script(?![^>]*\bsrc=)[^>]*>", RegexOptions.IgnoreCase), html);

        var config = await http.GetAsync($"{Base}/__sharpticker-config.js");
        var configJs = await config.Content.ReadAsStringAsync();
        Assert.Equal("no-cache", config.Headers.CacheControl?.ToString());
        Assert.Contains("__dynamic_base__", configJs);
        var asset = Regex.Match(configJs + html, @"assets/[A-Za-z0-9_.-]+-[A-Za-z0-9_-]{6,}\.js");
        Assert.True(asset.Success, "no hashed asset name in the configuration script or the page");

        var preload = await http.GetAsync($"{Base}/__sharpticker-preload.js");
        Assert.Equal("no-cache", preload.Headers.CacheControl?.ToString());
        Assert.Equal("/* The preload script is part of __sharpticker-config.js. */", await preload.Content.ReadAsStringAsync());

        var real = await http.GetAsync($"{Base}/{asset.Value}");
        Assert.Equal(HttpStatusCode.OK, real.StatusCode);
        Assert.Equal("public,max-age=3600", real.Headers.CacheControl?.ToString().Replace(" ", ""));

        var unknown = await http.GetAsync($"{Base}/assets/index-x.js");
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Equal("text/html", unknown.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", unknown.Headers.CacheControl?.ToString());

    }

    private static bool IsFinal(TickerStatus status)
        => status is TickerStatus.Done or TickerStatus.DueDone or TickerStatus.Failed or TickerStatus.Cancelled or TickerStatus.Skipped;

    private sealed class Pair<T>
    {
        public T Item1 { get; set; } = default!;
        public int Item2 { get; set; }
    }
}
