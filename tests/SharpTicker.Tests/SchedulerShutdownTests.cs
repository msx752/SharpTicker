using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpTicker.BackgroundServices;
using SharpTicker.DependencyInjection;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// What happens to the jobs that are running when the application stops. The host used to stop and dispose its services
/// while they ran, so even a job that honored the cancellation lost its result and stayed InProgress. The scheduler now
/// waits for them, for a bounded time, and records the ones that did not finish as cancelled.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class SchedulerShutdownTests
{
    private const string LoggerCategory = "SharpTicker.BackgroundServices.SharpTickerSchedulerBackgroundService";

    private readonly CapturedLogs _logs = new();

    private async Task<WebApplication> StartAsync(Action<IServiceCollection> map, TimeSpan? shutdownTimeout = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(_logs);
        if (shutdownTimeout is { } timeout)
            builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = timeout);
        map(builder.Services);
        builder.Services.AddSharpTicker();
        var app = builder.Build();
        app.UseSharpTicker();
        await app.StartAsync();
        return app;
    }

    private static async Task<TimeTickerEntity> AddJobAsync(WebApplication app, string function)
        => (await app.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>()
            .AddAsync(new TimeTickerEntity { Function = function, ExecutionTime = DateTime.UtcNow })).Result;

    private static ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> StoreOf(WebApplication app)
        => app.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();

    private CapturedLogs.Entry[] Warnings() => _logs.Entries.Where(e => e.Category == LoggerCategory && e.Level == LogLevel.Warning).ToArray();

    [Fact]
    public async Task AJobThatHonorsCancellation_IsRecordedAsCancelled_BeforeStopReturns()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = await StartAsync(s => s.MapTicker("ShutdownHonors", async (_, ct) => { started.TrySetResult(); await Task.Delay(TimeSpan.FromSeconds(30), ct); }));
        var store = StoreOf(app);
        var job = await AddJobAsync(app, "ShutdownHonors");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await app.StopAsync();

        // Read before the host is disposed and with no waiting: the result has to be there when StopAsync returns.
        var stored = await store.GetTimeTickerById(job.Id, default);
        await store.RemoveTimeTickers(new[] { job.Id }, default);
        await app.DisposeAsync();
        Assert.Equal(TickerStatus.Cancelled, stored.Status);
    }

    [Fact]
    public async Task StoppingWithNothingRunning_ReturnsAtOnce_AndLogsNothing()
    {
        var app = await StartAsync(s => s.MapTicker("ShutdownIdle", (_, _) => Task.CompletedTask));
        var sw = Stopwatch.StartNew();

        await app.StopAsync();

        var took = sw.Elapsed;
        await app.DisposeAsync();
        Assert.True(took < TimeSpan.FromSeconds(3), $"stopping took {took.TotalMilliseconds:F0} ms");
        Assert.Empty(Warnings());
    }

    [Fact]
    public async Task StoppingWithAJobRunning_LogsAWarningThatNamesTheJob()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = await StartAsync(s => s.MapTicker("ShutdownNamed", async (_, ct) => { started.TrySetResult(); await Task.Delay(TimeSpan.FromSeconds(30), ct); }));
        var store = StoreOf(app);
        var job = await AddJobAsync(app, "ShutdownNamed");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await app.StopAsync();

        await store.RemoveTimeTickers(new[] { job.Id }, default);
        await app.DisposeAsync();
        var warning = Assert.Single(Warnings());
        Assert.Contains("ShutdownNamed", warning.Message);
        Assert.Contains(job.Id.ToString(), warning.Message);
        Assert.Contains("1", warning.Message);
    }

    /// <summary>
    /// A job that ignores its token cannot be stopped, and whatever ends the process will not wait for it for ever. The wait ends
    /// after a limit, and the job is recorded as cancelled with its lock released, so it is not left InProgress and is not run
    /// again.
    /// </summary>
    [Fact]
    public async Task AJobThatIgnoresCancellation_IsRecordedAsCancelledWithItsLockReleased_WhenTheWaitLimitPasses()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = await StartAsync(s => s.MapTicker("ShutdownIgnores", async (_, _) => { started.TrySetResult(); await Task.Delay(TimeSpan.FromSeconds(30)); }));
        app.Services.GetRequiredService<SharpTickerSchedulerBackgroundService>().ShutdownWaitLimit = TimeSpan.FromMilliseconds(800);
        var store = StoreOf(app);
        var job = await AddJobAsync(app, "ShutdownIgnores");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(TickerStatus.InProgress, (await store.GetTimeTickerById(job.Id, default)).Status);
        var sw = Stopwatch.StartNew();

        await app.StopAsync();

        var took = sw.Elapsed;
        var stored = await store.GetTimeTickerById(job.Id, default);
        await store.RemoveTimeTickers(new[] { job.Id }, default);
        await app.DisposeAsync();
        Assert.InRange(took, TimeSpan.FromMilliseconds(700), TimeSpan.FromSeconds(5));
        Assert.Equal(TickerStatus.Cancelled, stored.Status);
        Assert.Null(stored.LockHolder);
        Assert.Equal(2, Warnings().Length);
        Assert.Contains("ShutdownIgnores", Warnings()[1].Message);
    }

    /// <summary>
    /// The host cancels the token it passes to StopAsync at its own shutdown timeout. When that is shorter than the wait limit the
    /// wait ends there, and the outcome is still recorded before the host goes on to dispose its services.
    /// </summary>
    [Fact]
    public async Task WhenTheHostTimeoutIsShorterThanTheWaitLimit_TheWaitEndsAtTheHostTimeout_AndTheJobIsStillRecorded()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = await StartAsync(s => s.MapTicker("ShutdownHostTimeout", async (_, _) => { started.TrySetResult(); await Task.Delay(TimeSpan.FromSeconds(30)); }),
            shutdownTimeout: TimeSpan.FromSeconds(1));
        var store = StoreOf(app);
        var job = await AddJobAsync(app, "ShutdownHostTimeout");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var sw = Stopwatch.StartNew();

        await app.StopAsync();

        var took = sw.Elapsed;
        var stored = await store.GetTimeTickerById(job.Id, default);
        await store.RemoveTimeTickers(new[] { job.Id }, default);
        await app.DisposeAsync();
        Assert.True(took < TimeSpan.FromSeconds(10), $"stopping took {took.TotalMilliseconds:F0} ms, the wait limit is 20 s");
        Assert.Equal(TickerStatus.Cancelled, stored.Status);
        Assert.Null(stored.LockHolder);
    }
}
