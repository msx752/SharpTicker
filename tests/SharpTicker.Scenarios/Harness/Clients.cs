using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpTicker.Scenarios.Harness;

/// <summary>The ticker statuses, in the order of SharpTicker's TickerStatus (the dashboard API sends numbers).</summary>
public enum TickerStatus
{
    Idle,
    Queued,
    InProgress,
    Done,
    DueDone,
    Failed,
    Cancelled,
    Skipped,
}

public enum RunCondition
{
    OnSuccess,
    OnFailure,
    OnCancelled,
    OnFailureOrCancelled,
    OnAnyCompletedStatus,
    InProgress,
}

public sealed class WaitRequest
{
    public int Seconds { get; set; }
}

/// <summary>A job to seed on a node (the host's TickerSeed).</summary>
public sealed class TickerSeed
{
    public string Function { get; set; } = Functions.Record;
    public DateTime? ExecutionTime { get; set; }
    public int Retries { get; set; }
    public int[]? RetryIntervals { get; set; }
    public WaitRequest? Request { get; set; }
    public RunCondition? RunCondition { get; set; }
    public List<TickerSeed> Children { get; set; } = new();
}

/// <summary>The job functions of the scenario node.</summary>
public static class Functions
{
    public const string Record = "Scenario.Record";
    public const string Slow = "Scenario.Slow";
    /// <summary>Waits N s and ignores its cancellation token.</summary>
    public const string Stubborn = "Scenario.Stubborn";
    public const string OneAtATime = "Scenario.OneAtATime";
    public const string HttpTimeout = "Scenario.HttpTimeout";
    public const string Fail = "Scenario.Fail";
    public const string ChainStep = "Scenario.ChainStep";
    public const string High = "Scenario.High";
    public const string Normal = "Scenario.Normal";
    public const string Low = "Scenario.Low";
    /// <summary>Holds its worker with Thread.Sleep for 8 s; no request.</summary>
    public const string Blocker = "Scenario.Blocker";
    /// <summary>Holds its worker with Thread.Sleep for 200 ms.</summary>
    public const string Wave = "Scenario.Wave";
    /// <summary>A cron defined in code; registered only when the node gets SCENARIO_SEEDED_CRON.</summary>
    public const string Seeded = "Scenario.Seeded";
}

/// <summary>Cron expressions the scenarios use.</summary>
public static class Crons
{
    /// <summary>
    /// Once a year, on the date about half a year from today: its next regular occurrence is always months away, so any
    /// occurrence within a day of now is one the scenario made (a run-now). A fixed date such as 1 January would fall within
    /// a day of now around the turn of the year. 29 February is avoided.
    /// </summary>
    public static string Far
    {
        get
        {
            var date = DateTime.UtcNow.Date.AddDays(180);
            var day = date is { Month: 2, Day: 29 } ? 28 : date.Day;
            return $"0 0 0 {day} {date.Month} *";
        }
    }
}

/// <summary>A time ticker as the dashboard API lists it, with its child tree.</summary>
public sealed class TickerView
{
    public Guid Id { get; set; }
    public string? Function { get; set; }
    public TickerStatus Status { get; set; }
    public string? LockHolder { get; set; }
    public Guid? ParentId { get; set; }
    public int RetryCount { get; set; }
    public DateTime? ExecutionTime { get; set; }
    public DateTime? ExecutedAt { get; set; }
    public string? SkippedReason { get; set; }
    public List<TickerView> Children { get; set; } = new();

    public IEnumerable<TickerView> SelfAndDescendants()
        => new[] { this }.Concat(Children.SelectMany(c => c.SelfAndDescendants()));
}

public sealed class CronView
{
    public Guid Id { get; set; }
    public string? Function { get; set; }
    public string? Expression { get; set; }
}

public sealed class OccurrenceView
{
    public Guid Id { get; set; }
    public TickerStatus Status { get; set; }
    public DateTime ExecutionTime { get; set; }
    public string? LockHolder { get; set; }
    public int RetryCount { get; set; }
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>The node's test-only <c>/scenario/*</c> endpoints.</summary>
public sealed class ScenarioClient(Func<HttpClient> http)
{
    public async Task<IReadOnlyList<Guid>> AddTimeTickersAsync(params TickerSeed[] seeds)
    {
        var response = await http().PostAsJsonAsync("/scenario/time-tickers", seeds, Json.Options);
        await EnsureSuccess(response);
        var ids = await response.Content.ReadFromJsonAsync<List<IdResult>>(Json.Options);
        return ids!.Select(i => i.Id).ToList();
    }

