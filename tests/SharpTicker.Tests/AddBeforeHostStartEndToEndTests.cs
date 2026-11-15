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
/// A ticker added between <c>app.Build()</c> and the start of the host (#75): <c>AddSharpTicker</c>, a function mapped
/// with <c>MapTicker</c>, <c>UseSharpTicker</c>, a time ticker added through <see cref="ITimeTickerManager{TTimeTicker}"/>,
/// then the host started with the scheduler running on the in-memory store. The test fails on anything the application
/// would see go wrong: a refused add, a call to its exception handler, or a log entry at Warning or above.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class AddBeforeHostStartEndToEndTests : IAsyncLifetime
{
    // A function with no cron: it stays in the process-wide registry after the test, and no later host seeds or runs it.
    private const string Function = "AddBeforeHostStart.Job";
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
    }

    private string Report()
        => $"handler: {string.Join("\n", RecordingExceptionHandler.Calls)}\nlog:\n{string.Join("\n", _logs.Entries.Select(e => $"{e.Level} {e.Category}: {e.Message}"))}";

    [Fact]
    public async Task ATimeTickerAddedBeforeTheHostStarted_IsAcceptedAndRunsOnceAfterTheStart()
    {
        var starts = 0;
        // Mapped as an application maps it; nothing in the test builds the function registry.
        new ServiceCollection().MapTicker(Function, (_, _) =>
        {
            Interlocked.Increment(ref starts);
            return Task.CompletedTask;
        });

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_logs);
        builder.Services.AddSharpTicker(o => o.SetExceptionHandler<RecordingExceptionHandler>());
        _app = builder.Build();
        _app.UseSharpTicker();

        var added = await _app.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>()
            .AddAsync(new TimeTickerEntity { Function = Function, ExecutionTime = DateTime.UtcNow.AddSeconds(1) });
        Assert.True(added.IsSucceeded, added.Exception?.Message);

        await _app.StartAsync();
        var store = _app.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();

        var deadline = DateTime.UtcNow + Limit;
        while (true)
        {
            var ticker = Assert.Single(await store.GetTimeTickers(null, default), t => t.Id == added.Result.Id);
            if (ticker.Status is TickerStatus.Done or TickerStatus.DueDone)
                break;
            Assert.True(DateTime.UtcNow < deadline, $"not within {Limit}: the ticker ran and ended (status {ticker.Status})\n{Report()}");
            await Task.Delay(100);
        }

        // A second start would come within the next passes of the scheduler.
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Volatile.Read(ref starts));
        Assert.True(RecordingExceptionHandler.Calls.IsEmpty, Report());
        Assert.DoesNotContain(_logs.Entries, e => e.Level >= LogLevel.Warning);
    }
}
