using SharpTicker.Tests.Shared;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>The dashboard status counts and jobs per machine on Redis are the same as when the rows are read and counted.</summary>
[Collection(RedisTestFixture.CollectionName)]
public class RedisDashboardStatusCountTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private static readonly DateTime Now = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public Task StatusCountsAndJobsPerMachine_AreTheSameAsCountingTheRows()
        => DashboardStatusCountCheck.SeedAndCheckAsync(fixture.CreateProvider("node-A", new FakeTickerClock(Now)), Now);
}
