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
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Models;

namespace SharpTicker.EntityFrameworkCore.Tests.DependencyInjection;

public class LifecycleSkippedChildDbContext(DbContextOptions<LifecycleSkippedChildDbContext> options) : DbContext(options);

/// <summary>
/// The lifecycle handler (#1) with the EF Core store on SQLite: a chain whose root fails. Its child that runs only on
/// success, and that child's own child, are skipped by the root's outcome; each is reported once to OnCompletedAsync as
/// Skipped, with its parent, and never as starting. The child that runs on failure runs and is reported as done. The EF Core
/// store skips children one by one only when a handler is registered, which this test needs to see them. It also fails on
/// any log entry at Warning or above other than the root's failure and EF Core's documented cancelled-open entry.
/// </summary>
public class LifecycleSkippedChildEndToEndTests : IAsyncLifetime
{
    private const string Root = "LifecycleSkippedChild.Ef.Root";
    private const string WithParent = "LifecycleSkippedChild.Ef.WithParent";
    private const string AfterParent = "LifecycleSkippedChild.Ef.AfterParent";
    private const string WithItsOwnTime = "LifecycleSkippedChild.Ef.WithItsOwnTime";
    private const string Grandchild = "LifecycleSkippedChild.Ef.Grandchild";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<Guid, Guid?> SeenParent = new();

    static LifecycleSkippedChildEndToEndTests()
    {
        var functions = new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>();
        foreach (var name in new[] { Root, WithParent, AfterParent, WithItsOwnTime, Grandchild })
            functions[name] = (string.Empty, TickerTaskPriority.Normal, (_, _, context) =>
            {
                SeenParent[context.Id] = context.ParentId;
                return name == Root ? throw new InvalidOperationException("LifecycleSkippedChild: the root failed") : Task.CompletedTask;
            }, 0);
        TickerFunctionProvider.RegisterFunctions(functions);
        TickerFunctionProvider.Build();
    }

    private static readonly ConcurrentQueue<(string Kind, TickerLifecycleContext Context)> Calls = new();

    private sealed class RecordingLifecycleHandler : ITickerLifecycleHandler
    {
        public Task OnStartingAsync(TickerLifecycleContext context, CancellationToken cancellationToken)
        {
            Calls.Enqueue(("starting", context));
            return Task.CompletedTask;
        }

        public Task OnCompletedAsync(TickerLifecycleContext context, CancellationToken cancellationToken)
        {
            Calls.Enqueue(("completed", context));
            return Task.CompletedTask;
        }
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

    private readonly string _databaseFile = Path.Combine(Path.GetTempPath(), $"sharpticker-lifecycle-skip-{Guid.NewGuid():N}.db");
    private readonly RecordingLoggerProvider _log = new();
    private IHost? _application;

    public Task InitializeAsync()
    {
        SeenParent.Clear();
        Calls.Clear();
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

    private sealed record Call(string Kind, TickerLifecycleContext Context);

    private string Report()
        => $"seen: {string.Join(", ", SeenParent.Select(p => $"{p.Key} -> {p.Value?.ToString() ?? "null"}"))}\nlog:\n{string.Join("\n", _log.Entries.Select(e => e.Text))}\ncalls:\n{string.Join("\n", Calls.Select(c => $"{c.Kind} {c.Context.FunctionName} {c.Context.Id} {c.Context.Status}"))}";

    [Fact]
    public async Task ChildrenThatTheRootsFailureSkips_AreReportedAsSkipped()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_log);
        // An EF Core warning that is not about this case is turned off as an application would: SQLite has no schemas.
        builder.Services.AddDbContext<LifecycleSkippedChildDbContext>(options => options.UseSqlite($"Data Source={_databaseFile}")
            .ConfigureWarnings(w => w.Ignore(SqliteEventId.SchemaConfiguredWarning)));
        builder.Services.AddSharpTicker(options =>
        {
            options.ConfigureScheduler(scheduler => scheduler.NodeIdentifier = "node-a");
            options.SetLifecycleHandler<RecordingLifecycleHandler>();
            options.AddOperationalStore(ef => ef.UseApplicationDbContext<LifecycleSkippedChildDbContext>(ConfigurationType.UseModelCustomizer));
        });
        _application = builder.Build();
        using (var scope = _application.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<LifecycleSkippedChildDbContext>().Database.EnsureCreated();
        _application.UseSharpTicker();
        await _application.StartAsync();

        var manager = _application.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
        var added = await manager.AddAsync(new TimeTickerEntity
        {
            Function = Root,
            ExecutionTime = DateTime.UtcNow.AddSeconds(1),
            Children =
            {
                new TimeTickerEntity
                {
                    Function = AfterParent,
                    RunCondition = RunCondition.OnSuccess,
                    Children = { new TimeTickerEntity { Function = Grandchild, RunCondition = RunCondition.OnSuccess } }
                },
                new TimeTickerEntity { Function = WithParent, RunCondition = RunCondition.OnFailure },
            }
        });
        Assert.True(added.IsSucceeded, added.Exception?.Message);
        var root = added.Result;
        var skippedChild = root.Children.Single(c => c.Function == AfterParent);
        var grandchild = skippedChild.Children.Single();
        var ranChild = root.Children.Single(c => c.Function == WithParent);

        Call[] Of(Guid id, string kind) => Calls.Where(c => c.Context.Id == id && c.Kind == kind).Select(c => new Call(c.Kind, c.Context)).ToArray();
        var deadline = DateTime.UtcNow + WaitLimit;
        while (Of(skippedChild.Id, "completed").Length == 0 || Of(grandchild.Id, "completed").Length == 0 || Of(ranChild.Id, "completed").Length == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, $"not within {WaitLimit}: the children were not reported{Environment.NewLine}{Report()}");
            await Task.Delay(100);
        }
        // A late or second call would show up within this time.
        await Task.Delay(1500);

        foreach (var (id, parentId) in new[] { (skippedChild.Id, root.Id), (grandchild.Id, skippedChild.Id) })
        {
            var completed = Assert.Single(Of(id, "completed")).Context;
            Assert.True(completed.Status == TickerStatus.Skipped, Report());
            Assert.Equal(parentId, completed.ParentId);
            Assert.Empty(Of(id, "starting"));
        }

        Assert.True(Assert.Single(Of(ranChild.Id, "completed")).Context.Status is TickerStatus.Done or TickerStatus.DueDone, Report());
        Assert.Equal(TickerStatus.Failed, Assert.Single(Of(root.Id, "completed")).Context.Status);
        Assert.True(!_log.Entries.Any(e => e.Level >= LogLevel.Warning
            && !e.Text.Contains(root.Id.ToString()) && !e.Text.Contains("the root failed")
            && !(e.Category == "Microsoft.EntityFrameworkCore.Database.Connection" && e.EventId == 20004)), Report());
    }
}
