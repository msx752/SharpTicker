using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpTicker.Caching.StackExchangeRedis.DependencyInjection;
using SharpTicker.Dashboard.DependencyInjection;
using SharpTicker.DependencyInjection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>
/// The Machines panel of the dashboard on the Redis store (#31), through the dashboard HTTP API the way the browser reads
/// it: an application with <c>AddStackExchangeRedis</c> and <c>AddDashboard</c> over a real Redis. A job keeps the name of
/// the node that locked it after it ended, so names of nodes that are gone stay in the list. With Redis the nodes write
/// heartbeats (<c>hb:{node}</c>), so the answer says for each name whether its heartbeat is there. The test also fails on
/// a log entry at Warning or above.
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class RedisDashboardLockHoldersEndToEndTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private const string Node = "dashboard-node";
    private const string LivePeer = "peer-with-heartbeat";
    private const string GoneNode = "container-gone";
    private const string Function = "Dashboard.LockHolders.Job";

    // A registered function, as an application has; without one the start logs a warning.
    static RedisDashboardLockHoldersEndToEndTests()
    {
        TickerFunctionProvider.RegisterFunctions(new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>
        {
            [Function] = (string.Empty, TickerTaskPriority.Normal, (_, _, _) => Task.CompletedTask, 0)
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
                => entries.Enqueue((logLevel, $"{logLevel} {category}: {formatter(state, exception)}"));
        }
    }

    private readonly RecordingLoggerProvider _log = new();
    private WebApplication? _app;

    public Task InitializeAsync() => fixture.ResetAsync();

    public async Task DisposeAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        await fixture.ResetAsync();
    }

    private async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_log);
        builder.Services.AddSharpTicker(options =>
        {
            // Nothing runs, so the rows stay as the test writes them.
            options.DisableBackgroundServices();
            options.ConfigureScheduler(scheduler => scheduler.NodeIdentifier = Node);
            options.AddStackExchangeRedis(redis => redis.Configuration = fixture.Container.GetConnectionString());
            options.AddDashboard(dashboard => dashboard.WithNoAuth());
        });

        _app = builder.Build();
        _app.UseSharpTicker();
        await _app.StartAsync();
        return _app;
    }

    private static TimeTickerEntity Locked(TickerStatus status, string holder, DateTime lockedAt) => new()
    {
        Id = Guid.NewGuid(),
        Function = Function,
        Status = status,
        ExecutionTime = lockedAt,
        LockHolder = holder,
        LockedAt = lockedAt,
        CreatedAt = lockedAt,
        UpdatedAt = lockedAt,
    };

    private static DateTime ParseUtc(string text)
        => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    [Fact]
    public async Task MachineJobs_SayWhichNamesHaveAHeartbeat_AndGiveTheirLatestLock()
    {
        var app = await StartAsync();
        var now = DateTime.UtcNow;
        var second = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var monthAgo = second.AddDays(-30);
        var minuteAgo = second.AddMinutes(-1);

        var store = app.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();
        await store.AddTimeTickers(new[]
        {
            Locked(TickerStatus.Done, GoneNode, monthAgo.AddHours(-1)),
            Locked(TickerStatus.Done, GoneNode, monthAgo),
            Locked(TickerStatus.InProgress, LivePeer, minuteAgo),
        });

        // The heartbeat entry another running node writes: the same cache and key as its heartbeat service.
        var cache = app.Services.GetRequiredKeyedService<IDistributedCache>("sharpticker");
        await cache.SetStringAsync($"hb:{LivePeer}", "{\"ts\":0,\"node\":\"" + LivePeer + "\"}",
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });

        using var client = app.GetTestClient();
        var response = await client.GetAsync("/ticker/dashboard/api/ticker/machine/jobs");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        var jobs = JsonDocument.Parse(body).RootElement.EnumerateArray().ToArray();

        Assert.Equal(new[] { LivePeer, GoneNode }, jobs.Select(j => j.GetProperty("item1").GetString()));
        Assert.Equal(new[] { 1, 2 }, jobs.Select(j => j.GetProperty("item2").GetInt32()));
        Assert.True(jobs[0].TryGetProperty("hasHeartbeat", out var live), body);
        Assert.True(live.GetBoolean(), body);
        Assert.False(jobs[1].GetProperty("hasHeartbeat").GetBoolean(), body);
        Assert.Equal(minuteAgo, ParseUtc(jobs[0].GetProperty("lastLockedAt").GetString()!));
        Assert.Equal(monthAgo, ParseUtc(jobs[1].GetProperty("lastLockedAt").GetString()!));

        // What the dashboard page says about an instance that serves only the dashboard: with the background services
        // off it still writes its heartbeat.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (string.IsNullOrEmpty(await cache.GetStringAsync($"hb:{Node}")))
        {
            Assert.True(DateTime.UtcNow < deadline, $"hb:{Node} was not written within 30 s");
            await Task.Delay(100);
        }

        var problems = _log.Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => e.Text).ToArray();
        Assert.True(problems.Length == 0, string.Join(Environment.NewLine, problems));
    }
}
