using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharpTicker.DependencyInjection;
using SharpTicker.Provider;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Base;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// What SharpTicker does when the host starts: seed the cron tickers defined in code, run the application's seeders,
/// skip the occurrences that were pending when the application stopped, and warn when UseSharpTicker was forgotten.
/// Each test starts a real host on the in-memory store, which is shared by the process, so the store is emptied
/// before and after.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class StartupServicesTests : IAsyncLifetime
{
    private const string DefinedCron = "Startup.DefinedCron";

    private WebApplication? _app;

    public StartupServicesTests()
    {
        new ServiceCollection().MapTicker(DefinedCron, (_, _) => Task.CompletedTask).WithCron("0 0 * * *");
        TickerFunctionProvider.Build();
    }

    public async Task InitializeAsync() => await ClearStoreAsync();

    public async Task DisposeAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        await ClearStoreAsync();
    }

    private static async Task ClearStoreAsync()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var store = new TickerInMemoryPersistenceProvider<TimeTickerEntity, CronTickerEntity>(services);
        var occurrences = await store.GetAllCronTickerOccurrences(null, default);
        await store.RemoveCronTickerOccurrences(occurrences.Select(o => o.Id).ToArray(), default);
        var crons = await store.GetCronTickers(null, default);
        await store.RemoveCronTickers(crons.Select(c => c.Id).ToArray(), default);
        var times = await store.GetTimeTickers(null, default);
        await store.RemoveTimeTickers(times.Select(t => t.Id).ToArray());
    }

    private async Task<WebApplication> StartAsync(
        Action<TickerOptionsBuilder<TimeTickerEntity, CronTickerEntity>>? configure = null,
        bool useSharpTicker = true,
        Func<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>, Task>? beforeStart = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSharpTicker(options =>
        {
            options.DisableBackgroundServices();
            configure?.Invoke(options);
        });

        _app = builder.Build();
        if (useSharpTicker)
            _app.UseSharpTicker();

        if (beforeStart != null)
            await beforeStart(_app.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>());

        await _app.StartAsync();
        return _app;
    }

    private ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> Store
        => _app!.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();

    [Fact]
    public async Task ACronTickerDefinedInCode_IsSeededOnStartup()
    {
        await StartAsync();

        var cron = Assert.Single(await Store.GetCronTickers(null, default), c => c.Function == DefinedCron);
        Assert.Equal("0 0 0 * * *", cron.Expression);
    }

    [Fact]
    public async Task ACronWithDayNames_IsSeededWithTheExpressionAsWritten()
    {
        new ServiceCollection().MapTicker("Startup.WeekdayCron", (_, _) => Task.CompletedTask).WithCron("0 0 9 * * MON-FRI");
        TickerFunctionProvider.Build();

        await StartAsync();

        var cron = Assert.Single(await Store.GetCronTickers(null, default), c => c.Function == "Startup.WeekdayCron");
        Assert.Equal("0 0 9 * * MON-FRI", cron.Expression);
    }

    [Fact]
    public async Task AFiveFieldCronWithNames_IsSeededWithTheSecondsFieldAdded()
    {
        new ServiceCollection().MapTicker("Startup.WeekdayCron5", (_, _) => Task.CompletedTask).WithCron("30 8 * * Mon-Fri");
        TickerFunctionProvider.Build();

        await StartAsync();

        var cron = Assert.Single(await Store.GetCronTickers(null, default), c => c.Function == "Startup.WeekdayCron5");
        Assert.Equal("0 30 8 * * Mon-Fri", cron.Expression);
    }

    [Fact]
    public async Task StartingTwice_DoesNotSeedTheSameCronTickerAgain()
    {
        await StartAsync();
        var first = (await Store.GetCronTickers(null, default)).Count(c => c.Function == DefinedCron);
        await _app!.StopAsync();
        await _app.DisposeAsync();

        await StartAsync();

        Assert.Equal(1, first);
        Assert.Equal(1, (await Store.GetCronTickers(null, default)).Count(c => c.Function == DefinedCron));
    }

    [Fact]
    public async Task IgnoringSeeding_LeavesTheCronTickerDefinedInCodeOut()
    {
        await StartAsync(o => o.IgnoreSeedDefinedCronTickers());

        Assert.DoesNotContain(await Store.GetCronTickers(null, default), c => c.Function == DefinedCron);
    }

    [Fact]
    public async Task WithoutUseSharpTicker_NothingIsSeeded()
    {
        await StartAsync(useSharpTicker: false);

        Assert.Empty(await Store.GetCronTickers(null, default));
    }

    [Fact]
    public async Task TheApplicationSeeders_RunOnStartup()
    {
        var timeSeeded = false;
        var cronSeeded = false;

        await StartAsync(o => o.UseTickerSeeder(
            _ => { timeSeeded = true; return Task.CompletedTask; },
            _ => { cronSeeded = true; return Task.CompletedTask; }));

        Assert.True(timeSeeded);
        Assert.True(cronSeeded);
    }

    [Fact]
    public async Task ASeeder_CanAddATimeTicker()
    {
        await StartAsync(o => o.UseTickerSeeder(
            (ITimeTickerManager<TimeTickerEntity> manager) => manager.AddAsync(new TimeTickerEntity
            {
                Function = DefinedCron,
                ExecutionTime = DateTime.UtcNow.AddDays(1),
            }),
            (ICronTickerManager<CronTickerEntity> _) => Task.CompletedTask));

        Assert.Single(await Store.GetTimeTickers(null, default));
    }

    [Fact]
    public async Task TheSeeders_DoNotRunWithoutUseSharpTicker()
    {
        var ran = false;

        await StartAsync(o => o.UseTickerSeeder(
            _ => { ran = true; return Task.CompletedTask; },
            _ => Task.CompletedTask), useSharpTicker: false);

        Assert.False(ran);
    }

    [Fact]
    public async Task AnOccurrenceThatWasPendingBeforeTheRestart_IsSkipped_WhenSkippingIsEnabled()
    {
        var stale = Guid.NewGuid();
        var recent = Guid.NewGuid();
        var done = Guid.NewGuid();
        var cronId = Guid.NewGuid();

        await StartAsync(o => o.SkipStaleCronOccurrencesOnStartup(TimeSpan.FromMinutes(1)), beforeStart: async store =>
        {
            await store.InsertCronTickers(new[] { new CronTickerEntity { Id = cronId, Function = "Startup.Other", Expression = "0 0 * * * *", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow } }, default);
            await store.InsertCronTickerOccurrences(new[]
            {
                Occurrence(stale, cronId, TickerStatus.Queued, DateTime.UtcNow.AddHours(-2)),
                Occurrence(recent, cronId, TickerStatus.Queued, DateTime.UtcNow.AddSeconds(-5)),
                Occurrence(done, cronId, TickerStatus.Done, DateTime.UtcNow.AddHours(-2)),
            }, default);
        });

        var occurrences = (await Store.GetAllCronTickerOccurrences(null, default)).ToDictionary(o => o.Id);
        Assert.Equal(TickerStatus.Skipped, occurrences[stale].Status);
        Assert.Contains("restarted", occurrences[stale].SkippedReason);
        Assert.Equal(TickerStatus.Queued, occurrences[recent].Status);
        Assert.Equal(TickerStatus.Done, occurrences[done].Status);
    }

    [Fact]
    public async Task APendingOccurrence_IsLeftAlone_WhenSkippingIsNotEnabled()
    {
        var stale = Guid.NewGuid();
        var cronId = Guid.NewGuid();

        await StartAsync(beforeStart: async store =>
        {
            await store.InsertCronTickers(new[] { new CronTickerEntity { Id = cronId, Function = "Startup.Other", Expression = "0 0 * * * *", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow } }, default);
            await store.InsertCronTickerOccurrences(new[] { Occurrence(stale, cronId, TickerStatus.Queued, DateTime.UtcNow.AddHours(-2)) }, default);
        });

        var occurrence = Assert.Single(await Store.GetAllCronTickerOccurrences(null, default));
        Assert.Equal(TickerStatus.Queued, occurrence.Status);
    }

    [Fact]
    public async Task ForgettingUseSharpTicker_IsReportedToTheHostAndTheLog()
    {
        var messages = new List<(object Value, CoreNotifyActionType Type)>();

        await StartAsync(useSharpTicker: false, beforeStart: store =>
        {
            _app!.Services.GetRequiredService<TickerExecutionContext>().NotifyCoreAction += (value, type) => messages.Add((value, type));
            return Task.CompletedTask;
        });

        var message = Assert.Single(messages);
        Assert.Equal(CoreNotifyActionType.NotifyHostExceptionMessage, message.Type);
        Assert.Contains("UseSharpTicker() was not called", (string)message.Value);
    }

    [Fact]
    public async Task CallingUseSharpTicker_ProducesNoWarning()
    {
        var messages = new List<object>();

        await StartAsync(beforeStart: store =>
        {
            _app!.Services.GetRequiredService<TickerExecutionContext>().NotifyCoreAction += (value, _) => messages.Add(value);
            return Task.CompletedTask;
        });

        Assert.Empty(messages);
    }

    private static CronTickerOccurrenceEntity<CronTickerEntity> Occurrence(Guid id, Guid cronId, TickerStatus status, DateTime executionTime)
        => new()
        {
            Id = id,
            CronTickerId = cronId,
            Status = status,
            ExecutionTime = executionTime,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
}
