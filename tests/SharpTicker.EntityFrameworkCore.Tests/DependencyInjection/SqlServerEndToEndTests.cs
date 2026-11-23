using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpTicker.DependencyInjection;
using SharpTicker.EntityFrameworkCore.Customizer;
using SharpTicker.EntityFrameworkCore.DependencyInjection;
using SharpTicker.Tests.Shared;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;

namespace SharpTicker.EntityFrameworkCore.Tests.DependencyInjection;

public class SqlServerApplicationDbContext(DbContextOptions<SqlServerApplicationDbContext> options) : DbContext(options);

/// <summary>
/// #247: an application on SQL Server: registered with <c>AddSharpTicker</c> and the EF Core store on the application's
/// context, started as a host, a cron and a time job added through the managers, the scheduler running them. The SQL Server
/// of <see cref="SqlServerTestFixture"/>, a database of its own for each test. Each test also fails on a call to the
/// application's exception handler and on a log entry at Error or above.
/// </summary>
[Collection(SqlServerTestFixture.CollectionName)]
public class SqlServerEndToEndTests(SqlServerTestFixture fixture) : IAsyncLifetime
{
    private const string CronFunction = "SqlServerEndToEnd.Cron";
    private const string TimeFunction = "SqlServerEndToEnd.Time";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(60);

    private static readonly ConcurrentDictionary<Guid, int> Runs = new();
    private static readonly ConcurrentQueue<string> Handled = new();

    static SqlServerEndToEndTests()
    {
        static Task Count(Guid id)
        {
            Runs.AddOrUpdate(id, 1, (_, n) => n + 1);
            return Task.CompletedTask;
        }

        TickerFunctionProvider.RegisterFunctions(new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>
        {
            [CronFunction] = (string.Empty, TickerTaskPriority.Normal, (_, _, context) => Count(context.Id), 0),
            [TimeFunction] = (string.Empty, TickerTaskPriority.Normal, (_, _, context) => Count(context.Id), 0)
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

    private readonly string _connectionString = fixture.ConnectionString($"e2e_{Guid.NewGuid():N}");
    private readonly RecordingLoggerProvider _log = new();
    private IHost? _application;

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

        SqlConnection.ClearAllPools();
    }

    private async Task<IHost> StartAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddProvider(_log);
        builder.Services.AddDbContext<SqlServerApplicationDbContext>(options => options.UseSqlServer(_connectionString));
        builder.Services.AddSharpTicker(options =>
        {
            options.ConfigureScheduler(scheduler => scheduler.NodeIdentifier = "sqlserver-e2e");
            options.SetExceptionHandler<RecordingExceptionHandler>();
            options.AddOperationalStore(ef => ef.UseApplicationDbContext<SqlServerApplicationDbContext>(ConfigurationType.UseModelCustomizer));
        });

        _application = builder.Build();
        using (var scope = _application.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<SqlServerApplicationDbContext>().Database.EnsureCreatedAsync();

        _application.UseSharpTicker();
        await _application.StartAsync();
        return _application;
    }

    private static ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> Store(IHost application)
        => application.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();

    private string Report()
        => $"runs: {string.Join(", ", Runs.Select(r => $"{r.Key}={r.Value}"))}\nhandled: {string.Join("\n", Handled)}\nlog:\n{_log.Report()}";

    private async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"not within {WaitLimit}: {what}\n{Report()}");
            await Task.Delay(200);
        }
    }

    // Left out: EF Core's own Error for a connection whose opening was cancelled (EF Core 8 and 9 log it, EF Core 10 does
    // not, so on EF Core 10 the filter leaves nothing out). The scheduler loop is
    // restarted when a job is added and once after startup, and stopped with the application; when that comes while the
    // loop opens a connection, EF Core logs it. SqlServerCancelledStoreCallLogTests covers that case on its own.
    private void AssertNothingWentWrong()
    {
        var problems = _log.Entries
            .Where(e => e.Level >= LogLevel.Error && !(e.Category == "Microsoft.EntityFrameworkCore.Database.Connection" && e.EventId.Id == 20004))
            .Select(e => e.ToString()).ToList();
        Assert.True(problems.Count == 0, $"the log has entries at Error or above:\n{string.Join("\n", problems)}");
        Assert.True(Handled.IsEmpty, $"the exception handler was called:\n{string.Join("\n", Handled)}");
    }

    // The scheduler creates each occurrence of a cron with an insert that does nothing when the occurrence is there
    // already; the library that writes it has to know the provider.
    [Fact]
    public async Task ACron_IsAdded_AndItsOccurrencesRunAndEnd()
    {
        var application = await StartAsync();
        var manager = application.Services.GetRequiredService<ICronTickerManager<CronTickerEntity>>();

        var added = await manager.AddAsync(new CronTickerEntity { Function = CronFunction, Expression = "* * * * * *" });

        Assert.True(added.IsSucceeded, $"the cron was not added: {added.Exception}\n{Report()}");
        var cronId = added.Result.Id;
        CronTickerOccurrenceEntity<CronTickerEntity>[] ended = [];
        await WaitUntilAsync(async () =>
        {
            ended = (await Store(application).GetAllCronTickerOccurrences(o => o.CronTickerId == cronId, CancellationToken.None))
                .Where(o => o.Status is TickerStatus.Done or TickerStatus.DueDone).ToArray();
            return ended.Length >= 2;
        }, "two occurrences of the cron ran and ended");

        Assert.All(ended, occurrence => Assert.Equal(1, Runs.GetValueOrDefault(occurrence.Id)));
        AssertNothingWentWrong();
    }

    [Fact]
    public async Task ATimeJob_IsAdded_RunsOnce_AndEnds()
    {
        var application = await StartAsync();
        var manager = application.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();

        var added = await manager.AddAsync(new TimeTickerEntity { Function = TimeFunction, ExecutionTime = DateTime.UtcNow.AddSeconds(2) });

        Assert.True(added.IsSucceeded, $"the job was not added: {added.Exception}\n{Report()}");
        var id = added.Result.Id;
        await WaitUntilAsync(async () => (await Store(application).GetTimeTickerById(id, CancellationToken.None))?.Status is TickerStatus.Done or TickerStatus.DueDone,
            "the time job ran and ended");

        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(1, Runs.GetValueOrDefault(id));
        AssertNothingWentWrong();
    }
}

