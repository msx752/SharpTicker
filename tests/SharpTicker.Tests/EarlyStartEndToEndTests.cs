using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SharpTicker.DependencyInjection;
using SharpTicker.Provider;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Interfaces.Managers;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// A job does not start before its execution time. Run by a real host with the in-memory store: the scheduler loop plans
/// the jobs of one second as one batch, and the batch must not start a job whose time is later in that second.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class EarlyStartEndToEndTests : IAsyncLifetime
{
    public sealed record EarlyStartRequest(string Name);

    // A timer can fire a few milliseconds before the clock reaches the target; an early start by the batch is hundreds.
    private static readonly TimeSpan TimerTolerance = TimeSpan.FromMilliseconds(30);

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
        var times = await store.GetTimeTickers(null, default);
        await store.RemoveTimeTickers(times.Select(t => t.Id).ToArray());
    }

    private async Task StartHostAsync()
    {
        TickerFunctionProvider.Build();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSharpTicker(o => o.ConfigureScheduler(s => s.FallbackIntervalChecker = TimeSpan.FromMinutes(10)));
        _app = builder.Build();
        _app.UseSharpTicker();
        await _app.StartAsync();
    }

    private ITimeTickerManager<TimeTickerEntity> Times => _app!.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();

    private static TaskCompletionSource<DateTime> MapStartRecorder(string function)
    {
        var started = new TaskCompletionSource<DateTime>(TaskCreationOptions.RunContinuationsAsynchronously);
        new ServiceCollection().MapTicker<EarlyStartRequest>(function, (_, _) =>
        {
            started.TrySetResult(DateTime.UtcNow);
            return Task.CompletedTask;
        });
        return started;
    }

    // A whole second far enough ahead that the loop sleeps until it (longer than the minimum polling interval).
    private static DateTime WholeSecondAhead(int seconds)
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, DateTimeKind.Utc).AddSeconds(seconds);
    }

    private static async Task<DateTime> StartOf(TaskCompletionSource<DateTime> started)
    {
        Assert.Same(started.Task, await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(20))));
        return await started.Task;
    }

    private static void AssertNotEarly(DateTime startedAt, DateTime executionTime)
        => Assert.True(startedAt >= executionTime - TimerTolerance,
            $"the job started {(executionTime - startedAt).TotalMilliseconds:F0} ms before its time ({startedAt:HH:mm:ss.fff} for {executionTime:HH:mm:ss.fff})");

    // Control: the same job alone in its second starts at its time.
    [Fact]
    public async Task AJobAloneInItsSecond_DoesNotStartBeforeItsTime()
    {
        var started = MapStartRecorder("EarlyStart.Alone");
        await StartHostAsync();

        var time = WholeSecondAhead(4).AddMilliseconds(700);
        var added = await Times.AddAsync(new TimeTickerEntity { Function = "EarlyStart.Alone", ExecutionTime = time });
        Assert.True(added.IsSucceeded, added.Exception?.ToString());

        AssertNotEarly(await StartOf(started), time);
    }

    [Fact]
    public async Task AJobLaterInTheSameSecondAsAnotherJob_DoesNotStartBeforeItsTime()
    {
        var firstStarted = MapStartRecorder("EarlyStart.First");
        var secondStarted = MapStartRecorder("EarlyStart.Second");
        await StartHostAsync();

        var second = WholeSecondAhead(4);
        var firstTime = second.AddMilliseconds(100);
        var secondTime = second.AddMilliseconds(800);
        var addedSecond = await Times.AddAsync(new TimeTickerEntity { Function = "EarlyStart.Second", ExecutionTime = secondTime });
        var addedFirst = await Times.AddAsync(new TimeTickerEntity { Function = "EarlyStart.First", ExecutionTime = firstTime });
        Assert.True(addedFirst.IsSucceeded, addedFirst.Exception?.ToString());
        Assert.True(addedSecond.IsSucceeded, addedSecond.Exception?.ToString());

        AssertNotEarly(await StartOf(firstStarted), firstTime);
        AssertNotEarly(await StartOf(secondStarted), secondTime);
    }

    [Fact]
    public async Task AJobInTheSameSecondAsACronOccurrence_DoesNotStartBeforeItsTime()
    {
        // The cron fires once a minute, at the whole second the job falls in.
        var second = WholeSecondAhead(6);
        new ServiceCollection()
            .MapTicker("EarlyStart.Cron", (_, _) => Task.CompletedTask)
            .WithCron($"{second.Second} * * * * *");
        var started = MapStartRecorder("EarlyStart.BesideCron");
        await StartHostAsync();

        var time = second.AddMilliseconds(700);
        var added = await Times.AddAsync(new TimeTickerEntity { Function = "EarlyStart.BesideCron", ExecutionTime = time });
        Assert.True(added.IsSucceeded, added.Exception?.ToString());
        Assert.True(second - DateTime.UtcNow > TimeSpan.FromSeconds(2), "the host started too slowly for this test's timing");

        AssertNotEarly(await StartOf(started), time);
    }
}
