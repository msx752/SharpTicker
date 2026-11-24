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
/// What a run writes to the store, as an application sees it: <c>AddSharpTicker</c> on the in-memory store, the jobs added
/// through <see cref="ITimeTickerManager{TTimeTicker}"/> and run by the scheduler. A job that succeeds is stored as done,
/// with the time it ran and its duration; a job that fails after its retry is stored as failed, with its retry count and
/// its exception. Each of these fields reaches the store only when the run records it as changed. The test also fails when
/// the exception handler is called for any job but the failing one, or anything but that job's failure is logged at Warning
/// or above (its retry Warning, its failure Errors).
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class JobOutcomeFieldsEndToEndTests : IAsyncLifetime
{
    // Functions with no cron: they stay in the process-wide registry after the test, and no later host seeds or runs them.
    private const string Succeeds = "JobOutcomeFields.Succeeds";
    private const string Fails = "JobOutcomeFields.Fails";
    private const string Failure = "JobOutcomeFields: the job failed";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private sealed class RecordingExceptionHandler : ITickerExceptionHandler
    {
        public static ConcurrentQueue<(Guid Id, string Call)> Calls { get; } = new();

        public Task HandleExceptionAsync(Exception exception, Guid tickerId, TickerType tickerType)
        {
            Calls.Enqueue((tickerId, $"exception {tickerId}: {exception.Message}"));
            return Task.CompletedTask;
        }

        public Task HandleCanceledExceptionAsync(Exception exception, Guid tickerId, TickerType tickerType)
        {
            Calls.Enqueue((tickerId, $"cancellation {tickerId}: {exception.Message}"));
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
        => $"handler: {string.Join("\n", RecordingExceptionHandler.Calls.Select(c => c.Call))}\nlog:\n{string.Join("\n", _logs.Entries.Select(e => $"{e.Level} {e.Category}: {e.Message}"))}";

    [Fact]
    public async Task A_Run_Stores_Its_Outcome_With_Every_Field_It_Set()
    {
        var services = new ServiceCollection();
        services.MapTicker(Succeeds, async (_, ct) => await Task.Delay(50, ct));
        services.MapTicker(Fails, (_, _) => throw new InvalidOperationException(Failure));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_logs);
        builder.Services.AddSharpTicker(o => o.SetExceptionHandler<RecordingExceptionHandler>());
        _app = builder.Build();
        _app.UseSharpTicker();
        await _app.StartAsync();

        var manager = _app.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
        var addedAt = DateTime.UtcNow;
        var ok = await manager.AddAsync(new TimeTickerEntity { Function = Succeeds, ExecutionTime = addedAt.AddSeconds(1) });
        var failing = await manager.AddAsync(new TimeTickerEntity
        {
            Function = Fails,
            ExecutionTime = addedAt.AddSeconds(1),
            Retries = 1,
            RetryIntervals = [1]
        });
        Assert.True(ok.IsSucceeded, ok.Exception?.Message);
        Assert.True(failing.IsSucceeded, failing.Exception?.Message);

        var store = _app.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();
        var deadline = DateTime.UtcNow + Limit;
        TimeTickerEntity done, failed;
        while (true)
        {
            var tickers = await store.GetTimeTickers(null, default);
            done = tickers.Single(t => t.Id == ok.Result.Id);
            failed = tickers.Single(t => t.Id == failing.Result.Id);
            if (done.Status is TickerStatus.Done or TickerStatus.DueDone && failed.Status == TickerStatus.Failed)
                break;
            Assert.True(DateTime.UtcNow < deadline,
                $"not within {Limit}: the jobs ended (status {done.Status}, {failed.Status})\n{Report()}");
            await Task.Delay(100);
        }

        Assert.NotNull(done.ExecutedAt);
        Assert.InRange(done.ExecutedAt!.Value, addedAt, DateTime.UtcNow);
        Assert.True(done.ElapsedTime >= 40, $"the done job's duration is {done.ElapsedTime} ms");
        Assert.Null(done.ExceptionMessage);

        Assert.NotNull(failed.ExecutedAt);
        Assert.InRange(failed.ExecutedAt!.Value, addedAt, DateTime.UtcNow);
        Assert.Equal(1, failed.RetryCount);
        Assert.Contains(Failure, failed.ExceptionMessage);
        // The retry waits 1 s, and the duration is that of the whole run.
        Assert.True(failed.ElapsedTime >= 900, $"the failed job's duration is {failed.ElapsedTime} ms");

        Assert.DoesNotContain(RecordingExceptionHandler.Calls, c => c.Id != failing.Result.Id);
        Assert.Contains(RecordingExceptionHandler.Calls, c => c.Id == failing.Result.Id);
        Assert.DoesNotContain(_logs.Entries, e => e.Level >= LogLevel.Warning
            && !e.Message.Contains(failing.Result.Id.ToString()) && !e.Message.Contains(Failure)
            && !e.Message.StartsWith("Retrying in 1s (retry 1 of 1)"));
    }
}
