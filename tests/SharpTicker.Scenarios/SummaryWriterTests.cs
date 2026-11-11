using SharpTicker.Scenarios.Harness;

namespace SharpTicker.Scenarios;

public class SummaryWriterTests
{
    private const string Trx = """
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult testName="SharpTicker.Scenarios.ChainScenarios.AGrandchild_RunsAfterItsParent(store: Redis)" outcome="Passed" duration="00:00:11.4000000" />
            <UnitTestResult testName="SharpTicker.Scenarios.StopScenarios.AJobWaitingForItsSlot_IsPutBackAtStop_AndRunsOnceOnAnotherNode(store: EfCorePostgres)" outcome="Failed" duration="00:00:23.0000000">
              <Output>
                <ErrorInfo>
                  <Message>cycle 1: the waiting job started on the stopping node | node-1
        second line</Message>
                </ErrorInfo>
              </Output>
            </UnitTestResult>
          </Results>
        </TestRun>
        """;

    private static RunRecord Record(string test, string store, string folder, int starts, string? sourceRef = null) => new()
    {
        Test = test, Store = store, LogFolder = folder, Jobs = starts, Starts = starts, Ends = starts, SourceRef = sourceRef,
    };

    [Fact]
    public void ReadTrx_ReadsNameOutcomeDurationAndMessage()
    {
        var results = SummaryWriter.ReadTrx(Trx);

        Assert.Equal(2, results.Count);
        var failed = results.Single(r => r.Outcome == "Failed");
        Assert.Equal("StopScenarios.AJobWaitingForItsSlot_IsPutBackAtStop_AndRunsOnceOnAnotherNode", failed.Method);
        Assert.Equal("EfCorePostgres", failed.Store);
        Assert.Equal(TimeSpan.FromSeconds(23), failed.Duration);
        Assert.StartsWith("cycle 1:", failed.Message);
        Assert.Null(results.Single(r => r.Outcome == "Passed").Message);
    }

    [Fact]
    public void Build_ListsFailedFirst_JoinsRecordsByTestAndStore_AndKeepsOtherRefsOut()
    {
        var results = SummaryWriter.ReadTrx(Trx);
        var records = new[]
        {
            Record("ChainScenarios.AGrandchild_RunsAfterItsParent", "Redis", "chain-grandchild-redis", 3),
            // Same test, other store: not this row.
            Record("ChainScenarios.AGrandchild_RunsAfterItsParent", "MongoDb", "chain-grandchild-mongo", 99),
            // Same test and store, but a pre-fix run: not this report.
            Record("ChainScenarios.AGrandchild_RunsAfterItsParent", "Redis", "at-x/chain-grandchild-redis", 77, sourceRef: "abc^1"),
            Record("StopScenarios.AJobWaitingForItsSlot_IsPutBackAtStop_AndRunsOnceOnAnotherNode", "EfCorePostgres", "stop-slot-efcore", 16),
        };

        var lines = SummaryWriter.Build(results, records, sourceRef: null).Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        Assert.Contains("Code: working tree. Tests: 2, passed 1, failed 1, other 0.", lines);
        var rows = lines.Where(l => l.StartsWith("| Passed") || l.StartsWith("| Failed")).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(
            "| Failed | StopScenarios.AJobWaitingForItsSlot_IsPutBackAtStop_AndRunsOnceOnAnotherNode(store: EfCorePostgres) | EfCorePostgres | 23 s | 16 | 16 | 16 | stop-slot-efcore | cycle 1: the waiting job started on the stopping node \\| node-1 |",
            rows[0]);
        Assert.Equal(
            "| Passed | ChainScenarios.AGrandchild_RunsAfterItsParent(store: Redis) | Redis | 11 s | 3 | 3 | 3 | chain-grandchild-redis |  |",
            rows[1]);
    }

    [Fact]
    public void Build_TwoCasesOfOneTheoryOnOneStore_TakeTheEnvironmentThatRanDuringEach()
    {
        var t0 = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        const string name = "SharpTicker.Scenarios.StoreFaultScenarios.AStoreOutage(store: Redis, fault: \"{0}\")";
        var results = new[]
        {
            new TestResult(string.Format(name, "refused"), "Passed", TimeSpan.FromSeconds(30), null, t0, t0.AddSeconds(30)),
            new TestResult(string.Format(name, "hanging"), "Passed", TimeSpan.FromSeconds(30), null, t0.AddSeconds(31), t0.AddSeconds(61)),
        };
        var records = new[]
        {
            new RunRecord { Test = "StoreFaultScenarios.AStoreOutage", Store = "Redis", LogFolder = "outage-refused-redis", Jobs = 20, Starts = 20, Ends = 20, StartedAt = t0.AddSeconds(1), EndedAt = t0.AddSeconds(29) },
            new RunRecord { Test = "StoreFaultScenarios.AStoreOutage", Store = "Redis", LogFolder = "outage-hanging-redis", Jobs = 21, Starts = 21, Ends = 21, StartedAt = t0.AddSeconds(32), EndedAt = t0.AddSeconds(60) },
        };

        var rows = SummaryWriter.Build(results, records, sourceRef: null).Split('\n').Where(l => l.StartsWith("| Passed")).ToList();

        Assert.Contains(rows, r => r.Contains("fault: \"refused\")") && r.Contains("| 20 | 20 | 20 | outage-refused-redis |"));
        Assert.Contains(rows, r => r.Contains("fault: \"hanging\")") && r.Contains("| 21 | 21 | 21 | outage-hanging-redis |"));
    }

    [Fact]
    public async Task CallingTest_FindsTheAsyncTestMethodOnTheStack()
    {
        await Task.Yield();
        Assert.Equal($"{nameof(SummaryWriterTests)}.{nameof(CallingTest_FindsTheAsyncTestMethodOnTheStack)}", Helper());
    }

    // A helper between the test and the harness, as StopScenarios.StopWhileRunning is.
    private static string Helper() => ScenarioEnvironment.CallingTest();
}
