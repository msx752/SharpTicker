using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpTicker.Caching.StackExchangeRedis.DependencyInjection;
using SharpTicker.Caching.StackExchangeRedis.Helpers;
using SharpTicker.DependencyInjection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>
/// An application whose own ticker type has a property the serializer writes and refuses to read back, the way an
/// application uses the library: registered with <c>AddSharpTicker</c> and the Redis store, started as a host, jobs added
/// through the manager, and the scheduler running over a real Redis. One job holds such a value; the tests are about the
/// other jobs of the application. Each test also fails on anything else the application would see go wrong: a log entry
/// at Warning or above that is not about that one row, or a call to its exception handler.
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class RedisRowOfUnsupportedTypeEndToEndTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private const string Node = "unsupported-type-node";
    private const string Function = "UnsupportedType.EndToEnd.Job";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(60);

    // How often each job ran, by its id.
    private static readonly ConcurrentDictionary<Guid, int> Runs = new();

    // What the application's exception handler was given.
    private static readonly ConcurrentQueue<string> Handled = new();

    static RedisRowOfUnsupportedTypeEndToEndTests()
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
        await StopAsync();
        await fixture.ResetAsync();
    }

    // The application: its own ticker type, the Redis store, its exception handler, and its logging.
    private IHost Build()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_log);
        builder.Services.AddSharpTicker<ShapedTimeTicker, CronTickerEntity>(options =>
        {
            options.ConfigureScheduler(scheduler =>
            {
                scheduler.NodeIdentifier = Node;
                // Short, so the waits of the tests span several fallback passes.
                scheduler.FallbackIntervalChecker = TimeSpan.FromSeconds(1);
            });
            options.SetExceptionHandler<RecordingExceptionHandler>();
            options.AddStackExchangeRedis(redis =>
            {
                redis.Configuration = fixture.Container.GetConnectionString();
                redis.JsonSerializerContext = ShapedJsonSerializerContext.Default;
            });
        });

        _host = builder.Build();
        _host.UseSharpTicker();
        return _host;
    }

    private async Task StopAsync()
    {
        if (_host == null) return;
        await _host.StopAsync();
        _host.Dispose();
        _host = null;
    }

    private static ITickerPersistenceProvider<ShapedTimeTicker, CronTickerEntity> Store(IHost host)
        => host.Services.GetRequiredService<ITickerPersistenceProvider<ShapedTimeTicker, CronTickerEntity>>();

    private string Report()
        => $"runs: {string.Join(", ", Runs.Select(r => $"{r.Key}={r.Value}"))}\nhandled: {string.Join("\n", Handled)}\nlog:\n{string.Join("\n", _log.Entries.Select(e => e.Text))}";

    private async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"not within {WaitLimit}: {what}\n{Report()}");
            await Task.Delay(100);
        }
    }

    // The only thing the application sees: warnings that name the row that does not read. Nothing above Warning, no
    // warning about anything else, nothing given to its handler.
    private void AssertOnlyThisRowIsReported(Guid unreadable)
    {
        var problems = _log.Entries.Where(e => e.Level >= LogLevel.Warning).ToList();
        var others = problems
            .Where(e => e.Level != LogLevel.Warning || !e.Text.Contains(RedisKeyBuilder.TimeTickerKey(unreadable)))
            .Select(e => e.Text).ToList();
        Assert.True(others.Count == 0, $"the log has entries at Warning or above that are not about the row that does not read:\n{string.Join("\n", others)}");
        Assert.NotEmpty(problems);
        Assert.True(Handled.IsEmpty, $"the exception handler was called:\n{string.Join("\n", Handled)}");
    }

    private static ShapedTimeTicker Job(bool withShape, DateTime executionTime)
        => new() { Function = Function, ExecutionTime = executionTime, Shape = withShape ? new Circle() : null };

    private static async Task<Guid> AddAsync(IHost host, bool withShape, DateTime executionTime)
    {
        var manager = host.Services.GetRequiredService<ITimeTickerManager<ShapedTimeTicker>>();
        var added = await manager.AddAsync(Job(withShape, executionTime));
        Assert.True(added.IsSucceeded, $"the job was not added: {added.Exception}");
        return added.Result.Id;
    }

    private static async Task<TickerStatus?> StatusAsync(IHost host, Guid id)
        => (await Store(host).GetTimeTickerById(id, CancellationToken.None))?.Status;

    // Jobs added to a running application: the ones around the job that does not read run one time each and end, and
    // the listing gives them.
    [Fact]
    public async Task JobsAddedNextToOneThatDoesNotRead_RunOnceEach_AndAreListed()
    {
        var host = Build();
        await host.StartAsync();
        var unreadable = await AddAsync(host, withShape: true, DateTime.UtcNow.AddSeconds(2));
        var before = await AddAsync(host, withShape: false, DateTime.UtcNow.AddSeconds(1));
        var same = await AddAsync(host, withShape: false, DateTime.UtcNow.AddSeconds(2));
        var after = await AddAsync(host, withShape: false, DateTime.UtcNow.AddSeconds(4));
        Guid[] readable = [before, same, after];

        await WaitUntilAsync(async () =>
        {
            foreach (var id in readable)
                if (await StatusAsync(host, id) is not (TickerStatus.Done or TickerStatus.DueDone)) return false;
            return true;
        }, "the three jobs that read ended");

        // Long enough for fallback passes to meet the job that does not read, and to take another job again if they could.
        await Task.Delay(TimeSpan.FromSeconds(3));
        var listed = await Store(host).GetTimeTickers(null!, CancellationToken.None);
        await StopAsync();

        Assert.All(readable, id => Assert.Equal(1, Runs.GetValueOrDefault(id)));
        Assert.False(Runs.ContainsKey(unreadable), Report());
        Assert.Equal(readable.Order(), listed.Select(x => x.Id).Order());
        AssertOnlyThisRowIsReported(unreadable);
    }

    // Jobs that became overdue while the application was stopped, one of them the job that does not read: after the
    // start the fallback runs the others one time each.
    [Fact]
    public async Task OverdueJobsNextToOneThatDoesNotRead_RunOnceEach_AfterTheApplicationStarts()
    {
        var host = Build();
        var overdue = DateTime.UtcNow.AddMinutes(-5);
        var jobs = new[] { Job(withShape: true, overdue), Job(withShape: false, overdue.AddSeconds(1)), Job(withShape: false, overdue.AddSeconds(2)) };
        foreach (var job in jobs)
        {
            job.Id = Guid.NewGuid();
            job.CreatedAt = job.UpdatedAt = overdue.AddMinutes(-1);
        }
        await Store(host).AddTimeTickers(jobs, CancellationToken.None);
        var unreadable = jobs[0].Id;
        Guid[] readable = [jobs[1].Id, jobs[2].Id];

        await host.StartAsync();
        await WaitUntilAsync(async () =>
        {
            foreach (var id in readable)
                if (await StatusAsync(host, id) is not (TickerStatus.Done or TickerStatus.DueDone)) return false;
            return true;
        }, "the two overdue jobs that read ended");

        await Task.Delay(TimeSpan.FromSeconds(3));
        await StopAsync();

        Assert.All(readable, id => Assert.Equal(1, Runs.GetValueOrDefault(id)));
        Assert.False(Runs.ContainsKey(unreadable), Report());
        AssertOnlyThisRowIsReported(unreadable);
    }
}
