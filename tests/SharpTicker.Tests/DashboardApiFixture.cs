using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpTicker.Dashboard;
using SharpTicker.Dashboard.DependencyInjection;
using SharpTicker.DependencyInjection;
using SharpTicker.EntityFrameworkCore.DbContextFactory;
using SharpTicker.EntityFrameworkCore.DependencyInjection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Base;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;

namespace SharpTicker.Tests;

/// <summary>
/// A real application with a store and the dashboard, served by a TestServer, so the tests call the
/// dashboard API over HTTP the way the browser does. The scheduler is switched off, so nothing runs and the data
/// stays exactly as seeded.
///
/// Seeded times are noon UTC on yesterday, today and tomorrow, not offsets from the current time, so a test that runs
/// close to midnight still finds each ticker on the day the test expects.
/// </summary>
public abstract class DashboardApiFixture : IAsyncLifetime
{
    public const string Api = "/ticker/dashboard/api";

    public static readonly Guid DoneId = Guid.Parse("00000000-0000-0000-0000-0000000000d1");
    public static readonly Guid FailedId = Guid.Parse("00000000-0000-0000-0000-0000000000f1");
    public static readonly Guid QueuedId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    public static readonly Guid CronId = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    public static readonly Guid PausedCronId = Guid.Parse("00000000-0000-0000-0000-0000000000c2");
    public static readonly Guid OccurrenceDoneId = Guid.Parse("00000000-0000-0000-0000-00000000e0d1");
    public static readonly Guid OccurrenceFailedId = Guid.Parse("00000000-0000-0000-0000-00000000e0f1");
    public static readonly Guid OccurrenceQueuedId = Guid.Parse("00000000-0000-0000-0000-00000000e0a1");

    public const string DoneRequest = "{\"OrderId\":\"A-1\",\"Amount\":10.50}";

    private WebApplication? _app;

    /// <summary>Everything the application logged while the test ran.</summary>
    public CapturedLogs Logs { get; } = new();

    public HttpClient Client { get; private set; } = null!;
    public ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> Store { get; private set; } = null!;

    public DateTime Today { get; } = DateTime.UtcNow.Date;
    public DateTime Yesterday => Today.AddDays(-1);
    public DateTime Tomorrow => Today.AddDays(1);

    /// <summary>The function the tests register with a typed request, so the example JSON can be checked.</summary>
    public const string TypedFunction = "ApiTypedJob";

    public sealed record ApiOrder(string OrderId, decimal Amount);

    /// <summary>Adding or updating a ticker is refused for a function nobody registered, so register the ones the tests use.</summary>
    private static void RegisterFunctions()
    {
        TickerFunctionDelegate noOp = (_, _, _) => Task.CompletedTask;
        var functions = new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>();
        foreach (var name in new[] { "SeedJob", "OtherJob", "SeedCron", "PausedCron", "WriteJob", "TzJob", "NewCron", TypedFunction })
            functions[name] = ("", TickerTaskPriority.Normal, noOp, 0);

        TickerFunctionProvider.RegisterFunctions(functions);
        TickerFunctionProvider.RegisterRequestType(new Dictionary<string, (string, Type)>
        {
            [TypedFunction] = (typeof(ApiOrder).FullName!, typeof(ApiOrder)),
        });
        TickerFunctionProvider.Build();
    }

    public async Task InitializeAsync()
    {
        RegisterFunctions();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(Logs);
        builder.Services.AddSharpTicker(options =>
        {
            options.DisableBackgroundServices();
            ConfigureStore(options);
            options.AddDashboard(ConfigureDashboard);
        });

        _app = builder.Build();
        _app.UseSharpTicker();
        await _app.StartAsync();

        Client = _app.GetTestClient();
        Store = _app.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();

        await ClearStoreAsync();
        await SeedAsync();
    }

    /// <summary>
    /// The in-memory store is shared by the whole process, so a previous test's tickers would still be there.
    /// Start from an empty store, then seed.
    /// </summary>
    private async Task ClearStoreAsync()
    {
        var occurrences = await Store.GetAllCronTickerOccurrences(null, default);
        await Store.RemoveCronTickerOccurrences(occurrences.Select(o => o.Id).ToArray(), default);
        var crons = await Store.GetCronTickers(null, default);
        await Store.RemoveCronTickers(crons.Select(c => c.Id).ToArray(), default);
        var times = await Store.GetTimeTickers(null, default);
        await Store.RemoveTimeTickers(times.Select(t => t.Id).ToArray());
    }

    /// <summary>Chooses how the dashboard authenticates. The default lets everyone in.</summary>
    protected virtual void ConfigureDashboard(DashboardOptionsBuilder dashboard) => dashboard.WithNoAuth();

    /// <summary>Chooses the store the application runs on. The default is the in-memory store.</summary>
    protected virtual void ConfigureStore(TickerOptionsBuilder<TimeTickerEntity, CronTickerEntity> options) { }

