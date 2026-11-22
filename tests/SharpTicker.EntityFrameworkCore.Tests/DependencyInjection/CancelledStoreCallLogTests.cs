using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpTicker.DependencyInjection;
using SharpTicker.EntityFrameworkCore.Customizer;
using SharpTicker.EntityFrameworkCore.DbContextFactory;
using SharpTicker.EntityFrameworkCore.DependencyInjection;
using SharpTicker.Tests.Shared;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Interfaces;

namespace SharpTicker.EntityFrameworkCore.Tests.DependencyInjection;

public class CancelledStoreCallDbContext(DbContextOptions<CancelledStoreCallDbContext> options) : DbContext(options);

/// <summary>
/// #255: the scheduler loop hands its token (linked to the application's stopping token, and cancelled by every restart
/// of the loop) to the store, and the lease service its stopping token. A stop or a restart is normal and must not reach the
/// application's log at Error. A started host on SQLite; an EF Core interceptor makes the cancel land at a chosen point every
/// time instead of by chance:
/// <list type="bullet">
/// <item>between two store calls (<see cref="StoreCallHold.AfterClose"/>) or on a command (<see cref="StoreCallHold.Command"/>):
/// no store call is started after the cancel, so nothing is logged at Error;</item>
/// <item>on an open that is already running (<see cref="StoreCallHold.Open"/>): EF Core 8 and 9 log that open as ConnectionError
/// at Error, which SharpTicker cannot prevent; SharpTicker logs nothing above Debug. EF Core 10 does not log it at Error.</item>
/// </list>
/// </summary>
public class CancelledStoreCallLogTests : IAsyncLifetime
{
    public enum Context
    {
        Owned,
        Application
    }

