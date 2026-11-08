using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SharpTicker.DependencyInjection;
using SharpTicker.Dispatcher;
using SharpTicker.Provider;
using SharpTicker.SharpTickerThreadPool;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Base;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Instrumentation;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Models;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// A job that runs the moment it is added is not tied to the request that added it. The dashboard adds a ticker inside
/// an HTTP request and the job finishes after the response, when the request's cancellation token has fired. Before
/// this was fixed the job was handed that token, so it was cancelled, or skipped without ever running, and stayed
/// InProgress; when it did run, reading its request failed on the cancelled token and it ran with an empty request.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class ImmediateJobLifetimeTests : IAsyncLifetime
{
    public sealed record LifetimeOrder(string OrderId, int Quantity);

    private WebApplication? _app;

    public Task InitializeAsync() => ClearStoreAsync();

    public async Task DisposeAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        await ClearStoreAsync();
    }

    private static async Task ClearStoreAsync()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var store = new TickerInMemoryPersistenceProvider<TimeTickerEntity, CronTickerEntity>(services);
        var occurrences = await store.GetAllCronTickerOccurrences(null, default);
        await store.RemoveCronTickerOccurrences(occurrences.Select(o => o.Id).ToArray(), default);
        var crons = await store.GetCronTickers(null, default);
        await store.RemoveCronTickers(crons.Select(c => c.Id).ToArray(), default);
        var times = await store.GetTimeTickers(null, default);
        await store.RemoveTimeTickers(times.Select(t => t.Id).ToArray());
    }

    private async Task StartHostAsync(TimeSpan? fallbackInterval = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        if (fallbackInterval is { } interval)
            builder.Services.AddSharpTicker(o => o.ConfigureScheduler(s => s.FallbackIntervalChecker = interval));
        else
            builder.Services.AddSharpTicker();
        _app = builder.Build();
        _app.UseSharpTicker();
        await _app.StartAsync();
    }

    private ITimeTickerManager<TimeTickerEntity> Times => _app!.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();

    private async Task<TimeTickerEntity?> WaitForStatusAsync(Guid id, params TickerStatus[] wanted)
    {
        var store = _app!.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        TimeTickerEntity? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await store.GetTimeTickerById(id);
            if (last != null && wanted.Contains(last.Status))
                return last;
            await Task.Delay(50);
        }
        return last;
    }

    [Fact]
    public async Task AJobAddedWithARequestToken_IsNotCancelledWhenThatTokenIsCancelledAfterwards()
    {
        var seen = new TaskCompletionSource<(bool CancelledAtStart, bool Finished)>(TaskCreationOptions.RunContinuationsAsynchronously);
        new ServiceCollection().MapTicker<LifetimeOrder>("Lifetime.Slow", async (ctx, ct) =>
        {
            var cancelledAtStart = ct.IsCancellationRequested;
            await Task.Delay(400, ct);           // long enough for the "request" to end first
            seen.TrySetResult((cancelledAtStart, true));
        });
        TickerFunctionProvider.Build();
        await StartHostAsync();

        using var request = new CancellationTokenSource();
        var added = await Times.AddAsync(new TimeTickerEntity { Function = "Lifetime.Slow", ExecutionTime = DateTime.UtcNow }, request.Token);
        request.Cancel();                        // what ASP.NET does when the request that added the job is over

        Assert.True(added.IsSucceeded, added.Exception?.ToString());
        var finished = await Task.WhenAny(seen.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(seen.Task, finished);
        Assert.True((await seen.Task).Finished);
        var stored = await WaitForStatusAsync(added.Result.Id, TickerStatus.Done, TickerStatus.Failed, TickerStatus.Cancelled);
        Assert.Equal(TickerStatus.Done, stored!.Status);
    }

    // A delayed chain child whose time passed while its parent ran starts right after its parent, not at the next fallback
    // pass (set far off here): the window only takes rows due within the last second.
    [Fact]
    public async Task AnOverdueDelayedChild_StartsRightAfterItsParent_NotAtTheNextFallbackPass()
    {
        var parentEnded = new TaskCompletionSource<DateTime>(TaskCreationOptions.RunContinuationsAsynchronously);
        var childStarted = new TaskCompletionSource<DateTime>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.MapTicker<LifetimeOrder>("HandOff.Parent", async (ctx, ct) =>
        {
            await Task.Delay(2000, ct);
            parentEnded.TrySetResult(DateTime.UtcNow);
        });
        services.MapTicker<LifetimeOrder>("HandOff.Child", (ctx, ct) =>
        {
            childStarted.TrySetResult(DateTime.UtcNow);
            return Task.CompletedTask;
        });
        TickerFunctionProvider.Build();
        await StartHostAsync(fallbackInterval: TimeSpan.FromMinutes(10));

        var now = DateTime.UtcNow;
        var child = new TimeTickerEntity { Function = "HandOff.Child", ExecutionTime = now.AddMilliseconds(200), RunCondition = RunCondition.OnSuccess };
        var added = await Times.AddAsync(new TimeTickerEntity { Function = "HandOff.Parent", ExecutionTime = now, Children = [child] });

        Assert.True(added.IsSucceeded, added.Exception?.ToString());
        Assert.Same(childStarted.Task, await Task.WhenAny(childStarted.Task, Task.Delay(TimeSpan.FromSeconds(15))));
        Assert.True(await childStarted.Task >= await parentEnded.Task, "the child started before its parent ended");
        var stored = await WaitForStatusAsync(child.Id, TickerStatus.Done, TickerStatus.DueDone, TickerStatus.Failed);
        Assert.Equal(TickerStatus.Done, stored!.Status);
    }

    [Fact]
    public async Task AJobAddedWithATokenThatIsAlreadyCancelled_StillRunsAndFinishes()
    {
        var ran = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        new ServiceCollection().MapTicker<LifetimeOrder>("Lifetime.Plain", (ctx, ct) =>
        {
            ran.TrySetResult(true);
            return Task.CompletedTask;
        });
        TickerFunctionProvider.Build();
        await StartHostAsync();

        // The ticker is stored and marked InProgress before it is dispatched, so a dropped job would stay InProgress.
        using var request = new CancellationTokenSource();
        var persistenceOnly = new TimeTickerEntity { Function = "Lifetime.Plain", ExecutionTime = DateTime.UtcNow };
        var added = await Times.AddAsync(persistenceOnly, request.Token);
        request.Cancel();

        Assert.True(added.IsSucceeded, added.Exception?.ToString());
        Assert.Same(ran.Task, await Task.WhenAny(ran.Task, Task.Delay(TimeSpan.FromSeconds(15))));
        var stored = await WaitForStatusAsync(added.Result.Id, TickerStatus.Done, TickerStatus.Failed, TickerStatus.Cancelled);
        Assert.Equal(TickerStatus.Done, stored!.Status);
    }

    // A job run now waits for its function's only slot, which a running job holds, and the application stops meanwhile. The
    // waiting job never started; its row was marked InProgress when it was dispatched and is put back, not left InProgress
    // with no worker running it.
    [Fact]
    public async Task StoppingWhileARunNowJobWaitsForItsConcurrencySlot_PutsItsRowBack()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        new ServiceCollection().MapTicker<LifetimeOrder>("Lifetime.OneAtATime", async (ctx, ct) =>
        {
            firstStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }).WithMaxConcurrency(1);
        TickerFunctionProvider.Build();
        await StartHostAsync();
        var store = _app!.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();

        var first = await Times.AddAsync(new TimeTickerEntity { Function = "Lifetime.OneAtATime", ExecutionTime = DateTime.UtcNow });
        Assert.True(first.IsSucceeded, first.Exception?.ToString());
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var second = await Times.AddAsync(new TimeTickerEntity { Function = "Lifetime.OneAtATime", ExecutionTime = DateTime.UtcNow });
        Assert.True(second.IsSucceeded, second.Exception?.ToString());
        Assert.Equal(TickerStatus.InProgress, (await WaitForStatusAsync(second.Result.Id, TickerStatus.InProgress))!.Status);

        await _app.StopAsync();

        var row = await store.GetTimeTickerById(second.Result.Id);
        Assert.Equal(TickerStatus.Idle, row.Status);
        Assert.Null(row.LockHolder);
    }

    [Fact]
    public async Task TheDispatcher_GivesTheJobATokenThatTheCallersTokenDoesNotCancel()
    {
        await using var scheduler = new SharpTickerTaskScheduler(2);
        var handler = Substitute.For<ITickerExecutionTaskHandler>();
        var gate = Substitute.For<ITickerFunctionConcurrencyGate>();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call => { started.TrySetResult(call.ArgAt<CancellationToken>(2)); return Task.CompletedTask; });
        var dispatcher = new SharpTickerDispatcher(scheduler, handler, gate);

        using var caller = new CancellationTokenSource();
        await dispatcher.DispatchAsync(new[] { new InternalFunctionContext { TickerId = Guid.NewGuid(), FunctionName = "Any", CachedPriority = TickerTaskPriority.Normal } }, caller.Token);
        caller.Cancel();

        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public async Task TheDispatcher_StillRunsAJob_WhenTheCallersTokenIsAlreadyCancelled()
    {
        await using var scheduler = new SharpTickerTaskScheduler(2);
        var handler = Substitute.For<ITickerExecutionTaskHandler>();
        var gate = Substitute.For<ITickerFunctionConcurrencyGate>();
        var ran = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.ExecuteTaskAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => { ran.TrySetResult(true); return Task.CompletedTask; });
        var dispatcher = new SharpTickerDispatcher(scheduler, handler, gate);

        using var caller = new CancellationTokenSource();
        caller.Cancel();
        await dispatcher.DispatchAsync(new[] { new InternalFunctionContext { TickerId = Guid.NewGuid(), FunctionName = "Any", CachedPriority = TickerTaskPriority.Normal } }, caller.Token);

        Assert.True(await ran.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ReadingARequest_PropagatesCancellation_InsteadOfRunningTheJobWithAnEmptyRequest()
    {
        var manager = Substitute.For<IInternalTickerManager>();
        manager.GetRequestAsync<LifetimeOrder>(Arg.Any<Guid>(), Arg.Any<TickerType>(), Arg.Any<CancellationToken>())
            .Returns<Task<LifetimeOrder>>(_ => throw new TaskCanceledException("A task was canceled."));
        var services = new ServiceCollection();
        services.AddSingleton(manager);
        services.AddSingleton(Substitute.For<ISharpTickerInstrumentation>());
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var context = new TickerFunctionContext { Id = Guid.NewGuid(), FunctionName = "Any", Type = TickerType.TimeTicker };
        context.SetServiceScope(scope);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => TickerRequestProvider.GetRequestAsync<LifetimeOrder>(context, cancelled.Token));
    }

    [Fact]
    public async Task ReadingARequest_StillReturnsNothing_WhenItCannotBeDeserialized()
    {
        var manager = Substitute.For<IInternalTickerManager>();
        manager.GetRequestAsync<LifetimeOrder>(Arg.Any<Guid>(), Arg.Any<TickerType>(), Arg.Any<CancellationToken>())
            .Returns<Task<LifetimeOrder>>(_ => throw new System.Text.Json.JsonException("bad json"));
        var instrumentation = Substitute.For<ISharpTickerInstrumentation>();
        var services = new ServiceCollection();
        services.AddSingleton(manager);
        services.AddSingleton(instrumentation);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var context = new TickerFunctionContext { Id = Guid.NewGuid(), FunctionName = "Any", Type = TickerType.TimeTicker };
        context.SetServiceScope(scope);

        var request = await TickerRequestProvider.GetRequestAsync<LifetimeOrder>(context, CancellationToken.None);

        Assert.Null(request);
        instrumentation.Received(1).LogRequestDeserializationFailure(
            Arg.Any<string>(), "Any", context.Id, TickerType.TimeTicker, Arg.Any<Exception>());
    }
}
