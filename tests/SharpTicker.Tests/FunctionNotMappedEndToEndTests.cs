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
/// The function check as an application meets it: <c>AddSharpTicker</c> on the in-memory store, tickers added through
/// the managers. A time ticker and a cron whose function is not mapped (no such name, or the name in other letter case) are
/// refused and not stored; a time ticker of a mapped function is stored and runs. The test fails when the exception handler
/// is called or anything is logged at Warning or above.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class FunctionNotMappedEndToEndTests : IAsyncLifetime
{
    // A function with no cron: it stays in the process-wide registry after the test, and no later host seeds or runs it.
    private const string Function = "FunctionNotMapped.Job";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

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
        var times = await store.GetTimeTickers(null, default);
        await store.RemoveTimeTickers(times.Select(t => t.Id).ToArray());
        var crons = await store.GetCronTickers(null, default);
        await store.RemoveCronTickers(crons.Select(c => c.Id).ToArray(), default);
    }

    private string Report()
        => $"handler: {string.Join("\n", RecordingExceptionHandler.Calls)}\nlog:\n{string.Join("\n", _logs.Entries.Select(e => $"{e.Level} {e.Category}: {e.Message}"))}";

    [Fact]
    public async Task A_Ticker_Of_A_Function_That_Is_Not_Mapped_Is_Refused_And_One_That_Is_Runs()
    {
        new ServiceCollection().MapTicker(Function, (_, _) => Task.CompletedTask);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_logs);
        builder.Services.AddSharpTicker(o => o.SetExceptionHandler<RecordingExceptionHandler>());
        _app = builder.Build();
        _app.UseSharpTicker();
        await _app.StartAsync();

        var times = _app.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
        var crons = _app.Services.GetRequiredService<ICronTickerManager<CronTickerEntity>>();
        string[] refused = ["FunctionNotMapped.Missing", Function.ToUpperInvariant()];
        foreach (var name in refused)
        {
            var time = await times.AddAsync(new TimeTickerEntity { Function = name, ExecutionTime = DateTime.UtcNow.AddSeconds(1) });
            Assert.False(time.IsSucceeded, $"a time ticker of {name} was added");
            Assert.Contains($"Cannot find TickerFunction with name {name}", time.Exception?.Message);
            var cron = await crons.AddAsync(new CronTickerEntity { Function = name, Expression = "0 0 * * * *" });
            Assert.False(cron.IsSucceeded, $"a cron of {name} was added");
            Assert.Contains($"Cannot find TickerFunction with name {name}", cron.Exception?.Message);
        }

        var added = await times.AddAsync(new TimeTickerEntity { Function = Function, ExecutionTime = DateTime.UtcNow.AddSeconds(1) });
        Assert.True(added.IsSucceeded, added.Exception?.Message);

        var store = _app.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();
        var deadline = DateTime.UtcNow + Limit;
        while (true)
        {
            var stored = await store.GetTimeTickers(null, default);
            Assert.Equal([added.Result.Id], stored.Select(t => t.Id));
            if (stored[0].Status is TickerStatus.Done or TickerStatus.DueDone)
                break;
            Assert.True(DateTime.UtcNow < deadline, $"not within {Limit}: the mapped job ran (status {stored[0].Status})\n{Report()}");
            await Task.Delay(100);
        }
        // Other crons can be there: the start seeds the crons of functions that other tests left in the registry.
        Assert.DoesNotContain(await store.GetCronTickers(null, default), c => refused.Contains(c.Function));

        Assert.True(RecordingExceptionHandler.Calls.IsEmpty, Report());
        Assert.DoesNotContain(_logs.Entries, e => e.Level >= LogLevel.Warning);
    }
}