    public enum Cancel
    {
        Stop,
        Restart
    }

    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    private readonly string _databaseFile = Path.Combine(Path.GetTempPath(), $"sharpticker-cancelled-call-{Guid.NewGuid():N}.db");
    private readonly RecordingLoggerProvider _log = new();
    private readonly CancelledStoreCallProbe _probe = new();
    private IHost? _application;
    private bool _stopped;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_application != null)
        {
            if (!_stopped)
                await _application.StopAsync();
            _application.Dispose();
        }

        SqliteConnection.ClearAllPools();
        if (File.Exists(_databaseFile))
            File.Delete(_databaseFile);
    }

    // A started application whose startup step has ended. Without a lease, nothing else of SharpTicker calls the store with
    // a token that can be cancelled then, apart from the scheduler loop (the fallback waits ten minutes); with one, the loop
    // sleeps (no jobs) and the lease service renews every fifth of it.
    private async Task<IHost> StartAsync(Context context, TimeSpan? lease = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_log);
        var connection = $"Data Source={_databaseFile}";
        if (context == Context.Application)
            builder.Services.AddDbContext<CancelledStoreCallDbContext>(options => options.UseSqlite(connection).AddInterceptors(_probe));

        builder.Services.AddSharpTicker(options =>
        {
            options.ConfigureScheduler(scheduler =>
            {
                scheduler.NodeIdentifier = "candidate-98";
                scheduler.FallbackIntervalChecker = TimeSpan.FromMinutes(10);
            });
            options.AddOperationalStore(ef =>
            {
                if (context == Context.Application)
                    ef.UseApplicationDbContext<CancelledStoreCallDbContext>(ConfigurationType.UseModelCustomizer);
                else
                    ef.UseSharpTickerDbContext<SharpTickerDbContext>(db => db.UseSqlite(connection).AddInterceptors(_probe));
                ef.SetInProgressLease(lease ?? TimeSpan.Zero);
            });
        });

        _application = builder.Build();
        if (context == Context.Application)
        {
            using var scope = _application.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<CancelledStoreCallDbContext>().Database.EnsureCreatedAsync();
        }
        else
        {
            await using var db = await _application.Services.GetRequiredService<IDbContextFactory<SharpTickerDbContext>>().CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
        }

        _application.UseSharpTicker();
        await _application.StartAsync();
        var startup = _application.Services.GetRequiredService<TickerExecutionContext>().ExternalProviderStartup;
        if (startup != null)
            await startup.WaitAsync(WaitLimit);
        // The restart the startup step asks for, and the pass after it.
        await Task.Delay(500);
        Assert.True(_log.Errors().Length == 0, $"precondition: an Error before the test began:\n{string.Join("\n", _log.Errors().AsEnumerable())}");
        // From here on, the log is the test's.
        _log.Entries.Clear();
        return _application;
    }

    private string Failure(string what, IEnumerable<LogEntry> entries)
        => $"{what}:\n{string.Join("\n", entries)}\nprovider failures: {string.Join("; ", _probe.Failures)}\n--- log ---\n{_log.Report()}";

    // The loop's pass is held at the chosen point; then the application stops, or the loop is restarted, which cancels the
    // token of that pass.
    private async Task CutOffALoopPassAsync(IHost application, StoreCallHold hold, Cancel cancel)
    {
        var scheduler = application.Services.GetRequiredService<ISharpTickerHostScheduler>();
        _probe.Arm(hold);
        scheduler.Restart();
        await _probe.Held.Task.WaitAsync(WaitLimit);
        // The loop reads the cron and the time jobs at once. The read beside the held one ends first, so the cancel lands at
        // the held point and not inside the other read, where it would be an open that is already under way.
        await _probe.OtherCallEnded.Task.WaitAsync(WaitLimit);
        await Task.Delay(300);

        if (cancel == Cancel.Stop)
        {
            await application.StopAsync();
            _stopped = true;
        }
        else
            scheduler.Restart();

        await _probe.Released.Task.WaitAsync(WaitLimit);
        Assert.True(_probe.ReleasedByCancel, "precondition: the held call was not released by a cancel of its token");
        // The pass that was cut off ends and the loop goes on (restart) or ends (stop).
        await Task.Delay(1000);
    }

    // The cancel comes between two store calls of the pass, or on a command: the store call after it is not started.
    [Theory]
    [InlineData(Context.Owned, StoreCallHold.AfterClose, Cancel.Stop)]
    [InlineData(Context.Owned, StoreCallHold.AfterClose, Cancel.Restart)]
    [InlineData(Context.Owned, StoreCallHold.Command, Cancel.Stop)]
    [InlineData(Context.Owned, StoreCallHold.Command, Cancel.Restart)]
    [InlineData(Context.Application, StoreCallHold.AfterClose, Cancel.Stop)]
    [InlineData(Context.Application, StoreCallHold.AfterClose, Cancel.Restart)]
    [InlineData(Context.Application, StoreCallHold.Command, Cancel.Stop)]
    [InlineData(Context.Application, StoreCallHold.Command, Cancel.Restart)]
    public async Task AStopOrARestartBetweenTheLoopsStoreCalls_LogsNoError(Context context, StoreCallHold hold, Cancel cancel)
    {
        var application = await StartAsync(context);

        await CutOffALoopPassAsync(application, hold, cancel);

        var errors = _log.Errors();
        Assert.True(errors.Length == 0, Failure("the log has entries at Error or above", errors));
    }

    // The lease pass renews the lock of this node's jobs, then records the expired jobs of other nodes: a stop between the
    // two does not start the second. The renewal is two statements, so the close of the second open is held.
    [Theory]
    [InlineData(Context.Owned)]
    [InlineData(Context.Application)]
    public async Task AStopBetweenTheLeasePassesStoreCalls_LogsNoError(Context context)
    {
        var application = await StartAsync(context, lease: TimeSpan.FromSeconds(1));

        _probe.Arm(StoreCallHold.AfterClose, nth: 2);
        await _probe.Held.Task.WaitAsync(WaitLimit);
        await application.StopAsync();
        _stopped = true;
        await _probe.Released.Task.WaitAsync(WaitLimit);
        Assert.True(_probe.ReleasedByCancel, "precondition: the held call was not released by a cancel of its token");
        await Task.Delay(500);

        var errors = _log.Errors();
        Assert.True(errors.Length == 0, Failure("the log has entries at Error or above", errors));
    }

    // The cancel comes while the loop's open is running. EF Core 8 and 9 log it as ConnectionError at Error, with the
    // TaskCanceledException the provider throws; nothing else is at Error and SharpTicker logs nothing above Debug. EF Core 10
    // does not log it at Error.
    [Theory]
    [InlineData(Context.Owned, Cancel.Stop)]
    [InlineData(Context.Owned, Cancel.Restart)]
    [InlineData(Context.Application, Cancel.Stop)]
    [InlineData(Context.Application, Cancel.Restart)]
    public async Task AStopOrARestartDuringTheLoopsOpen_IsLoggedByEfCoreOnly_AsDocumented(Context context, Cancel cancel)
    {
        var application = await StartAsync(context);

        await CutOffALoopPassAsync(application, StoreCallHold.Open, cancel);

        var errors = _log.Errors();
        if (RecordingLoggerProvider.EfCoreLogsACancelledOpenAtError)
        {
            Assert.True(errors.Length > 0, Failure("EF Core 8/9: the cancelled open was not logged at Error", errors));
            Assert.True(errors.All(e => e.Category == "Microsoft.EntityFrameworkCore.Database.Connection" && e.EventId.Id == 20004),
                Failure("EF Core 8/9: an entry at Error other than ConnectionError (20004)", errors));
            Assert.True(_probe.Failures.Any(f => f.StartsWith("open: System.Threading.Tasks.TaskCanceledException", StringComparison.Ordinal)),
                Failure("EF Core 8/9: the open did not fail with a TaskCanceledException", errors));
        }
        else
            Assert.True(errors.Length == 0, Failure("the log has entries at Error or above", errors));

        var sharpTicker = _log.SharpTickerAboveDebug();
        Assert.True(sharpTicker.Length == 0, Failure("SharpTicker logged above Debug", sharpTicker));
    }

    // Control: the same hold, let go after a moment without any cancel. The hold alone logs nothing at Error.
    [Theory]
    [InlineData(Context.Owned, StoreCallHold.Open)]
    [InlineData(Context.Owned, StoreCallHold.Command)]
    [InlineData(Context.Application, StoreCallHold.Open)]
    [InlineData(Context.Application, StoreCallHold.Command)]
    public async Task TheSameHoldWithoutACancel_LogsNoError(Context context, StoreCallHold hold)
    {
        var application = await StartAsync(context);
        var scheduler = application.Services.GetRequiredService<ISharpTickerHostScheduler>();

        _probe.Arm(hold, releaseAfter: TimeSpan.FromMilliseconds(300));
        scheduler.Restart();
        await _probe.Held.Task.WaitAsync(WaitLimit);
        await _probe.Released.Task.WaitAsync(WaitLimit);
        Assert.False(_probe.ReleasedByCancel, "precondition: the held call was cancelled");
        await Task.Delay(1000);

        var errors = _log.Errors();
        Assert.True(errors.Length == 0, Failure("the log has entries at Error or above", errors));
    }
}
