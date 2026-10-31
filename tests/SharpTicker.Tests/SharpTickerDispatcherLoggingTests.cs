using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SharpTicker.Dispatcher;
using SharpTicker.SharpTickerThreadPool;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Models;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// A job that is dispatched while the application is already stopping never starts: the scheduler skips work whose token is
/// cancelled. Its ticker was marked InProgress before it was dispatched, so the row stays InProgress. That is not changed
/// here, but it must not happen without a word in the log.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class SharpTickerDispatcherLoggingTests
{
    private readonly CapturedLogs _logs = new();

    private async Task<(ITickerExecutionTaskHandler Handler, InternalFunctionContext Context)> DispatchAsync(bool stopping)
    {
        var handler = Substitute.For<ITickerExecutionTaskHandler>();
        handler.ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(new CancellationToken(stopping));
        using var loggerFactory = new LoggerFactory(new[] { _logs });
        await using var scheduler = new SharpTickerTaskScheduler(1);
        var dispatcher = new SharpTickerDispatcher(scheduler, handler, Substitute.For<ITickerFunctionConcurrencyGate>(), lifetime,
            loggerFactory.CreateLogger<SharpTickerDispatcher>());
        var context = new InternalFunctionContext { TickerId = Guid.NewGuid(), FunctionName = "SendInvoices", CachedPriority = TickerTaskPriority.Normal };

        await dispatcher.DispatchAsync(new[] { context });
        await Task.Delay(300);
        return (handler, context);
    }

    [Fact]
    public async Task JobsDispatchedWhileTheApplicationIsStopping_AreLogged_WithTheirFunctionAndId()
    {
        var (handler, context) = await DispatchAsync(stopping: true);

        var warning = Assert.Single(_logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("SendInvoices", warning.Message);
        Assert.Contains(context.TickerId.ToString(), warning.Message);
        Assert.Equal(0, handler.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "ExecuteTaskAsync"));
    }

    [Fact]
    public async Task JobsDispatchedWhileTheApplicationRuns_AreStartedAndNothingIsLogged()
    {
        var (handler, _) = await DispatchAsync(stopping: false);

        Assert.Empty(_logs.Entries);
        Assert.Equal(1, handler.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "ExecuteTaskAsync"));
    }
}
