using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SharpTicker.Tests.Shared;

// Candidate 98: what the EF Core store's tests use to make a stop or a restart of the scheduler loop land on a store call
// of the loop every time, and to read what that leaves in the log.

/// <summary>Where the store call is held until its token is cancelled.</summary>
public enum StoreCallHold
{
    /// <summary>The open of the connection: held, then the real open runs with the cancelled token.</summary>
    Open,

    /// <summary>A command: held, then the real command runs with the cancelled token.</summary>
    Command,

    /// <summary>
    /// A command that is on its way on the database when its token is cancelled: it is sent with a wait in front of it
    /// (<see cref="CancelledStoreCallProbe.InFlightPrefix"/>), so the cancel reaches the provider while the database runs it.
    /// </summary>
    CommandInFlight
}

public sealed record LogEntry(string Category, LogLevel Level, EventId EventId, string Text)
{
    public override string ToString() => $"{Level} {Category} ({EventId.Id} {EventId.Name}): {Text}";
}

public sealed class RecordingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

    public void Dispose()
    {
    }

    /// <summary>The entries at Error or above from EF Core and from SharpTicker.</summary>
    public LogEntry[] Errors() => Entries
        .Where(e => e.Level >= LogLevel.Error
                    && (e.Category.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                        || e.Category.StartsWith("SharpTicker", StringComparison.Ordinal)))
        .ToArray();

    /// <summary>What a failure message shows of the log: warnings and above, SharpTicker, and EF Core's error and cancel events.</summary>
    public string Report() => string.Join("\n", Entries
        .Where(e => e.Level >= LogLevel.Warning
                    || (e.EventId.Name ?? "").Contains("Cancel", StringComparison.Ordinal)
                    || (e.EventId.Name ?? "").Contains("Error", StringComparison.Ordinal)
                    || (e.EventId.Name ?? "").Contains("Failed", StringComparison.Ordinal)
                    || e.Category.StartsWith("SharpTicker", StringComparison.Ordinal)));

    private sealed class Logger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => entries.Enqueue(new LogEntry(category, logLevel, eventId,
                $"{formatter(state, exception)}{(exception == null ? "" : " | " + exception.GetType().FullName + ": " + exception.Message)}"));
    }
}

/// <summary>
/// Holds the first store call that gets a token which can be cancelled, once armed. With <c>releaseAfter</c> it lets the
/// call go on after that time without a cancel (the control); otherwise it waits for the cancel of the call's token.
/// Calls with a token that cannot be cancelled (the application's own writes, the release at a stop) are not held.
/// </summary>
public sealed class CancelledStoreCallProbe : DbConnectionInterceptor, IDbCommandInterceptor
{
    /// <summary>Put in front of the command that <see cref="StoreCallHold.CommandInFlight"/> sends (MySQL).</summary>
    public string InFlightPrefix { get; set; } = "DO SLEEP(10); ";

    private int _armed;
    private StoreCallHold _what;
    private TimeSpan? _releaseAfter;
    private DbCommand? _inFlight;

    public TaskCompletionSource Held { get; private set; } = NewSignal();
    public TaskCompletionSource Released { get; private set; } = NewSignal();
    public bool ReleasedByCancel { get; private set; }

    /// <summary>What the provider threw for each failed open or command, as EF Core hands it to the interceptors.</summary>
    public ConcurrentQueue<string> Failures { get; } = new();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Arm(StoreCallHold what, TimeSpan? releaseAfter = null)
    {
        _what = what;
        _releaseAfter = releaseAfter;
        _inFlight = null;
        Held = NewSignal();
        Released = NewSignal();
        ReleasedByCancel = false;
        Volatile.Write(ref _armed, 1);
    }

    private bool Take(StoreCallHold what, CancellationToken cancellationToken)
        => _what == what && cancellationToken.CanBeCanceled && !cancellationToken.IsCancellationRequested
           && Interlocked.CompareExchange(ref _armed, 0, 1) == 1;

    private async Task HoldAsync(StoreCallHold what, CancellationToken cancellationToken)
    {
        if (!Take(what, cancellationToken))
            return;

        Held.TrySetResult();
        try
        {
            await Task.Delay(_releaseAfter ?? TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        ReleasedByCancel = cancellationToken.IsCancellationRequested;
        Released.TrySetResult();
    }

    private void SendInFlight(DbCommand command, CancellationToken cancellationToken)
    {
        if (!Take(StoreCallHold.CommandInFlight, cancellationToken))
            return;

        _inFlight = command;
        command.CommandText = InFlightPrefix + command.CommandText;
        Held.TrySetResult();
    }

    private void EndInFlight(DbCommand command, bool cancelled)
    {
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _inFlight, null, command), command))
            return;

        ReleasedByCancel = cancelled;
        Released.TrySetResult();
    }

    public override Task ConnectionFailedAsync(DbConnection connection, ConnectionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Failures.Enqueue($"open: {eventData.Exception.GetType().FullName}: {eventData.Exception.Message}");
        return Task.CompletedTask;
    }

    public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
        InterceptionResult result, CancellationToken cancellationToken = default)
    {
        await HoldAsync(StoreCallHold.Open, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        SendInFlight(command, cancellationToken);
        await HoldAsync(StoreCallHold.Command, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        SendInFlight(command, cancellationToken);
        await HoldAsync(StoreCallHold.Command, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        EndInFlight(command, cancellationToken.IsCancellationRequested);
        return ValueTask.FromResult(result);
    }

    public ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        EndInFlight(command, cancellationToken.IsCancellationRequested);
        return ValueTask.FromResult(result);
    }

    public Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Failures.Enqueue($"command: {eventData.Exception.GetType().FullName}: {eventData.Exception.Message}");
        EndInFlight(command, cancellationToken.IsCancellationRequested);
        return Task.CompletedTask;
    }

    public Task CommandCanceledAsync(DbCommand command, CommandEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Failures.Enqueue("command: canceled (EF Core's CommandCanceled)");
        EndInFlight(command, true);
        return Task.CompletedTask;
    }
}
