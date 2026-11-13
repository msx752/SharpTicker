using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpTicker.Caching.StackExchangeRedis.DependencyInjection;
using SharpTicker.Caching.StackExchangeRedis.Helpers;
using SharpTicker.DependencyInjection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>
/// An application that is upgraded with chains in its Redis that a version before 1.3.0 stored (the children held inside
/// the parent's row). The records are written as the raw text such a version stored, then the application starts the way
/// an application does: <c>AddSharpTicker</c> with the Redis store, a host, the scheduler running over a real Redis. Each
/// test also fails on anything the application would see go wrong: a log entry at Warning or above, or a call to its
/// exception handler.
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class RedisChainHeldInParentEndToEndTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private const string Node = "chain-held-in-parent-node";
    private const string Function = "ChainHeldInParent.EndToEnd.Job";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(60);

    // How often each job ran, by its id.
    private static readonly ConcurrentDictionary<Guid, int> Runs = new();

    // What the application's exception handler was given.
    private static readonly ConcurrentQueue<string> Handled = new();

    static RedisChainHeldInParentEndToEndTests()
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

    // The application: the library's own ticker types, the Redis store, its exception handler, and its logging.
    private async Task StartAsync()
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
                // Short, so that the waits below span several fallback passes.
                scheduler.FallbackIntervalChecker = TimeSpan.FromSeconds(1);
            });
            options.SetExceptionHandler<RecordingExceptionHandler>();
            options.AddStackExchangeRedis(redis => redis.Configuration = fixture.Container.GetConnectionString());
        });

        _host = builder.Build();
        _host.UseSharpTicker();
        await _host.StartAsync();
    }

    private async Task StopAsync()
    {
        if (_host == null) return;
        await _host.StopAsync();
        _host.Dispose();
        _host = null;
    }

    #region The records as a version before 1.3.0 stored them

    // parent -> child0 (OnSuccess) -> grand (OnSuccess); child1 (OnFailure); delayed (OnSuccess, a time of its own)
    private sealed record Chain(Guid Parent, Guid Child0, Guid Grand, Guid Child1, Guid Delayed)
    {
        public Guid[] RunAfterASuccess => [Parent, Child0, Grand, Delayed];

        public Guid[] All => [Parent, Child0, Grand, Child1, Delayed];
    }

    private static string Q(DateTime time) => "\"" + time.ToString("O") + "\"";

    private static string NodeText(Guid id, string executionTime, int? runCondition, string children, int status = 0, string executedAt = "null")
        => "{\"Status\":" + status + ",\"LockHolder\":null,\"Request\":\"\",\"ExecutionTime\":" + executionTime
           + ",\"LockedAt\":null,\"ExecutedAt\":" + executedAt + ",\"ExceptionMessage\":null,\"SkippedReason\":null,\"ElapsedTime\":0,"
           + "\"Retries\":0,\"RetryCount\":0,\"RetryIntervals\":null,\"ParentId\":null,\"Children\":" + children
           + ",\"RunCondition\":" + (runCondition?.ToString() ?? "null") + ",\"Id\":\"" + id + "\",\"Function\":\"" + Function + "\","
           + "\"Description\":null,\"InitIdentifier\":null,\"CreatedAt\":" + Q(DateTime.UtcNow.AddHours(-1)) + ",\"UpdatedAt\":" + Q(DateTime.UtcNow.AddHours(-1)) + "}";

    private async Task<Chain> SeedAsync(DateTime parentTime, DateTime delayedTime, int parentStatus = 0, string parentExecutedAt = "null")
    {
        var chain = new Chain(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var grand = NodeText(chain.Grand, "null", (int)RunCondition.OnSuccess, "[]");
        var child0 = NodeText(chain.Child0, "null", (int)RunCondition.OnSuccess, "[" + grand + "]");
        var child1 = NodeText(chain.Child1, "null", (int)RunCondition.OnFailure, "[]");
        var delayed = NodeText(chain.Delayed, Q(delayedTime), (int)RunCondition.OnSuccess, "[]");
        var parent = NodeText(chain.Parent, Q(parentTime), null, "[" + child0 + "," + child1 + "," + delayed + "]", parentStatus, parentExecutedAt);

        var db = fixture.Database;
        await db.StringSetAsync(RedisKeyBuilder.TimeTickerKey(chain.Parent), parent);
        await db.SetAddAsync(RedisKeyBuilder.TimeTickerIdsKey, chain.Parent.ToString());
        if (parentStatus == (int)TickerStatus.Idle)
            await db.SortedSetAddAsync(RedisKeyBuilder.TimeTickerPendingKey, chain.Parent.ToString(), RedisKeyBuilder.ToScore(parentTime));
        return chain;
    }

    private async Task<string?> RawAsync(Guid id) => (string?)await fixture.Database.StringGetAsync(RedisKeyBuilder.TimeTickerKey(id));

    // Null while the node has no row of its own.
    private async Task<TickerStatus?> StatusAsync(Guid id)
    {
        var raw = await RawAsync(id);
        if (raw == null) return null;
        using var doc = JsonDocument.Parse(raw);
        return (TickerStatus)doc.RootElement.GetProperty("Status").GetInt32();
    }

    private async Task<bool> RanOutAsync(Chain chain)
    {
        foreach (var id in chain.RunAfterASuccess)
            if (await StatusAsync(id) is not (TickerStatus.Done or TickerStatus.DueDone)) return false;
        return await StatusAsync(chain.Child1) == TickerStatus.Skipped;
    }

    #endregion

    private async Task<string> ReportAsync(params Chain[] chains)
    {
        var statuses = new List<string>();
        foreach (var chain in chains)
        foreach (var id in chain.All)
            statuses.Add($"{id}={(await StatusAsync(id))?.ToString() ?? "no row"}");
        return $"statuses: {string.Join(", ", statuses)}\nruns: {string.Join(", ", Runs.Select(r => $"{r.Key}={r.Value}"))}\n"
               + $"handled: {string.Join("\n", Handled)}\nlog:\n{string.Join("\n", _log.Entries.Select(e => e.Text))}";
    }

    private async Task WaitUntilAsync(Func<Task<bool>> condition, string what, params Chain[] chains)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"not within {WaitLimit}: {what}\n{await ReportAsync(chains)}");
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

    private static void AssertRanOnceEachByItsCondition(Chain chain)
    {
        Assert.All(chain.RunAfterASuccess, id => Assert.Equal(1, Runs.GetValueOrDefault(id)));
        Assert.False(Runs.ContainsKey(chain.Child1), "the child that waits for a failure ran after a parent that succeeded");
    }

    // The upgrade: one chain that is overdue when the application starts, one that becomes due a few seconds later, and
    // one whose parent ended on the older version. The first two run each node once, by its condition; the ended one runs
    // nothing; and a restart runs nothing again. Which claim path takes which chain is not pinned here (the store-level
    // tests pin each path).
    [Fact]
    public async Task ChainsStoredByAnOlderVersion_RunEachNodeOnceByItsCondition_AndAnEndedOneRunsNothing()
    {
        var now = DateTime.UtcNow;
        var overdue = await SeedAsync(now.AddMinutes(-1), delayedTime: now.AddSeconds(-30));
        var dueSoon = await SeedAsync(now.AddSeconds(6), delayedTime: now.AddSeconds(9));
        var ended = await SeedAsync(now.AddHours(-2), delayedTime: now.AddHours(-1), parentStatus: (int)TickerStatus.Done, parentExecutedAt: Q(now.AddHours(-2)));
        var endedBefore = await RawAsync(ended.Parent);

        await StartAsync();
        await WaitUntilAsync(async () => await RanOutAsync(overdue) && await RanOutAsync(dueSoon),
            "both chains ran each node by its condition", overdue, dueSoon);

        // Several fallback passes: long enough for one to take a node again if it could.
        await Task.Delay(TimeSpan.FromSeconds(4));
        await StopAsync();

        AssertRanOnceEachByItsCondition(overdue);
        AssertRanOnceEachByItsCondition(dueSoon);
        Assert.All(ended.All, id => Assert.False(Runs.ContainsKey(id), "a node of the chain that ended on the older version ran"));
        Assert.Equal(endedBefore, await RawAsync(ended.Parent));
        Assert.All(ended.All.Skip(1), id => Assert.Null(RawAsync(id).Result));
        AssertNothingWentWrong();

        // The application starts again under the same name: nothing that ran runs again, the ended chain still runs nothing.
        var ranBefore = Runs.ToDictionary(r => r.Key, r => r.Value);
        await StartAsync();
        // Long enough for the start release and several fallback passes of the new run.
        await Task.Delay(TimeSpan.FromSeconds(5));
        await StopAsync();

        Assert.Equal(ranBefore.OrderBy(r => r.Key), Runs.OrderBy(r => r.Key));
        Assert.Equal(endedBefore, await RawAsync(ended.Parent));
        AssertNothingWentWrong();
    }
}
