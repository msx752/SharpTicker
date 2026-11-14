using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpTicker.Caching.StackExchangeRedis.DependencyInjection;
using SharpTicker.DependencyInjection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>
/// An application with ticker types of its own that have collection properties, the way an application uses the library:
/// registered with <c>AddSharpTicker</c> and the Redis store, started as a host, jobs added through the managers, and the
/// scheduler running them over a real Redis. Each test also fails on anything the application would see go wrong: a log
/// entry at Warning or above, or a call to its exception handler.
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class RedisOwnCollectionEndToEndTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private const string Node = "own-collections-node";
    private const string TimeFunction = "OwnCollections.EndToEnd.Time";
    private const string CronFunction = "OwnCollections.EndToEnd.Cron";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(40);

    // How often each job (a time job, or an occurrence of the cron) ran, by its id.
    private static readonly ConcurrentDictionary<Guid, int> Runs = new();

    // What the application's exception handler was given.
    private static readonly ConcurrentQueue<string> Handled = new();

    static RedisOwnCollectionEndToEndTests()
    {
        static Task Count(Guid id)
        {
            Runs.AddOrUpdate(id, 1, (_, n) => n + 1);
            return Task.CompletedTask;
        }

        TickerFunctionProvider.RegisterFunctions(new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>
        {
            [TimeFunction] = (string.Empty, TickerTaskPriority.Normal, (_, _, context) => Count(context.Id), 0),
            [CronFunction] = (string.Empty, TickerTaskPriority.Normal, (_, _, context) => Count(context.Id), 0)
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

    // The application: its own ticker types, the Redis store, its exception handler, and its logging.
    private async Task<IHost> StartAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_log);
        builder.Services.AddSharpTicker<TaggedTimeTicker, TaggedCronTicker>(options =>
        {
            options.ConfigureScheduler(scheduler => scheduler.NodeIdentifier = Node);
            options.SetExceptionHandler<RecordingExceptionHandler>();
            options.AddStackExchangeRedis(redis =>
            {
                redis.Configuration = fixture.Container.GetConnectionString();
                redis.JsonSerializerContext = TaggedJsonSerializerContext.Default;
            });
        });

        _host = builder.Build();
        _host.UseSharpTicker();
        await _host.StartAsync();
        return _host;
    }

    private async Task StopAsync()
    {
        if (_host == null) return;
        await _host.StopAsync();
        _host.Dispose();
        _host = null;
    }

    private static ITickerPersistenceProvider<TaggedTimeTicker, TaggedCronTicker> Store(IHost host)
        => host.Services.GetRequiredService<ITickerPersistenceProvider<TaggedTimeTicker, TaggedCronTicker>>();

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

    // Nothing the application would see as a problem: no log entry at Warning or above, nothing given to its handler.
    private void AssertNothingWentWrong()
    {
        var problems = _log.Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => e.Text).ToList();
        Assert.True(problems.Count == 0, $"the log has entries at Warning or above:\n{string.Join("\n", problems)}");
        Assert.True(Handled.IsEmpty, $"the exception handler was called:\n{string.Join("\n", Handled)}");
    }

    private static async Task<Guid> AddTimeJobAsync(IHost host, bool empty)
    {
        var manager = host.Services.GetRequiredService<ITimeTickerManager<TaggedTimeTicker>>();
        var added = await manager.AddAsync(new TaggedTimeTicker
        {
            Function = TimeFunction, ExecutionTime = DateTime.UtcNow.AddSeconds(1),
            Tags = empty ? [] : ["a"], Groups = empty ? [] : [["g"]]
        });
        Assert.True(added.IsSucceeded, $"the job was not added: {added.Exception}");
        return added.Result.Id;
    }

    private static async Task<Guid> AddCronAsync(IHost host)
    {
        var manager = host.Services.GetRequiredService<ICronTickerManager<TaggedCronTicker>>();
        var added = await manager.AddAsync(new TaggedCronTicker { Function = CronFunction, Expression = "* * * * * *" });
        Assert.True(added.IsSucceeded, $"the cron was not added: {added.Exception}");
        return added.Result.Id;
    }

    private static async Task<TickerStatus?> TimeJobStatusAsync(IHost host, Guid id)
        => (await Store(host).GetTimeTickerById(id, CancellationToken.None))?.Status;

    // A time job added through the manager runs one time and ends Done, whether its lists are empty or filled.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATimeJobOfAnOwnType_RunsOnce_AndEndsDone(bool empty)
    {
        var host = await StartAsync();
        var id = await AddTimeJobAsync(host, empty);

        await WaitUntilAsync(async () => await TimeJobStatusAsync(host, id) == TickerStatus.Done, "the time job ends Done");

        // Long enough for a fallback pass to take it again if it could.
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(1, Runs.GetValueOrDefault(id));
        var stored = await Store(host).GetTimeTickerById(id, CancellationToken.None);
        Assert.Equal(TickerStatus.Done, stored.Status);
        Assert.Equal(empty ? 0 : 1, stored.Tags.Count);
        Assert.Equal(empty ? 0 : 1, stored.Groups.Count);
        await StopAsync();
        AssertNothingWentWrong();
    }

    // A chain: the child is a record of its own with empty lists, and starts once its parent has ended.
    [Fact]
    public async Task AChainOfAnOwnType_RunsTheParentAndThenItsChild_OnceEach()
    {
        var host = await StartAsync();
        var manager = host.Services.GetRequiredService<ITimeTickerManager<TaggedTimeTicker>>();
        var added = await manager.AddAsync(new TaggedTimeTicker
        {
            Function = TimeFunction, ExecutionTime = DateTime.UtcNow.AddSeconds(1),
            Children = [new TaggedTimeTicker { Function = TimeFunction, RunCondition = RunCondition.OnSuccess }]
        });
        Assert.True(added.IsSucceeded, $"the chain was not added: {added.Exception}");
        var parentId = added.Result.Id;
        var childId = Assert.Single(added.Result.Children).Id;

        await WaitUntilAsync(async () => await TimeJobStatusAsync(host, parentId) is TickerStatus.Done or TickerStatus.DueDone
                                         && await TimeJobStatusAsync(host, childId) is TickerStatus.Done or TickerStatus.DueDone,
            "the parent and its child ended");

        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(1, Runs.GetValueOrDefault(parentId));
        Assert.Equal(1, Runs.GetValueOrDefault(childId));
        await StopAsync();
        AssertNothingWentWrong();
    }

    // The occurrences of a cron of an own type run one time each and end Done; none is left InProgress after it ran.
    [Fact]
    public async Task TheOccurrencesOfACronOfAnOwnType_RunOnceEach_AndEndDone()
    {
        var host = await StartAsync();
        var cronId = await AddCronAsync(host);

        async Task<List<TickerStatus>> StatusesOfThoseThatRanAsync()
            => (await Store(host).GetAllCronTickerOccurrences(o => o.CronTickerId == cronId, CancellationToken.None))
                .Where(o => Runs.ContainsKey(o.Id)).Select(o => o.Status).ToList();

        await WaitUntilAsync(async () => (await StatusesOfThoseThatRanAsync()).Count(s => s == TickerStatus.Done) >= 3,
            "three occurrences ran and ended Done");

        // Stopped, so nothing is running any more; the store is read before the host is disposed.
        await host.StopAsync();
        var occurrences = await Store(host).GetAllCronTickerOccurrences(o => o.CronTickerId == cronId, CancellationToken.None);
        var ran = occurrences.Where(o => Runs.ContainsKey(o.Id)).ToList();
        Assert.True(ran.Count >= 3, Report());
        Assert.All(ran, o => Assert.Equal(TickerStatus.Done, o.Status));
        Assert.All(Runs, r => Assert.Equal(1, r.Value));
        AssertNothingWentWrong();
    }

    // The application stops and starts again under the same node name: what ran before does not run again.
    [Fact]
    public async Task AfterARestartUnderTheSameName_NothingThatRanRunsAgain()
    {
        var host = await StartAsync();
        var timeId = await AddTimeJobAsync(host, empty: true);
        var cronId = await AddCronAsync(host);
        await WaitUntilAsync(async () => await TimeJobStatusAsync(host, timeId) == TickerStatus.Done && Runs.Count >= 3,
            "the time job ended Done and two occurrences ran");
        await StopAsync();
        var ranBefore = Runs.Keys.ToList();

        host = await StartAsync();
        // Long enough for the start release and a fallback pass of the new run.
        await Task.Delay(TimeSpan.FromSeconds(5));
        await StopAsync();

        Assert.All(ranBefore, id => Assert.Equal(1, Runs[id]));
        Assert.All(Runs, r => Assert.Equal(1, r.Value));
        Assert.NotEqual(Guid.Empty, cronId);
        AssertNothingWentWrong();
    }
}
