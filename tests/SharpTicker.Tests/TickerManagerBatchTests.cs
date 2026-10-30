using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SharpTicker.DependencyInjection;
using SharpTicker.Provider;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Base;
using SharpTicker.Utilities.Exceptions;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// The public managers, used the way an application uses them: resolved from the container of a running host, adding,
/// updating and deleting tickers one by one, in batches and by function type. The store is the in-memory one, which the
/// whole process shares, so it is emptied before and after every test.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class TickerManagerBatchTests : IAsyncLifetime
{
    private const string TimeFunction = "ManagerTests.Time";
    private const string CronFunction = "ManagerTests.Cron";

    public sealed record ManagerOrder(string OrderId, int Quantity);

    public sealed class OrderJob : ITickerFunction<ManagerOrder>
    {
        public Task ExecuteAsync(TickerFunctionContext<ManagerOrder> context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    public sealed class CleanupJob : ITickerFunction
    {
        public Task ExecuteAsync(TickerFunctionContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private WebApplication _app = null!;
    private ITimeTickerManager<TimeTickerEntity> Times => _app.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
    private ICronTickerManager<CronTickerEntity> Crons => _app.Services.GetRequiredService<ICronTickerManager<CronTickerEntity>>();
    private ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> Store
        => _app.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.MapTicker(TimeFunction, (_, _) => Task.CompletedTask);
        services.MapTicker(CronFunction, (_, _) => Task.CompletedTask);
        services.MapTicker<CleanupJob>();
        services.MapTicker<OrderJob, ManagerOrder>();
        TickerFunctionProvider.Build();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSharpTicker(options => options.DisableBackgroundServices());
        _app = builder.Build();
        _app.UseSharpTicker();
        await _app.StartAsync();

        await ClearStoreAsync();
    }

    public async Task DisposeAsync()
    {
        await ClearStoreAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task ClearStoreAsync()
    {
        var occurrences = await Store.GetAllCronTickerOccurrences(null, default);
        await Store.RemoveCronTickerOccurrences(occurrences.Select(o => o.Id).ToArray(), default);
        var crons = await Store.GetCronTickers(null, default);
        await Store.RemoveCronTickers(crons.Select(c => c.Id).ToArray(), default);
        var times = await Store.GetTimeTickers(null, default);
        await Store.RemoveTimeTickers(times.Select(t => t.Id).ToArray());
    }

    private static TimeTickerEntity Time(string function = TimeFunction, DateTime? at = null)
        => new() { Function = function, ExecutionTime = at ?? DateTime.UtcNow.AddHours(1) };

    private static CronTickerEntity Cron(string function = CronFunction, string expression = "0 0 * * * *")
        => new() { Function = function, Expression = expression };

    #region Time tickers in a batch

    [Fact]
    public async Task AddingATimeTickerBatch_StoresEveryTicker_WithIdsAndTimestamps()
    {
        var result = await Times.AddBatchAsync(new List<TimeTickerEntity> { Time(), Time(), Time() });

        Assert.True(result.IsSucceeded);
        Assert.Equal(3, result.Result.Count);
        Assert.Equal(3, result.Result.Select(t => t.Id).Distinct().Count());
        Assert.DoesNotContain(result.Result, t => t.Id == Guid.Empty);
        Assert.All(result.Result, t => Assert.NotEqual(default, t.CreatedAt));
        Assert.Equal(3, (await Store.GetTimeTickers(null, default)).Length);
    }

    [Fact]
    public async Task AddingATimeTickerBatch_WithoutAnExecutionTime_RunsItNow()
    {
        var before = DateTime.UtcNow;

        var result = await Times.AddBatchAsync(new List<TimeTickerEntity> { new() { Function = TimeFunction } });

        Assert.True(result.IsSucceeded);
        var stored = Assert.Single(await Store.GetTimeTickers(null, default));
        Assert.InRange(stored.ExecutionTime!.Value, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task AddingATimeTickerBatch_StoresLocalTimesAsUtc()
    {
        var local = DateTime.SpecifyKind(DateTime.Now.AddHours(3), DateTimeKind.Local);

        await Times.AddBatchAsync(new List<TimeTickerEntity> { Time(at: local) });

        var stored = Assert.Single(await Store.GetTimeTickers(null, default));
        Assert.Equal(local.ToUniversalTime(), stored.ExecutionTime);
    }

    [Fact]
    public async Task AddingATimeTickerBatch_WithAnUnknownFunction_FailsAndStoresNothing()
    {
        var result = await Times.AddBatchAsync(new List<TimeTickerEntity> { Time(), Time("No.Such.Function") });

        Assert.False(result.IsSucceeded);
        Assert.IsType<TickerValidatorException>(result.Exception);
        Assert.Contains("No.Such.Function", result.Exception.Message);
        Assert.Empty(await Store.GetTimeTickers(null, default));
    }

    [Fact]
    public async Task AddingAnEmptyOrMissingTimeTickerBatch_SucceedsWithNothingToDo()
    {
        var empty = await Times.AddBatchAsync(new List<TimeTickerEntity>());
        var missing = await Times.AddBatchAsync(null!);

        Assert.True(empty.IsSucceeded);
        Assert.True(missing.IsSucceeded);
        Assert.Empty(await Store.GetTimeTickers(null, default));
    }

    [Fact]
    public async Task UpdatingATimeTickerBatch_ChangesTheStoredTickers_AndCountsTheRows()
    {
        var added = (await Times.AddBatchAsync(new List<TimeTickerEntity> { Time(), Time() })).Result;
        var newTime = DateTime.UtcNow.AddDays(2);
        var update = added.Select(t => new TimeTickerEntity { Id = t.Id, Function = TimeFunction, ExecutionTime = newTime, Description = "rescheduled" }).ToList();

        var result = await Times.UpdateBatchAsync(update);

        Assert.True(result.IsSucceeded);
        Assert.Equal(2, result.AffectedRows);
        var stored = await Store.GetTimeTickers(null, default);
        Assert.All(stored, t => Assert.Equal("rescheduled", t.Description));
        Assert.All(stored, t => Assert.Equal(newTime, t.ExecutionTime));
    }

    [Fact]
    public async Task UpdatingATimeTickerBatch_WithoutAnExecutionTime_FailsAndChangesNothing()
    {
        var added = (await Times.AddBatchAsync(new List<TimeTickerEntity> { Time(), Time() })).Result;
        var update = new List<TimeTickerEntity>
        {
            new() { Id = added[0].Id, Function = TimeFunction, ExecutionTime = DateTime.UtcNow.AddHours(2), Description = "changed" },
            new() { Id = added[1].Id, Function = TimeFunction, ExecutionTime = null },
        };

        var result = await Times.UpdateBatchAsync(update);

        Assert.False(result.IsSucceeded);
        Assert.IsType<TickerValidatorException>(result.Exception);
        Assert.All(await Store.GetTimeTickers(null, default), t => Assert.NotEqual("changed", t.Description));
    }

    [Fact]
    public async Task UpdatingATimeTickerBatchWithANullTicker_Fails()
    {
        var result = await Times.UpdateBatchAsync(new List<TimeTickerEntity> { null! });

        Assert.False(result.IsSucceeded);
        Assert.IsType<TickerValidatorException>(result.Exception);
    }

    [Fact]
    public async Task DeletingATimeTickerBatch_RemovesThemAndReportsTheCount()
    {
        var added = (await Times.AddBatchAsync(new List<TimeTickerEntity> { Time(), Time(), Time() })).Result;

        var result = await Times.DeleteBatchAsync(added.Take(2).Select(t => t.Id).ToList());

        Assert.True(result.IsSucceeded);
        Assert.Equal(2, result.AffectedRows);
        Assert.Equal(added[2].Id, Assert.Single(await Store.GetTimeTickers(null, default)).Id);
    }

    [Fact]
    public async Task DeletingTimeTickersThatDoNotExist_ReportsZeroRows()
    {
        var result = await Times.DeleteBatchAsync(new List<Guid> { Guid.NewGuid() });

        Assert.True(result.IsSucceeded);
        Assert.Equal(0, result.AffectedRows);
    }

    #endregion

    #region Cron tickers in a batch

    [Fact]
    public async Task AddingACronTickerBatch_StoresEveryTicker()
    {
        var result = await Crons.AddBatchAsync(new List<CronTickerEntity> { Cron(expression: "0 0 * * * *"), Cron(expression: "0 30 * * * *") });

        Assert.True(result.IsSucceeded);
        Assert.Equal(2, result.Result.Count);
        Assert.DoesNotContain(result.Result, c => c.Id == Guid.Empty);
        Assert.All(result.Result, c => Assert.NotEqual(default, c.CreatedAt));
        Assert.Equal(2, (await Store.GetCronTickers(null, default)).Length);
    }

    [Fact]
    public async Task AddingACronTickerBatch_WithAnInvalidExpression_FailsAndStoresNothing()
    {
        var result = await Crons.AddBatchAsync(new List<CronTickerEntity> { Cron(), Cron(expression: "not a cron") });

        Assert.False(result.IsSucceeded);
        Assert.IsType<TickerValidatorException>(result.Exception);
        Assert.Contains("not a cron", result.Exception.Message);
        Assert.Empty(await Store.GetCronTickers(null, default));
    }

    [Fact]
    public async Task AddingACronTickerBatch_WithAnUnknownFunction_FailsAndStoresNothing()
    {
        var result = await Crons.AddBatchAsync(new List<CronTickerEntity> { Cron("No.Such.Function") });

        Assert.False(result.IsSucceeded);
        Assert.Contains("No.Such.Function", result.Exception.Message);
        Assert.Empty(await Store.GetCronTickers(null, default));
    }

    [Fact]
    public async Task AddingAMissingCronTickerBatch_SucceedsWithNothingToDo_LikeTimeTickers()
    {
        var result = await Crons.AddBatchAsync(null!);

        Assert.True(result.IsSucceeded);
        Assert.Empty(await Store.GetCronTickers(null, default));
    }

    [Fact]
    public async Task AddingAnEmptyCronTickerBatch_Succeeds()
    {
        var result = await Crons.AddBatchAsync(new List<CronTickerEntity>());

        Assert.True(result.IsSucceeded);
        Assert.Empty(result.Result);
    }

    [Fact]
    public async Task UpdatingACronTickerBatch_ChangesTheStoredTickers()
    {
        var added = (await Crons.AddBatchAsync(new List<CronTickerEntity> { Cron(), Cron() })).Result;
        var update = added.Select(c => new CronTickerEntity { Id = c.Id, Function = CronFunction, Expression = "0 15 * * * *" }).ToList();

        var result = await Crons.UpdateBatchAsync(update);

        Assert.True(result.IsSucceeded);
        Assert.Equal(2, result.AffectedRows);
        Assert.All(await Store.GetCronTickers(null, default), c => Assert.Equal("0 15 * * * *", c.Expression));
    }

    [Fact]
    public async Task UpdatingACronTickerBatch_WithAnInvalidExpression_FailsAndChangesNothing()
    {
        var added = (await Crons.AddBatchAsync(new List<CronTickerEntity> { Cron(), Cron() })).Result;
        var update = new List<CronTickerEntity>
        {
            new() { Id = added[0].Id, Function = CronFunction, Expression = "0 5 * * * *", Description = "changed" },
            new() { Id = added[1].Id, Function = CronFunction, Expression = "nope" },
        };

        var result = await Crons.UpdateBatchAsync(update);

        Assert.False(result.IsSucceeded);
        Assert.IsType<TickerValidatorException>(result.Exception);
        Assert.All(await Store.GetCronTickers(null, default), c => Assert.NotEqual("changed", c.Description));
    }

    [Fact]
    public async Task UpdatingACronTickerBatch_WithAnUnknownFunction_Fails()
    {
        var added = (await Crons.AddBatchAsync(new List<CronTickerEntity> { Cron() })).Result;
        added[0].Function = "No.Such.Function";

        var result = await Crons.UpdateBatchAsync(added);

        Assert.False(result.IsSucceeded);
        Assert.Contains("No.Such.Function", result.Exception.Message);
    }

    [Fact]
    public async Task UpdatingACronTickerBatchWithANullTicker_Fails()
    {
        var result = await Crons.UpdateBatchAsync(new List<CronTickerEntity> { null! });

        Assert.False(result.IsSucceeded);
    }

    [Fact]
    public async Task DeletingACronTickerBatch_RemovesThemAndReportsTheCount()
    {
        var added = (await Crons.AddBatchAsync(new List<CronTickerEntity> { Cron(), Cron(), Cron() })).Result;

        var result = await Crons.DeleteBatchAsync(added.Take(2).Select(c => c.Id).ToList());

        Assert.True(result.IsSucceeded);
        Assert.Equal(2, result.AffectedRows);
        Assert.Equal(added[2].Id, Assert.Single(await Store.GetCronTickers(null, default)).Id);
    }

    #endregion

    #region Backwards cron ranges

    [Fact]
    public async Task AddingACronTickerWithABackwardsRange_IsRefusedAndStoresNothing()
    {
        var result = await Crons.AddAsync(Cron(expression: "0 0 22-2 * * *"));

        Assert.False(result.IsSucceeded);
        Assert.IsType<TickerValidatorException>(result.Exception);
        Assert.Contains("backwards", result.Exception.Message);
        Assert.Empty(await Store.GetCronTickers(null, default));
    }

    [Fact]
    public async Task UpdatingACronTickerToABackwardsRange_IsRefusedAndKeepsTheStoredExpression()
    {
        var added = (await Crons.AddAsync(Cron(expression: "0 0 9 * * MON-FRI"))).Result;

        var result = await Crons.UpdateAsync(new CronTickerEntity { Id = added.Id, Function = CronFunction, Expression = "0 0 0 * * FRI-MON" });

        Assert.False(result.IsSucceeded);
        Assert.IsType<TickerValidatorException>(result.Exception);
        Assert.Equal("0 0 9 * * MON-FRI", Assert.Single(await Store.GetCronTickers(null, default)).Expression);
    }

    [Fact]
    public async Task AddingACronTickerBatchWithABackwardsRange_IsRefusedAndStoresNothing()
    {
        var result = await Crons.AddBatchAsync(new List<CronTickerEntity> { Cron(), Cron(expression: "0 0 0 * 11-2 *") });

        Assert.False(result.IsSucceeded);
        Assert.Contains("backwards", result.Exception.Message);
        Assert.Empty(await Store.GetCronTickers(null, default));
    }

    [Fact]
    public async Task UpdatingACronTickerBatchToABackwardsRange_IsRefusedAndChangesNothing()
    {
        var added = (await Crons.AddBatchAsync(new List<CronTickerEntity> { Cron(), Cron() })).Result;
        var update = new List<CronTickerEntity>
        {
            new() { Id = added[0].Id, Function = CronFunction, Expression = "0 5 * * * *" },
            new() { Id = added[1].Id, Function = CronFunction, Expression = "0 0 22-2 * * *" },
        };

        var result = await Crons.UpdateBatchAsync(update);

        Assert.False(result.IsSucceeded);
        Assert.All(await Store.GetCronTickers(null, default), c => Assert.Equal("0 0 * * * *", c.Expression));
    }

    [Fact]
    public async Task TwoForwardRanges_AreAcceptedWhereABackwardsOneIsNot()
    {
        var result = await Crons.AddAsync(Cron(expression: "0 0 22-23,0-2 * * *"));

        Assert.True(result.IsSucceeded);
    }

    #endregion

    #region Missing input

    [Fact]
    public async Task EveryBatchMethod_TreatsAMissingOrEmptyListAsNothingToDo()
    {
        var results = new object[]
        {
            await Times.AddBatchAsync(null!), await Times.AddBatchAsync(new List<TimeTickerEntity>()),
            await Times.UpdateBatchAsync(null!), await Times.UpdateBatchAsync(new List<TimeTickerEntity>()),
            await Times.DeleteBatchAsync(null!), await Times.DeleteBatchAsync(new List<Guid>()),
            await Crons.AddBatchAsync(null!), await Crons.AddBatchAsync(new List<CronTickerEntity>()),
            await Crons.UpdateBatchAsync(null!), await Crons.UpdateBatchAsync(new List<CronTickerEntity>()),
            await Crons.DeleteBatchAsync(null!), await Crons.DeleteBatchAsync(new List<Guid>()),
        };

        Assert.All(results, r => Assert.True((bool)r.GetType().GetField("IsSucceeded")!.GetValue(r)!));
        Assert.All(results, r => Assert.Equal(0, (int)r.GetType().GetField("AffectedRows")!.GetValue(r)!));
    }

    [Fact]
    public async Task AddingATimeTickerBatchWithANullTicker_FailsInsteadOfThrowing_AndStoresNothing()
    {
        var result = await Times.AddBatchAsync(new List<TimeTickerEntity> { Time(), null! });

        Assert.False(result.IsSucceeded);
        Assert.IsType<TickerValidatorException>(result.Exception);
        Assert.Empty(await Store.GetTimeTickers(null, default));
    }

    [Fact]
    public async Task AddingACronTickerBatchWithANullTicker_FailsInsteadOfThrowing_AndStoresNothing()
    {
        var result = await Crons.AddBatchAsync(new List<CronTickerEntity> { Cron(), null! });

        Assert.False(result.IsSucceeded);
        Assert.IsType<TickerValidatorException>(result.Exception);
        Assert.Empty(await Store.GetCronTickers(null, default));
    }

    [Fact]
    public async Task UpdatingACronTickerBatchWithANullTicker_FailsWithAValidatorException()
    {
        var result = await Crons.UpdateBatchAsync(new List<CronTickerEntity> { null! });

        Assert.False(result.IsSucceeded);
        Assert.IsType<TickerValidatorException>(result.Exception);
    }

    #endregion

    #region Adding by function type

    [Fact]
    public async Task AddingByFunctionType_UsesTheNameTheFunctionWasRegisteredUnder()
    {
        var result = await Times.AddAsync<CleanupJob>(DateTime.UtcNow.AddHours(1));

        Assert.True(result.IsSucceeded);
        Assert.Equal("CleanupJob", Assert.Single(await Store.GetTimeTickers(null, default)).Function);
    }

    [Fact]
    public async Task AddingByFunctionTypeWithoutATime_RunsItNow()
    {
        var before = DateTime.UtcNow;

        var result = await Times.AddAsync<CleanupJob>();

        Assert.True(result.IsSucceeded);
        Assert.InRange(result.Result.ExecutionTime!.Value, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task AddingByFunctionTypeWithARequest_StoresTheRequestAsJson()
    {
        var result = await Times.AddAsync<OrderJob, ManagerOrder>(DateTime.UtcNow.AddHours(1), new ManagerOrder("A-7", 3));

        Assert.True(result.IsSucceeded);
        var stored = Assert.Single(await Store.GetTimeTickers(null, default));
        Assert.Equal("OrderJob", stored.Function);
        var json = JsonDocument.Parse(Encoding.UTF8.GetString(stored.Request!)).RootElement;
        Assert.Equal("A-7", json.GetProperty(json.EnumerateObject().First().Name).GetString());
        Assert.Contains("3", json.GetRawText());
    }

    #endregion
}
