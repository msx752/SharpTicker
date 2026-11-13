using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpTicker.Caching.StackExchangeRedis.DependencyInjection;
using SharpTicker.DependencyInjection;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using Testcontainers.Redis;

namespace SharpTicker.Caching.StackExchangeRedis.OwnTypes.Tests;

/// <summary>A time job type of the application's own, with properties the packages do not know.</summary>
public sealed class OwnTimeTicker : TimeTickerEntity<OwnTimeTicker>
{
    public string? Team { get; set; }

    public List<string> Labels { get; set; } = [];
}

/// <summary>A cron type of the application's own.</summary>
public sealed class OwnCronTicker : CronTickerEntity
{
    public string? Team { get; set; }

    public List<string> Labels { get; set; } = [];
}

/// <summary>
/// An application with ticker types of its own on the Redis store that sets no <c>JsonSerializerContext</c> (#162):
/// registered with <c>AddSharpTicker</c>, started as a host, jobs added through the managers, the scheduler running them
/// over a real Redis. This assembly is not named in an <c>InternalsVisibleTo</c> of the packages, as no application is.
/// Each test also fails on anything the application would see go wrong: a log entry at Warning or above, or a call to its
/// exception handler.
/// </summary>
public class OwnTypesWithoutAContextTests : IAsyncLifetime
{
    private const string TimeFunction = "OwnTypes.NoContext.Time";
    private const string CronFunction = "OwnTypes.NoContext.Cron";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(40);

    // How often each job (a time job, or an occurrence of the cron) ran, by its id.
    private static readonly ConcurrentDictionary<Guid, int> Runs = new();

    // What the application's exception handler was given.
    private static readonly ConcurrentQueue<string> Handled = new();

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

    private readonly RedisContainer _redis = new RedisBuilder("redis:7").Build();
    private readonly RecordingLoggerProvider _log = new();
    private IHost? _host;

    public async Task InitializeAsync()
    {
        Runs.Clear();
        Handled.Clear();
        await _redis.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await StopAsync();
        await _redis.DisposeAsync();
    }

    // The application: its own ticker types, the Redis store with nothing but the connection set, its exception handler
    // and its logging.
    private async Task<IHost> StartAsync()
    {
        static Task Count(Guid id)
        {
            Runs.AddOrUpdate(id, 1, (_, n) => n + 1);
            return Task.CompletedTask;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_log);
        builder.Services.MapTicker(TimeFunction, (context, _) => Count(context.Id));
        builder.Services.MapTicker(CronFunction, (context, _) => Count(context.Id));
        builder.Services.AddSharpTicker<OwnTimeTicker, OwnCronTicker>(options =>
        {
            options.SetExceptionHandler<RecordingExceptionHandler>();
            options.AddStackExchangeRedis(redis => redis.Configuration = _redis.GetConnectionString());
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

    private static ITickerPersistenceProvider<OwnTimeTicker, OwnCronTicker> Store(IHost host)
        => host.Services.GetRequiredService<ITickerPersistenceProvider<OwnTimeTicker, OwnCronTicker>>();

    private string Report()
        => $"runs: {string.Join(", ", Runs.Select(r => $"{r.Key}={r.Value}"))}\nhandled: {string.Join("\n", Handled)}\nlog:\n{string.Join("\n", _log.Entries.Select(e => e.Text))}";

    private Task WaitUntilAsync(Func<Task<bool>> condition, string what) => WaitUntilAsync(condition, () => what);

    private async Task WaitUntilAsync(Func<Task<bool>> condition, Func<string> what)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"not within {WaitLimit}: {what()}\n{Report()}");
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

    // The tests below mean something only while this assembly sees the packages as an application does.
    [Fact]
    public void ThisAssembly_IsNotNamedInAnInternalsVisibleToOfThePackages()
    {
        var self = typeof(OwnTypesWithoutAContextTests).Assembly.GetName().Name!;
        var packages = new[]
        {
            typeof(TimeTickerEntity).Assembly,
            typeof(SharpTickerServiceExtensions).Assembly,
            typeof(ServiceExtension).Assembly
        };

        Assert.All(packages, package => Assert.DoesNotContain(
            package.GetCustomAttributes(typeof(InternalsVisibleToAttribute), false).Cast<InternalsVisibleToAttribute>(),
            attribute => attribute.AssemblyName.Split(',')[0].Trim() == self));
    }

    // A time job added through the manager is stored, runs one time and ends, and its own properties are read back.
    [Fact]
    public async Task ATimeJobOfAnOwnType_IsAdded_RunsOnce_AndEnds()
    {
        var host = await StartAsync();
        var manager = host.Services.GetRequiredService<ITimeTickerManager<OwnTimeTicker>>();

        var added = await manager.AddAsync(new OwnTimeTicker
        {
            Function = TimeFunction, ExecutionTime = DateTime.UtcNow.AddSeconds(2), Team = "billing", Labels = ["a", "b"]
        });

        Assert.True(added.IsSucceeded, $"the job was not added: {added.Exception}\n{Report()}");
        var id = added.Result.Id;
        OwnTimeTicker? row = null;
        await WaitUntilAsync(async () =>
        {
            row = await Store(host).GetTimeTickerById(id, CancellationToken.None);
            // DueDone: the job was started late (by the fallback); it ran and ended all the same.
            return row?.Status is TickerStatus.Done or TickerStatus.DueDone;
        }, () => $"the time job ran and ended (stored:status {row?.Status}, holder {row?.LockHolder}, locked at {row?.LockedAt:o}, executed at {row?.ExecutedAt:o}, execution time {row?.ExecutionTime:o})");

        // Long enough for a fallback pass to take it again if it could.
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(1, Runs.GetValueOrDefault(id));
        var stored = await Store(host).GetTimeTickerById(id, CancellationToken.None);
        Assert.True(stored.Status is TickerStatus.Done or TickerStatus.DueDone, $"stored status: {stored.Status}");
        Assert.Equal("billing", stored.Team);
        Assert.Equal(["a", "b"], stored.Labels);
        await StopAsync();
        AssertNothingWentWrong();
    }

    // A cron added through the manager is stored and its occurrences run and end Done.
    [Fact]
    public async Task ACronOfAnOwnType_IsAdded_AndItsOccurrencesRunAndEndDone()
    {
        var host = await StartAsync();
        var manager = host.Services.GetRequiredService<ICronTickerManager<OwnCronTicker>>();

        var added = await manager.AddAsync(new OwnCronTicker
        {
            Function = CronFunction, Expression = "* * * * * *", Team = "billing", Labels = ["a"]
        });

        Assert.True(added.IsSucceeded, $"the cron was not added: {added.Exception}\n{Report()}");
        var cronId = added.Result.Id;
        CronTickerOccurrenceEntity<OwnCronTicker>[] done = [];
        await WaitUntilAsync(async () =>
        {
            done = (await Store(host).GetAllCronTickerOccurrences(o => o.CronTickerId == cronId, CancellationToken.None))
                .Where(o => o.Status == TickerStatus.Done).ToArray();
            return done.Length >= 2;
        }, "two occurrences of the cron end Done");

        Assert.All(done, occurrence => Assert.Equal(1, Runs.GetValueOrDefault(occurrence.Id)));
        var stored = Assert.Single(await Store(host).GetCronTickers(c => c.Id == cronId, CancellationToken.None));
        Assert.Equal("billing", stored.Team);
        Assert.Equal(["a"], stored.Labels);
        await StopAsync();
        AssertNothingWentWrong();
    }
}
