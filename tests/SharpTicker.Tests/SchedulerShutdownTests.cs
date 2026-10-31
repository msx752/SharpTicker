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
using SharpTicker.Utilities;
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
        // The in-memory store is shared by the whole process. What an earlier test left in it, a cron occurrence that is due for
        // example, would be run by this host's scheduler and counted among its running jobs.
        await ClearStoreAsync(app);
        await app.StartAsync();
        return app;
    }

    private static async Task ClearStoreAsync(WebApplication app)
    {
        var store = StoreOf(app);
        var occurrences = await store.GetAllCronTickerOccurrences(null, default);
        await store.RemoveCronTickerOccurrences(occurrences.Select(o => o.Id).ToArray(), default);
        var crons = await store.GetCronTickers(null, default);
        await store.RemoveCronTickers(crons.Select(c => c.Id).ToArray(), default);
        var times = await store.GetTimeTickers(null, default);
        await store.RemoveTimeTickers(times.Select(t => t.Id).ToArray(), default);
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

    // The job honors the cancellation but needs time to clean up, and the test holds it there until the warning has been written.
    // A job that ends at once can be gone before the stop looks at the running jobs, and then there is nothing to warn about.
    [Fact]
    public async Task StoppingWithAJobRunning_LogsAWarningThatNamesTheJob()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanedUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = await StartAsync(s => s.MapTicker("ShutdownNamed", async (_, ct) =>
        {
            started.TrySetResult();
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { await cleanedUp.Task; throw; }
        }));
        var store = StoreOf(app);
        var job = await AddJobAsync(app, "ShutdownNamed");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var stopping = app.StopAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Warnings().Length == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        var logged = Warnings();
        cleanedUp.TrySetResult();
        await stopping;

        await store.RemoveTimeTickers(new[] { job.Id }, default);
        await app.DisposeAsync();
        var warning = Assert.Single(logged);
        Assert.Contains("ShutdownNamed", warning.Message);
        Assert.Contains(job.Id.ToString(), warning.Message);
        Assert.True(warning.Message.StartsWith("The application is stopping and 1 job(s)"), warning.Message);
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
    /// The wait follows the host's shutdown timeout, the setting an application already manages for how long a stop may take: it
    /// ends a little before it, so there is time left to record the jobs that did not finish, and the host is not the one that
    /// gives up first.
    /// </summary>
    [Fact]
    public async Task TheWaitFollowsTheHostShutdownTimeout_AndEndsBeforeIt()
    {
        var hostTimeout = TimeSpan.FromSeconds(3);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = await StartAsync(s => s.MapTicker("ShutdownHostTimeout", async (_, _) => { started.TrySetResult(); await Task.Delay(TimeSpan.FromSeconds(30)); }), hostTimeout);
        var store = StoreOf(app);
        var job = await AddJobAsync(app, "ShutdownHostTimeout");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var sw = Stopwatch.StartNew();

        await app.StopAsync();

        var took = sw.Elapsed;
        var stored = await store.GetTimeTickerById(job.Id, default);
        await store.RemoveTimeTickers(new[] { job.Id }, default);
        await app.DisposeAsync();
        Assert.InRange(took, TimeSpan.FromSeconds(1.5), hostTimeout);
        Assert.Equal(TickerStatus.Cancelled, stored.Status);
        Assert.Null(stored.LockHolder);
    }

    [Theory]
    [InlineData(30_000, 25_000)]    // the host default: a fifth, at most five seconds, is left over
    [InlineData(60_000, 55_000)]
    [InlineData(10_000, 8_000)]
    [InlineData(1_000, 800)]
    [InlineData(100, 0)]            // too short for any wait
    [InlineData(0, 25_000)]         // no limit set on the host: its default is used
    [InlineData(-1, 25_000)]        // Timeout.InfiniteTimeSpan
    public void TheWaitLimit_IsALittleLessThanTheHostShutdownTimeout(int hostTimeoutMs, int expectedMs)
        => Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), SharpTickerSchedulerBackgroundService.WaitLimitFor(TimeSpan.FromMilliseconds(hostTimeoutMs)));

    /// <summary>The same wait and the same outcome for a cron occurrence as for a time ticker.</summary>
    [Fact]
    public async Task ACronOccurrenceThatHonorsCancellation_IsRecordedAsCancelled_BeforeStopReturns()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = await StartAsync(s => s.MapTicker("ShutdownCron", async (_, ct) => { started.TrySetResult(); await Task.Delay(TimeSpan.FromSeconds(30), ct); }));
        var store = StoreOf(app);
        var cron = (await app.Services.GetRequiredService<ICronTickerManager<CronTickerEntity>>()
            .AddAsync(new CronTickerEntity { Function = "ShutdownCron", Expression = "* * * * * *" })).Result;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        // The occurrences whose jobs are running now. Others may be marked InProgress for the next second without having started,
        // and a job that never starts is not something the stop waits for.
        var handler = app.Services.GetRequiredService<ITickerExecutionTaskHandler>();
        var running = TickerCancellationTokenManager.GetRunningJobs(handler).Select(j => j.TickerId).ToArray();
        Assert.NotEmpty(running);

        await app.StopAsync();

        var occurrences = (await store.GetAllCronTickerOccurrences(x => x.CronTickerId == cron.Id, default)).ToList();
        await store.RemoveCronTickerOccurrences(occurrences.Select(o => o.Id).ToArray(), default);
        await store.RemoveCronTickers(new[] { cron.Id }, default);
        await app.DisposeAsync();
        Assert.All(running, id => Assert.Equal(TickerStatus.Cancelled, occurrences.Single(o => o.Id == id).Status));
    }
}
