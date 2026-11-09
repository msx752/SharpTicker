using SharpTicker.Scenarios.Harness;

namespace SharpTicker.Scenarios;

/// <summary>
/// #45: a node's loop acquires the next due rows ahead of time (Queued, locked to the node) and sleeps
/// until they are due. When that node is late by more than a second, the other node's fallback takes the rows and runs
/// them. Before the fix the late node then marked them InProgress over the fallback's claim and ran them a second time.
/// </summary>
public class ExactlyOnceScenarios
{
    private static readonly TimeSpan DueIn = TimeSpan.FromSeconds(10);

    // Not on Redis: there the other node's fallback does not take a frozen node's Queued time tickers (the frozen node runs
    // them when it wakes, once), and before the fix the Redis loop did not hold them Queued ahead at all, so the race of
    // #45 cannot be reached this way. The cron case below covers Redis.
    [Theory]
    [InlineData(Store.EfCorePostgres)]
    [InlineData(Store.MongoDb)]
    public async Task ALateLoop_DoesNotRunTimeTickersTheFallbackTook(Store store)
    {
        await using var env = await ScenarioEnvironment.StartAsync("late-loop-time", store, nodes: 2);

        var due = DateTime.UtcNow + DueIn;
        var ids = await env.Nodes[0].Api.AddTimeTickersAsync(Enumerable.Range(0, 20)
            .Select(_ => new TickerSeed { Function = Functions.Record, ExecutionTime = due }).ToArray());

        // Wait until one node's loop holds them Queued, then freeze that node past their time and two fallback passes.
        IReadOnlyList<TickerView> tickers = [];
        await env.UntilAsync(async () =>
        {
            tickers = await env.Nodes[0].Dashboard.GetAllTimeTickersAsync();
            return ids.All(id => tickers.Single(t => t.Id == id) is { Status: TickerStatus.Queued, LockHolder: not null });
        }, () => string.Join(", ", tickers.Where(t => ids.Contains(t.Id)).GroupBy(t => (t.Status, t.LockHolder)).Select(g => $"{g.Key} x{g.Count()}")),
            DueIn - TimeSpan.FromSeconds(3));
        // Usually one loop holds them all; if both loops took a share, the node with the larger share is the late one (its
        // rows are the race under test, the other node's own rows simply run).
        var holder = ids.GroupBy(id => tickers.Single(t => t.Id == id).LockHolder).OrderByDescending(g => g.Count()).First().Key;
        var late = env.Nodes.Single(n => n.Identifier == holder);
        var other = env.Nodes.Single(n => n != late);

        await env.PauseNodeAsync(late.Index, due - DateTime.UtcNow + TimeSpan.FromSeconds(6));

        await env.UntilAsync(async () =>
        {
            tickers = await other.Dashboard.GetAllTimeTickersAsync();
            return ids.All(id => tickers.Single(t => t.Id == id).Status is TickerStatus.Done or TickerStatus.DueDone);
        }, () => string.Join(", ", tickers.Where(t => ids.Contains(t.Id)).GroupBy(t => t.Status).Select(g => $"{g.Key} x{g.Count()}")));

        // Give the late node time to do whatever it would do with the rows it held.
        await Task.Delay(TimeSpan.FromSeconds(5));
        foreach (var id in ids)
            Assert.True(env.Runs.Starts(id) == 1, $"ticker {id} started {env.Runs.Starts(id)} times: {string.Join(", ", env.Runs.For(id).Where(e => e.IsStart).Select(e => e.Node))}");
        Assert.Contains(env.Runs.Events, e => e.IsStart && e.Node == other.Identifier);
    }

    [Theory]
    [InlineData(Store.EfCorePostgres)]
    [InlineData(Store.MongoDb)]
    [InlineData(Store.Redis)]
    public async Task ALateLoop_DoesNotRunACronOccurrenceTheFallbackTook(Store store)
    {
        await using var env = await ScenarioEnvironment.StartAsync("late-loop-cron", store, nodes: 2);

        // Every minute at second 0; the occurrence used is at least 10 s away.
        var cronId = await env.Nodes[0].Api.AddCronAsync(Functions.Record, "0 * * * * *");
        var now = DateTime.UtcNow;
        var due = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc).AddMinutes(1);
        if (due - now < TimeSpan.FromSeconds(10))
            due = due.AddMinutes(1);

        IReadOnlyList<OccurrenceView> occurrences = [];
        OccurrenceView? held = null;
        await env.UntilAsync(async () =>
        {
            occurrences = await env.Nodes[0].Dashboard.GetOccurrencesAsync(cronId);
            held = occurrences.SingleOrDefault(o => o.ExecutionTime == due && o.Status == TickerStatus.Queued && o.LockHolder != null);
            return held != null;
        }, () => $"occurrences: {string.Join(", ", occurrences.Select(o => $"{o.ExecutionTime:O} {o.Status} {o.LockHolder}"))}",
            due - DateTime.UtcNow - TimeSpan.FromSeconds(2));
        var late = env.Nodes.Single(n => n.Identifier == held!.LockHolder);
        var other = env.Nodes.Single(n => n != late);

        await env.PauseNodeAsync(late.Index, due - DateTime.UtcNow + TimeSpan.FromSeconds(6));

        await env.UntilAsync(async () =>
        {
            occurrences = await other.Dashboard.GetOccurrencesAsync(cronId);
            return occurrences.Single(o => o.Id == held!.Id).Status is TickerStatus.Done or TickerStatus.DueDone;
        }, () => $"occurrence {held!.Id}: {occurrences.SingleOrDefault(o => o.Id == held.Id)?.Status}");

        await Task.Delay(TimeSpan.FromSeconds(5));
        Assert.True(env.Runs.Starts(held!.Id) == 1,
            $"occurrence {held.Id} started {env.Runs.Starts(held.Id)} times: {string.Join(", ", env.Runs.For(held.Id).Where(e => e.IsStart).Select(e => e.Node))}");
        Assert.Equal(other.Identifier, env.Runs.For(held.Id).Single(e => e.IsStart).Node);
    }
}
