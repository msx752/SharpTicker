using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpTicker.Caching.StackExchangeRedis.DependencyInjection;
using SharpTicker.DependencyInjection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>
/// #158 in an application: a chain child that starts with its parent (<c>RunCondition.InProgress</c>, no time of its own)
/// was running on a node that stopped, and its row was released with that node's rows. The application starts the way an
/// application does (<c>AddSharpTicker</c> with the Redis store, a host, the scheduler over a real Redis); its fallback
/// runs the child once, as <c>DueDone</c>. The test also fails on a log entry at Warning or above or a call to the
/// application's exception handler.
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class RedisReleasedInProgressChildEndToEndTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private const string Node = "released-child-node";
    private const string StoppedNode = "released-child-stopped-node";
    private const string Function = "ReleasedInProgressChild.EndToEnd.Job";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(60);

    // How often each job ran, by its id.
    private static readonly ConcurrentDictionary<Guid, int> Runs = new();

    // What the application's exception handler was given.
    private static readonly ConcurrentQueue<string> Handled = new();

    static RedisReleasedInProgressChildEndToEndTests()
    {
        TickerFunctionProvider.RegisterFunctions(new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>
        {
            [Function] = (string.Empty, TickerTaskPriority.Normal, (_, _, context) =>
            {
                Runs.AddOrUpdate(context.Id, 1, (_, n) => n + 1);
                return Task.CompletedTask;
            }, 0)
        });
        TickerFunctionProvider.Build();
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

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Text)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<(LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue((logLevel, $"{logLevel} {category}: {formatter(state, exception)}{(exception == null ? "" : " | " + exception)}"));
        }
    }

    private readonly RecordingLoggerProvider _log = new();
    private IHost? _host;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        Runs.Clear();
        Handled.Clear();
    }

    public async Task DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        await fixture.ResetAsync();
    }

    [Fact]
    public async Task AChildThatStartedWithItsParent_AndWasReleasedWithItsStoppedNode_RunsOnce()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_log);
        builder.Services.AddSharpTicker(options =>
        {
            options.ConfigureScheduler(scheduler =>
            {
                scheduler.NodeIdentifier = Node;
                // Short, so that the wait below spans several fallback passes.
                scheduler.FallbackIntervalChecker = TimeSpan.FromSeconds(1);
            });
            options.SetExceptionHandler<RecordingExceptionHandler>();
            options.AddStackExchangeRedis(redis => redis.Configuration = fixture.Container.GetConnectionString());
        });
        _host = builder.Build();
        _host.UseSharpTicker();
        var store = _host.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();

        // The parent ran on the stopped node and ended a minute ago; its child was started with it there and was running
        // when that node stopped.
        var now = DateTime.UtcNow;
        var child = new TimeTickerEntity
        {
            Id = Guid.NewGuid(),
            Function = Function,
            RunCondition = RunCondition.InProgress,
            Status = TickerStatus.InProgress,
            LockHolder = StoppedNode,
            LockedAt = now.AddMinutes(-2),
            CreatedAt = now.AddMinutes(-3),
            UpdatedAt = now.AddMinutes(-2)
        };
        var parent = new TimeTickerEntity
        {
            Id = Guid.NewGuid(),
            Function = Function,
            ExecutionTime = now.AddMinutes(-3),
            Status = TickerStatus.Done,
            ExecutedAt = now.AddMinutes(-1),
            CreatedAt = now.AddMinutes(-3),
            UpdatedAt = now.AddMinutes(-1),
            Children = [child]
        };
        await store.AddTimeTickers([parent], CancellationToken.None);

        await _host.StartAsync();
        // What another instance does once the stopped node's heartbeat stayed missing.
        await store.ReleaseDeadNodeTimeTickerResources(StoppedNode, CancellationToken.None);

        var deadline = DateTime.UtcNow + WaitLimit;
        TimeTickerEntity? row = null;
        while (DateTime.UtcNow < deadline)
        {
            row = await store.GetTimeTickerById(child.Id, CancellationToken.None);
            if (row?.Status == TickerStatus.DueDone) break;
            await Task.Delay(200);
        }

        Assert.Equal(TickerStatus.DueDone, row?.Status);
        // A few more fallback passes: it is not run a second time.
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(1, Runs.GetValueOrDefault(child.Id));
        Assert.Equal(0, Runs.GetValueOrDefault(parent.Id));
        Assert.Empty(Handled);
        Assert.Empty(_log.Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => e.Text));
    }
}
