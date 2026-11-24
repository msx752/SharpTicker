using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpTicker.DependencyInjection;
using SharpTicker.Provider;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;

namespace SharpTicker.Tests;

/// <summary>
/// Crons as an application adds them: <c>AddSharpTicker</c> on the in-memory store, two crons added through
/// <see cref="ICronTickerManager{TCronTicker}"/>, one written in the normal form and one with other white space between and
/// around its fields, and the scheduler planning and running them. Both run, each occurrence at a time its expression
/// matches. The test fails when the exception handler is called or anything is logged at Warning or above.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class CronWhiteSpaceEndToEndTests : IAsyncLifetime
{
    // Functions with no cron of their own: they stay in the process-wide registry, and no later host seeds them.
    private const string Normal = "CronWhiteSpace.Normal";
    private const string Spaced = "CronWhiteSpace.Spaced";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private sealed class RecordingExceptionHandler : ITickerExceptionHandler
    {
        public static ConcurrentQueue<string> Calls { get; } = new();

        public Task HandleExceptionAsync(Exception exception, Guid tickerId, TickerType tickerType)
        {
            Calls.Enqueue($"exception {tickerId}: {exception}");
            return Task.CompletedTask;
        }

        public Task HandleCanceledExceptionAsync(Exception exception, Guid tickerId, TickerType tickerType)
        {
            Calls.Enqueue($"cancellation {tickerId}: {exception}");
            return Task.CompletedTask;
        }
    }

    private static readonly ConcurrentDictionary<string, int> Runs = new();
    private readonly CapturedLogs _logs = new();
    // AddSharpTicker sets the scheduler's time zone for the process; it is put back after the test.
    private readonly TimeZoneInfo _originalTimeZone = CronScheduleCache.TimeZoneInfo;
    private WebApplication? _app;

    public async Task InitializeAsync()
    {
        RecordingExceptionHandler.Calls.Clear();
        Runs.Clear();
        await ClearStoreAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        await ClearStoreAsync();
        CronScheduleCache.TimeZoneInfo = _originalTimeZone;
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

    private string Report()
        => $"handler: {string.Join("\n", RecordingExceptionHandler.Calls)}\nlog:\n{string.Join("\n", _logs.Entries.Select(e => $"{e.Level} {e.Category}: {e.Message}"))}";

    [Fact]
    public async Task A_Cron_Written_With_Other_White_Space_Runs_Like_Its_Normal_Form()
    {
        var services = new ServiceCollection();
        services.MapTicker(Normal, (_, _) => { Runs.AddOrUpdate(Normal, 1, (_, n) => n + 1); return Task.CompletedTask; });
        services.MapTicker(Spaced, (_, _) => { Runs.AddOrUpdate(Spaced, 1, (_, n) => n + 1); return Task.CompletedTask; });

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_logs);
        builder.Services.AddSharpTicker(o => o.SetExceptionHandler<RecordingExceptionHandler>());
        _app = builder.Build();
        _app.UseSharpTicker();
        await _app.StartAsync();

        var manager = _app.Services.GetRequiredService<ICronTickerManager<CronTickerEntity>>();
        var normal = await manager.AddAsync(new CronTickerEntity { Function = Normal, Expression = "*/2 * * * * *" });
        var spaced = await manager.AddAsync(new CronTickerEntity { Function = Spaced, Expression = " */2\t*  * *\u00A0* * " });
        Assert.True(normal.IsSucceeded, normal.Exception?.Message);
        Assert.True(spaced.IsSucceeded, spaced.Exception?.Message);

        var store = _app.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();
        async Task<CronTickerOccurrenceEntity<CronTickerEntity>[]> DoneAsync(Guid id)
            => (await store.GetAllCronTickerOccurrences(x => x.CronTickerId == id, default))
                .Where(o => o.Status is TickerStatus.Done or TickerStatus.DueDone)
                .ToArray();

        // Waits for the stored outcome: a run counted in the function may not be stored as done yet.
        var deadline = DateTime.UtcNow + Limit;
        while ((await DoneAsync(normal.Result.Id)).Length < 2 || (await DoneAsync(spaced.Result.Id)).Length < 2)
        {
            Assert.True(DateTime.UtcNow < deadline,
                $"not within {Limit}: two occurrences of each cron done (runs: normal {Runs.GetValueOrDefault(Normal)}, spaced {Runs.GetValueOrDefault(Spaced)})\n{Report()}");
            await Task.Delay(100);
        }

        foreach (var id in new[] { normal.Result.Id, spaced.Result.Id })
        {
            // Every second second: the expression is read as written, white space aside.
            Assert.All(await DoneAsync(id), o => Assert.True(o.ExecutionTime.Second % 2 == 0 && o.ExecutionTime.Millisecond == 0,
                $"cron {id}: an occurrence at {o.ExecutionTime:HH:mm:ss.fff}"));
        }

        Assert.True(RecordingExceptionHandler.Calls.IsEmpty, Report());
        Assert.DoesNotContain(_logs.Entries, e => e.Level >= LogLevel.Warning);
    }
}
