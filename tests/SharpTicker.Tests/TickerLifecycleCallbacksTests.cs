using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpTicker.DependencyInjection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Tests;

/// <summary>
/// <see cref="TickerLifecycleCallbacks"/> on its own: what the stop's wait does with an end call that is still running, that
/// a status that is not final makes no call, and that a handler which cannot be built is logged and goes no further.
/// </summary>
public class TickerLifecycleCallbacksTests
{
    private sealed class WaitsForItsToken : ITickerLifecycleHandler
    {
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task OnCompletedAsync(TickerLifecycleContext context, CancellationToken cancellationToken)
        {
            Called.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class Counts : ITickerLifecycleHandler
    {
        public int Calls;

        public Task OnStartingAsync(TickerLifecycleContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.CompletedTask;
        }

        public Task OnCompletedAsync(TickerLifecycleContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.CompletedTask;
        }
    }

    private sealed class CannotBeBuilt : ITickerLifecycleHandler
    {
        public CannotBeBuilt() => throw new InvalidOperationException("no dependency");
    }

    private sealed class RecordingLogger : ILogger<TickerLifecycleCallbacks>
    {
        public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((logLevel, formatter(state, exception), exception));
    }

    private static (TickerLifecycleCallbacks Callbacks, RecordingLogger Log) Create<THandler>(THandler? instance, TimeSpan timeout)
        where THandler : class, ITickerLifecycleHandler
    {
        var services = new ServiceCollection();
        if (instance != null)
            services.AddSingleton<ITickerLifecycleHandler>(instance);
        else
            services.AddSingleton<ITickerLifecycleHandler, THandler>();
        var log = new RecordingLogger();
        return (new TickerLifecycleCallbacks(services.BuildServiceProvider(), typeof(THandler), timeout, log), log);
    }

    private static InternalFunctionContext Ended(TickerStatus status)
        => new() { TickerId = Guid.NewGuid(), FunctionName = "LifecycleCallbacks.Job", Type = TickerType.TimeTicker, Status = status };

    [Fact]
    public async Task An_End_Call_Still_Running_When_The_Stop_Ends_Its_Wait_Is_Cancelled_And_Counted()
    {
        var handler = new WaitsForItsToken();
        var (callbacks, log) = Create(handler, TimeSpan.FromMinutes(1));

        callbacks.Completed(Ended(TickerStatus.Done));
        await handler.Called.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, callbacks.RunningCompletedCalls);

        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await callbacks.WaitForCompletedCallsAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(10));

        await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning && e.Message.StartsWith("The application stopped while"));
        Assert.Contains("1 call(s)", warning.Message);
    }

    [Fact]
    public async Task The_Stop_Waits_For_An_End_Call_That_Ends_In_Time()
    {
        var handler = new Counts();
        var (callbacks, log) = Create(handler, TimeSpan.FromMinutes(1));

        callbacks.Completed(Ended(TickerStatus.Failed));
        await callbacks.WaitForCompletedCallsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, callbacks.RunningCompletedCalls);
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Theory]
    [InlineData(TickerStatus.Idle)]
    [InlineData(TickerStatus.Queued)]
    [InlineData(TickerStatus.InProgress)]
    public async Task A_Status_That_Is_Not_Final_Makes_No_End_Call(TickerStatus status)
    {
        var handler = new Counts();
        var (callbacks, _) = Create(handler, TimeSpan.FromMinutes(1));

        callbacks.Completed(Ended(status));
        await callbacks.WaitForCompletedCallsAsync(CancellationToken.None);

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_Handler_That_Cannot_Be_Built_Is_Logged_And_The_Start_Returns()
    {
        var (callbacks, log) = Create<CannotBeBuilt>(null, TimeSpan.FromMinutes(1));

        await callbacks.StartingAsync(Ended(TickerStatus.InProgress)).WaitAsync(TimeSpan.FromSeconds(10));
        callbacks.Completed(Ended(TickerStatus.Done));
        await callbacks.WaitForCompletedCallsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        var entries = log.Entries.Where(e => e.Level == LogLevel.Warning).ToArray();
        Assert.Equal(2, entries.Length);
        Assert.All(entries, e => Assert.Contains("no dependency", e.Exception?.ToString()));
    }

    private sealed class RecordsItsToken : ITickerLifecycleHandler
    {
        public ConcurrentQueue<CancellationToken> Tokens { get; } = new();
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task OnCompletedAsync(TickerLifecycleContext context, CancellationToken cancellationToken)
        {
            Tokens.Enqueue(cancellationToken);
            await Release.Task;
        }
    }

    [Fact]
    public async Task A_Call_After_A_Stop_Gets_A_Token_That_Is_Not_Cancelled()
    {
        var handler = new RecordsItsToken();
        handler.Release.SetResult();
        var (callbacks, _) = Create(handler, TimeSpan.FromMinutes(1));

        await callbacks.WaitForCompletedCallsAsync(CancellationToken.None);
        callbacks.Completed(Ended(TickerStatus.Done));
        await callbacks.WaitForCompletedCallsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        var token = Assert.Single(handler.Tokens);
        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public async Task The_Stop_Waits_For_An_End_Call_That_Starts_While_It_Waits()
    {
        var first = new RecordsItsToken();
        var (callbacks, log) = Create(first, TimeSpan.FromMinutes(1));

        callbacks.Completed(Ended(TickerStatus.Done));
        var wait = callbacks.WaitForCompletedCallsAsync(CancellationToken.None);
        // A job ends while the stop waits; then the first call returns.
        callbacks.Completed(Ended(TickerStatus.Done));
        first.Release.SetResult();
        await wait.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, first.Tokens.Count);
        Assert.All(first.Tokens, t => Assert.False(t.IsCancellationRequested));
        Assert.Equal(0, callbacks.RunningCompletedCalls);
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task A_Handler_Left_Behind_At_Its_Timeout_Has_Its_Token_Cancelled()
    {
        var handler = new RecordsItsToken();
        var (callbacks, log) = Create(handler, TimeSpan.FromMilliseconds(200));

        callbacks.Completed(Ended(TickerStatus.Done));
        await callbacks.WaitForCompletedCallsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        var token = Assert.Single(handler.Tokens);
        Assert.True(token.IsCancellationRequested);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("did not end within"));
        handler.Release.SetResult();
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    public void A_Timeout_Out_Of_Range_Is_Refused(long ticks)
    {
        var services = new ServiceCollection();
        services.AddSharpTicker(o => Assert.Throws<ArgumentOutOfRangeException>(() => o.SetLifecycleHandlerTimeout(TimeSpan.FromTicks(ticks))));
    }

    [Fact]
    public void The_Longest_Timeout_Is_Accepted()
    {
        var services = new ServiceCollection();
        services.AddSharpTicker(o => o.SetLifecycleHandlerTimeout(TimeSpan.FromMilliseconds(int.MaxValue)));
    }
}
