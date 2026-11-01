using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SharpTicker.Utilities.DashboardDtos;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// The public managers on EF Core, in the cases that look tickers up by a list of ids. A lookup written as
/// <c>ids.Contains(x.Id)</c> on an array compiles, with C# 14, to the span overload of Contains, which EF Core on net8
/// cannot translate; these tests run on every target framework to catch that.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class EfCoreTickerManagerTests : IClassFixture<EfCoreDashboardApiFixture>
{
    private readonly EfCoreDashboardApiFixture _api;

    public EfCoreTickerManagerTests(EfCoreDashboardApiFixture api) => _api = api;

    private ITimeTickerManager<TimeTickerEntity> Times => _api.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
    private ICronTickerManager<CronTickerEntity> Crons => _api.Services.GetRequiredService<ICronTickerManager<CronTickerEntity>>();

    [Fact]
    public async Task AddingAndUpdatingATimeTickerBatch_Works_OnEfCore()
    {
        var added = await Times.AddBatchAsync(new List<TimeTickerEntity>
        {
            new() { Function = "SeedJob", ExecutionTime = DateTime.UtcNow.AddDays(5), Description = "one" },
            new() { Function = "SeedJob", ExecutionTime = DateTime.UtcNow.AddDays(6), Description = "two" },
        });
        Assert.True(added.IsSucceeded, added.Exception?.ToString());

        var update = added.Result.Select(t => new TimeTickerEntity
        {
            Id = t.Id, Function = "SeedJob", ExecutionTime = DateTime.UtcNow.AddDays(7), Description = "moved",
        }).ToList();
        var updated = await Times.UpdateBatchAsync(update);

        Assert.True(updated.IsSucceeded, updated.Exception?.ToString());
        Assert.Equal(2, updated.AffectedRows);

        var deleted = await Times.DeleteBatchAsync(update.Select(t => t.Id).ToList());
        Assert.True(deleted.IsSucceeded, deleted.Exception?.ToString());
    }

    [Fact]
    public async Task AddingAndUpdatingACronTickerBatch_Works_OnEfCore()
    {
        var added = await Crons.AddBatchAsync(new List<CronTickerEntity>
        {
            new() { Function = "SeedCron", Expression = "0 0 3 * * *", Description = "one" },
            new() { Function = "SeedCron", Expression = "0 0 4 * * *", Description = "two" },
        });
        Assert.True(added.IsSucceeded, added.Exception?.ToString());

        var update = added.Result.Select(c => new CronTickerEntity
        {
            Id = c.Id, Function = "SeedCron", Expression = "0 0 5 * * *", Description = "moved", IsEnabled = true,
        }).ToList();
        var updated = await Crons.UpdateBatchAsync(update);

        Assert.True(updated.IsSucceeded, updated.Exception?.ToString());
        Assert.Equal(2, updated.AffectedRows);
    }

    [Fact]
    public async Task TheDashboardDataService_FiltersByStatus_OnEfCore()
    {
        var data = _api.Services.GetRequiredService<ITickerDashboardDataService<TimeTickerEntity, CronTickerEntity>>();

        var times = await data.GetTimeTickersFlatAsync(new TimeTickerQueryFilter { Statuses = new[] { TickerStatus.Done, TickerStatus.Failed } });
        var occurrences = await data.GetCronOccurrencesFlatAsync(
            DashboardApiFixture.CronId, new CronOccurrenceQueryFilter { Statuses = new[] { TickerStatus.Failed } });

        Assert.All(times.Items, t => Assert.Contains(t.Status, new[] { TickerStatus.Done, TickerStatus.Failed }));
        Assert.All(occurrences.Items, o => Assert.Equal(TickerStatus.Failed, o.Status));
    }
}
