using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpTicker.DependencyInjection;
using SharpTicker.Provider;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;

namespace SharpTicker.Tests;

/// <summary>
/// The node's fence in an application as a user writes it (#46): <c>AddSharpTicker</c>, a started host, the in-memory
/// store, a job added through the manager and run by the scheduler. Nothing in this package raises the fence, so the test
/// does, through the instance the host registered. The test also fails on anything the application would see go wrong:
/// a call to its exception handler, or a log entry at Warning or above other than the ones that say what happened to
/// this job.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class NodeFenceEndToEndTests : IAsyncLifetime
{
    private const string Function = "NodeFence.EndToEnd.Job";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private sealed class RecordingExceptionHandler : ITickerExceptionHandler
    {
        public static ConcurrentQueue<string> Calls { get; } = new();

        public Task HandleExceptionAsync(Exception exception, Guid tickerId, TickerType tickerType)
        {
            Calls.Enqueue($"exception {tickerId}: {exception}");
            return Task.CompletedTask;
        }

        public Task HandleCanceledExceptionAsync(Exception exception, Guid tickerId, TickerType tickerType)
        {
            Calls.Enqueue($"cancellation {tickerId}: {exception}");
            return Task.CompletedTask;
        }
    }

    private readonly CapturedLogs _logs = new();
    private WebApplication? _app;

    public async Task InitializeAsync()
    {
        RecordingExceptionHandler.Calls.Clear();
        await ClearStoreAsync();
    }

    public async Task DisposeAsync()
    {
        await StopAsync();
        await ClearStoreAsync();
    }

    private async Task StopAsync()
    {
        if (_app == null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
    }

    private static async Task ClearStoreAsync()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var store = new TickerInMemoryPersistenceProvider<TimeTickerEntity, CronTickerEntity>(services);
        var times = await store.GetTimeTickers(null, default);
        await store.RemoveTimeTickers(times.Select(t => t.Id).ToArray());
    }

    private async Task StartHostAsync()
    {
        TickerFunctionProvider.Build();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_logs);
        builder.Services.AddSharpTicker(o =>
        {
            // Short, so the waits below span several fallback passes.
            o.ConfigureScheduler(s => s.FallbackIntervalChecker = TimeSpan.FromSeconds(1));
            o.SetExceptionHandler<RecordingExceptionHandler>();
        });
        _app = builder.Build();
        _app.UseSharpTicker();
        await _app.StartAsync();
    }

    private string Report()
        => $"handler: {string.Join("\n", RecordingExceptionHandler.Calls)}\nlog:\n{string.Join("\n", _logs.Entries.Select(e => $"{e.Level} {e.Category}: {e.Message}"))}";

    private async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"not within {Limit}: {what}\n{Report()}");
            await Task.Delay(50);
        }
    }

    // A job that becomes due while the fence is raised: the scheduler loop does not claim it, so it is neither started nor
    // claimed and put back; once the fence is cleared it runs one time.
    [Fact]
    public async Task AJobThatBecomesDueWhileTheFenceIsRaised_IsNotClaimed_AndRunsOnceTheFenceIsCleared()
    {
        var starts = 0;
        new ServiceCollection().MapTicker(Function + ".Due", (_, _) =>
        {
            Interlocked.Increment(ref starts);
            return Task.CompletedTask;
        });
        await StartHostAsync();
        var services = _app!.Services;
        var manager = services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
        var store = services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();
        var fence = services.GetRequiredService<NodeFence>();
        Assert.Equal(0, fence.Raise(services.GetRequiredService<ITickerExecutionTaskHandler>()));

        var added = await manager.AddAsync(new TimeTickerEntity { Function = Function + ".Due", ExecutionTime = DateTime.UtcNow.AddSeconds(2) });
        Assert.True(added.IsSucceeded, added.Exception?.ToString());
        var id = added.Result.Id;

        // Past its time, and long enough for the loop and several fallback passes to have met it.
        await Task.Delay(TimeSpan.FromSeconds(6));
        Assert.Equal(0, Volatile.Read(ref starts));
        Assert.DoesNotContain(_logs.Entries, e => e.Level >= LogLevel.Warning);

        fence.Clear();
        await WaitUntilAsync(async () => (await store.GetTimeTickerById(id, CancellationToken.None)).Status is TickerStatus.Done or TickerStatus.DueDone,
            "the job ran and ended");
        await Task.Delay(TimeSpan.FromSeconds(3));
        await StopAsync();

        Assert.Equal(1, Volatile.Read(ref starts));
        Assert.True(RecordingExceptionHandler.Calls.IsEmpty, Report());
        Assert.DoesNotContain(_logs.Entries, e => e.Level >= LogLevel.Warning);
    }

    // A job that honours its token is asked to stop when the fence is raised. While the fence stays raised the job is not
    // started again, although its row has been put back and the fallback keeps finding it. Once the fence is cleared the
    // job runs again and ends; the application's handler is never told, and the row is not recorded as cancelled.
    [Fact]
    public async Task AJobStoppedByTheFence_IsNotStartedWhileTheFenceIsRaised_AndRunsAgainOnceItIsCleared()
    {
        var starts = 0;
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        new ServiceCollection().MapTicker(Function, async (_, token) =>
        {
            // The first run waits until it is asked to stop; a later run ends at once.
            if (Interlocked.Increment(ref starts) > 1)
                return;
            firstStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            finally
            {
                firstEnded.TrySetResult();
            }
        });
        await StartHostAsync();
        var services = _app!.Services;
        var manager = services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
        var store = services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();
        var fence = services.GetRequiredService<NodeFence>();

        var added = await manager.AddAsync(new TimeTickerEntity { Function = Function, ExecutionTime = DateTime.UtcNow.AddSeconds(1) });
        Assert.True(added.IsSucceeded, added.Exception?.ToString());
        var id = added.Result.Id;
        await firstStarted.Task.WaitAsync(Limit);

        Assert.Equal(1, fence.Raise(services.GetRequiredService<ITickerExecutionTaskHandler>()));
        await firstEnded.Task.WaitAsync(Limit);

        // The row is put back, so the fallback could take it. Then long enough for several fallback passes.
        await WaitUntilAsync(async () => (await store.GetTimeTickerById(id, CancellationToken.None)).Status == TickerStatus.Idle, "the row is put back");
        await Task.Delay(TimeSpan.FromSeconds(4));
        Assert.Equal(1, Volatile.Read(ref starts));
        Assert.Equal(TickerStatus.Idle, (await store.GetTimeTickerById(id, CancellationToken.None)).Status);

        fence.Clear();
        await WaitUntilAsync(async () => (await store.GetTimeTickerById(id, CancellationToken.None)).Status is TickerStatus.Done or TickerStatus.DueDone,
            "the job ran again and ended");

        // Long enough for a fallback pass to take it once more if it could.
        await Task.Delay(TimeSpan.FromSeconds(3));
        await StopAsync();

        Assert.Equal(2, Volatile.Read(ref starts));
        Assert.True(RecordingExceptionHandler.Calls.IsEmpty, Report());
        var problems = _logs.Entries.Where(e => e.Level >= LogLevel.Warning).ToList();
        Assert.Contains(problems, e => e.Message.Contains("was asked to stop") && e.Message.Contains(id.ToString()));
        var others = problems
            .Where(e => e.Level != LogLevel.Warning || !e.Message.Contains(id.ToString())
                        || !(e.Message.Contains("was asked to stop") || e.Message.Contains("starts no job while it cannot show")))
            .Select(e => $"{e.Level} {e.Category}: {e.Message}").ToList();

        // While the fence is raised the node does not keep claiming the row and putting it back: at most a claim that was
        // already under way when the fence was raised meets the gate.
        var gate = problems.Count(e => e.Message.Contains("starts no job while it cannot show"));
        Assert.True(gate <= 1, $"the job was claimed and put back {gate} times while the fence was raised");
        Assert.True(others.Count == 0, $"the log has other entries at Warning or above:\n{string.Join("\n", others)}");
    }
}
