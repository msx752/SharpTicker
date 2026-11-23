using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// The EF Core store loads a time ticker with its children and grandchildren. EF Core warns about that on every first query
/// unless the query says how it is loaded, and that warning would show up in the log of every application that uses the
/// store, although the application did nothing wrong. The queries say it, and what they do stays what it was: one query.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class EfCoreQueryWarningsTests : IAsyncLifetime
{
    private readonly EfCoreDashboardApiFixture _api = new();

    public Task InitializeAsync() => _api.InitializeAsync();
    public Task DisposeAsync() => _api.DisposeAsync();

    [Fact]
    public async Task ReadingAndRemovingTimeTickersWithTheirChildren_LogsNoEfCoreQueryWarning()
    {
        await _api.Store.GetTimeTickers(null, default);
        await _api.Store.GetTimeTickersPaginated(null, 1, 10, default);
        await _api.Store.TimeTickersQuery().WithRelated(TickerRelation.ChildrenDeep).ToArrayAsync();
        await _api.Store.RemoveTimeTickers(new[] { Guid.NewGuid() }, default);

        var warnings = _api.Logs.Entries
            .Where(e => e.Category.StartsWith("Microsoft.EntityFrameworkCore") && e.Level >= LogLevel.Warning)
            .Select(e => e.Message)
            .ToArray();
        Assert.True(warnings.Length == 0, "EF Core warned: " + string.Join(" | ", warnings.Select(m => m.Substring(0, Math.Min(110, m.Length)))));
    }

    // Candidate 96: the scheduler loop, the fallback and run-now read the jobs they claim with their children and
    // grandchildren too, and a running application runs them all. A child without a time that still waits after its
    // parent ended makes the fallback run its second read of that kind.
    [Fact]
    public async Task TheReadsOfTheLoopTheFallbackAndRunNow_LogNoEfCoreQueryWarning()
    {
        var ended = DateTime.UtcNow.AddHours(-1);
        var parent = new TimeTickerEntity
        {
            Id = Guid.NewGuid(), Function = "warnings-fn", ExecutionTime = ended, ExecutedAt = ended, Status = TickerStatus.Done,
            CreatedAt = ended, UpdatedAt = ended, Request = [],
            Children =
            [
                new TimeTickerEntity
                {
                    Id = Guid.NewGuid(), Function = "warnings-fn", ExecutionTime = null, Status = TickerStatus.Idle,
                    RunCondition = RunCondition.OnSuccess, CreatedAt = ended, UpdatedAt = ended, Request = []
                }
            ]
        };
        // Run-now reads a job back only when it won its claim.
        var later = new TimeTickerEntity
        {
            Id = Guid.NewGuid(), Function = "warnings-fn", ExecutionTime = DateTime.UtcNow.AddHours(1), Status = TickerStatus.Idle,
            CreatedAt = ended, UpdatedAt = ended, Request = []
        };
        await _api.Store.AddTimeTickers([parent, later], default);
        var before = _api.Logs.Entries.Length;

        await _api.Store.GetEarliestTimeTickers(default);
        await foreach (var _ in _api.Store.QueueTimedOutTimeTickers(default)) { }
        Assert.Single(await _api.Store.AcquireImmediateTimeTickersAsync([later.Id], default));

        var warnings = _api.Logs.Entries
            .Skip(before)
            .Where(e => e.Category.StartsWith("Microsoft.EntityFrameworkCore") && e.Level >= LogLevel.Warning)
            .Select(e => e.Message)
            .ToArray();
        Assert.True(warnings.Length == 0, "EF Core warned: " + string.Join(" | ", warnings.Select(m => m.Substring(0, Math.Min(110, m.Length)))));
    }
}