    protected virtual void DisposeStore() { }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        DisposeStore();
    }

    private static DateTime NoonOf(DateTime day) => day.Date.AddHours(12);

    /// <summary>A time ticker in the given status, scheduled for noon UTC on the given day.</summary>
    public TimeTickerEntity Time(Guid id, string function, TickerStatus status, DateTime day, string? request = null, string? exception = null)
        => new()
        {
            Id = id,
            Function = function,
            Status = status,
            ExecutionTime = NoonOf(day),
            ExecutedAt = status is TickerStatus.Done or TickerStatus.Failed ? NoonOf(day) : null,
            ExceptionMessage = exception,
            Description = $"{status} job",
            Request = request == null ? null : Encoding.UTF8.GetBytes(request),
            Retries = 2,
            RetryIntervals = new[] { 30, 90 },
            CreatedAt = Today,
            UpdatedAt = Today,
        };

    private async Task SeedAsync()
    {
        await Store.AddTimeTickers(new[]
        {
            Time(DoneId, "SeedJob", TickerStatus.Done, Yesterday, request: DoneRequest),
            Time(FailedId, "SeedJob", TickerStatus.Failed, Today, exception: "boom: the order was rejected"),
            Time(QueuedId, "OtherJob", TickerStatus.Queued, Tomorrow),
        });

        await Store.InsertCronTickers(new[]
        {
            new CronTickerEntity
            {
                Id = CronId, Function = "SeedCron", Expression = "0 * * * * *", Description = "every minute",
                Retries = 1, IsEnabled = true, CreatedAt = Today, UpdatedAt = Today,
            },
            new CronTickerEntity
            {
                Id = PausedCronId, Function = "PausedCron", Expression = "0 0 * * * *", Description = "paused",
                IsEnabled = false, CreatedAt = Today, UpdatedAt = Today,
            },
        }, default);

        CronTickerOccurrenceEntity<CronTickerEntity> Occurrence(Guid id, TickerStatus status, DateTime day)
            => new()
            {
                Id = id,
                CronTickerId = CronId,
                Status = status,
                ExecutionTime = NoonOf(day),
                ExecutedAt = status is TickerStatus.Done or TickerStatus.Failed ? NoonOf(day) : null,
                CreatedAt = Today,
                UpdatedAt = Today,
            };

        await Store.InsertCronTickerOccurrences(new[]
        {
            Occurrence(OccurrenceDoneId, TickerStatus.Done, Yesterday),
            Occurrence(OccurrenceFailedId, TickerStatus.Failed, Today),
            Occurrence(OccurrenceQueuedId, TickerStatus.Queued, Tomorrow),
        }, default);
    }

    public async Task<JsonElement> GetAsync(string path)
    {
        var response = await Client.GetAsync(Api + path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"GET {path} returned {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public async Task<(int Status, JsonElement? Body)> SendAsync(HttpMethod method, string path, string? json = null)
    {
        using var request = new HttpRequestMessage(method, Api + path);
        if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, string.IsNullOrWhiteSpace(body) ? null : JsonDocument.Parse(body).RootElement.Clone());
    }
}

/// <summary>The default store: tickers live in memory, shared by the whole process.</summary>
public sealed class InMemoryDashboardApiFixture : DashboardApiFixture
{
}

/// <summary>EF Core on SQLite, so the same API tests run against a relational store and its queries.</summary>
public sealed class EfCoreDashboardApiFixture : DashboardApiFixture
{
    private SqliteConnection? _connection;

    protected override void ConfigureStore(TickerOptionsBuilder<TimeTickerEntity, CronTickerEntity> options)
    {
        // An in-memory SQLite database lives as long as its connection stays open.
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        // The scheduler is off but the initializer still reads the store when the host starts, so the schema has
        // to exist before the application starts.
        var schema = new DbContextOptionsBuilder<SharpTickerDbContext>().UseSqlite(_connection).Options;
        using (var context = new SharpTickerDbContext(schema))
            context.Database.EnsureCreated();

        options.AddOperationalStore(ef => ef.UseSharpTickerDbContext<SharpTickerDbContext>(db => db.UseSqlite(_connection)));
    }

    protected override void DisposeStore() => _connection?.Dispose();
}

/// <summary>A logger provider that keeps what was logged, so a test can check which entries a request wrote.</summary>
public sealed class CapturedLogs : ILoggerProvider
{
    public sealed record Entry(string Category, LogLevel Level, string Message, Exception? Exception);

    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();

    public Entry[] Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose() { }

    private sealed class Logger : ILogger
    {
        private readonly CapturedLogs _owner;
        private readonly string _category;

        public Logger(CapturedLogs owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_owner._gate)
                _owner._entries.Add(new Entry(_category, logLevel, formatter(state, exception), exception));
        }
    }
}
