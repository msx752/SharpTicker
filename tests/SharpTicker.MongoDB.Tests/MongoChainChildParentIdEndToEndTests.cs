using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpTicker.DependencyInjection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.MongoDB.DependencyInjection;

namespace SharpTicker.MongoDB.Tests;

/// <summary>
/// The <see cref="TickerFunctionContext.ParentId"/> a chain child's function sees, with the MongoDB store: a started host,
/// a chain added through <see cref="ITimeTickerManager{TTimeTicker}"/>. Each kind of child sees its parent: one that starts
/// with its parent, one without a time that runs after it, one with a time of its own, and a grandchild. The root sees no
/// parent. The test also fails on any log entry at Warning or above.
/// </summary>
[Collection("Mongo")]
public class MongoChainChildParentIdEndToEndTests(MongoTestFixture fixture) : IClassFixture<MongoTestFixture>, IAsyncLifetime
{
    private const string Root = "ChainChildParentId.Mongo.Root";
    private const string WithParent = "ChainChildParentId.Mongo.WithParent";
    private const string AfterParent = "ChainChildParentId.Mongo.AfterParent";
    private const string WithItsOwnTime = "ChainChildParentId.Mongo.WithItsOwnTime";
    private const string Grandchild = "ChainChildParentId.Mongo.Grandchild";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<Guid, Guid?> SeenParent = new();

    static MongoChainChildParentIdEndToEndTests()
    {
        var functions = new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>();
        foreach (var name in new[] { Root, WithParent, AfterParent, WithItsOwnTime, Grandchild })
            functions[name] = (string.Empty, TickerTaskPriority.Normal, (_, _, context) =>
            {
                SeenParent[context.Id] = context.ParentId;
                return Task.CompletedTask;
            }, 0);
        TickerFunctionProvider.RegisterFunctions(functions);
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
                => entries.Enqueue((logLevel, $"{logLevel} {category}: {formatter(state, exception)}{(exception == null ? "" : " | " + exception.GetType().Name + ": " + exception.Message)}"));
        }
    }

    private readonly string _database = $"sharpticker_childparent_{Guid.NewGuid():N}";
    private readonly RecordingLoggerProvider _log = new();
    private IHost? _application;

    public async Task InitializeAsync()
    {
        SeenParent.Clear();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_application != null)
        {
            await _application.StopAsync();
            _application.Dispose();
        }
        await fixture.Client.DropDatabaseAsync(_database);
    }

    private string Report()
        => $"seen: {string.Join(", ", SeenParent.Select(p => $"{p.Key} -> {p.Value?.ToString() ?? "null"}"))}\nlog:\n{string.Join("\n", _log.Entries.Select(e => e.Text))}";

    [Fact]
    public async Task EachChainChild_SeesItsParentAsParentId()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_log);
        builder.Services.AddSharpTicker(options =>
        {
            options.ConfigureScheduler(scheduler => scheduler.NodeIdentifier = "node-a");
            options.AddOperationalStore(mongo => mongo.UseSharpTickerMongoClient(fixture.Container.GetConnectionString(), _database));
        });
        _application = builder.Build();
        _application.UseSharpTicker();
        await _application.StartAsync();

        var manager = _application.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
        var at = DateTime.UtcNow.AddSeconds(1);
        var added = await manager.AddAsync(new TimeTickerEntity
        {
            Function = Root,
            ExecutionTime = at,
            Children =
            {
                new TimeTickerEntity { Function = WithParent, RunCondition = RunCondition.InProgress },
                new TimeTickerEntity
                {
                    Function = AfterParent,
                    RunCondition = RunCondition.OnSuccess,
                    Children = { new TimeTickerEntity { Function = Grandchild, RunCondition = RunCondition.OnSuccess } }
                },
                new TimeTickerEntity { Function = WithItsOwnTime, RunCondition = RunCondition.OnSuccess, ExecutionTime = at.AddSeconds(1) },
            }
        });
        Assert.True(added.IsSucceeded, added.Exception?.Message);
        var root = added.Result;
        var afterParent = root.Children.Single(c => c.Function == AfterParent);

        var deadline = DateTime.UtcNow + WaitLimit;
        while (SeenParent.Count < 5)
        {
            Assert.True(DateTime.UtcNow < deadline, $"not within {WaitLimit}: {SeenParent.Count} of 5 jobs ran\n{Report()}");
            await Task.Delay(100);
        }

        Assert.True(SeenParent[root.Id] == null, Report());
        Assert.True(SeenParent[root.Children.Single(c => c.Function == WithParent).Id] == root.Id, Report());
        Assert.True(SeenParent[afterParent.Id] == root.Id, Report());
        Assert.True(SeenParent[root.Children.Single(c => c.Function == WithItsOwnTime).Id] == root.Id, Report());
        Assert.True(SeenParent[afterParent.Children.Single().Id] == afterParent.Id, Report());
        Assert.True(!_log.Entries.Any(e => e.Level >= LogLevel.Warning), Report());
    }
}
