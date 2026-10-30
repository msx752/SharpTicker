using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SharpTicker.DependencyInjection;
using SharpTicker.Provider;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// A cron written with month and day names, run by a real host with the scheduler switched on: the cron is seeded
/// from the function definition, the scheduler picks its next occurrence, and the function runs. The expression
/// lists every month and every day by name, so it matches every second and the test does not depend on the date.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class CronNamesEndToEndTests : IAsyncLifetime
{
    private const string Function = "EndToEnd.NamedCron";
    private const string EveryDayByName = "* * * * JAN,FEB,MAR,APR,MAY,JUN,JUL,AUG,SEP,OCT,NOV,DEC SUN,MON,TUE,WED,THU,FRI,SAT";

    private WebApplication? _app;

    public Task InitializeAsync() => ClearStoreAsync();

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
    }

    private async Task<TaskCompletionSource<DateTime>> StartHostWithCronAsync(string function, string expression)
    {
        var ran = new TaskCompletionSource<DateTime>(TaskCreationOptions.RunContinuationsAsynchronously);
        new ServiceCollection()
            .MapTicker(function, (_, _) => { ran.TrySetResult(DateTime.UtcNow); return Task.CompletedTask; })
            .WithCron(expression);
        TickerFunctionProvider.Build();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSharpTicker();
        _app = builder.Build();
        _app.UseSharpTicker();
        await _app.StartAsync();
        return ran;
    }

    [Fact]
    public async Task ACronWithNames_IsSeeded_Scheduled_AndRunsTheFunction()
    {
        var ran = await StartHostWithCronAsync(Function, EveryDayByName);

        var finished = await Task.WhenAny(ran.Task, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.Same(ran.Task, finished);

        var store = _app.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();
        var cron = Assert.Single(await store.GetCronTickers(null, default), c => c.Function == Function);
        Assert.Equal(EveryDayByName, cron.Expression);

        var occurrence = (await store.GetAllCronTickerOccurrences(x => x.CronTickerId == cron.Id, default)).First();
        Assert.Contains(occurrence.Status, new[] { TickerStatus.Done, TickerStatus.InProgress, TickerStatus.Queued });
    }

    [Fact]
    public async Task ACronNamingAMonthThatIsNotNow_DoesNotRun_SoTheNamesAreReallyEvaluated()
    {
        // Six months from now, so it is never this month in the UTC or the local time zone.
        var otherMonth = new[] { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" }[(DateTime.UtcNow.Month + 5) % 12];
        var ran = await StartHostWithCronAsync("EndToEnd.OtherMonth", $"* * * * {otherMonth} *");

        var finished = await Task.WhenAny(ran.Task, Task.Delay(TimeSpan.FromSeconds(4)));

        Assert.NotSame(ran.Task, finished);
    }
}
