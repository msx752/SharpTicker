using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpTicker.Caching.StackExchangeRedis.DependencyInjection;
using SharpTicker.DependencyInjection;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace SharpTicker.Caching.StackExchangeRedis.OwnTypes.Tests;

/// <summary>A time job type of the application's own with numbers in it.</summary>
public sealed class NumbersTimeTicker : TimeTickerEntity<NumbersTimeTicker>
{
    public long Big { get; set; }

    public double Ratio { get; set; }

    public string? Exact { get; set; }
}

/// <summary>
/// What the storage guide says about numbers in an application's own ticker type on Redis (#151). Up to 1.5.1 the scripts
/// of the store read a record and wrote it back with the JSON library of Redis, which keeps a number as a double with 14
/// significant digits; since #151 the store writes a record with the serializer that wrote it. These tests pin what an
/// application meets: a started host, jobs added through the manager, a real Redis.
/// </summary>
[Collection(OwnTypesTests.CollectionName)]
public class OwnTypeNumbersTests : IAsyncLifetime
{
    private const string Function = "OwnTypes.Numbers";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(40);

    private static readonly ConcurrentDictionary<Guid, int> Runs = new();

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

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                    entries.Enqueue((logLevel, $"{logLevel} {category}: {formatter(state, exception)}"));
            }
        }
    }

    private readonly RedisContainer _redis = new RedisBuilder("redis:7").Build();
    private readonly RecordingLoggerProvider _log = new();
    private IHost? _host;

    public async Task InitializeAsync()
    {
        Runs.Clear();
        await _redis.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        await _redis.DisposeAsync();
    }

    private async Task<IHost> StartAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(_log);
        builder.Services.MapTicker(Function, (context, _) =>
        {
            Runs.AddOrUpdate(context.Id, 1, (_, n) => n + 1);
            return Task.CompletedTask;
        });
        builder.Services.AddSharpTicker<NumbersTimeTicker, CronTickerEntity>(options =>
        {
            // Short, so the waits below span several fallback passes.
            options.ConfigureScheduler(scheduler => scheduler.FallbackIntervalChecker = TimeSpan.FromSeconds(2));
            options.AddStackExchangeRedis(redis => redis.Configuration = _redis.GetConnectionString());
        });

        _host = builder.Build();
        _host.UseSharpTicker();
        await _host.StartAsync();
        return _host;
    }

    private static ITickerPersistenceProvider<NumbersTimeTicker, CronTickerEntity> Store(IHost host)
        => host.Services.GetRequiredService<ITickerPersistenceProvider<NumbersTimeTicker, CronTickerEntity>>();

    private static async Task<Guid> AddAsync(IHost host, long big = 0, double ratio = 0, string? exact = null)
    {
        var added = await host.Services.GetRequiredService<ITimeTickerManager<NumbersTimeTicker>>()
            .AddAsync(new NumbersTimeTicker { Function = Function, ExecutionTime = DateTime.UtcNow.AddSeconds(2), Big = big, Ratio = ratio, Exact = exact });
        Assert.True(added.IsSucceeded, $"the job was not added: {added.Exception}");
        return added.Result.Id;
    }

    private string Report()
        => $"runs: {string.Join(", ", Runs.Select(r => $"{r.Key}={r.Value}"))}\nlog:\n{string.Join("\n", _log.Entries.Select(e => e.Text))}";

    private async Task WaitUntilEndedAsync(IHost host, Guid id)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while ((await Store(host).GetTimeTickerById(id, CancellationToken.None))?.Status is not (TickerStatus.Done or TickerStatus.DueDone))
        {
            Assert.True(DateTime.UtcNow < deadline, $"not within {WaitLimit}: job {id} ran and ended\n{Report()}");
            await Task.Delay(100);
        }
    }

    // Up to 14 digits a long comes back as it was stored, and the job runs.
    [Theory]
    [InlineData(123L)]
    [InlineData(99_999_999_999_999L)]
    [InlineData(-99_999_999_999_999L)]
    public async Task ALongOfUpTo14Digits_IsKept_AndTheJobRuns(long value)
    {
        var host = await StartAsync();
        var id = await AddAsync(host, big: value);

        await WaitUntilEndedAsync(host, id);

        Assert.Equal(1, Runs.GetValueOrDefault(id));
        Assert.Equal(value, (await Store(host).GetTimeTickerById(id, CancellationToken.None)).Big);
    }

    // From 15 digits on too: the number comes back as it was stored, and the job runs. Up to 1.5.1 the claim wrote it back
    // in exponent form, which is not read as a long, and the job did not run.
    [Theory]
    [InlineData(100_000_000_000_000L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public async Task ALongOf15DigitsOrMore_IsKept_AndTheJobRuns(long value)
    {
        var host = await StartAsync();
        var id = await AddAsync(host, big: value);

        await WaitUntilEndedAsync(host, id);

        Assert.Equal(1, Runs.GetValueOrDefault(id));
        Assert.Equal(value, (await Store(host).GetTimeTickerById(id, CancellationToken.None)).Big);
        Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
    }

    // A record that a version up to 1.5.1 already wrote back in exponent form is not repaired: its digits are lost. It is
    // left out of the reads and named in the log, and a job next to it runs.
    [Fact]
    public async Task ARecordAnEarlierVersionWroteBackInExponentForm_IsNamedInTheLog_AndAJobNextToItRuns()
    {
        var host = await StartAsync();
        var affected = await AddAsync(host, big: 100_000_000_000_000L);
        await using var redis = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        var key = $"tq:tt:{affected}";
        var record = (string?)await redis.GetDatabase().StringGetAsync(key);
        Assert.Contains("\"Big\":100000000000000", record);
        await redis.GetDatabase().StringSetAsync(key, record!.Replace("\"Big\":100000000000000", "\"Big\":1e+14"));
        var other = await AddAsync(host, big: 1);

        await WaitUntilEndedAsync(host, other);
        // Long enough for the scheduler loop and several fallback passes to have met the affected job.
        await Task.Delay(TimeSpan.FromSeconds(8));

        Assert.Equal(1, Runs.GetValueOrDefault(other));
        Assert.Equal(0, Runs.GetValueOrDefault(affected));
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Text.Contains(key));
        Assert.Null(await Store(host).GetTimeTickerById(affected, CancellationToken.None));
        Assert.Contains("\"Big\":1e+14", (string?)await redis.GetDatabase().StringGetAsync(key));
    }

    // A double comes back as it was stored, and the job runs. Up to 1.5.1 it was rounded to 14 significant digits.
    [Fact]
    public async Task ADouble_IsKept_AndTheJobRuns()
    {
        var host = await StartAsync();
        var exact = await AddAsync(host, ratio: 0.5);
        var long1 = await AddAsync(host, ratio: 0.12345678901234559);
        var long2 = await AddAsync(host, ratio: 1234567890.1234567);

        foreach (var id in new[] { exact, long1, long2 })
            await WaitUntilEndedAsync(host, id);

        Assert.All(new[] { exact, long1, long2 }, id => Assert.Equal(1, Runs.GetValueOrDefault(id)));
        Assert.Equal(0.5, (await Store(host).GetTimeTickerById(exact, CancellationToken.None)).Ratio);
        Assert.Equal(0.12345678901234559, (await Store(host).GetTimeTickerById(long1, CancellationToken.None)).Ratio);
        Assert.Equal(1234567890.1234567, (await Store(host).GetTimeTickerById(long2, CancellationToken.None)).Ratio);
    }
}
