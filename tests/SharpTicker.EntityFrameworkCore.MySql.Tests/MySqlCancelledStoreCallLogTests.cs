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
/// Candidate 98 on MySQL through the official provider: a stop of the application or a restart of the scheduler loop
/// cancels the token of a store call of the loop. The loop's open or command is held until the cancel
/// (<see cref="StoreCallHold.Open"/>, <see cref="StoreCallHold.Command"/>), or its command is on the database with a
/// wait in front of it when the cancel comes (<see cref="StoreCallHold.CommandInFlight"/>). Neither may leave an entry at
/// Error from EF Core or from SharpTicker. A real MySQL in a container, shared by the tests of the class; each test
/// starts from new databases.
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
        // The startup step of the store (internal here) and the restart it asks for end well within this.
        await Task.Delay(3000);
        // What EnsureCreated logs on a database that is not there yet is not part of the test.
        _log.Entries.Clear();
        return _application;
    }

    private string Failure(LogEntry[] errors)
        => $"the log has entries at Error or above:\n{string.Join("\n", errors.AsEnumerable())}\n"
           + $"provider failures: {string.Join("; ", _probe.Failures)}\n--- log ---\n{_log.Report()}";

    [Theory]
    [InlineData(StoreCallHold.Open, Cancel.Stop)]
    [InlineData(StoreCallHold.Open, Cancel.Restart)]
    [InlineData(StoreCallHold.Command, Cancel.Stop)]
    [InlineData(StoreCallHold.Command, Cancel.Restart)]
    [InlineData(StoreCallHold.CommandInFlight, Cancel.Stop)]
    [InlineData(StoreCallHold.CommandInFlight, Cancel.Restart)]
    public async Task AStopOrARestartThatCancelsTheLoopsStoreCall_LogsNoError(StoreCallHold hold, Cancel cancel)
    {
        var application = await StartAsync();
        var scheduler = application.Services.GetRequiredService<ISharpTickerHostScheduler>();

        _probe.Arm(hold);
        scheduler.Restart();
        await _probe.Held.Task.WaitAsync(WaitLimit);
        // In flight: the command is on its way to the database; give it the time to get there.
        if (hold == StoreCallHold.CommandInFlight)
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

        var errors = _log.Errors();
        Assert.True(errors.Length == 0, Failure(errors));
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
        Assert.True(errors.Length == 0, Failure(errors));
    }
}
