using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using SharpTicker.DependencyInjection;
using SharpTicker.EntityFrameworkCore.Customizer;
using SharpTicker.EntityFrameworkCore.DependencyInjection;
using SharpTicker.Tests.Shared;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Interfaces;
using Testcontainers.MySql;

namespace SharpTicker.EntityFrameworkCore.MySql.Tests;

public class MySqlCancelledCallDbContext(DbContextOptions<MySqlCancelledCallDbContext> options) : DbContext(options);

public sealed class MySqlContainerFixture : IAsyncLifetime
{
    public MySqlContainer MySql { get; } = new MySqlBuilder("mysql:8.4").WithUsername("root").Build();

    public Task InitializeAsync() => MySql.StartAsync();

    public Task DisposeAsync() => MySql.DisposeAsync().AsTask();
}

/// <summary>
/// #255 on MySQL through the official provider: a stop of the application or a restart of the scheduler loop cancels the
/// token of the loop's pass. The cancel lands between two store calls (<see cref="StoreCallHold.AfterClose"/>), on a held
/// command (<see cref="StoreCallHold.Command"/>), on a command that runs on the database with a wait in front of it
/// (<see cref="StoreCallHold.CommandInFlight"/>), or on an open that is already running (<see cref="StoreCallHold.Open"/>).
/// A real MySQL in a container, shared by the tests of the class; each test starts from new databases.
/// </summary>
public class MySqlCancelledStoreCallLogTests(MySqlContainerFixture fixture) : IClassFixture<MySqlContainerFixture>, IAsyncLifetime
{
    public enum Cancel
    {
        Stop,
        Restart
    }

    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    private readonly RecordingLoggerProvider _log = new();
    private readonly CancelledStoreCallProbe _probe = new();
    private IHost? _application;
    private bool _stopped;

    public async Task InitializeAsync()
    {
        var server = new MySqlConnectionStringBuilder(fixture.MySql.GetConnectionString());
        var database = server.Database;
        server.Database = "";
        await using var connection = new MySqlConnection(server.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS `ticker`; DROP DATABASE IF EXISTS `{database}`;";
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_application != null)
        {
            if (!_stopped)
                await _application.StopAsync();
            _application.Dispose();
        }

        MySqlConnection.ClearAllPools();
    }

    private async Task<IHost> StartAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_log);
        builder.Services.AddDbContext<MySqlCancelledCallDbContext>(options => options.UseMySQL(fixture.MySql.GetConnectionString()).AddInterceptors(_probe));
        builder.Services.AddSharpTicker(options =>
        {
            options.ConfigureScheduler(scheduler =>
            {
                scheduler.NodeIdentifier = "candidate-98";
                scheduler.FallbackIntervalChecker = TimeSpan.FromMinutes(10);
            });
            options.AddOperationalStore(ef =>
            {
                ef.UseApplicationDbContext<MySqlCancelledCallDbContext>(ConfigurationType.UseModelCustomizer);
                ef.SetInProgressLease(TimeSpan.Zero);
            });
        });

        _application = builder.Build();
        using (var scope = _application.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<MySqlCancelledCallDbContext>().Database.EnsureCreatedAsync();

        _application.UseSharpTicker();
        await _application.StartAsync();
        // The startup step of the store and the restart it asks for end well within this; the task that tells when the step
        // ended (TickerExecutionContext.ExternalProviderStartup) is internal, and this project does not see it.
        await Task.Delay(3000);
        Assert.True(_log.Errors().Length == 0, $"precondition: an Error before the test began:\n{string.Join("\n", _log.Errors().AsEnumerable())}");
        // From here on, the log is the test's.
        _log.Entries.Clear();
        return _application;
    }

    private string Failure(string what, IEnumerable<LogEntry> entries)
        => $"{what}:\n{string.Join("\n", entries)}\nprovider failures: {string.Join("; ", _probe.Failures)}\n--- log ---\n{_log.Report()}";

    private async Task CutOffALoopPassAsync(IHost application, StoreCallHold hold, Cancel cancel)
    {
        var scheduler = application.Services.GetRequiredService<ISharpTickerHostScheduler>();
        _probe.Arm(hold);
        scheduler.Restart();
        await _probe.Held.Task.WaitAsync(WaitLimit);
        // The loop reads the cron and the time jobs at once. The read beside the held one ends first, so the cancel lands at
        // the held point and not inside the other read. In flight: the held command is on the database by then.
        await _probe.OtherCallEnded.Task.WaitAsync(WaitLimit);
        await Task.Delay(500);

        if (cancel == Cancel.Stop)
        {
            await application.StopAsync();
            _stopped = true;
        }
        else
            scheduler.Restart();

        await _probe.Released.Task.WaitAsync(WaitLimit);
        Assert.True(_probe.ReleasedByCancel, $"precondition: the held call was not released by a cancel of its token\n{_log.Report()}");
        await Task.Delay(1000);
    }

    // No store call is started after the cancel, so nothing is logged at Error.
    [Theory]
    [InlineData(StoreCallHold.AfterClose, Cancel.Stop)]
    [InlineData(StoreCallHold.AfterClose, Cancel.Restart)]
    [InlineData(StoreCallHold.Command, Cancel.Stop)]
    [InlineData(StoreCallHold.Command, Cancel.Restart)]
    [InlineData(StoreCallHold.CommandInFlight, Cancel.Stop)]
    [InlineData(StoreCallHold.CommandInFlight, Cancel.Restart)]
    public async Task AStopOrARestartBetweenTheLoopsStoreCalls_LogsNoError(StoreCallHold hold, Cancel cancel)
    {
        var application = await StartAsync();

        await CutOffALoopPassAsync(application, hold, cancel);

        var errors = _log.Errors();
        Assert.True(errors.Length == 0, Failure("the log has entries at Error or above", errors));
    }

    // The cancel comes while the loop's open is running. EF Core 8 and 9 log it as ConnectionError (20004) at Error, with the
    // TaskCanceledException the provider throws; SharpTicker logs nothing above Debug. EF Core 10 does not log it at Error.
    [Theory]
    [InlineData(Cancel.Stop)]
    [InlineData(Cancel.Restart)]
    public async Task AStopOrARestartDuringTheLoopsOpen_IsLoggedByEfCoreOnly_AsDocumented(Cancel cancel)
    {
        var application = await StartAsync();

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

    // Control: the same hold, let go after a moment without any cancel.
    [Theory]
    [InlineData(StoreCallHold.Open)]
    [InlineData(StoreCallHold.Command)]
    public async Task TheSameHoldWithoutACancel_LogsNoError(StoreCallHold hold)
    {
        var application = await StartAsync();
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
