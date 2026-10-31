using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
}
