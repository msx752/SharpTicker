using SharpTicker.Scenarios.Harness;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace SharpTicker.Scenarios;

/// <summary>
/// #94: on the Redis job store a row and its index entries are written by separate commands. The fault here is the one
/// the fix is about: Redis refuses the index write of an update (an ACL takes <c>ZADD</c> away for as long as the update
/// runs; the row is written with <c>SET</c>, which stays allowed). The job must then start at the time its row shows.
/// <para>
/// A running scheduler loop that has the job in its window reads the row and plans it at the row's time whatever its
/// entry says, so that case shows nothing (a single job on a running node passes on the code before the fix too). The
/// scenarios are the two cases the loop does not cover: the node is down when the old time passes and only its fallback
/// sees the stale entry after the start, and another job holds the loop's window while the moved job's entry is still
/// at its old, later time.
/// </para>
/// </summary>
public class RedisWriteOrderScenarios(ITestOutputHelper output)
{
    private const string Pending = "tq:tt:pending";

    // Before the fix the row was written first and its entry after it: the entry stayed at the old, earlier time, and
    // after the restart the fallback took the overdue entry at once, a minute before the time the row (and the dashboard)
    // shows. Now the readers take a row only at its own time and move its entry to that time.
    [Fact]
    public async Task AJobMovedLater_WhoseIndexWriteIsRefused_DoesNotStartAtItsOldTimeAfterARestart()
    {
        await using var env = await ScenarioEnvironment.StartAsync("write-order-later", Store.Redis, nodes: 1);
        await using var redis = await ConnectionMultiplexer.ConnectAsync(env.DirectStoreConnection);
        var db = redis.GetDatabase();

        var first = DateTime.UtcNow.AddSeconds(25);
        var id = await env.Nodes[0].Api.AddTimeTickerAsync(new TickerSeed { Function = Functions.Record, ExecutionTime = first });
        var (row, entry) = await MoveWithRefusedIndexWriteAsync(env, db, id, first.AddSeconds(75));
        Assert.True(entry < row, $"inconclusive: the entry ({entry:HH:mm:ss.fff}) is not earlier than the row ({row:HH:mm:ss.fff})");

        // Down while the old time passes; started again after it.
        await env.StopNodeAsync(0);
        Assert.True(env.Runs.Starts(id) == 0 && DateTime.UtcNow < first, $"inconclusive: the job started, or its old time passed, before the stop: {Describe(env, id)}");
        await Task.Delay(first - DateTime.UtcNow + TimeSpan.FromSeconds(5));
        await env.StartNodeAsync(0);

        await AssertStartsAtTheRowTimeAsync(env, id, row, entry);
    }

    // Before the fix the row was written with the new, earlier time and the entry stayed at the old, later one. The loop's
    // window held the other job, the fallback takes only an overdue entry, so the job started at its old time, two minutes
    // after the time its row shows. Now the update first moves an entry that is later than the new time; that write is
    // refused here, so nothing is written, the update reports the failure, and the job keeps the time its row shows.
    [Fact]
    public async Task AJobMovedEarlier_WhoseIndexWriteIsRefused_WhileAnotherJobHoldsTheWindow_StartsAtTheTimeItsRowShows()
    {
        await using var env = await ScenarioEnvironment.StartAsync("write-order-earlier", Store.Redis, nodes: 1);
        await using var redis = await ConnectionMultiplexer.ConnectAsync(env.DirectStoreConnection);
        var db = redis.GetDatabase();

        var now = DateTime.UtcNow;
        var other = await env.Nodes[0].Api.AddTimeTickerAsync(new TickerSeed { Function = Functions.Record, ExecutionTime = now.AddSeconds(30) });
        var id = await env.Nodes[0].Api.AddTimeTickerAsync(new TickerSeed { Function = Functions.Record, ExecutionTime = now.AddSeconds(150) });
        var (row, entry) = await MoveWithRefusedIndexWriteAsync(env, db, id, now.AddSeconds(10));
        Assert.True(env.Runs.Starts(other) == 0, $"inconclusive: the other job ran before the update: {Describe(env, other)}");

        await AssertStartsAtTheRowTimeAsync(env, id, row, entry);
    }

