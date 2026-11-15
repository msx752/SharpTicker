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
/// #151 in an application: a time job of the application's own ticker type with a <c>long</c> of 19 digits, added through
/// the manager and run by the scheduler over a real Redis. It runs once, its number is stored as it was given, and nothing
/// is logged at Warning or above. Up to 1.5.1 the claim rewrote the number in exponent form and the job did not run.
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class RedisOwnNumberEndToEndTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private const string Node = "own-number-node";
    private const string Function = "OwnNumbers.EndToEnd.Time";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(40);

    // How often each job ran, by its id.
    private static readonly ConcurrentDictionary<Guid, int> Runs = new();

    static RedisOwnNumberEndToEndTests()
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
    public async Task AJobWithALongOf19Digits_RunsOnce_AndKeepsItsNumber()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_log);
        builder.Services.AddSharpTicker<NumberedTimeTicker, NumberedCronTicker>(options =>
        {
            options.ConfigureScheduler(scheduler => scheduler.NodeIdentifier = Node);
            options.AddStackExchangeRedis(redis =>
            {
                redis.Configuration = fixture.Container.GetConnectionString();
                redis.JsonSerializerContext = NumberedJsonSerializerContext.Default;
            });
        });
        _host = builder.Build();
        _host.UseSharpTicker();
        await _host.StartAsync();

        var manager = _host.Services.GetRequiredService<ITimeTickerManager<NumberedTimeTicker>>();
        var added = await manager.AddAsync(new NumberedTimeTicker
        {
            Function = Function, ExecutionTime = DateTime.UtcNow.AddSeconds(1), Ticks = long.MaxValue, Ratio = 0.12345678901234559
        });
        Assert.True(added.IsSucceeded, added.Exception?.ToString());
        var id = added.Result.Id;

        var store = _host.Services.GetRequiredService<ITickerPersistenceProvider<NumberedTimeTicker, NumberedCronTicker>>();
        var deadline = DateTime.UtcNow + WaitLimit;
        NumberedTimeTicker? row = null;
        while (DateTime.UtcNow < deadline)
        {
            row = await store.GetTimeTickerById(id, CancellationToken.None);
            if (row?.Status is TickerStatus.Done or TickerStatus.DueDone) break;
            await Task.Delay(200);
        }

        Assert.True(row?.Status is TickerStatus.Done or TickerStatus.DueDone,
            $"status {row?.Status.ToString() ?? "unreadable"}\n{string.Join("\n", _log.Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => e.Text))}");
        Assert.Equal(1, Runs.GetValueOrDefault(id));
        Assert.Equal(long.MaxValue, row!.Ticks);
        Assert.Equal(0.12345678901234559, row.Ratio);
        Assert.Empty(_log.Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => e.Text));
    }
}
