using System.Collections.Concurrent;
using System.Data.Common;
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

namespace SharpTicker.EntityFrameworkCore.Tests.DependencyInjection;

public class CronDeletedWhileQueuedDbContext(DbContextOptions<CronDeletedWhileQueuedDbContext> options) : DbContext(options);

/// <summary>
/// The scheduler loop reads the crons due in a slot, then adds an occurrence for each. A cron deleted in between, by
/// another node for example, must not reach the application's log as a failure, and the other crons of the slot keep
/// running. A started host on SQLite; an EF Core interceptor deletes the cron, through a connection of its own, right
/// before the loop's insert of that cron's occurrence, so the delete lands in the window every time instead of by chance.
/// </summary>
public class CronDeletedWhileQueuedEndToEndTests : IAsyncLifetime
{
    private const string Deleted = "CronDeletedWhileQueued.Deleted";
    private const string Kept = "CronDeletedWhileQueued.Kept";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentQueue<(string Function, DateTime At)> Runs = new();
    private static readonly ConcurrentQueue<string> Handled = new();

    static CronDeletedWhileQueuedEndToEndTests()
    {
        TickerFunctionProvider.RegisterFunctions(new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>
        {
            [Deleted] = (string.Empty, TickerTaskPriority.Normal, (_, _, _) =>
            {
                Runs.Enqueue((Deleted, DateTime.UtcNow));
                return Task.CompletedTask;
            }, 0),
            [Kept] = (string.Empty, TickerTaskPriority.Normal, (_, _, _) =>
            {
                Runs.Enqueue((Kept, DateTime.UtcNow));
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
                => entries.Enqueue((logLevel, $"{logLevel} {category}: {formatter(state, exception)}{(exception == null ? "" : " | " + exception.GetType().Name + ": " + exception.Message)}"));
        }
    }

    // Once armed, deletes the given cron right before the first insert of an occurrence that points to it.
    private sealed class DeleteBeforeItsOccurrenceIsAdded(string databaseFile) : DbCommandInterceptor
    {
        private Guid? _cronId;

        public TaskCompletionSource Deleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm(Guid cronId) => _cronId = cronId;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await DeleteIfItsOccurrence(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await DeleteIfItsOccurrence(command);
            return result;
        }

        private async Task DeleteIfItsOccurrence(DbCommand command)
        {
            if (_cronId is not { } id
                || !command.CommandText.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
                || !command.CommandText.Contains("CronTickerOccurrences", StringComparison.Ordinal)
                || !command.Parameters.Cast<DbParameter>().Any(p => string.Equals(p.Value?.ToString(), id.ToString(), StringComparison.OrdinalIgnoreCase)))
                return;

            _cronId = null;
            // Another node's delete: a connection of its own, committed before the insert is sent.
            await using var connection = new SqliteConnection($"Data Source={databaseFile}");
            await connection.OpenAsync();
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM \"CronTickers\" WHERE \"Function\" = $function";
            delete.Parameters.AddWithValue("$function", CronDeletedWhileQueuedEndToEndTests.Deleted);
            Assert.Equal(1, await delete.ExecuteNonQueryAsync());
            Deleted.TrySetResult();
        }
    }

    private readonly string _databaseFile = Path.Combine(Path.GetTempPath(), $"sharpticker-crondeleted-{Guid.NewGuid():N}.db");
    private readonly RecordingLoggerProvider _log = new();
    private readonly DeleteBeforeItsOccurrenceIsAdded _probe;
    private IHost? _application;

    public CronDeletedWhileQueuedEndToEndTests() => _probe = new DeleteBeforeItsOccurrenceIsAdded(_databaseFile);

    public Task InitializeAsync()
    {
        Runs.Clear();
        Handled.Clear();
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

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(100);
        }

        return condition();
    }

    private int RunsOf(string function) => Runs.Count(r => r.Function == function);

    private static bool IsEfCoresOwnEntryForTheFailedInsert((LogLevel Level, string Text) entry)
        => entry.Level == LogLevel.Error
           && entry.Text.StartsWith("Error Microsoft.EntityFrameworkCore.Database.Command: Failed executing DbCommand", StringComparison.Ordinal)
           && entry.Text.Contains("INSERT INTO", StringComparison.Ordinal)
           && entry.Text.Contains("CronTickerOccurrences", StringComparison.Ordinal);

    [Fact]
    public async Task ACronDeletedByAnotherNodeWhileTheLoopQueuesItsOccurrence_IsNotLoggedAsAFailure_AndTheOtherCronKeepsRunning()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_log);
        // An EF Core warning that is not about this case is turned off as an application would: SQLite has no schemas.
        builder.Services.AddDbContext<CronDeletedWhileQueuedDbContext>(options => options.UseSqlite($"Data Source={_databaseFile}")
            .ConfigureWarnings(w => w.Ignore(SqliteEventId.SchemaConfiguredWarning))
            .AddInterceptors(_probe));
        builder.Services.AddSharpTicker(options =>
        {
            options.ConfigureScheduler(scheduler => scheduler.NodeIdentifier = "node-a");
            options.SetExceptionHandler<RecordingExceptionHandler>();
            options.AddOperationalStore(ef => ef.UseApplicationDbContext<CronDeletedWhileQueuedDbContext>(ConfigurationType.UseModelCustomizer));
        });
        _application = builder.Build();

        var earlier = DateTime.UtcNow.AddMinutes(-1);
        var deleted = new CronTickerEntity { Id = Guid.NewGuid(), Function = Deleted, Expression = "* * * * * *", CreatedAt = earlier, UpdatedAt = earlier, Request = [] };
        var kept = new CronTickerEntity { Id = Guid.NewGuid(), Function = Kept, Expression = "* * * * * *", CreatedAt = earlier, UpdatedAt = earlier, Request = [] };
        using (var scope = _application.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CronDeletedWhileQueuedDbContext>();
            db.Database.EnsureCreated();
            db.AddRange(deleted, kept);
            await db.SaveChangesAsync();
        }

        _application.UseSharpTicker();
        await _application.StartAsync();
        Assert.True(await WaitUntilAsync(() => RunsOf(Deleted) >= 2 && RunsOf(Kept) >= 2, WaitLimit),
            $"precondition: both crons run ({RunsOf(Deleted)}, {RunsOf(Kept)})");
        var before = _log.Entries.Where(e => e.Level >= LogLevel.Warning).Select(e => e.Text).ToArray();
        Assert.True(before.Length == 0, "precondition: a warning before the delete:\n" + string.Join("\n", before));
        Assert.Empty(Handled);

        _probe.Arm(deleted.Id);
        await _probe.Deleted.Task.WaitAsync(WaitLimit);
        var deletedAt = DateTime.UtcNow;
        // The other cron goes on running; a timing bound on its runs is left out, as a busy machine can make an every-second
        // cron late without a failure. The failure of the pass is told by the log.
        Assert.True(await WaitUntilAsync(() => Runs.Count(r => r.Function == Kept && r.At > deletedAt) >= 4, WaitLimit),
            $"the kept cron ran {Runs.Count(r => r.Function == Kept && r.At > deletedAt)} time(s) after the delete");
        await Task.Delay(TimeSpan.FromSeconds(1));

        // An occurrence of the deleted cron that was running when it was deleted cannot store its outcome; that warning
        // is documented and is not what this test is about. EF Core logs the failed insert itself at Error before
        // SharpTicker sees the exception (documented); exactly one such entry, for this insert, is expected.
        var efInsertFailures = _log.Entries.Where(IsEfCoresOwnEntryForTheFailedInsert).ToArray();
        var failures = _log.Entries
            .Where(e => e.Level >= LogLevel.Warning)
            .Where(e => !(e.Level == LogLevel.Warning && e.Text.Contains($"outcome of {Deleted}", StringComparison.Ordinal) && e.Text.Contains("was not stored", StringComparison.Ordinal)))
            .Where(e => !IsEfCoresOwnEntryForTheFailedInsert(e))
            .Select(e => e.Text).ToArray();
        Assert.True(efInsertFailures.Length == 1, $"EF Core's entries for the failed insert: {efInsertFailures.Length}\n{string.Join("\n", _log.Entries.Select(e => e.Text))}");
        var keptAfter = Runs.Where(r => r.Function == Kept && r.At > deletedAt).Select(r => r.At).OrderBy(x => x).ToArray();
        var report = $"runs of the kept cron after the delete: {string.Join(", ", keptAfter.Select(t => t.ToString("HH:mm:ss.fff")))} (delete {deletedAt:HH:mm:ss.fff})\n"
                     + $"handler calls: {string.Join("\n", Handled)}\n--- warnings and errors ---\n{string.Join("\n", failures)}";
        Assert.True(failures.Length == 0, report);
        Assert.True(Handled.IsEmpty, report);
    }
}
