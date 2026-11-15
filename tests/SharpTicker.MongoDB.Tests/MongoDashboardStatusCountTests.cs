using SharpTicker.Tests.Shared;

namespace SharpTicker.MongoDB.Tests;

/// <summary>The dashboard status counts and jobs per machine on MongoDB are the same as when the rows are read and counted.</summary>
[Collection("Mongo")]
public class MongoDashboardStatusCountTests : IClassFixture<MongoTestFixture>, IAsyncLifetime
{
    private readonly MongoTestFixture _f;

    public MongoDashboardStatusCountTests(MongoTestFixture fixture) => _f = fixture;

    public Task InitializeAsync() => _f.DropAllAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public Task StatusCountsAndJobsPerMachine_AreTheSameAsCountingTheRows()
        => DashboardStatusCountCheck.SeedAndCheckAsync(_f.Provider, _f.FixedNow);
}
