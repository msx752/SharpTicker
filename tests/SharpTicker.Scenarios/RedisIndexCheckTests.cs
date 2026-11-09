using SharpTicker.Scenarios.Harness;

namespace SharpTicker.Scenarios;

/// <summary>The soak's Redis index rules on hand-made snapshots: a clean one, then one break per rule.</summary>
public class RedisIndexCheckTests
{
    private static readonly Guid Tt = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Child = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid Co = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid Cron = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid Gone = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");
    private static readonly DateTime At = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    // A pending time ticker with an inline child (no time of its own, so not pending), and a pending occurrence.
    private static RedisSnapshot Clean()
    {
        var s = new RedisSnapshot();
        s.TimeTickerIds.UnionWith([Tt, Child]);
        s.TimeTickers[Tt] = new RedisRow(TickerStatus.Idle, At);
        s.TimeTickers[Child] = new RedisRow(TickerStatus.Idle, null);
        s.TimeTickerPending.Add(Tt);
        s.Children[Tt] = [Child];
        s.OccurrenceIds.Add(Co);
        s.Occurrences[Co] = new RedisRow(TickerStatus.Queued, At);
        s.OccurrencePending.Add(Co);
        s.CronOccurrences[Cron] = [Co];
        return s;
    }

    [Fact]
    public void ACleanStore_HasNoProblems() => Assert.Empty(RedisIndexCheck.Problems(Clean()));

    public static TheoryData<string, string> Breaks => new()
    {
        { "tt-ids", "tq:tt:ids holds eeeeeeee" },
        { "co-ids", "tq:co:ids holds eeeeeeee" },
        { "cron-occurrences", "tq:cron:dddddddd-0000-0000-0000-000000000001:occurrences holds eeeeeeee" },
        { "children", "tq:tt:aaaaaaaa-0000-0000-0000-000000000001:children holds eeeeeeee" },
        { "tt-pending-done", "tq:tt:pending holds aaaaaaaa-0000-0000-0000-000000000001, its row is Done" },
        { "co-pending-gone", "tq:co:pending holds eeeeeeee-0000-0000-0000-000000000001, its row is gone" },
        { "lost", "time ticker aaaaaaaa-0000-0000-0000-000000000001 is Idle" },
    };

    [Theory]
    [MemberData(nameof(Breaks))]
    public void EachBreak_IsReported(string kind, string expected)
    {
        var s = Clean();
        switch (kind)
        {
            case "tt-ids": s.TimeTickerIds.Add(Gone); break;
            case "co-ids": s.OccurrenceIds.Add(Gone); break;
            case "cron-occurrences": s.CronOccurrences[Cron].Add(Gone); break;
            case "children": s.Children[Tt].Add(Gone); break;
            case "tt-pending-done": s.TimeTickers[Tt] = new RedisRow(TickerStatus.Done, At); break;
            case "co-pending-gone": s.OccurrencePending.Add(Gone); break;
            case "lost": s.TimeTickerPending.Remove(Tt); break;
        }

        var problem = Assert.Single(RedisIndexCheck.Problems(s));
        Assert.StartsWith(expected, problem);
    }

    [Theory]
    [InlineData("{\"Id\":\"x\",\"Status\":3,\"ExecutionTime\":\"2026-11-09T12:00:00Z\"}", TickerStatus.Done, true)]
    [InlineData("{\"status\":\"Queued\",\"executionTime\":null}", TickerStatus.Queued, false)]
    public void Row_ReadsTheStatusAsANumberOrAName(string json, TickerStatus status, bool hasTime)
    {
        var row = RedisIndexCheck.Row(json)!;
        Assert.Equal(status, row.Status);
        Assert.Equal(hasTime, row.ExecutionTime != null);
    }

    [Theory]
    [InlineData("123.5MiB ", 129_499_136L)]
    [InlineData("1.5GiB", 1_610_612_736L)]
    [InlineData("512KiB", 524_288L)]
    [InlineData("900B", 900L)]
    public void ParseMemory_ReadsDockerStats(string text, long bytes) => Assert.Equal(bytes, ScenarioEnvironment.ParseMemory(text));
}
