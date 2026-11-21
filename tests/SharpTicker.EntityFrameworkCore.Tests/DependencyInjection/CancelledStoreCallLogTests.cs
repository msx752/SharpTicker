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
/// Candidate 98: the scheduler loop hands its token (linked to the application's stopping token, and cancelled by every
/// restart of the loop) to the store, so a stop or a restart can cancel a connection the loop is opening or a command it
/// runs. That is a normal stop or restart and must not reach the application's log at Error. A started host on SQLite; an
/// EF Core interceptor holds the loop's open (or command) until its token is cancelled, then lets the real call go on
/// with the cancelled token, so the cancel lands on the call every time instead of by chance.
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

    // A started application whose startup step has ended; nothing else of SharpTicker calls the store with a token that
    // can be cancelled then, apart from the scheduler loop (the lease is off, the fallback waits ten minutes).
    private async Task<IHost> StartAsync(Context context)
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
                ef.SetInProgressLease(TimeSpan.Zero);
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
        return _application;
    }

    private string Failure(LogEntry[] errors)
        => $"the log has entries at Error or above:\n{string.Join("\n", errors.AsEnumerable())}\n"
           + $"provider failures: {string.Join("; ", _probe.Failures)}\n--- log ---\n{_log.Report()}";

    // The loop's next pass is held at the store call; then the application stops, or the loop is restarted, which cancels
    // the token of that call. Neither may leave an entry at Error from EF Core or from SharpTicker.
    [Theory]
    [InlineData(Context.Owned, StoreCallHold.Open, Cancel.Stop)]
    [InlineData(Context.Owned, StoreCallHold.Open, Cancel.Restart)]
    [InlineData(Context.Owned, StoreCallHold.Command, Cancel.Stop)]
    [InlineData(Context.Owned, StoreCallHold.Command, Cancel.Restart)]
    [InlineData(Context.Application, StoreCallHold.Open, Cancel.Stop)]
    [InlineData(Context.Application, StoreCallHold.Open, Cancel.Restart)]
    [InlineData(Context.Application, StoreCallHold.Command, Cancel.Stop)]
    [InlineData(Context.Application, StoreCallHold.Command, Cancel.Restart)]
    public async Task AStopOrARestartThatCancelsTheLoopsStoreCall_LogsNoError(Context context, StoreCallHold hold, Cancel cancel)
    {
        var application = await StartAsync(context);
        var scheduler = application.Services.GetRequiredService<ISharpTickerHostScheduler>();
        Assert.True(_log.Errors().Length == 0, $"precondition: an Error before the test began:\n{string.Join("\n", _log.Errors().AsEnumerable())}");

        _probe.Arm(hold);
        scheduler.Restart();
        await _probe.Held.Task.WaitAsync(WaitLimit);

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

        var errors = _log.Errors();
        Assert.True(errors.Length == 0, Failure(errors));
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
        Assert.True(errors.Length == 0, Failure(errors));
    }
}
