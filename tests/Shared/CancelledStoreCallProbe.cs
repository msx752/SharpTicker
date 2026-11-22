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
    CommandInFlight,

    /// <summary>
    /// The close of a connection whose call has ended: held until the token the connection was opened with is cancelled,
    /// then the call returns its result. The caller's next store call starts after the cancel.
    /// </summary>
    AfterClose
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

    /// <summary>
    /// EF Core 8 and 9 log an open whose token is cancelled as ConnectionError (20004) at Error; EF Core 10 does not log it
    /// at Error.
    /// </summary>
    public static bool EfCoreLogsACancelledOpenAtError
        => typeof(RelationalEventId).Assembly.GetName().Version!.Major < 10;

    /// <summary>The entries from SharpTicker above Debug.</summary>
    public LogEntry[] SharpTickerAboveDebug() => Entries
        .Where(e => e.Level > LogLevel.Debug && e.Category.StartsWith("SharpTicker", StringComparison.Ordinal))
        .ToArray();

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

    /// <summary>
    /// Once the hold began, no connection but the held call's is open: a call that runs beside the held one (the scheduler
    /// loop reads the cron and the time jobs at once) has ended or has not opened yet. The tests wait a little after it, so
    /// such a call ends before the cancel instead of having it land inside it.
    /// </summary>
    public TaskCompletionSource OtherCallEnded { get; private set; } = NewSignal();
    public bool ReleasedByCancel { get; private set; }

    /// <summary>What the provider threw for each failed open or command, as EF Core hands it to the interceptors.</summary>
    public ConcurrentQueue<string> Failures { get; } = new();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="what">Where the call is held.</param>
    /// <param name="releaseAfter">Set: the call goes on after this time without a cancel (the control).</param>
    /// <param name="nth">The how-many-th call with a token that can be cancelled is held (1: the first one).</param>
    public void Arm(StoreCallHold what, TimeSpan? releaseAfter = null, int nth = 1)
    {
        _what = what;
        _releaseAfter = releaseAfter;
        _inFlight = null;
        _closing = null;
        _heldConnection = null;
        _skip = nth - 1;
        Held = NewSignal();
        Released = NewSignal();
        OtherCallEnded = NewSignal();
        ReleasedByCancel = false;
        Volatile.Write(ref _armed, 1);
    }

    private int _skip;

    private bool Take(StoreCallHold what, CancellationToken cancellationToken)
    {
        if (_what != what || !cancellationToken.CanBeCanceled || cancellationToken.IsCancellationRequested || Volatile.Read(ref _armed) != 1)
            return false;
        if (Interlocked.Decrement(ref _skip) >= 0)
            return false;
        return Interlocked.CompareExchange(ref _armed, 0, 1) == 1;
    }

    private async Task HoldAsync(StoreCallHold what, DbConnection? connection, CancellationToken cancellationToken)
    {
        if (!Take(what, cancellationToken))
            return;

        MarkHeld(connection);
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
        MarkHeld(command.Connection);
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
        if (Take(StoreCallHold.AfterClose, cancellationToken))
        {
            _closeToken = cancellationToken;
            Volatile.Write(ref _closing, connection);
        }

        await HoldAsync(StoreCallHold.Open, connection, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private DbConnection? _closing;
    private CancellationToken _closeToken;

    // The connections that are open now, and the one the held call uses (a held command's).
    private readonly ConcurrentDictionary<DbConnection, byte> _open = new();
    private DbConnection? _heldConnection;

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        _open[connection] = 0;
        return Task.CompletedTask;
    }

    // Called once the hold began and after each close: no connection but the held call's is open.
    private void SignalWhenNoOtherCallIsOpen()
    {
        if (!Held.Task.IsCompleted)
            return;

        var held = Volatile.Read(ref _heldConnection);
        if (_open.Keys.All(c => ReferenceEquals(c, held)))
            OtherCallEnded.TrySetResult();
    }

    private void MarkHeld(DbConnection? connection)
    {
        Volatile.Write(ref _heldConnection, connection);
        Held.TrySetResult();
        SignalWhenNoOtherCallIsOpen();
    }

    public override async Task ConnectionClosedAsync(DbConnection connection, ConnectionEndEventData eventData)
    {
        _open.TryRemove(connection, out _);
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _closing, null, connection), connection))
        {
            SignalWhenNoOtherCallIsOpen();
            return;
        }

        MarkHeld(null);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), _closeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        ReleasedByCancel = _closeToken.IsCancellationRequested;
        Released.TrySetResult();
    }

    public async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        SendInFlight(command, cancellationToken);
        await HoldAsync(StoreCallHold.Command, command.Connection, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        SendInFlight(command, cancellationToken);
        await HoldAsync(StoreCallHold.Command, command.Connection, cancellationToken).ConfigureAwait(false);
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
