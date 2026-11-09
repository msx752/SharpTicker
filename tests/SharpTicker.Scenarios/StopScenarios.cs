using SharpTicker.Scenarios.Harness;

namespace SharpTicker.Scenarios;

/// <summary>What a graceful stop does with running jobs.</summary>
public class StopScenarios
{
    // #26: the stop cancels the job's token, then waits a little less than the host's shutdown timeout (10 s here) for the running
    // jobs (docs/guide/jobs.md). A job that ignores its token and finishes within the wait is recorded Done.
    [Theory]
    [InlineData(Store.EfCorePostgres)]
    [InlineData(Store.MongoDb)]
    [InlineData(Store.Redis)]
    public async Task StopWaitsForAShortJob_ItEndsDone(Store store)
    {
        var (status, starts, end) = await StopWhileRunning("stop-short", store, Functions.Stubborn, seconds: 4);

        Assert.Equal(TickerStatus.Done, status);
        Assert.Equal(1, starts);
        Assert.Equal("Done", end);
    }

    // #26: a job that ignores its token and outlasts the wait is recorded Cancelled by the stop itself; it never ends in the process.
    [Theory]
    [InlineData(Store.EfCorePostgres)]
    [InlineData(Store.MongoDb)]
    [InlineData(Store.Redis)]
    public async Task StopRecordsALongJobCancelled_NotInProgress(Store store)
    {
        var (status, starts, end) = await StopWhileRunning("stop-long", store, Functions.Stubborn, seconds: 60);

        Assert.Equal(TickerStatus.Cancelled, status);
        Assert.Equal(1, starts);
        Assert.Null(end);
    }

    // #26: a job that honours its token ends at once when the stop cancels it, and records Cancelled itself.
    [Theory]
    [InlineData(Store.EfCorePostgres)]
    [InlineData(Store.MongoDb)]
    [InlineData(Store.Redis)]
    public async Task StopCancelsAJobThatHonoursItsToken_ItRecordsCancelled(Store store)
    {
        var (status, starts, end) = await StopWhileRunning("stop-token", store, Functions.Slow, seconds: 60);

        Assert.Equal(TickerStatus.Cancelled, status);
        Assert.Equal(1, starts);
        Assert.Equal("Cancelled", end);
    }

    /// <summary>
    /// Runs one job, stops the node that started it 1 s later, and reads the final status from the other node. Returns the
    /// status and the start count after a further 5 s (no re-run), and the status of the job's own end line (null: none).
    /// </summary>
    private static async Task<(TickerStatus Status, int Starts, string? End)> StopWhileRunning(string scenario, Store store, string function, int seconds)
    {
        await using var env = await ScenarioEnvironment.StartAsync(scenario, store, nodes: 2);

        var id = await env.Nodes[0].Api.AddTimeTickerAsync(new TickerSeed
        {
            Function = function, ExecutionTime = DateTime.UtcNow, Request = new WaitRequest { Seconds = seconds },
        });

        await env.UntilAsync(() => Task.FromResult(env.Runs.Starts(id) == 1), () => $"no start line for {id}");
        var runner = env.Nodes.Single(n => n.Identifier == env.Runs.For(id).Single(e => e.IsStart).Node);
        var other = env.Nodes.Single(n => n != runner);

        await Task.Delay(TimeSpan.FromSeconds(1));
        await env.StopNodeAsync(runner.Index);

        TickerView? ticker = null;
        await env.UntilAsync(async () =>
        {
            ticker = (await other.Dashboard.GetAllTimeTickersAsync()).Single(t => t.Id == id);
            return ticker.Status is not (TickerStatus.InProgress or TickerStatus.Queued or TickerStatus.Idle);
        }, () => $"status {ticker?.Status}");

        await Task.Delay(TimeSpan.FromSeconds(5));
        ticker = (await other.Dashboard.GetAllTimeTickersAsync()).Single(t => t.Id == id);
        return (ticker.Status, env.Runs.Starts(id), env.Runs.For(id).SingleOrDefault(e => e.IsEnd)?.Status);
    }
}
