using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpTicker.DependencyInjection;
using SharpTicker.EntityFrameworkCore.Customizer;
using SharpTicker.EntityFrameworkCore.DependencyInjection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces.Managers;

namespace SharpTicker.EntityFrameworkCore.Tests.DependencyInjection;

public class ChainChildParentIdDbContext(DbContextOptions<ChainChildParentIdDbContext> options) : DbContext(options);

/// <summary>
/// The <see cref="TickerFunctionContext.ParentId"/> a chain child's function sees, with the EF Core store on SQLite: a
/// started host, a chain added through <see cref="ITimeTickerManager{TTimeTicker}"/>. Each kind of child sees its parent:
/// one that starts with its parent, one without a time that runs after it, one with a time of its own, and a grandchild.
/// The root sees no parent. The test also fails on any log entry at Warning or above.
/// </summary>
public class ChainChildParentIdEndToEndTests : IAsyncLifetime
{
    private const string Root = "ChainChildParentId.Ef.Root";
    private const string WithParent = "ChainChildParentId.Ef.WithParent";
    private const string AfterParent = "ChainChildParentId.Ef.AfterParent";
    private const string WithItsOwnTime = "ChainChildParentId.Ef.WithItsOwnTime";
    private const string Grandchild = "ChainChildParentId.Ef.Grandchild";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<Guid, Guid?> SeenParent = new();

    static ChainChildParentIdEndToEndTests()
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
        public ConcurrentQueue<(LogLevel Level, string Text, string Category, int EventId)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<(LogLevel, string, string, int)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue((logLevel, $"{logLevel} {category}: {formatter(state, exception)}{(exception == null ? "" : " | " + exception.GetType().Name + ": " + exception.Message)}", category, eventId.Id));
        }
    }

    private readonly string _databaseFile = Path.Combine(Path.GetTempPath(), $"sharpticker-childparent-{Guid.NewGuid():N}.db");
    private readonly RecordingLoggerProvider _log = new();
    private IHost? _application;

    public Task InitializeAsync()
    {
        SeenParent.Clear();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_application != null)
        {
            await _application.StopAsync();
            _application.Dispose();
        }

        SqliteConnection.ClearAllPools();
        if (File.Exists(_databaseFile))
            File.Delete(_databaseFile);
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
        // An EF Core warning that is not about this case is turned off as an application would: SQLite has no schemas.
        builder.Services.AddDbContext<ChainChildParentIdDbContext>(options => options.UseSqlite($"Data Source={_databaseFile}")
            .ConfigureWarnings(w => w.Ignore(SqliteEventId.SchemaConfiguredWarning)));
        builder.Services.AddSharpTicker(options =>
        {
            options.ConfigureScheduler(scheduler => scheduler.NodeIdentifier = "node-a");
            options.AddOperationalStore(ef => ef.UseApplicationDbContext<ChainChildParentIdDbContext>(ConfigurationType.UseModelCustomizer));
        });
        _application = builder.Build();
        using (var scope = _application.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<ChainChildParentIdDbContext>().Database.EnsureCreated();
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
        // EF Core 8 and 9 log a connection open that a loop restart cancels as ConnectionError (20004) at Error; it is a
        // documented limit (Tracing and logs), the same entry SqlServerEndToEndTests leaves out. EF Core 10 logs it at Debug.
        Assert.True(!_log.Entries.Any(e => e.Level >= LogLevel.Warning
            && !(e.Category == "Microsoft.EntityFrameworkCore.Database.Connection" && e.EventId == 20004)), Report());
    }
}