    public async Task<Guid> AddTimeTickerAsync(TickerSeed seed) => (await AddTimeTickersAsync(seed))[0];

    /// <summary>Seeds a chain; returns the root id and every id of the tree, depth first, root first.</summary>
    public async Task<(Guid Root, IReadOnlyList<Guid> Tree)> AddChainAsync(TickerSeed root)
    {
        var response = await http().PostAsJsonAsync("/scenario/chains", root, Json.Options);
        await EnsureSuccess(response);
        var result = await response.Content.ReadFromJsonAsync<ChainResult>(Json.Options);
        return (result!.Id, result.Tree);
    }

    public async Task<Guid> AddCronAsync(string function, string expression)
    {
        var response = await http().PostAsJsonAsync("/scenario/crons", new { function, expression }, Json.Options);
        await EnsureSuccess(response);
        return (await response.Content.ReadFromJsonAsync<IdResult>(Json.Options))!.Id;
    }

    public async Task DeleteTimeTickerAsync(Guid id) => await EnsureSuccess(await http().DeleteAsync($"/scenario/time-tickers/{id}"));

    /// <summary>Starves the node's .NET thread pool; returns the node-clock window of the starvation.</summary>
    public async Task<(DateTimeOffset From, DateTimeOffset Until)> StarvePoolAsync(int seconds)
    {
        var response = await http().PostAsync($"/scenario/starve-pool?seconds={seconds}", null);
        await EnsureSuccess(response);
        var window = await response.Content.ReadFromJsonAsync<StarveResult>(Json.Options);
        return (window!.From, window.Until);
    }

    private sealed class StarveResult
    {
        public DateTimeOffset From { get; set; }
        public DateTimeOffset Until { get; set; }
    }

    internal static async Task EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} -> {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private sealed class IdResult
    {
        public Guid Id { get; set; }
    }

    private sealed class ChainResult
    {
        public Guid Id { get; set; }
        public List<Guid> Tree { get; set; } = new();
    }
}

/// <summary>The dashboard API of a node, at <c>/ticker/dashboard/api</c>.</summary>
public sealed class DashboardClient(Func<HttpClient> http)
{
    private const string Api = "/ticker/dashboard/api";

    /// <summary>GET of any path on the node, read as JSON.</summary>
    public Task<T?> GetJsonAsync<T>(string path) => http().GetFromJsonAsync<T>(path, Json.Options);

    /// <summary>The root time tickers with their full child trees.</summary>
    public async Task<IReadOnlyList<TickerView>> GetTimeTickersAsync()
        => (await http().GetFromJsonAsync<List<TickerView>>($"{Api}/time-tickers", Json.Options))!;

    /// <summary>Every time ticker, roots and children, in one flat list.</summary>
    public async Task<IReadOnlyList<TickerView>> GetAllTimeTickersAsync()
        => (await GetTimeTickersAsync()).SelectMany(t => t.SelfAndDescendants()).ToList();

    public async Task<IReadOnlyList<CronView>> GetCronsAsync()
        => (await http().GetFromJsonAsync<List<CronView>>($"{Api}/cron-tickers", Json.Options))!;

    public async Task<IReadOnlyList<OccurrenceView>> GetOccurrencesAsync(Guid cronId)
        => (await http().GetFromJsonAsync<List<OccurrenceView>>($"{Api}/cron-ticker-occurrences/{cronId}", Json.Options))!;

    public async Task RunCronNowAsync(Guid id)
        => await ScenarioClient.EnsureSuccess(await http().PostAsync($"{Api}/cron-ticker/run?id={id}", null));

    public async Task<(HttpStatusCode Status, string Body)> CancelAsync(Guid id)
    {
        var response = await http().PostAsync($"{Api}/ticker/cancel?id={id}", null);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    public async Task<(HttpStatusCode Status, string Body)> DeleteTimeTickerAsync(Guid id)
    {
        var response = await http().DeleteAsync($"{Api}/time-ticker/delete?id={id}");
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