/// <summary>
/// #255 and #214 on SQL Server (#247): a stop of the application or a restart of the scheduler loop cancels the token of the
/// loop's pass. The cancel lands between two store calls (<see cref="StoreCallHold.AfterClose"/>), on a held command
/// (<see cref="StoreCallHold.Command"/>), on a command that runs on the database with a wait in front of it
/// (<see cref="StoreCallHold.CommandInFlight"/>), or on an open that is already running (<see cref="StoreCallHold.Open"/>).
/// Microsoft.Data.SqlClient reports the cancel of a running command as SqlException, not as OperationCanceledException
/// (#214). The SQL Server of <see cref="SqlServerTestFixture"/>, a database of its own for each test.
/// </summary>
[Collection(SqlServerTestFixture.CollectionName)]
public class SqlServerCancelledStoreCallLogTests(SqlServerTestFixture fixture) : IAsyncLifetime
{
    public enum Cancel
    {
        Stop,
        Restart
    }

    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    private readonly string _connectionString = fixture.ConnectionString($"cancel_{Guid.NewGuid():N}");
    private readonly RecordingLoggerProvider _log = new();
    private readonly CancelledStoreCallProbe _probe = new() { InFlightPrefix = "WAITFOR DELAY '00:00:10'; " };
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

        SqlConnection.ClearAllPools();
    }

    // A started application whose startup step has ended. Without a lease, nothing else of SharpTicker calls the store with
    // a token that can be cancelled then, apart from the scheduler loop (the fallback waits ten minutes).
    private async Task<IHost> StartAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_log);
        builder.Services.AddDbContext<SqlServerApplicationDbContext>(options => options.UseSqlServer(_connectionString).AddInterceptors(_probe));
        builder.Services.AddSharpTicker(options =>
        {
            options.ConfigureScheduler(scheduler =>
            {
                scheduler.NodeIdentifier = "candidate-98";
                scheduler.FallbackIntervalChecker = TimeSpan.FromMinutes(10);
            });
            options.AddOperationalStore(ef =>
            {
                ef.UseApplicationDbContext<SqlServerApplicationDbContext>(ConfigurationType.UseModelCustomizer);
                ef.SetInProgressLease(TimeSpan.Zero);
            });
        });

        _application = builder.Build();
        using (var scope = _application.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<SqlServerApplicationDbContext>().Database.EnsureCreatedAsync();

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

    // #214: the restart lands while a read of the loop runs on SQL Server. SqlClient ends the command with SqlException
    // ("Operation cancelled by user."), which reaches the loop; the loop takes it as the restart it asked for: SharpTicker
    // logs nothing above Debug, so no "The scheduler loop failed" and no "recovered" after it.
    [Fact]
    public async Task ARestartWhileACommandRunsOnSqlServer_EndsInSqlException_AndIsARestartNotAFailure()
    {
        var application = await StartAsync();

        await CutOffALoopPassAsync(application, StoreCallHold.CommandInFlight, Cancel.Restart);

        // EF Core reports the command as cancelled (CommandCanceled) and hands on the provider's exception. Whatever the loop
        // makes of it, its log entry for the pass that was cut off carries that exception.
        Assert.True(_log.Entries.Any(e => e.Category == "SharpTicker.BackgroundServices.SharpTickerSchedulerBackgroundService"
                                          && e.Text.Contains("Microsoft.Data.SqlClient.SqlException", StringComparison.Ordinal)),
            Failure("precondition: no SqlException reached the scheduler loop", []));
        var sharpTicker = _log.SharpTickerAboveDebug();
        Assert.True(sharpTicker.Length == 0, Failure("SharpTicker logged above Debug", sharpTicker));
        var errors = _log.Errors();
        Assert.True(errors.Length == 0, Failure("the log has entries at Error or above", errors));
    }

    // The cancel comes while the loop's open is running. EF Core 8 and 9 log it as ConnectionError (20004) at Error; SharpTicker
    // logs nothing above Debug. EF Core 10 does not log it at Error.
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
