using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Instrumentation;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Models;
using Xunit.Abstractions;

namespace SharpTicker.Tests;

/// <summary>
/// What one successful run of a job allocates in the execution handler, with the store and the instrumentation as
/// substitutes. The run sets the job's status at its start and its time, duration and status at its end. Each of those set
/// with <c>SetProperty</c> built an expression tree and boxed the value, about 700 B a field: about 8.3 KB a run in all
/// (net8.0 to net10.0), against about 5.2 KB with the setters that take no expression. The bound sits between the two.
/// </summary>
[Collection("TickerCancellationTokenState")]
public class JobRunAllocationTests(ITestOutputHelper output) : IDisposable
{
    private const int Runs = 200;
    private const double BoundPerRun = 6_800;

    public void Dispose() => TickerCancellationTokenManager.CleanUpTickerCancellationTokens();

    [Fact]
    public async Task A_Successful_Run_Allocates_Less_Than_The_Bound()
    {
        var manager = Substitute.For<IInternalTickerManager>();
        manager.UpdateTickerAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>()).Returns(true);
        var clock = Substitute.For<ITickerClock>();
        clock.UtcNow.Returns(DateTime.UtcNow);
        var instrumentation = Substitute.For<ISharpTickerInstrumentation>();
        var dispatcher = Substitute.For<ISharpTickerDispatcher>();
        dispatcher.IsEnabled.Returns(true);
        var services = new ServiceCollection();
        services.AddSingleton(manager);
        services.AddSingleton(instrumentation);
        services.AddSingleton(dispatcher);
        services.AddSingleton(Substitute.For<ISharpTickerHostScheduler>());
        var handler = new TickerExecutionTaskHandler(services.BuildServiceProvider(), clock, instrumentation, manager);

        static InternalFunctionContext Job() => new()
        {
            TickerId = Guid.NewGuid(),
            FunctionName = "fn",
            Type = TickerType.TimeTicker,
            RetryIntervals = [],
            Status = TickerStatus.Idle,
            CachedDelegate = (_, _, _) => Task.CompletedTask,
            TimeTickerChildren = []
        };

        // Warm up: the first runs fill caches and compile what they use once.
        for (var i = 0; i < 50; i++)
            await handler.ExecuteTaskAsync(Job(), isDue: false);
        manager.ClearReceivedCalls();

        var jobs = Enumerable.Range(0, Runs).Select(_ => Job()).ToArray();
        var thread = Environment.CurrentManagedThreadId;
        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var job in jobs)
            await handler.ExecuteTaskAsync(job, isDue: false);
        var perRun = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Runs;
        // Counted on this thread only, so tests running in parallel do not count; the runs complete synchronously here.
        Assert.Equal(thread, Environment.CurrentManagedThreadId);
        output.WriteLine($"allocated per run: {perRun:0} B");

        Assert.All(jobs, job => Assert.Equal(TickerStatus.Done, job.Status));
        Assert.True(perRun < BoundPerRun, $"one successful run allocated {perRun:0} B, not under {BoundPerRun:0} B");
    }
}
