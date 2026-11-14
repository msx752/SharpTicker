using System.Collections.Concurrent;
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
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using Testcontainers.MySql;

namespace SharpTicker.EntityFrameworkCore.MySql.Tests;

public class MySqlOfficialDbContext(DbContextOptions<MySqlOfficialDbContext> options) : DbContext(options);

/// <summary>
/// An application on MySQL through the official provider, MySql.EntityFrameworkCore (#91): registered with
/// <c>AddSharpTicker</c> and the EF Core store, started as a host, a cron and a time job added through the managers, the
/// scheduler running them. A real MySQL in a container. Each test also fails on a call to the application's exception
/// handler and on a log entry at Error or above.
/// </summary>
public class MySqlOfficialProviderEndToEndTests : IAsyncLifetime
{
    private const string CronFunction = "MySqlOfficial.Cron";
    private const string TimeFunction = "MySqlOfficial.Time";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(60);

    private static readonly ConcurrentDictionary<Guid, int> Runs = new();
    private static readonly ConcurrentQueue<string> Handled = new();

    static MySqlOfficialProviderEndToEndTests()
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

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                    entries.Enqueue((logLevel, $"{logLevel} {category}: {formatter(state, exception)}{(exception == null ? "" : " | " + exception)}"));
            }
        }
    }

    // The store keeps its tables in the schema "ticker", which is a database of its own on MySQL: the application's
    // user has to be allowed to create it, so the test connects as root.
    private readonly MySqlContainer _mySql = new MySqlBuilder("mysql:8.4").WithUsername("root").Build();
    private readonly RecordingLoggerProvider _log = new();
    private IHost? _application;

    public async Task InitializeAsync()
    {
        Runs.Clear();
        Handled.Clear();
        await _mySql.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_application != null)
        {
            await _application.StopAsync();
            _application.Dispose();
        }

        await _mySql.DisposeAsync();
    }

    private async Task<IHost> StartAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddProvider(_log);
        builder.Services.AddDbContext<MySqlOfficialDbContext>(options => options.UseMySQL(_mySql.GetConnectionString()));
        builder.Services.AddSharpTicker(options =>
        {
            options.ConfigureScheduler(scheduler => scheduler.NodeIdentifier = "mysql-official");
            options.SetExceptionHandler<RecordingExceptionHandler>();
            options.AddOperationalStore(ef => ef.UseApplicationDbContext<MySqlOfficialDbContext>(ConfigurationType.UseModelCustomizer));
        });

        _application = builder.Build();
        using (var scope = _application.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<MySqlOfficialDbContext>().Database.EnsureCreatedAsync();

        _application.UseSharpTicker();
        await _application.StartAsync();
        return _application;
    }

    private static ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> Store(IHost application)
        => application.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();

    private string Report()
        => $"runs: {string.Join(", ", Runs.Select(r => $"{r.Key}={r.Value}"))}\nhandled: {string.Join("\n", Handled)}\nlog:\n{string.Join("\n", _log.Entries.Select(e => e.Text))}";

    private async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"not within {WaitLimit}: {what}\n{Report()}");
            await Task.Delay(200);
        }
    }

    // Left out: EF Core's own Error for a connection whose opening was cancelled. The scheduler loop is restarted when
    // a job is added and once after startup, and stopped with the application; when that comes while the loop opens
    // a connection, EF Core logs it. It says nothing about the provider this test is about.
    private void AssertNothingWentWrong()
    {
        var problems = _log.Entries
            .Where(e => e.Level >= LogLevel.Error && !e.Text.Contains("An error occurred using the connection to database"))
            .Select(e => e.Text).ToList();
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
