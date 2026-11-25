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
/// An exception handler that throws, as an application sees it: <c>AddSharpTicker</c> on the in-memory store with an
/// <see cref="ITickerExceptionHandler"/> whose two methods throw, the jobs added through
/// <see cref="ITimeTickerManager{TTimeTicker}"/> and run by the scheduler. Each job still ends with the outcome it would have
/// had with no handler: a failing job is stored as Failed (after its retry, when it has one), a cancelled job as Cancelled,
/// and the chain child that waits for its parent's failure runs. The handler is called for each failed attempt and for the
/// cancellation. The test also fails on any log entry at Warning or above other than the jobs' own failure and cancellation
/// entries and one entry per handler call that names the handler's exception.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class ExceptionHandlerThatThrowsEndToEndTests : IAsyncLifetime
{
    // Functions with no cron: they stay in the process-wide registry after the test, and no later host seeds or runs them.
    private const string Fails = "ExceptionHandlerThatThrows.Fails";
    private const string FailsOnce = "ExceptionHandlerThatThrows.FailsOnce";
    private const string WaitsForCancel = "ExceptionHandlerThatThrows.WaitsForCancel";
    private const string AfterFailure = "ExceptionHandlerThatThrows.AfterFailure";
    private const string Failure = "ExceptionHandlerThatThrows: the job failed";
    private const string HandlerFailure = "ExceptionHandlerThatThrows: the handler failed";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<Guid, int> FailsOnceRuns = new();
    private static readonly ConcurrentQueue<Guid> ChildRuns = new();
    private static readonly ConcurrentQueue<Guid> CancelWaiterStarted = new();

    private sealed class ThrowingExceptionHandler : ITickerExceptionHandler
    {
        public static ConcurrentQueue<string> Calls { get; } = new();

        public Task HandleExceptionAsync(Exception exception, Guid tickerId, TickerType tickerType)
        {
            Calls.Enqueue($"exception {tickerId}");
            throw new InvalidOperationException(HandlerFailure);
        }

        public Task HandleCanceledExceptionAsync(Exception exception, Guid tickerId, TickerType tickerType)
        {
            Calls.Enqueue($"cancellation {tickerId}");
            throw new InvalidOperationException(HandlerFailure);
        }
    }

    private readonly CapturedLogs _logs = new();
    private WebApplication? _app;

    public async Task InitializeAsync()
    {
        ThrowingExceptionHandler.Calls.Clear();
        FailsOnceRuns.Clear();
        ChildRuns.Clear();
        CancelWaiterStarted.Clear();
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
        => $"handler: {string.Join(", ", ThrowingExceptionHandler.Calls)}\nlog:\n{string.Join("\n", _logs.Entries.Select(e => $"{e.Level} {e.Category}: {e.Message}"))}";

    [Fact]
    public async Task A_Handler_That_Throws_Leaves_Every_Job_With_Its_Outcome()
    {
        var services = new ServiceCollection();
        services.MapTicker(Fails, (_, _) => throw new InvalidOperationException(Failure));
        services.MapTicker(FailsOnce, (context, _) =>
        {
            if (FailsOnceRuns.AddOrUpdate(context.Id, 1, (_, n) => n + 1) == 1)
                throw new InvalidOperationException(Failure);
            return Task.CompletedTask;
        });
        services.MapTicker(WaitsForCancel, async (context, ct) =>
        {
            CancelWaiterStarted.Enqueue(context.Id);
            await Task.Delay(Timeout.Infinite, ct);
        });
        services.MapTicker(AfterFailure, (context, _) =>
        {
            ChildRuns.Enqueue(context.Id);
            return Task.CompletedTask;
        });

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_logs);
        builder.Services.AddSharpTicker(o => o.SetExceptionHandler<ThrowingExceptionHandler>());
        _app = builder.Build();
        _app.UseSharpTicker();
        await _app.StartAsync();

        var manager = _app.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
        var at = DateTime.UtcNow.AddSeconds(1);
        var failing = await manager.AddAsync(new TimeTickerEntity
        {
            Function = Fails,
            ExecutionTime = at,
            Children = { new TimeTickerEntity { Function = AfterFailure, RunCondition = RunCondition.OnFailure } }
        });
        var retried = await manager.AddAsync(new TimeTickerEntity { Function = FailsOnce, ExecutionTime = at, Retries = 1, RetryIntervals = [1] });
        var cancelled = await manager.AddAsync(new TimeTickerEntity { Function = WaitsForCancel, ExecutionTime = at });
        Assert.True(failing.IsSucceeded, failing.Exception?.Message);
        Assert.True(retried.IsSucceeded, retried.Exception?.Message);
        Assert.True(cancelled.IsSucceeded, cancelled.Exception?.Message);
        var childId = failing.Result.Children.Single().Id;

        var store = _app.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();
        var deadline = DateTime.UtcNow + Limit;
        var cancelRequested = false;
        while (true)
        {
            if (!cancelRequested && CancelWaiterStarted.Contains(cancelled.Result.Id))
                cancelRequested = TickerCancellationTokenManager.RequestTickerCancellationById(cancelled.Result.Id);

            var parent = await store.GetTimeTickerById(failing.Result.Id);
            var child = await store.GetTimeTickerById(childId);
            var once = await store.GetTimeTickerById(retried.Result.Id);
            var stopped = await store.GetTimeTickerById(cancelled.Result.Id);
            if (parent.Status == TickerStatus.Failed && child.Status is TickerStatus.Done or TickerStatus.DueDone
                && once.Status is TickerStatus.Done or TickerStatus.DueDone && stopped.Status == TickerStatus.Cancelled)
            {
                Assert.Contains(Failure, parent.ExceptionMessage);
                Assert.Equal(1, once.RetryCount);
                break;
            }

            Assert.True(DateTime.UtcNow < deadline,
                $"not within {Limit}: failing {parent.Status}, its child {child.Status}, retried {once.Status} (runs {FailsOnceRuns.GetValueOrDefault(retried.Result.Id)}), cancelled {stopped.Status} (cancel requested: {cancelRequested})\n{Report()}");
            await Task.Delay(100);
        }

        Assert.Equal([childId], ChildRuns.ToArray());
        Assert.Equal(2, FailsOnceRuns[retried.Result.Id]);
        Assert.Equal(1, ThrowingExceptionHandler.Calls.Count(c => c == $"exception {failing.Result.Id}"));
        Assert.Equal(1, ThrowingExceptionHandler.Calls.Count(c => c == $"exception {retried.Result.Id}"));
        Assert.Equal(1, ThrowingExceptionHandler.Calls.Count(c => c == $"cancellation {cancelled.Result.Id}"));
        Assert.Equal(3, ThrowingExceptionHandler.Calls.Count);

        // One entry per handler call names the handler's exception; the rest at Warning or above are the jobs' own failures.
        var handlerEntries = _logs.Entries.Where(e => e.Level >= LogLevel.Warning && e.Exception?.Message == HandlerFailure).ToArray();
        Assert.True(handlerEntries.Length == 3, Report());
        // The entries of a run that loses its outcome or its attempt name the job too; none may be among them.
        Assert.True(!_logs.Entries.Any(e => e.Level >= LogLevel.Warning
            && (e.Message.Contains("was not stored") || e.Message.Contains("could not be stored")
                || e.Message.Contains("Recording attempt") || e.Message.Contains("nothing handled"))), Report());
        var unexpected = _logs.Entries.Where(e => e.Level >= LogLevel.Warning
            && e.Exception?.Message != HandlerFailure
            && !e.Message.Contains(failing.Result.Id.ToString()) && !e.Message.Contains(retried.Result.Id.ToString())
            && !e.Message.Contains(cancelled.Result.Id.ToString()) && !e.Message.Contains(Failure) && !e.Message.StartsWith("Retrying in 1s (retry 1 of 1)")).ToArray();
        Assert.True(unexpected.Length == 0, Report());
    }
}
