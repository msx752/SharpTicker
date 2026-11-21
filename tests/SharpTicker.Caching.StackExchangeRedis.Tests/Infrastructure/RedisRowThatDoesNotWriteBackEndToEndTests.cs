using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpTicker.Caching.StackExchangeRedis.DependencyInjection;
using SharpTicker.Caching.StackExchangeRedis.Helpers;
using SharpTicker.DependencyInjection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>
/// An application's own time ticker type with a property whose converter reads an older form of its value (a number) and
/// only writes the current one (a string): a row that holds the older form deserializes, but cannot be serialized again.
/// </summary>
public sealed class LabelledTimeTicker : TimeTickerEntity<LabelledTimeTicker>
{
    [JsonConverter(typeof(LabelConverter))]
    public string? Label { get; set; }

    public const string OlderFormPrefix = "older:";

    public sealed class LabelConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.TokenType == JsonTokenType.Number ? OlderFormPrefix + reader.GetInt32() : reader.GetString();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            if (value.StartsWith(OlderFormPrefix, StringComparison.Ordinal))
                throw new NotSupportedException("A label in the older form is read, not written.");
            writer.WriteStringValue(value);
        }
    }
}

/// <summary>
/// Candidate 108. An application with its own ticker type (above), registered with <c>AddSharpTicker</c> and the Redis
/// store, started as a host, jobs added through the manager, and the scheduler running over a real Redis. One job's row
/// holds a label in the older form (written by an earlier version of the application): it reads, but the store cannot
/// write it again, and since #151 every claim writes the row back through the serializer. The tests are about the other
/// jobs of the application: they run on time, one time each. Each test also fails on anything else the application would
/// see go wrong: a log entry at Warning or above that is not about that one row, or a call to its exception handler.
/// The control (<c>olderForm: false</c>) keeps the label in the current form, so that row is an ordinary job.
/// On 1.6.4 the <c>olderForm: true</c> cases fail: the claim's serialize throws (CompareAndSetAsync), the scheduler loop
/// and every fallback pass end at that row, and the jobs behind it in the same pass do not run (#246). From 1.6.5 that
/// row is left as it is, with a warning that names its key, and it does not run.
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class RedisRowThatDoesNotWriteBackEndToEndTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private const string Node = "no-write-back-node";
    private const string Function = "NoWriteBack.EndToEnd.Job";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    // The lowest id there is: in the pending index, rows due in the same second are read in the order of their ids, so
    // every other job of that second comes after this one.
    private static readonly Guid FirstId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    // When each job ran, by its id.
    private static readonly ConcurrentDictionary<Guid, ConcurrentQueue<DateTime>> Runs = new();

    // What the application's exception handler was given.
    private static readonly ConcurrentQueue<string> Handled = new();

    static RedisRowThatDoesNotWriteBackEndToEndTests()
    {
        TickerFunctionProvider.RegisterFunctions(new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>
        {
            [Function] = (string.Empty, TickerTaskPriority.Normal, (_, _, context) =>
            {
                Runs.GetOrAdd(context.Id, _ => new ConcurrentQueue<DateTime>()).Enqueue(DateTime.UtcNow);
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
                => entries.Enqueue((logLevel, $"{DateTime.UtcNow:HH:mm:ss.fff} {logLevel} {category}: {formatter(state, exception)}{(exception == null ? "" : " | " + exception)}"));
        }
    }

    private readonly RecordingLoggerProvider _log = new();
    private IHost? _host;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        Runs.Clear();
        Handled.Clear();
    }

    public async Task DisposeAsync()
    {
        await StopAsync();
        await fixture.ResetAsync();
    }

    // The application: its own ticker type (read through reflection, no context of its own), the Redis store, its
    // exception handler, and its logging.
    private IHost Build()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_log);
        builder.Services.AddSharpTicker<LabelledTimeTicker, CronTickerEntity>(options =>
        {
            options.ConfigureScheduler(scheduler =>
            {
                scheduler.NodeIdentifier = Node;
                // Short, so the waits of the tests span many fallback passes.
                scheduler.FallbackIntervalChecker = TimeSpan.FromSeconds(1);
            });
            options.SetExceptionHandler<RecordingExceptionHandler>();
            options.AddStackExchangeRedis(redis => redis.Configuration = fixture.Container.GetConnectionString());
        });

        _host = builder.Build();
        _host.UseSharpTicker();
        return _host;
    }

    private async Task StopAsync()
    {
        if (_host == null) return;
        await _host.StopAsync();
        _host.Dispose();
        _host = null;
    }

    private static ITickerPersistenceProvider<LabelledTimeTicker, CronTickerEntity> Store(IHost host)
        => host.Services.GetRequiredService<ITickerPersistenceProvider<LabelledTimeTicker, CronTickerEntity>>();

    private string Report()
        => $"runs: {string.Join(", ", Runs.Select(r => $"{r.Key}=[{string.Join(", ", r.Value.Select(t => t.ToString("HH:mm:ss.fff")))}]"))}\n" +
           $"handled: {string.Join("\n", Handled)}\nlog (Information and above, and the loops' own Debug lines):\n" +
           string.Join("\n", _log.Entries
               .Where(e => e.Level >= LogLevel.Information || e.Text.Contains("SharpTicker.BackgroundServices"))
               .Select(e => e.Text));

    private async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"not within {WaitLimit}: {what}\n{Report()}");
            await Task.Delay(100);
        }
    }

    // What the application may see about that one row: warnings that name its key. Nothing above Warning, no warning
    // about anything else, nothing given to its handler.
    private void AssertOnlyThisRowIsReported(Guid row, bool olderForm)
    {
        if (olderForm)
            Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning
                && e.Text.Contains($"Redis key '{RedisKeyBuilder.TimeTickerKey(row)}' cannot be written as {nameof(LabelledTimeTicker)}"));

        var others = _log.Entries
            .Where(e => e.Level >= LogLevel.Warning)
            .Where(e => e.Level != LogLevel.Warning || !e.Text.Contains(RedisKeyBuilder.TimeTickerKey(row)))
            .Select(e => e.Text).ToList();
        Assert.True(others.Count == 0, $"the log has entries at Warning or above that are not about the row that cannot be written back:\n{string.Join("\n", others)}");
        Assert.True(Handled.IsEmpty, $"the exception handler was called:\n{string.Join("\n", Handled)}");
    }

    private static LabelledTimeTicker Job(DateTime executionTime, Guid? id = null)
        => new() { Id = id ?? Guid.NewGuid(), Function = Function, ExecutionTime = executionTime, Label = "current" };

    // The row as an earlier version of the application wrote it: its label in the older form. Only the label changes.
    private async Task WriteLabelInTheOlderFormAsync(Guid id)
    {
        var key = RedisKeyBuilder.TimeTickerKey(id);
        var row = JsonNode.Parse((string)(await fixture.Database.StringGetAsync(key))!)!.AsObject();
        var name = row.Select(p => p.Key).Single(k => string.Equals(k, nameof(LabelledTimeTicker.Label), StringComparison.OrdinalIgnoreCase));
        row[name] = 17;
        Assert.True(await fixture.Database.StringSetAsync(key, row.ToJsonString()));
    }

    private static async Task<TickerStatus?> StatusAsync(IHost host, Guid id)
        => (await Store(host).GetTimeTickerById(id, CancellationToken.None))?.Status;

    private async Task WaitUntilEndedAsync(IHost host, IEnumerable<Guid> ids, string what)
        => await WaitUntilAsync(async () =>
        {
            foreach (var id in ids)
                if (await StatusAsync(host, id) is not (TickerStatus.Done or TickerStatus.DueDone)) return false;
            return true;
        }, what);

    // Jobs added to a running application, due in the same second as the job whose row cannot be written back and
    // after it: each runs one time, on time (well inside the 30 s the test waits). The first job is stored, in the older
    // form, before the application starts: the loop claims a job of its next second as soon as it reads it, so a job
    // added to the running application could be claimed before its row is changed.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task JobsDueWithOrAfterOneThatCannotBeWrittenBack_RunOnceEach(bool olderForm)
    {
        var host = Build();
        var due = DateTime.UtcNow.AddSeconds(4);
        var firstJob = Job(due, FirstId);
        firstJob.CreatedAt = firstJob.UpdatedAt = DateTime.UtcNow;
        await Store(host).AddTimeTickers([firstJob], CancellationToken.None);
        var first = firstJob.Id;
        if (olderForm)
            await WriteLabelInTheOlderFormAsync(first);

        await host.StartAsync();
        var manager = host.Services.GetRequiredService<ITimeTickerManager<LabelledTimeTicker>>();

        var others = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var added = await manager.AddAsync(Job(due));
            Assert.True(added.IsSucceeded, $"the job was not added: {added.Exception}");
            others.Add(added.Result.Id);
        }
        var later = await manager.AddAsync(Job(due.AddSeconds(2)));
        Assert.True(later.IsSucceeded, $"the job was not added: {later.Exception}");
        others.Add(later.Result.Id);

        await WaitUntilEndedAsync(host, others, "the jobs next to and after the first one ended");

        // Long enough for more fallback passes to meet the first job, and to take another job again if they could.
        await Task.Delay(TimeSpan.FromSeconds(3));
        await StopAsync();

        Assert.All(others, id => Assert.True(Runs.TryGetValue(id, out var runs) && runs.Count == 1, Report()));
        if (!olderForm)
            Assert.True(Runs.TryGetValue(first, out var runs) && runs.Count == 1, Report());
        else
            Assert.False(Runs.ContainsKey(first), Report());
        AssertOnlyThisRowIsReported(first, olderForm);
    }

    // Jobs that became overdue while the application was stopped, behind the job whose row cannot be written back (it is
    // the most overdue one): after the start the fallback runs the others one time each.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OverdueJobsBehindOneThatCannotBeWrittenBack_RunOnceEach_AfterTheApplicationStarts(bool olderForm)
    {
        var host = Build();
        var overdue = DateTime.UtcNow.AddMinutes(-5);
        var jobs = new[] { Job(overdue, FirstId), Job(overdue), Job(overdue.AddSeconds(1)), Job(overdue.AddSeconds(2)) };
        foreach (var job in jobs)
            job.CreatedAt = job.UpdatedAt = overdue.AddMinutes(-1);
        await Store(host).AddTimeTickers(jobs, CancellationToken.None);
        if (olderForm)
            await WriteLabelInTheOlderFormAsync(FirstId);
        Guid[] others = [jobs[1].Id, jobs[2].Id, jobs[3].Id];

        await host.StartAsync();
        await WaitUntilEndedAsync(host, others, "the overdue jobs behind the first one ended");

        await Task.Delay(TimeSpan.FromSeconds(3));
        await StopAsync();

        Assert.All(others, id => Assert.True(Runs.TryGetValue(id, out var runs) && runs.Count == 1, Report()));
        if (!olderForm)
            Assert.True(Runs.TryGetValue(FirstId, out var runs) && runs.Count == 1, Report());
        else
            Assert.False(Runs.ContainsKey(FirstId), Report());
        AssertOnlyThisRowIsReported(FirstId, olderForm);
    }

    // A cron occurrence that became overdue while the application was stopped: the fallback takes the overdue cron
    // occurrences after the overdue time tickers in the same pass, so the job whose row cannot be written back is in
    // front of it. After the start the occurrence runs one time.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnOverdueCronOccurrenceBehindOneThatCannotBeWrittenBack_RunsOnce_AfterTheApplicationStarts(bool olderForm)
    {
        var host = Build();
        var overdue = DateTime.UtcNow.AddMinutes(-5);
        var first = Job(overdue, FirstId);
        first.CreatedAt = first.UpdatedAt = overdue.AddMinutes(-1);
        await Store(host).AddTimeTickers([first], CancellationToken.None);
        if (olderForm)
            await WriteLabelInTheOlderFormAsync(FirstId);

        // Due only once a year, so the scheduler loop does not make an occurrence of its own during the test.
        var cron = new CronTickerEntity
        {
            Id = Guid.NewGuid(), Function = Function, Expression = "0 0 0 1 1 *", IsEnabled = true, RetryIntervals = [],
            CreatedAt = overdue.AddMinutes(-1), UpdatedAt = overdue.AddMinutes(-1)
        };
        await Store(host).InsertCronTickers([cron], CancellationToken.None);
        var occurrence = new CronTickerOccurrenceEntity<CronTickerEntity>
        {
            Id = Guid.NewGuid(), CronTickerId = cron.Id, ExecutionTime = overdue, Status = TickerStatus.Idle,
            CreatedAt = overdue.AddMinutes(-1), UpdatedAt = overdue.AddMinutes(-1), CronTicker = cron
        };
        await Store(host).InsertCronTickerOccurrences([occurrence], CancellationToken.None);

        await host.StartAsync();
        await WaitUntilAsync(() => Task.FromResult(Runs.ContainsKey(occurrence.Id)), "the overdue cron occurrence ran");

        await Task.Delay(TimeSpan.FromSeconds(3));
        await StopAsync();

        Assert.True(Runs.TryGetValue(occurrence.Id, out var occurrenceRuns) && occurrenceRuns.Count == 1, Report());
        if (!olderForm)
            Assert.True(Runs.TryGetValue(FirstId, out var runs) && runs.Count == 1, Report());
        else
            Assert.False(Runs.ContainsKey(FirstId), Report());
        AssertOnlyThisRowIsReported(FirstId, olderForm);
    }
}
