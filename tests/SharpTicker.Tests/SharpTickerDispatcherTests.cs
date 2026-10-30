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