    // The update through the dashboard, with ZADD refused for as long as it runs. Returns the row's time and the entry's.
    private async Task<(DateTime Row, DateTime? Entry)> MoveWithRefusedIndexWriteAsync(ScenarioEnvironment env, IDatabase db, Guid id, DateTime moved)
    {
        await env.UntilAsync(async () => await db.SortedSetScoreAsync(Pending, id.ToString()) != null,
            () => "the added job has no pending entry", TimeSpan.FromSeconds(10));
        string? refused = null;
        await db.ExecuteAsync("ACL", "SETUSER", "default", "-zadd");
        try
        {
            await env.Nodes[0].Dashboard.UpdateTimeTickerAsync(id, Functions.Record, null, moved);
        }
        catch (Exception e)
        {
            refused = e.Message;
        }
        finally
        {
            await db.ExecuteAsync("ACL", "SETUSER", "default", "+zadd");
        }
        Assert.True(refused != null, "inconclusive: the update went through, the refused index write did not take hold");

        var row = (await env.Nodes[0].Dashboard.GetAllTimeTickersAsync()).Single(t => t.Id == id).ExecutionTime!.Value.ToUniversalTime();
        var score = await db.SortedSetScoreAsync(Pending, id.ToString());
        var entry = score.HasValue ? new DateTime((long)score.Value, DateTimeKind.Utc) : (DateTime?)null;
        output.WriteLine($"moved to {moved:HH:mm:ss}; after the refused update the row shows {row:HH:mm:ss.fff}, its entry {entry:HH:mm:ss.fff}");
        return (row, entry);
    }

    private async Task AssertStartsAtTheRowTimeAsync(ScenarioEnvironment env, Guid id, DateTime row, DateTime? entry)
    {
        var latest = new[] { row, entry ?? row }.Max();
        await env.UntilAsync(() => Task.FromResult(env.Runs.Starts(id) > 0),
            () => $"the job did not start (row {row:HH:mm:ss.fff}, entry {entry:HH:mm:ss.fff})", latest - DateTime.UtcNow + TimeSpan.FromSeconds(60));
        var start = env.Runs.For(id).First(e => e.IsStart).At.UtcDateTime;
        output.WriteLine($"started {start:HH:mm:ss.fff}; {Describe(env, id)}");
        // The scheduler starts a due job within about a second; the fallback (every 2 s) within a few more.
        Assert.True(start >= row - TimeSpan.FromSeconds(1),
            $"the job started at {start:HH:mm:ss.fff}, {(row - start).TotalSeconds:0.0} s before the time its row shows ({row:HH:mm:ss.fff}; entry {entry:HH:mm:ss.fff})");
        Assert.True(start <= row + TimeSpan.FromSeconds(15),
            $"the job started at {start:HH:mm:ss.fff}, {(start - row).TotalSeconds:0.0} s after the time its row shows ({row:HH:mm:ss.fff}; entry {entry:HH:mm:ss.fff})");

        var status = TickerStatus.InProgress;
        await env.UntilAsync(
            async () => (status = (await env.Nodes[0].Dashboard.GetAllTimeTickersAsync()).Single(t => t.Id == id).Status) is TickerStatus.Done or TickerStatus.DueDone,
            () => $"the job did not end: {status}", TimeSpan.FromSeconds(30));
        Assert.Equal(1, env.Runs.Starts(id));
    }

    private static string Describe(ScenarioEnvironment env, Guid id)
        => string.Join("; ", env.Runs.For(id).Select(e => $"{e.Event} {e.Node} {e.At:HH:mm:ss.fff} {e.Status}"));
}
