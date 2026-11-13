using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpTicker.DependencyInjection;
using SharpTicker.EntityFrameworkCore.Customizer;
using SharpTicker.EntityFrameworkCore.DependencyInjection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;

namespace SharpTicker.EntityFrameworkCore.Tests.DependencyInjection;

public class OtherNodesDbContext(DbContextOptions<OtherNodesDbContext> options) : DbContext(options);

/// <summary>
/// An application on the EF Core store alone that starts under a node identifier while the store holds jobs that are
/// InProgress under another one (#24): a pod that was replaced, for example. Nothing releases those jobs; the starting
/// node says so in its log. A started host, SQLite in a file of its own per test.
/// </summary>
public class JobsOfOtherNodesEndToEndTests : IAsyncLifetime
{
    private const string StartupCategory = "SharpTicker.EntityFrameworkCore.StartupRelease";
    private static readonly DateTime Earlier = DateTime.UtcNow.AddHours(-1);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, LogLevel Level, string Text)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<(string, LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue((category, logLevel, $"{formatter(state, exception)}{(exception == null ? "" : " | " + exception)}"));
        }
    }

    private readonly string _databaseFile = Path.Combine(Path.GetTempPath(), $"sharpticker-othernodes-{Guid.NewGuid():N}.db");
    private readonly RecordingLoggerProvider _log = new();
    private IHost? _application;

    public Task InitializeAsync() => Task.CompletedTask;

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

    // The application, built and not started; the database has its tables.
    private IHost Application(string node)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_log);
        builder.Services.AddDbContext<OtherNodesDbContext>(options => options.UseSqlite($"Data Source={_databaseFile}"));
        builder.Services.AddSharpTicker(options =>
        {
            options.ConfigureScheduler(scheduler => scheduler.NodeIdentifier = node);
            options.AddOperationalStore(ef => ef.UseApplicationDbContext<OtherNodesDbContext>(ConfigurationType.UseModelCustomizer));
        });

        _application = builder.Build();
        using var scope = _application.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<OtherNodesDbContext>().Database.EnsureCreated();
        return _application;
    }

    // A job as a node that ended without a signal leaves it: far in the future, so nothing claims it in this test.
    private static async Task<Guid> LeaveAJobInProgressAsync(IHost application, string lockHolder)
    {
        var job = new TimeTickerEntity
        {
            Id = Guid.NewGuid(), Function = "OtherNodes.Job", Status = TickerStatus.InProgress, ExecutionTime = DateTime.UtcNow.AddDays(1),
            LockHolder = lockHolder, LockedAt = Earlier, CreatedAt = Earlier, UpdatedAt = Earlier, Request = []
        };
        using var scope = application.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OtherNodesDbContext>();
        db.Set<TimeTickerEntity>().Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    private static async Task<TimeTickerEntity> StoredAsync(IHost application, Guid id)
    {
        using var scope = application.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OtherNodesDbContext>().Set<TimeTickerEntity>().AsNoTracking().SingleAsync(x => x.Id == id);
    }

    // The startup step of the store runs on a task of its own once the application has started.
    private static async Task StartAndWaitForTheStartupStepAsync(IHost application)
    {
        application.UseSharpTicker();
        await application.StartAsync();
        var startup = application.Services.GetRequiredService<TickerExecutionContext>().ExternalProviderStartup;
        Assert.NotNull(startup);
        await startup.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private string[] Reports() => _log.Entries
        .Where(e => e.Category == StartupCategory && e.Text.Contains("under other node identifiers"))
        .Select(e => $"{e.Level}: {e.Text}").ToArray();

    [Fact]
    public async Task JobsLeftInProgressUnderAnotherIdentifier_AreNamedInTheLogAtStartup_AndStayAsTheyAre()
    {
        var application = Application("pod-b");
        var first = await LeaveAJobInProgressAsync(application, "pod-a");
        var second = await LeaveAJobInProgressAsync(application, "pod-a");

        await StartAndWaitForTheStartupStepAsync(application);

        var report = Assert.Single(Reports());
        Assert.StartsWith("Information: 2 job(s) are InProgress under other node identifiers: pod-a (2).", report);
        foreach (var id in new[] { first, second })
        {
            var stored = await StoredAsync(application, id);
            Assert.Equal(TickerStatus.InProgress, stored.Status);
            Assert.Equal("pod-a", stored.LockHolder);
        }
    }

    // What an earlier run of the same identifier left is released, as before, and is not reported.
    [Fact]
    public async Task AJobLeftInProgressByAnEarlierRunOfTheSameIdentifier_IsReleased_AndNothingIsReported()
    {
        var application = Application("pod-b");
        var id = await LeaveAJobInProgressAsync(application, "pod-b");

        await StartAndWaitForTheStartupStepAsync(application);

        Assert.Empty(Reports());
        var stored = await StoredAsync(application, id);
        Assert.Equal(TickerStatus.Idle, stored.Status);
        Assert.Null(stored.LockHolder);
    }
}
