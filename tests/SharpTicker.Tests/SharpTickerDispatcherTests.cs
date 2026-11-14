using NSubstitute;
using SharpTicker.Dispatcher;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Tests;

public class SharpTickerDispatcherTests
{
    private readonly ISharpTickerTaskScheduler _taskScheduler;
    private readonly ITickerExecutionTaskHandler _taskHandler;
    private readonly SharpTickerDispatcher _dispatcher;

    public SharpTickerDispatcherTests()
    {
        _taskScheduler = Substitute.For<ISharpTickerTaskScheduler>();
        _taskHandler = Substitute.For<ITickerExecutionTaskHandler>();
        _dispatcher = new SharpTickerDispatcher(_taskScheduler, _taskHandler, new TickerFunctionConcurrencyGate());
    }

    // A claimed run-now job goes through the claimed-work queue and only there: it runs exactly once, not also on the path
    // for jobs without a claim marker.
    [Fact]
    public async Task DispatchAsync_ClaimedJob_RunsExactlyOnce()
    {
        var running = new List<Task>();
        _taskScheduler.QueueAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<TickerTaskPriority>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                running.Add(call.ArgAt<Func<CancellationToken, Task>>(0)(CancellationToken.None));
                return ValueTask.CompletedTask;
            });
        var runs = 0;
        _taskHandler.ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => { Interlocked.Increment(ref runs); return Task.CompletedTask; });
        var gate = new TickerFunctionConcurrencyGate();
        var work = new SharpTicker.BackgroundServices.ClaimedWorkQueue(
            Substitute.For<SharpTicker.Utilities.Interfaces.Managers.IInternalTickerManager>(), _taskScheduler, _taskHandler, gate);
        var dispatcher = new SharpTickerDispatcher(_taskScheduler, _taskHandler, gate, null, null, work);

        await dispatcher.DispatchAsync([new InternalFunctionContext { TickerId = Guid.NewGuid(), FunctionName = "fn", ClaimMarker = DateTime.UtcNow }]);
        await Task.WhenAll(running);

        Assert.Equal(1, runs);
        Assert.Equal(0, work.Claims.Count);
    }

    // A claimed run-now job returns to its caller only once the task scheduler has accepted it, as an unclaimed one does,
    // so a full scheduler slows the callers down instead of piling up claimed rows.
    [Fact]
    public async Task DispatchAsync_ClaimedJob_WaitsUntilTheSchedulerAcceptsIt()
    {
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _taskScheduler.QueueAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<TickerTaskPriority>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask(accepted.Task));
        var gate = new TickerFunctionConcurrencyGate();
        var work = new SharpTicker.BackgroundServices.ClaimedWorkQueue(
            Substitute.For<SharpTicker.Utilities.Interfaces.Managers.IInternalTickerManager>(), _taskScheduler, _taskHandler, gate);
        var dispatcher = new SharpTickerDispatcher(_taskScheduler, _taskHandler, gate, null, null, work);
        var context = new InternalFunctionContext { TickerId = Guid.NewGuid(), FunctionName = "fn", ClaimMarker = DateTime.UtcNow };

        var dispatch = dispatcher.DispatchAsync([context]);
        await Task.Delay(100);
        Assert.False(dispatch.IsCompleted);

        accepted.SetResult();
        await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // A job without a claim marker waits for its function's only slot, and the application stops. The job that holds the slot
    // ends on that stop and frees the slot before the stop reaches the waiting job, so its wait succeeds although the stop has
    // begun. It must not start: it would run with a cancelled token and end Cancelled (#88). The control: a slot freed without
    // a stop starts the waiting job.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DispatchAsync_UnclaimedJob_ASlotFreedByTheStop_DoesNotStartTheWaitingJob(bool freedByTheStop)
    {
        var running = new List<Task>();
        _taskScheduler.QueueAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<TickerTaskPriority>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                running.Add(call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(2)));
                return ValueTask.CompletedTask;
            });
        var startedWith = new List<bool>();
        _taskHandler.ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call => { lock (startedWith) startedWith.Add(call.ArgAt<CancellationToken>(2).IsCancellationRequested); return Task.CompletedTask; });
        var gate = new TickerFunctionConcurrencyGate();
        var function = "Dispatcher.SlotFreedByTheStop." + freedByTheStop;
        var slot = gate.GetSemaphoreOrNull(function, 1)!;
        await slot.WaitAsync(); // another run of the same function holds the only slot
        using var stopping = new CancellationTokenSource();
        var lifetime = Substitute.For<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(stopping.Token);
        var dispatcher = new SharpTickerDispatcher(_taskScheduler, _taskHandler, gate, lifetime);

        await dispatcher.DispatchAsync([new InternalFunctionContext
        {
            TickerId = Guid.NewGuid(), FunctionName = function, CachedMaxConcurrency = 1, CachedPriority = TickerTaskPriority.Normal
        }]);
        if (freedByTheStop)
        {
            // Registered after the waiting job's wait, so it runs first: the holder ends on the stop and frees the slot.
            stopping.Token.Register(() => slot.Release());
            stopping.Cancel();
        }
        else
        {
            slot.Release();
        }
        await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(5));

        if (freedByTheStop)
            Assert.True(startedWith.Count == 0, $"the job started after the stop began; its token was cancelled: {string.Join(", ", startedWith)}");
        else
            Assert.Equal([false], startedWith);
        Assert.Equal(1, slot.CurrentCount);
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenTaskSchedulerIsNull()
    {
        var act = () => new SharpTickerDispatcher(null!, _taskHandler, new TickerFunctionConcurrencyGate());

        var ex = Assert.Throws<ArgumentNullException>(act);
        Assert.Equal("taskScheduler", ex.ParamName);
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenTaskHandlerIsNull()
    {
        var act = () => new SharpTickerDispatcher(_taskScheduler, null!, new TickerFunctionConcurrencyGate());

        var ex = Assert.Throws<ArgumentNullException>(act);
        Assert.Equal("taskHandler", ex.ParamName);
    }

    [Fact]
    public void IsEnabled_ReturnsTrue()
    {
        Assert.True(_dispatcher.IsEnabled);
    }

    [Fact]
    public async Task DispatchAsync_DoesNothing_WhenContextsIsNull()
    {
        await _dispatcher.DispatchAsync(null!);

        await _taskScheduler.DidNotReceive().QueueAsync(
            Arg.Any<Func<CancellationToken, Task>>(),
            Arg.Any<TickerTaskPriority>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_DoesNothing_WhenContextsIsEmpty()
    {
        await _dispatcher.DispatchAsync([]);

        await _taskScheduler.DidNotReceive().QueueAsync(
            Arg.Any<Func<CancellationToken, Task>>(),
            Arg.Any<TickerTaskPriority>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_QueuesEachContext_InTaskScheduler()
    {
        var contexts = new[]
        {
            new InternalFunctionContext
            {
                TickerId = Guid.NewGuid(),
                FunctionName = "Func1",
                CachedPriority = TickerTaskPriority.Normal,
                TimeTickerChildren = []
            },
            new InternalFunctionContext
            {
                TickerId = Guid.NewGuid(),
                FunctionName = "Func2",
                CachedPriority = TickerTaskPriority.High,
                TimeTickerChildren = []
            }
        };

        await _dispatcher.DispatchAsync(contexts);

        await _taskScheduler.Received(2).QueueAsync(
            Arg.Any<Func<CancellationToken, Task>>(),
            Arg.Any<TickerTaskPriority>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_UsesContextPriority_WhenQueuing()
    {
        var context = new InternalFunctionContext
        {
            TickerId = Guid.NewGuid(),
            FunctionName = "Func1",
            CachedPriority = TickerTaskPriority.LongRunning,
            TimeTickerChildren = []
        };

        await _dispatcher.DispatchAsync([context]);

        await _taskScheduler.Received(1).QueueAsync(
            Arg.Any<Func<CancellationToken, Task>>(),
            Arg.Is(TickerTaskPriority.LongRunning),
            Arg.Any<CancellationToken>());
    }
}
