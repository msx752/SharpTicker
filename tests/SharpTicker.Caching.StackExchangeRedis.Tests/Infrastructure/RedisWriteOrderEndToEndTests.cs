using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using SharpTicker.Caching.StackExchangeRedis.DependencyInjection;
using SharpTicker.DependencyInjection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>
/// As an application meets #94: a started host on the Redis store moves a job with <c>UpdateAsync</c>, and the write of
/// the job's index entries does not reach Redis (only that one command of the update's call fails). The update reports
/// the failure; the job then starts at the time its row holds, not at the time of its index entry.
/// <para>
/// With one job and a running scheduler loop, the loop reads the row and plans it at the row's time, whatever its entry
/// says. The cases below are the ones the loop does not cover: the jobs are moved on an application without background
/// services (an API) and the worker starts after the old time, so only its fallback sees the overdue entry; and a job due
/// earlier keeps the loop's window, so only the fallback can take the job that was moved earlier.
/// </para>
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class RedisWriteOrderEndToEndTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private const string Function = "WriteOrder.EndToEnd";
    private static readonly ConcurrentDictionary<Guid, DateTime> Started = new();
    private static readonly ConcurrentQueue<string> Handled = new();

    // The index batch to fail, for the calls of the flow that set it; background services never see it.
    private static readonly AsyncLocal<FailBox?> FailIndexBatch = new();

    private sealed class FailBox
    {
        public int Left = 1;
    }

    static RedisWriteOrderEndToEndTests()
    {
        TickerFunctionProvider.RegisterFunctions(new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>
        {
            [Function] = (string.Empty, TickerTaskPriority.Normal, (_, _, context) =>
            {
                Started[context.Id] = DateTime.UtcNow;
                return Task.CompletedTask;
            }, 0)
        });
        TickerFunctionProvider.Build();
    }

    private readonly ConcurrentQueue<(LogLevel Level, string Text)> _log = new();
    private readonly List<IHost> _hosts = new();

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        Started.Clear();
        Handled.Clear();
    }

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.StopAsync();
            host.Dispose();
        }
        await fixture.ResetAsync();
    }

    public class FailingBatchDatabase : DispatchProxy
    {
        internal IDatabase Inner = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IDatabase.CreateBatch) && FailIndexBatch.Value is { } box && Interlocked.Decrement(ref box.Left) == 0)
                throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "The index write did not reach Redis.");
            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }
    }

    private sealed class RecordingExceptionHandler : ITickerExceptionHandler
    {
        public Task HandleExceptionAsync(Exception exception, Guid tickerId, TickerType tickerType)
        {
            Handled.Enqueue($"exception for {tickerType} {tickerId}: {exception}");
            return Task.CompletedTask;
        }

        public Task HandleCanceledExceptionAsync(Exception exception, Guid tickerId, TickerType tickerType)
        {
            Handled.Enqueue($"cancellation for {tickerType} {tickerId}: {exception}");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLoggerProvider(ConcurrentQueue<(LogLevel, string)> entries) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, entries);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<(LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue((logLevel, $"{logLevel} {category}: {formatter(state, exception)}{(exception == null ? "" : " | " + exception.GetType().Name + ": " + exception.Message)}"));
        }
    }

    // An application on the Redis store; one without background services runs no job and claims none.
    private async Task<ITimeTickerManager<TimeTickerEntity>> StartAsync(string node, bool backgroundServices = true)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(new RecordingLoggerProvider(_log));
        builder.Services.AddSharpTicker(options =>
        {
            options.ConfigureScheduler(scheduler =>
            {
                scheduler.NodeIdentifier = node;
                scheduler.FallbackIntervalChecker = TimeSpan.FromSeconds(1);
            });
            options.SetExceptionHandler<RecordingExceptionHandler>();
            if (!backgroundServices)
                options.DisableBackgroundServices();
            options.AddStackExchangeRedis(redis => redis.Configuration = fixture.Container.GetConnectionString());
        });
        // Registered after the library's own, so the store gets this one.
        builder.Services.AddKeyedSingleton<IDatabase>("sharpticker", (sp, _) =>
        {
            var proxy = DispatchProxy.Create<IDatabase, FailingBatchDatabase>();
            ((FailingBatchDatabase)(object)proxy).Inner = sp.GetRequiredKeyedService<IConnectionMultiplexer>("sharpticker").GetDatabase();
            return proxy;
        });
        var host = builder.Build();
        _hosts.Add(host);
        host.UseSharpTicker();
        await host.StartAsync();
        return host.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
    }

    private static async Task<Guid> AddAsync(ITimeTickerManager<TimeTickerEntity> manager, DateTime executionTime)
    {
        var added = await manager.AddAsync(new TimeTickerEntity { Function = Function, ExecutionTime = executionTime });
        Assert.True(added.IsSucceeded, $"the job was not added: {added.Exception}");
        return added.Result.Id;
    }

    // The update's index write fails; the update reports it.
    private static async Task MoveWithFailingIndexWriteAsync(ITimeTickerManager<TimeTickerEntity> manager, Guid id, DateTime executionTime)
    {
        FailIndexBatch.Value = new FailBox();
        try
        {
            var updated = await manager.UpdateAsync(new TimeTickerEntity { Id = id, Function = Function, ExecutionTime = executionTime });
            Assert.False(updated.IsSucceeded, "the update did not report the failed index write");
            Assert.IsType<RedisConnectionException>(updated.Exception);
        }
        finally
        {
            FailIndexBatch.Value = null;
        }
    }

    private string Report()
        => $"started: {string.Join(", ", Started.Select(s => $"{s.Key} at {s.Value:O}"))}\nhandled: {string.Join("\n", Handled)}\n" +
           $"log:\n{string.Join("\n", _log.Where(e => e.Level >= LogLevel.Information && !e.Text.Contains("Microsoft.Hosting")).Select(e => e.Text))}";

    private void AssertNoProblems()
    {
        Assert.True(Handled.IsEmpty, $"the exception handler was called\n{Report()}");
        var problems = _log.Where(e => e.Level >= LogLevel.Warning).Select(e => e.Text).ToList();
        Assert.True(problems.Count == 0, $"the log has entries at Warning or above:\n{string.Join("\n", problems)}");
    }

    // An API moves a job due in two seconds to an hour later; a worker starts four seconds later, after the old time.
    // Its fallback (every second) would take the overdue entry at once, so six seconds cover several passes.
    [Fact]
    public async Task AJobMovedLater_WhoseIndexWriteFails_DoesNotStartAtItsOldTime()
    {
        var api = await StartAsync("write-order-api", backgroundServices: false);
        var id = await AddAsync(api, DateTime.UtcNow.AddSeconds(2));
        await MoveWithFailingIndexWriteAsync(api, id, DateTime.UtcNow.AddHours(1));

        await Task.Delay(TimeSpan.FromSeconds(4));
        await StartAsync("write-order-worker");
        await Task.Delay(TimeSpan.FromSeconds(6));

        Assert.False(Started.ContainsKey(id), $"the job started at its old time\n{Report()}");
        AssertNoProblems();
    }

    // A job due in an hour is moved to two seconds from now; another job is due in twenty seconds, so the loop's window
    // is that one. The moved job starts at its new time, not in an hour.
    [Fact]
    public async Task AJobMovedEarlier_WhoseIndexWriteFails_StartsAtItsNewTime()
    {
        var manager = await StartAsync("write-order-worker");
        var id = await AddAsync(manager, DateTime.UtcNow.AddHours(1));
        await AddAsync(manager, DateTime.UtcNow.AddSeconds(20));

        var due = DateTime.UtcNow.AddSeconds(2);
        await MoveWithFailingIndexWriteAsync(manager, id, due);
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (!Started.ContainsKey(id) && DateTime.UtcNow < deadline)
            await Task.Delay(200);

        Assert.True(Started.TryGetValue(id, out var startedAt), $"the job did not start at its new time\n{Report()}");
        Assert.True(startedAt >= due.AddMilliseconds(-50), $"the job started at {startedAt:O}, before {due:O}\n{Report()}");
        AssertNoProblems();
    }
}
