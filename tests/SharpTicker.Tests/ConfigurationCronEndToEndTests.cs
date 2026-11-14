using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpTicker.DependencyInjection;
using SharpTicker.Provider;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;

namespace SharpTicker.Tests;

/// <summary>
/// A cron that comes from configuration in an application as a user writes it (#79): <c>AddSharpTicker</c>, a function
/// mapped with a <c>%Key%</c> placeholder, the value in the application's configuration with five fields, a started host,
/// the in-memory store and the scheduler running. The test waits for the real clock to reach the next minute, so it takes
/// up to a minute. It also fails on anything the application would see go wrong: a call to its exception handler, or a
/// log entry at Warning or above.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class ConfigurationCronEndToEndTests : IAsyncLifetime
{
    private const string Function = "ConfigurationCron.EndToEnd.Job";
    private const string ConfigurationKey = "ConfigurationCron:EveryMinute";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(90);

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

    private readonly CapturedLogs _logs = new();
    private WebApplication? _app;

    public async Task InitializeAsync()
    {
        RecordingExceptionHandler.Calls.Clear();
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
    public async Task AFiveFieldCronFromConfiguration_RunsAtTheStartOfTheNextMinute()
    {
        var starts = 0;
        new ServiceCollection().MapTicker(Function, (_, _) =>
        {
            Interlocked.Increment(ref starts);
            return Task.CompletedTask;
        }).WithCron($"%{ConfigurationKey}%");
        TickerFunctionProvider.Build();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [ConfigurationKey] = "* * * * *" });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_logs);
        builder.Services.AddSharpTicker(o => o.SetExceptionHandler<RecordingExceptionHandler>());
        _app = builder.Build();
        _app.UseSharpTicker();
        await _app.StartAsync();
        var store = _app.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();

        var cron = Assert.Single(await store.GetCronTickers(null, default), c => c.Function == Function);
        Assert.Equal("0 * * * * *", cron.Expression);

        var deadline = DateTime.UtcNow + Limit;
        CronTickerOccurrenceEntity<CronTickerEntity>[] ended;
        while (true)
        {
            ended = (await store.GetAllCronTickerOccurrences(null, default))
                .Where(o => o.CronTickerId == cron.Id && o.Status is TickerStatus.Done or TickerStatus.DueDone)
                .ToArray();
            if (ended.Length > 0)
                break;
            Assert.True(DateTime.UtcNow < deadline, $"not within {Limit}: the configured cron ran and ended\n{Report()}");
            await Task.Delay(100);
        }

        Assert.True(Volatile.Read(ref starts) >= 1, Report());
        Assert.All(ended, o => Assert.Equal(0, o.ExecutionTime.Second));
        Assert.True(RecordingExceptionHandler.Calls.IsEmpty, Report());
        Assert.DoesNotContain(_logs.Entries, e => e.Level >= LogLevel.Warning);
    }
}
