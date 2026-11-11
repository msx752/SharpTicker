using SharpTicker.Scenarios.Harness;

namespace SharpTicker.Scenarios;

/// <summary>Cron run-now, and a timeout inside a job.</summary>
public class RunNowAndRetryScenarios
{
    // #52: a dashboard "Run now" of a cron runs its occurrence once and it ends; before the fix MongoDB claimed the
    // occurrence without its cron and never ran it.
    [Theory]
    [InlineData(Store.EfCorePostgres)]
    [InlineData(Store.MongoDb)]
    [InlineData(Store.Redis)]
    public async Task CronRunNow_RunsTheOccurrenceOnce(Store store)
    {
        await using var env = await ScenarioEnvironment.StartAsync("run-now", store, nodes: 2);
        var node = env.Nodes[0];

        // Far in the future: only run-now makes it run.
        var cronId = await node.Api.AddCronAsync(Functions.Record, Crons.Far);
        await node.Dashboard.RunCronNowAsync(cronId);

        // The run-now occurrence is the one due now; the cron's next regular one (about 180 days ahead, Crons.Far) may be listed too.
        var soon = DateTime.UtcNow.AddDays(1);
        IReadOnlyList<OccurrenceView> occurrences = [];
        await env.UntilAsync(async () =>
        {
            occurrences = await node.Dashboard.GetOccurrencesAsync(cronId);
            var now = occurrences.Where(o => o.ExecutionTime < soon).ToList();
            return now.Count > 0 && now.All(o => o.Status is TickerStatus.Done or TickerStatus.DueDone);
        }, () => $"occurrences: {string.Join(", ", occurrences.Select(o => $"{o.Id} {o.ExecutionTime:O} {o.Status}"))}");

        await Task.Delay(TimeSpan.FromSeconds(5));
        occurrences = await node.Dashboard.GetOccurrencesAsync(cronId);
        var occurrence = Assert.Single(occurrences, o => o.ExecutionTime < soon);
        Assert.Equal(TickerStatus.Done, occurrence.Status);
        Assert.Equal(1, env.Runs.Starts(occurrence.Id));
        Assert.Single(env.Runs.Events, e => e.IsStart);
        Assert.All(occurrences.Where(o => o.Id != occurrence.Id), o =>
        {
            Assert.True(o.ExecutionTime > DateTime.UtcNow.AddDays(1), $"unexpected occurrence {o.Id} at {o.ExecutionTime:O} ({o.Status})");
            Assert.Equal(0, env.Runs.Starts(o.Id));
        });
    }

    // #21: a timeout inside the job (HttpClient's own) is a failure: retried, and never recorded Cancelled.
    [Fact]
    public async Task ATimeoutInsideAJob_IsAFailureAndIsRetried()
    {
        await using var env = await ScenarioEnvironment.StartAsync("timeout-retried", Store.EfCorePostgres, nodes: 1);
        var node = env.Nodes[0];

        var id = await node.Api.AddTimeTickerAsync(new TickerSeed
        {
            Function = Functions.HttpTimeout, ExecutionTime = DateTime.UtcNow, Retries = 2, RetryIntervals = [1, 1],
        });

        TickerView? ticker = null;
        await env.UntilAsync(async () =>
        {
            ticker = (await node.Dashboard.GetAllTimeTickersAsync()).Single(t => t.Id == id);
            return ticker.Status is TickerStatus.Failed or TickerStatus.Cancelled or TickerStatus.Done;
        }, () => $"status {ticker?.Status}, attempts {string.Join(",", env.Runs.For(id).Where(e => e.IsStart).Select(e => e.Attempt))}");

        Assert.Equal(TickerStatus.Failed, ticker!.Status);
        Assert.Equal(new[] { 0, 1, 2 }, env.Runs.For(id).Where(e => e.IsStart).Select(e => e.Attempt).ToArray());
        Assert.All(env.Runs.For(id).Where(e => e.IsEnd), e => Assert.Equal("Failed", e.Status));
        // Each attempt failed by the HttpClient timeout itself, not by another error on the way.
        Assert.Equal(3, env.CountNodeLogLines($"SCENARIO_HTTP_TIMEOUT {id}"));
    }
}
