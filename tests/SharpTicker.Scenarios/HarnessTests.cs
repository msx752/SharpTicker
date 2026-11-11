using SharpTicker.Scenarios.Harness;

namespace SharpTicker.Scenarios;

public class HarnessTests
{
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void RunLog_ParsesStartAndEndLines_AndCountsPerAttempt()
    {
        var log = new RunLog();
        log.Add("node-1", $"SCENARIO_RUN {{\"seq\":1,\"event\":\"start\",\"tickerId\":\"{Id}\",\"function\":\"Scenario.Fail\",\"node\":\"n1\",\"attempt\":0,\"at\":\"2026-11-09T04:30:29.4462024Z\"}}");
        log.Add("node-1", $"SCENARIO_RUN {{\"seq\":2,\"event\":\"end\",\"tickerId\":\"{Id}\",\"function\":\"Scenario.Fail\",\"node\":\"n1\",\"attempt\":0,\"at\":\"2026-11-09T04:30:29.5Z\",\"status\":\"Failed\"}}");
        // A line with something in front of the marker, as a log stream may carry.
        log.Add("node-1", $"2026-11-09 stdout SCENARIO_RUN {{\"seq\":3,\"event\":\"start\",\"tickerId\":\"{Id}\",\"function\":\"Scenario.Fail\",\"node\":\"n1\",\"attempt\":1,\"at\":\"2026-11-09T04:30:31Z\"}}");
        log.Add("node-1", "info: some other log line");
        log.Add("node-1", $"SCENARIO_RUN {{\"seq\":4,\"event\":\"start\",\"tickerId\":\"{Other}\",\"function\":\"Scenario.Record\",\"node\":\"n1\",\"attempt\":0,\"at\":\"2026-11-09T04:30:32Z\"}}");

        Assert.Equal(1, log.Starts(Id, 0));
        Assert.Equal(1, log.Starts(Id, 1));
        Assert.Equal(2, log.Starts(Id));
        Assert.Equal(1, log.Ends(Id));
        Assert.Equal(1, log.Starts(Other));
        Assert.Equal(4, log.Events.Count);

        var end = log.Events.Single(e => e.Event == "end");
        Assert.Equal("Failed", end.Status);
        Assert.Equal("n1", end.Node);
        Assert.Equal("node-1", end.Container);
        // The node's own time is kept as written, not converted to the test clock.
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 4, 30, 29, 500, TimeSpan.Zero), end.At);
    }

    [Fact]
    public async Task Eventually_OnTimeout_NamesTheStateAndTheLogFolder()
    {
        var state = new FakeState("C:\\logs\\scenario-redis", "node-0: running; node-1: exited (137)");

        var ex = await Assert.ThrowsAsync<EventuallyTimeoutException>(() =>
            Eventually.UntilAsync(() => Task.FromResult(false), TimeSpan.FromMilliseconds(300), () => "status was InProgress", state,
                pollInterval: TimeSpan.FromMilliseconds(50)));

        Assert.Contains("status was InProgress", ex.Message);
        Assert.Contains("C:\\logs\\scenario-redis", ex.Message);
        Assert.Contains("node-1: exited (137)", ex.Message);
    }

    [Fact]
    public async Task Eventually_ReturnsWhenTheConditionHolds()
    {
        var calls = 0;
        await Eventually.UntilAsync(() => Task.FromResult(++calls >= 3), TimeSpan.FromSeconds(5), () => "never", null,
            pollInterval: TimeSpan.FromMilliseconds(10));
        Assert.Equal(3, calls);
    }

    private sealed class FakeState(string folder, string containers) : IScenarioState
    {
        public string LogFolder => folder;
        public Task<string> DescribeContainersAsync() => Task.FromResult(containers);
    }
}
