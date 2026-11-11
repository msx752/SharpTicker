using SharpTicker.Scenarios.Harness;
using Xunit.Sdk;

namespace SharpTicker.Scenarios;

/// <summary>The invariants of the composite scenarios, on hand-made run logs: one passing and one failing case each.</summary>
public class InvariantTests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private long _seq;

    private void Line(RunLog log, string evt, Guid id, string node, int seconds, string? status = null, int attempt = 0, string container = "c")
        => log.Add(container, $"SCENARIO_RUN {{\"seq\":{++_seq},\"event\":\"{evt}\",\"tickerId\":\"{id}\",\"function\":\"f\",\"node\":\"{node}\",\"attempt\":{attempt},\"at\":\"{T0.AddSeconds(seconds):O}\"{(status == null ? "" : $",\"status\":\"{status}\"")}}}");

    private static readonly Guid C = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private static TickerView View(Guid id, TickerStatus status, Guid? parent = null, DateTime? at = null)
        => new() { Id = id, Status = status, ParentId = parent, ExecutionTime = at };

    private static FaultEvent StoreCut(int fromSeconds, int toSeconds)
        => new("cut") { At = T0.AddSeconds(fromSeconds), Until = T0.AddSeconds(toSeconds) };

    [Fact]
    public void DoubleStart_ExplainedByAKillOfTheFirstNode_Passes()
    {
        var log = new RunLog();
        Line(log, "start", A, "n1", 0);
        Line(log, "start", A, "n2", 30);
        Line(log, "end", A, "n2", 31, "Done");

        Invariants.AssertNoUnexplainedDoubleStart(log, [new FaultEvent("kill", "n1") { At = T0.AddSeconds(5) }]);
    }

    [Fact]
    public void DoubleStart_AfterAKillBeforeTheFirstStart_Fails()
    {
        // The node was killed and started again before it took the job: that kill explains nothing about this run.
        var log = new RunLog();
        Line(log, "start", A, "n1", 20);
        Line(log, "start", A, "n2", 30);

        Assert.ThrowsAny<XunitException>(() => Invariants.AssertNoUnexplainedDoubleStart(log, [new FaultEvent("kill", "n1") { At = T0.AddSeconds(5) }]));
    }

    [Fact]
    public void DoubleStart_WithoutAKill_Fails()
    {
        var log = new RunLog();
        Line(log, "start", A, "n1", 0);
        Line(log, "end", A, "n1", 1, "Done");
        Line(log, "start", A, "n2", 2);

        Assert.ThrowsAny<XunitException>(() => Invariants.AssertNoUnexplainedDoubleStart(log, [new FaultEvent("stop", "n1")]));
    }

    [Fact]
    public void DoubleStart_AfterAnEndDuringAStoreCut_AndALaterKill_IsTheKnownIssue74()
    {
        var log = new RunLog();
        Line(log, "start", A, "n1", 0);
        Line(log, "end", A, "n1", 10, "Done");
        Line(log, "start", A, "n2", 30);
        var kill = new FaultEvent("kill", "n1") { At = T0.AddSeconds(25) };

        var (afterKill, after74) = Invariants.AssertNoUnexplainedDoubleStart(log, [StoreCut(9, 12), kill]);
        Assert.Equal((0, 1), (afterKill, after74));
    }

    [Fact]
    public void DoubleStart_AfterAnEndDuringAStoreCut_WithoutALaterKill_Fails()
    {
        // First: the end fell in the cut, but the node's only kill came before that end, so no restart released the row.
        // Second: a kill after the end, but the end was outside the cut, so its final status write had no reason to fail.
        var log = new RunLog();
        Line(log, "start", A, "n1", 0);
        Line(log, "end", A, "n1", 10, "Done");
        Line(log, "start", A, "n2", 30);
        var early = new FaultEvent("kill", "n1") { At = T0.AddSeconds(5) };

        Assert.ThrowsAny<XunitException>(() => Invariants.AssertNoUnexplainedDoubleStart(log, [StoreCut(9, 12), early]));
        Assert.ThrowsAny<XunitException>(() => Invariants.AssertNoUnexplainedDoubleStart(log,
            [StoreCut(0, 5), new FaultEvent("kill", "n1") { At = T0.AddSeconds(25) }]));
    }

    [Fact]
    public void Retries_AreNotDoubleStarts()
    {
        var log = new RunLog();
        Line(log, "start", A, "n1", 0, attempt: 0);
        Line(log, "end", A, "n1", 1, "Failed", attempt: 0);
        Line(log, "start", A, "n1", 2, attempt: 1);

        Invariants.AssertNoUnexplainedDoubleStart(log, []);
    }

    [Fact]
    public void InProgress_OnMongoAfterAKill_IsAKnownLimit()
    {
        var log = new RunLog();
        Line(log, "start", A, "n1", 0);

        Invariants.AssertNothingInProgressExceptKnownLimits([View(A, TickerStatus.InProgress)], log, [new FaultEvent("kill", "n1")], Store.MongoDb);
    }

    // #74 on a parent: it ran and ended during a store cut, its final write failed, so it stays InProgress and its child
    // never starts.
    [Fact]
    public void AChildOfAParentStuckBy74_IsExplained_AChildOfARunningParentIsNot()
    {
        var log = new RunLog();
        Line(log, "start", A, "n1", 0);
        Line(log, "end", A, "n1", 3, "Done");
        var parent = View(A, TickerStatus.InProgress);
        var child = View(B, TickerStatus.Idle, parent: A);

        Invariants.AssertEveryDueJobEnded([parent, child], [A, B], log, [StoreCut(2, 6)], Store.EfCorePostgres);

        // The same parent still running (no end line) explains nothing.
        var running = new RunLog();
        Line(running, "start", A, "n1", 0);
        Assert.ThrowsAny<XunitException>(() => Invariants.AssertEveryDueJobEnded([parent, child], [A, B], running, [StoreCut(2, 6)], Store.EfCorePostgres));
    }

    // An inline chain: the root is stuck by #74, so neither its child nor its grandchild ever starts.
    [Fact]
    public void AGrandchildOfARootStuckBy74_IsExplained()
    {
        var log = new RunLog();
        Line(log, "start", A, "n1", 0);
        Line(log, "end", A, "n1", 2, "Done");
        TickerView[] chain = [View(A, TickerStatus.InProgress), View(B, TickerStatus.Idle, parent: A), View(C, TickerStatus.Idle, parent: B)];

        Invariants.AssertEveryDueJobEnded(chain, [A, B, C], log, [StoreCut(1, 4)], Store.EfCorePostgres);
    }

    // A middle row that ran breaks the chain of explanations: its own child is not waiting for the stuck root.
    [Fact]
    public void AGrandchildBelowAStartedChild_IsNotExplainedByTheRoot()
    {
        var log = new RunLog();
        Line(log, "start", A, "n1", 0);
        Line(log, "end", A, "n1", 2, "Done");
        Line(log, "start", B, "n1", 3);
        Line(log, "end", B, "n1", 4, "Done");
        TickerView[] chain = [View(A, TickerStatus.InProgress), View(B, TickerStatus.Done, parent: A), View(C, TickerStatus.Idle, parent: B)];

        Assert.ThrowsAny<XunitException>(() => Invariants.AssertEveryDueJobEnded(chain, [A, B, C], log, [StoreCut(1, 4)], Store.EfCorePostgres));
    }

    // A claim of the killed node whose job never started: no start line, the row is held by that node.
    [Theory]
    [InlineData(Store.MongoDb)]
    public void InProgress_ClaimNeverStarted_OnAKilledNode_IsAKnownLimit(Store store)
    {
        var claimed = new TickerView { Id = A, Status = TickerStatus.InProgress, LockHolder = "n1" };

        Invariants.AssertNothingInProgressExceptKnownLimits([claimed], new RunLog(), [new FaultEvent("kill", "n1")], store);
    }

    [Theory]
    [InlineData(Store.MongoDb)]
    [InlineData(Store.Redis)]
    public void InProgress_ClaimNeverStarted_OnANodeThatWasNotKilled_Fails(Store store)
    {
        var claimed = new TickerView { Id = A, Status = TickerStatus.InProgress, LockHolder = "n2" };

        Assert.ThrowsAny<XunitException>(() => Invariants.AssertNothingInProgressExceptKnownLimits(
            [claimed], new RunLog(), [new FaultEvent("kill", "n1")], store));
    }

    // A Redis node that is killed and started again releases what its earlier run left (and another node releases a node
    // that stays down), so a kill does not explain a row left InProgress there.
    [Fact]
    public void InProgress_OnRedisAfterAKill_Fails()
    {
        var log = new RunLog();
        Line(log, "start", A, "n1", 0);
        var claimed = new TickerView { Id = B, Status = TickerStatus.InProgress, LockHolder = "n1" };

        Assert.ThrowsAny<XunitException>(() => Invariants.AssertNothingInProgressExceptKnownLimits(
            [View(A, TickerStatus.InProgress)], log, [new FaultEvent("kill", "n1") { At = T0.AddSeconds(5) }], Store.Redis));
        Assert.ThrowsAny<XunitException>(() => Invariants.AssertNothingInProgressExceptKnownLimits(
            [claimed], new RunLog(), [new FaultEvent("kill", "n1")], Store.Redis));
    }

    [Fact]
    public void InProgress_OnEfCoreAfterAKill_Fails()
    {
        var log = new RunLog();
        Line(log, "start", A, "n1", 0);

        Assert.ThrowsAny<XunitException>(() => Invariants.AssertNothingInProgressExceptKnownLimits(
            [View(A, TickerStatus.InProgress)], log, [new FaultEvent("kill", "n1")], Store.EfCorePostgres));
    }

    [Fact]
    public void InProgress_InlineChildOfAStoppedNode_IsAKnownLimit()
    {
        var log = new RunLog();
        Line(log, "start", B, "n1", 0);
        Line(log, "end", B, "n1", 1, "Done");
        Line(log, "start", A, "n1", 1);

        Invariants.AssertNothingInProgressExceptKnownLimits(
            [View(B, TickerStatus.Done), View(A, TickerStatus.InProgress, parent: B)], log, [new FaultEvent("stop", "n1") { At = T0.AddSeconds(3) }], Store.Redis);
    }

    [Fact]
    public void InProgress_InlineChildOfANodeStoppedBeforeItStarted_Fails()
    {
        var log = new RunLog();
        Line(log, "start", B, "n1", 0);
        Line(log, "end", B, "n1", 10, "Done");
        Line(log, "start", A, "n1", 10);

        Assert.ThrowsAny<XunitException>(() => Invariants.AssertNothingInProgressExceptKnownLimits(
            [View(B, TickerStatus.Done), View(A, TickerStatus.InProgress, parent: B)], log, [new FaultEvent("stop", "n1") { At = T0.AddSeconds(2) }], Store.Redis));
    }

    // In a chaos run most nodes are killed at some point; only a fault after the row was taken explains it.
    [Theory]
    [InlineData(Store.MongoDb)]
    public void InProgress_AfterAKillBeforeItsStart_Fails(Store store)
    {
        var log = new RunLog();
        Line(log, "start", A, "n1", 20);

        Assert.ThrowsAny<XunitException>(() => Invariants.AssertNothingInProgressExceptKnownLimits(
            [View(A, TickerStatus.InProgress)], log, [new FaultEvent("kill", "n1") { At = T0.AddSeconds(5) }], store));
        Invariants.AssertNothingInProgressExceptKnownLimits(
            [View(A, TickerStatus.InProgress)], log, [new FaultEvent("kill", "n1") { At = T0.AddSeconds(25) }], store);
    }

    [Theory]
    [InlineData(Store.MongoDb)]
    public void InProgress_ClaimNeverStarted_KilledLongBeforeItWasDue_Fails(Store store)
    {
        var claimed = new TickerView { Id = A, Status = TickerStatus.InProgress, LockHolder = "n1", ExecutionTime = T0.AddMinutes(5).UtcDateTime };

        Assert.ThrowsAny<XunitException>(() => Invariants.AssertNothingInProgressExceptKnownLimits(
            [claimed], new RunLog(), [new FaultEvent("kill", "n1") { At = T0 }], store));
        Invariants.AssertNothingInProgressExceptKnownLimits(
            [claimed], new RunLog(), [new FaultEvent("kill", "n1") { At = T0.AddMinutes(5).AddSeconds(1) }], store);
    }

    // #74 is tolerated only where a fault can explain the failed final write: a store fault around the end, or a stop or
    // kill of the job's node right after it.
    [Fact]
    public void InProgress_AfterItsJobEnded_IsTheKnownIssue74_OnlyWithAFaultAtItsEnd()
    {
        var log = new RunLog();
        Line(log, "start", A, "n1", 0);
        Line(log, "end", A, "n1", 10, "Done");
        TickerView[] row = [View(A, TickerStatus.InProgress)];

        Invariants.AssertNothingInProgressExceptKnownLimits(row, log, [StoreCut(9, 12)], Store.EfCorePostgres);
        Invariants.AssertNothingInProgressExceptKnownLimits(row, log, [new FaultEvent("kill", "n1") { At = T0.AddSeconds(11) }], Store.EfCorePostgres);

        Assert.ThrowsAny<XunitException>(() => Invariants.AssertNothingInProgressExceptKnownLimits(row, log, [], Store.EfCorePostgres));
        Assert.ThrowsAny<XunitException>(() => Invariants.AssertNothingInProgressExceptKnownLimits(row, log, [StoreCut(0, 5)], Store.EfCorePostgres));
        Assert.ThrowsAny<XunitException>(() => Invariants.AssertNothingInProgressExceptKnownLimits(
            row, log, [new FaultEvent("kill", "n2") { At = T0.AddSeconds(11) }], Store.EfCorePostgres));
    }

    [Fact]
    public void EveryDueJobEnded_Passes_AndFailsOnAnIdleOne()
    {
        var log = new RunLog();
        Line(log, "start", A, "n1", 0);
        Line(log, "end", A, "n1", 1, "Done");

        Invariants.AssertEveryDueJobEnded([View(A, TickerStatus.Done)], [A], log, [], Store.Redis);
        Assert.ThrowsAny<XunitException>(() =>
            Invariants.AssertEveryDueJobEnded([View(A, TickerStatus.Done), View(B, TickerStatus.Idle)], [A, B], log, [], Store.Redis));
    }

    [Fact]
    public void DelayedChild_AfterItsParentAndItsTime_Passes()
    {
        var log = new RunLog();
        Line(log, "start", A, "n1", 0);
        Line(log, "end", A, "n1", 5, "Done");
        Line(log, "start", B, "n1", 10);

        Invariants.AssertDelayedChildrenOrder(
            [View(A, TickerStatus.Done), View(B, TickerStatus.Done, parent: A, at: T0.AddSeconds(8).UtcDateTime)], log);
    }

    [Fact]
    public void DelayedChild_BeforeItsParentEnded_Fails()
    {
        var log = new RunLog();
        Line(log, "start", A, "n1", 0);
        Line(log, "start", B, "n1", 3);
        Line(log, "end", A, "n1", 5, "Done");

        Assert.ThrowsAny<XunitException>(() => Invariants.AssertDelayedChildrenOrder(
            [View(A, TickerStatus.Done), View(B, TickerStatus.Done, parent: A, at: T0.AddSeconds(2).UtcDateTime)], log));
    }

    [Fact]
    public void DelayedChild_BeforeItsOwnTime_Fails()
    {
        var log = new RunLog();
        Line(log, "start", A, "n1", 0);
        Line(log, "end", A, "n1", 1, "Done");
        Line(log, "start", B, "n1", 3);

        Assert.ThrowsAny<XunitException>(() => Invariants.AssertDelayedChildrenOrder(
            [View(A, TickerStatus.Done), View(B, TickerStatus.Done, parent: A, at: T0.AddSeconds(8).UtcDateTime)], log));
    }
}
