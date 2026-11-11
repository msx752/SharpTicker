namespace SharpTicker.Scenarios.Harness;

/// <summary>
/// A fault the scenario injected: its type (stop, kill, pause, cut, latency), the node it hit (none for a store fault), when
/// it began and when it ended (the same instant for a stop or a kill).
/// </summary>
public sealed record FaultEvent(string Type, string? NodeIdentifier = null)
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset Until { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// What must hold after any mix of load and faults. Each documented exception is matched to its cause; anything else
/// fails with the ticker ids involved.
/// </summary>
public static class Invariants
{
    /// <summary>
    /// A job attempt starts again only when the node of the earlier start was killed between that start and the next one,
    /// before that run ended, or (open issue
    /// #74) when the earlier run ended while a fault made its final status write fail and its node was killed or stopped
    /// after that end and before the new start: the node's next start releases the row it still holds as InProgress, and
    /// the job runs again. Returns how many re-starts each cause explains.
    /// </summary>
    public static (int AfterKill, int After74) AssertNoUnexplainedDoubleStart(RunLog runs, IReadOnlyList<FaultEvent> faults)
    {
        var afterKill = 0;
        var after74 = 0;
        var events = runs.Events;
        foreach (var group in events.Where(e => e.IsStart).GroupBy(e => (e.TickerId, e.Attempt)))
        {
            var starts = group.OrderBy(e => e.At).ToList();
            for (var i = 0; i < starts.Count - 1; i++)
            {
                var earlier = starts[i];
                var later = starts[i + 1];
                var end = events.FirstOrDefault(e => e.IsEnd && e.TickerId == earlier.TickerId && e.Attempt == earlier.Attempt
                                                     && e.Node == earlier.Node && e.Container == earlier.Container);
                if (end == null && faults.Any(f => f.Type == "kill" && f.NodeIdentifier == earlier.Node && f.At >= earlier.At - Slack && f.At <= later.At + Slack))
                {
                    afterKill++;
                    continue;
                }

                var by74 = end != null && FinalWriteCouldFail(end, faults)
                           && faults.Any(f => f.NodeIdentifier == earlier.Node && f.Type is "kill" or "stop" && f.At >= end.At && f.At <= later.At);
                Assert.True(by74,
                    $"ticker {group.Key.TickerId} attempt {group.Key.Attempt} started {starts.Count} times " +
                    $"({string.Join(", ", starts.Select(s => $"{s.Node} at {s.At:O}"))}) and no kill explains it");
                after74++;
            }
        }

        return (afterKill, after74);
    }

    /// <summary>
    /// Nothing is left InProgress except the documented limits, each matched to its cause: a MongoDB job of a killed node
    /// (no release on MongoDB); tolerated open issues, only where an injected fault explains them: an inline child whose
    /// node stopped or was killed while it ran (#84), a job that ended while a fault made its final status write fail (#74).
    /// </summary>
    public static void AssertNothingInProgressExceptKnownLimits(IReadOnlyList<TickerView> tickers, RunLog runs, IReadOnlyList<FaultEvent> faults, Store store)
    {
        foreach (var ticker in tickers.Where(t => t.Status == TickerStatus.InProgress))
            Assert.True(KnownLimit(ticker, tickers, runs, faults, store) != null, $"ticker {ticker.Id} ({ticker.Function}) is InProgress with no known cause; starts {runs.Starts(ticker.Id)}, ends {runs.Ends(ticker.Id)}");
    }

    /// <summary>Every due job ended (a final status), or is InProgress for a known cause.</summary>
    public static void AssertEveryDueJobEnded(IReadOnlyList<TickerView> tickers, IReadOnlyCollection<Guid> dueIds, RunLog runs, IReadOnlyList<FaultEvent> faults, Store store)
    {
        foreach (var id in dueIds)
        {
            var ticker = tickers.SingleOrDefault(t => t.Id == id);
            Assert.True(ticker != null, $"ticker {id} is not listed any more");
            var ok = IsFinal(ticker!.Status)
                     || (ticker.Status == TickerStatus.InProgress && KnownLimit(ticker, tickers, runs, faults, store) != null)
                     || WaitsForAStuckParent(ticker, tickers, runs, faults, store) != null;
            Assert.True(ok, $"ticker {id} ({ticker.Function}) did not end: {ticker.Status}, starts {runs.Starts(id)}");
        }
    }

    /// <summary>A delayed child (a child with a time of its own) never starts before its parent ended or before its time.</summary>
    public static void AssertDelayedChildrenOrder(IReadOnlyList<TickerView> tickers, RunLog runs)
    {
        foreach (var child in tickers.Where(t => t.ParentId != null && t.ExecutionTime != null))
        {
            var start = runs.For(child.Id).Where(e => e.IsStart).OrderBy(e => e.At).FirstOrDefault();
            if (start == null)
                continue;

            var parentEnd = runs.For(child.ParentId!.Value).Where(e => e.IsEnd).OrderBy(e => e.At).LastOrDefault();
            Assert.True(parentEnd != null && start.At >= parentEnd.At,
                $"delayed child {child.Id} started at {start.At:O}, before its parent {child.ParentId} ended ({parentEnd?.At.ToString("O") ?? "never"})");
            var own = new DateTimeOffset(DateTime.SpecifyKind(child.ExecutionTime!.Value, DateTimeKind.Utc));
            Assert.True(start.At >= own, $"delayed child {child.Id} started at {start.At:O}, before its own time {own:O}");
        }
    }

    /// <summary>
    /// A child that never started because an ancestor is InProgress for a known cause (e.g. #74: the ancestor ran and its
    /// final status write failed, so it never ends and nothing below it starts); every row between them never started
    /// either. Null otherwise.
    /// </summary>
    public static string? WaitsForAStuckParent(TickerView ticker, IReadOnlyList<TickerView> tickers, RunLog runs, IReadOnlyList<FaultEvent> faults, Store store)
    {
        var current = ticker;
        for (var depth = 0; depth < 32; depth++)
        {
            if (current.ParentId is not { } parentId || runs.Starts(current.Id) > 0 || IsFinal(current.Status))
                return null;
            var parent = tickers.SingleOrDefault(t => t.Id == parentId);
            if (parent == null)
                return null;
            if (parent.Status == TickerStatus.InProgress)
                return KnownLimit(parent, tickers, runs, faults, store) is { } limit ? $"ancestor {parent.Id} stuck ({limit})" : null;
            current = parent;
        }

        return null;
    }

    /// <summary>
    /// The documented cause of an InProgress row, or null. Known open issues are tolerated here only where the injected
    /// faults explain them; they are not the behaviour under test.
    /// </summary>
    public static string? KnownLimit(TickerView ticker, IReadOnlyList<TickerView> tickers, RunLog runs, IReadOnlyList<FaultEvent> faults, Store store)
    {
        var events = runs.For(ticker.Id);
        var end = events.Where(e => e.IsEnd).OrderBy(e => e.At).LastOrDefault();
        if (end != null)
            return FinalWriteCouldFail(end, faults) ? "#74" : null;

        // The node that started the job, or for a claim whose job never started, the node that holds the row.
        var start = events.Where(e => e.IsStart).OrderBy(e => e.At).LastOrDefault();
        var node = start?.Node ?? ticker.LockHolder;
        if (string.IsNullOrEmpty(node))
            return null;

        // Only a fault after the row was taken explains it: after the start, or for a claim that never started, after a
        // moment shortly before the row was due (30 s leaves room for an early claim). A row with neither is not judged
        // by time.
        DateTimeOffset? taken = start?.At
                                ?? (ticker.ExecutionTime is { } due ? new DateTimeOffset(DateTime.SpecifyKind(due, DateTimeKind.Utc)).AddSeconds(-30) : null);

        bool Hit(params string[] types) => faults.Any(f => types.Contains(f.Type) && f.NodeIdentifier == node
                                                         && (taken == null || f.At >= taken.Value - Slack));

        // docs/guide/operations.md, "What is guaranteed": MongoDB has no release step for an instance that stopped. A Redis
        // node that is killed and started again releases what its earlier run left, so a kill explains nothing there.
        if (store == Store.MongoDb && Hit("kill"))
            return "MongoDB kill";

        var isInlineChild = ticker.ParentId != null && ticker.ExecutionTime == null;
        if (isInlineChild && Hit("stop", "kill"))
            return "#84";

        return null;
    }

    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Open issue #74: a job that ended stays InProgress when its final status write fails. Only a fault can make that
    /// write fail: a store cut or latency around the end, or a stop or kill of the job's node right after it.
    /// </summary>
    public static bool FinalWriteCouldFail(RunEvent end, IReadOnlyList<FaultEvent> faults)
        => faults.Any(f =>
            (f.NodeIdentifier == null && f.Type is "cut" or "latency" && end.At >= f.At - Slack && end.At <= f.Until + Slack)
            || (f.NodeIdentifier == end.Node && f.Type is "stop" or "kill" && f.At >= end.At - Slack && f.At <= end.At + Slack));

    private static bool IsFinal(TickerStatus status)
        => status is TickerStatus.Done or TickerStatus.DueDone or TickerStatus.Failed or TickerStatus.Cancelled or TickerStatus.Skipped;
}
