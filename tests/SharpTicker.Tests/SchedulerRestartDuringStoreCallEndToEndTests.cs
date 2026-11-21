using System.Collections.Concurrent;
using System.Data.Common;
using System.Reflection;
using System.Runtime.ExceptionServices;
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
/// A restart of the scheduler loop that lands while a store call of the pass runs (#214). Microsoft.Data.SqlClient reports the
/// cancel of a running command as SqlException ("Operation cancelled by user."), not as OperationCanceledException. The
/// application here runs on the in-memory store behind a decorator that does the same for one read of the loop: once armed,
/// the next read of the earliest time tickers waits until the loop's token is cancelled and then throws a DbException. The
/// restarts come from tickers the application adds through <see cref="ITimeTickerManager{TTimeTicker}"/>. The test fails on
/// anything the application would see go wrong: a refused add, a call to its exception handler, or a log entry at Warning
/// or above.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class SchedulerRestartDuringStoreCallEndToEndTests : IAsyncLifetime
{
    // A function with no cron: it stays in the process-wide registry after the test, and no later host seeds or runs it.
    private const string Function = "RestartDuringStoreCall.Job";
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

    /// <summary>Stands in for SqlException: a provider's own exception for a command it cancelled.</summary>
    private sealed class ProviderCancelledException : DbException
    {
        public ProviderCancelledException() : base("Operation cancelled by user.") { }
    }

    /// <summary>Everything the in-memory store implements, so the decorator keeps the claims, skips and fenced writes of it.</summary>
    internal interface IInMemoryStore : ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>, IClaimFanOut, IChildSkipWriter, IFencedRunWriter
    {
    }

    public class CancelReportingStore : DispatchProxy
    {
        internal object Inner = null!;
        internal int Armed;
        internal readonly TaskCompletionSource InFlight = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ConcurrentQueue<Exception> Thrown = new();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>.GetEarliestTimeTickers)
                && Interlocked.Exchange(ref Armed, 0) == 1)
                return ReportTheCancelAsync((CancellationToken)args![0]!);

            try
            {
                return method.Invoke(Inner, args);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }

        private async Task<TimeTickerEntity[]> ReportTheCancelAsync(CancellationToken token)
        {
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (token.Register(() => cancelled.TrySetResult()))
            {
                InFlight.TrySetResult();
                await cancelled.Task;
            }

            var reported = new ProviderCancelledException();
            Thrown.Enqueue(reported);
            throw reported;
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
        => $"handler: {string.Join("\n", RecordingExceptionHandler.Calls)}\nlog:\n{string.Join("\n", _logs.Entries.Select(e => $"{e.Level} {e.Category}: {e.Message} {e.Exception?.GetType().Name}"))}";

    [Fact]
    public async Task ARestartThatCutsOffAStoreCall_WhichTheProviderReportsAsDbException_IsNoFailure_AndTheJobsAddedRun()
    {
        var starts = new ConcurrentDictionary<Guid, int>();
        new ServiceCollection().MapTicker(Function, (context, _) =>
        {
            starts.AddOrUpdate(context.Id, 1, (_, n) => n + 1);
            return Task.CompletedTask;
        });

        var proxy = DispatchProxy.Create<IInMemoryStore, CancelReportingStore>();
        var store = (CancelReportingStore)(object)proxy;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_logs);
        // Registered before AddSharpTicker, as an application registers a store of its own; the default is added only when none is.
        builder.Services.AddSingleton<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>(sp =>
        {
            store.Inner = new TickerInMemoryPersistenceProvider<TimeTickerEntity, CronTickerEntity>(sp);
            return proxy;
        });
        builder.Services.AddSharpTicker(o => o.SetExceptionHandler<RecordingExceptionHandler>());
        _app = builder.Build();
        _app.UseSharpTicker();
        await _app.StartAsync();
        var tickers = _app.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();

        // The next read of the loop waits for its token. A ticker due in a few seconds (one due within a second is run at once,
        // without the loop) restarts the loop, whose new pass makes that read.
        Interlocked.Exchange(ref store.Armed, 1);
        var first = await tickers.AddAsync(new TimeTickerEntity { Function = Function, ExecutionTime = DateTime.UtcNow.AddSeconds(3) });
        Assert.True(first.IsSucceeded, first.Exception?.Message);
        await store.InFlight.Task.WaitAsync(Limit);

        // The planned restart: one more ticker, added while the read runs. The read ends in the DbException.
        var second = await tickers.AddAsync(new TimeTickerEntity { Function = Function, ExecutionTime = DateTime.UtcNow.AddSeconds(3) });
        Assert.True(second.IsSucceeded, second.Exception?.Message);

        var persisted = _app.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();
        var deadline = DateTime.UtcNow + Limit;
        while (true)
        {
            var rows = (await persisted.GetTimeTickers(null, default))
                .Where(t => t.Id == first.Result.Id || t.Id == second.Result.Id).ToArray();
            if (rows.Length == 2 && rows.All(t => t.Status is TickerStatus.Done or TickerStatus.DueDone))
                break;
            Assert.True(DateTime.UtcNow < deadline,
                $"not within {Limit}: both tickers ran and ended ({string.Join(", ", rows.Select(t => t.Status))})\n{Report()}");
            await Task.Delay(100);
        }

        // A second start would come within the next passes of the scheduler.
        await Task.Delay(TimeSpan.FromSeconds(2));
        var thrown = Assert.Single(store.Thrown);
        Assert.Equal(1, starts.GetValueOrDefault(first.Result.Id));
        Assert.Equal(1, starts.GetValueOrDefault(second.Result.Id));
        Assert.True(RecordingExceptionHandler.Calls.IsEmpty, Report());
        Assert.DoesNotContain(_logs.Entries, e => e.Level >= LogLevel.Warning);
        Assert.DoesNotContain(_logs.Entries, e => e.Message.StartsWith("The scheduler loop recovered"));
        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Debug && e.Exception == thrown
            && e.Message.StartsWith("The scheduler loop restarted; the pass it cut off ended in"));
    }
}
