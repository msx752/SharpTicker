using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpTicker.DependencyInjection;
using SharpTicker.Exceptions;
using SharpTicker.Provider;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Tests;

/// <summary>
/// The lifecycle handler (#1), as an application sees it: <c>AddSharpTicker</c> on the in-memory store with
/// <c>SetLifecycleHandler</c>, the jobs added through the managers and run by the scheduler.
/// <list type="bullet">
/// <item>Each job gets one <c>OnStartingAsync</c> before its first attempt and one <c>OnCompletedAsync</c> with the status
/// that is stored: Done, Failed after its retry (not between), Cancelled, Skipped, a chain child, a cron occurrence.</item>
/// <item>A handler that throws or runs past its timeout leaves the jobs as they would be without it, and the next job
/// starts.</item>
/// <item>A job that the stop records as Cancelled gets its <c>OnCompletedAsync</c> before the stop returns.</item>
/// </list>
/// Each test also fails on a log entry at Warning or above that it does not expect.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class LifecycleHandlerEndToEndTests : IAsyncLifetime
{
    // Functions with no cron attribute: they stay in the process-wide registry after the test, and no later host seeds them.
    private const string Succeeds = "LifecycleHandler.Succeeds";
    private const string FailsOnce = "LifecycleHandler.FailsOnce";
    private const string Fails = "LifecycleHandler.Fails";
    private const string WaitsForCancel = "LifecycleHandler.WaitsForCancel";
    private const string SkipsItself = "LifecycleHandler.SkipsItself";
    private const string Child = "LifecycleHandler.Child";
    private const string Cron = "LifecycleHandler.Cron";
    private const string IgnoresCancel = "LifecycleHandler.IgnoresCancel";
    private const string Failure = "LifecycleHandler: the job failed";
    private const string SkipReason = "LifecycleHandler: nothing to do";
    private const string HandlerFailure = "LifecycleHandler: the handler failed";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<Guid, int> Attempts = new();
    private static readonly ConcurrentQueue<Guid> Started = new();

    public enum Mode { Record, Throw, Hang }

    private sealed record Call(string Kind, TickerLifecycleContext Context, DateTime At);

    private sealed class RecordingLifecycleHandler : ITickerLifecycleHandler
    {
        public static ConcurrentQueue<Call> Calls { get; } = new();
        public static Mode Mode { get; set; }

        public async Task OnStartingAsync(TickerLifecycleContext context, CancellationToken cancellationToken)
        {
            Calls.Enqueue(new Call("starting", context, DateTime.UtcNow));
            await Act(cancellationToken);
        }

        public async Task OnCompletedAsync(TickerLifecycleContext context, CancellationToken cancellationToken)
        {
            Calls.Enqueue(new Call("completed", context, DateTime.UtcNow));
            await Act(cancellationToken);
        }

        private static async Task Act(CancellationToken cancellationToken)
        {
            switch (Mode)
            {
                case Mode.Throw:
                    throw new InvalidOperationException(HandlerFailure);
                case Mode.Hang:
                    // Ignores its token: the scheduler must not wait for it longer than the timeout.
                    await Task.Delay(TimeSpan.FromSeconds(20), CancellationToken.None);
                    break;
            }
        }
    }

    private readonly CapturedLogs _logs = new();
    private WebApplication? _app;

    public async Task InitializeAsync()
    {
        RecordingLifecycleHandler.Calls.Clear();
        RecordingLifecycleHandler.Mode = Mode.Record;
        Attempts.Clear();
        Started.Clear();
        await ClearStoreAsync();
    }

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
        var times = await store.GetTimeTickers(null, default);
        await store.RemoveTimeTickers(times.SelectMany(t => t.Children.Select(c => c.Id).Prepend(t.Id)).ToArray());
        var occurrences = await store.GetAllCronTickerOccurrences(null, default);
        await store.RemoveCronTickerOccurrences(occurrences.Select(o => o.Id).ToArray(), default);
        var crons = await store.GetCronTickers(null, default);
        await store.RemoveCronTickers(crons.Select(c => c.Id).ToArray(), default);
    }

    private string Report()
        => $"calls:\n{string.Join("\n", RecordingLifecycleHandler.Calls.Select(c => $"{c.Kind} {c.Context.FunctionName} {c.Context.Id} {c.Context.Status}"))}\nlog:\n{string.Join("\n", _logs.Entries.Select(e => $"{e.Level} {e.Category}: {e.Message} {e.Exception?.Message}"))}";

    private static void MapFunctions(IServiceCollection services)
    {
        services.MapTicker(Succeeds, (context, _) => { Started.Enqueue(context.Id); return Task.CompletedTask; });
        services.MapTicker(FailsOnce, (context, _) =>
        {
            if (Attempts.AddOrUpdate(context.Id, 1, (_, n) => n + 1) == 1)
                throw new InvalidOperationException(Failure);
            return Task.CompletedTask;
        });
        services.MapTicker(Fails, (_, _) => throw new InvalidOperationException(Failure));
        services.MapTicker(WaitsForCancel, async (context, ct) =>
        {
            Started.Enqueue(context.Id);
            await Task.Delay(Timeout.Infinite, ct);
        });
        services.MapTicker(SkipsItself, (_, _) => throw new TerminateExecutionException(SkipReason));
        services.MapTicker(Child, (_, _) => Task.CompletedTask);
        services.MapTicker(Cron, (_, _) => Task.CompletedTask);
        // Does not watch its token: a stop cannot end it, and records it as Cancelled itself once its wait is over.
        services.MapTicker(IgnoresCancel, async (context, _) =>
        {
            Started.Enqueue(context.Id);
            await Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None);
        });
    }

    private async Task StartAsync(Action<TickerOptionsBuilder<TimeTickerEntity, CronTickerEntity>>? configure = null, TimeSpan? shutdownTimeout = null)
    {
        var services = new ServiceCollection();
        MapFunctions(services);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_logs);
        if (shutdownTimeout is { } limit)
            builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = limit);
        builder.Services.AddSharpTicker(o =>
        {
            o.SetLifecycleHandler<RecordingLifecycleHandler>();
            configure?.Invoke(o);
        });
        _app = builder.Build();
        _app.UseSharpTicker();
        await _app.StartAsync();
    }

    private async Task<TickerStatus> StatusAsync(Guid id)
    {
        var store = _app!.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();
        return (await store.GetTimeTickerById(id)).Status;
    }

    private async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"not within {Limit}: {what}\n{Report()}");
            await Task.Delay(100);
        }
    }

    private static Call[] CallsOf(Guid id, string kind)
        => RecordingLifecycleHandler.Calls.Where(c => c.Context.Id == id && c.Kind == kind).ToArray();

    private void AssertNoWarningBut(Func<CapturedLogs.Entry, bool> expected)
    {
        var unexpected = _logs.Entries.Where(e => e.Level >= LogLevel.Warning && !expected(e)).ToArray();
        Assert.True(unexpected.Length == 0, Report());
    }

    [Fact]
    public async Task Each_Job_Gets_One_Starting_And_One_Completed_Call_With_Its_Stored_Status()
    {
        await StartAsync();
        var times = _app!.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
        var crons = _app.Services.GetRequiredService<ICronTickerManager<CronTickerEntity>>();
        var at = DateTime.UtcNow.AddSeconds(1);

        var done = await times.AddAsync(new TimeTickerEntity
        {
            Function = Succeeds,
            ExecutionTime = at,
            Children = { new TimeTickerEntity { Function = Child, RunCondition = RunCondition.OnSuccess } }
        });
        var retried = await times.AddAsync(new TimeTickerEntity { Function = FailsOnce, ExecutionTime = at, Retries = 1, RetryIntervals = [1] });
        var failed = await times.AddAsync(new TimeTickerEntity { Function = Fails, ExecutionTime = at, Retries = 1, RetryIntervals = [1] });
        var cancelled = await times.AddAsync(new TimeTickerEntity { Function = WaitsForCancel, ExecutionTime = at });
        var skipped = await times.AddAsync(new TimeTickerEntity { Function = SkipsItself, ExecutionTime = at });
        var cron = await crons.AddAsync(new CronTickerEntity { Function = Cron, Expression = "* * * * * *" });
        foreach (var added in new[] { done, retried, failed, cancelled, skipped })
            Assert.True(added.IsSucceeded, added.Exception?.Message);
        Assert.True(cron.IsSucceeded, cron.Exception?.Message);
        var childId = done.Result.Children.Single().Id;

        var cancelRequested = false;
        await WaitUntilAsync(async () =>
        {
            if (!cancelRequested && Started.Contains(cancelled.Result.Id))
                cancelRequested = TickerCancellationTokenManager.RequestTickerCancellationById(cancelled.Result.Id);

            return CallsOf(childId, "completed").Length > 0 && CallsOf(retried.Result.Id, "completed").Length > 0
                && CallsOf(failed.Result.Id, "completed").Length > 0 && CallsOf(cancelled.Result.Id, "completed").Length > 0
                && CallsOf(skipped.Result.Id, "completed").Length > 0
                && RecordingLifecycleHandler.Calls.Any(c => c.Kind == "completed" && c.Context.Type == TickerType.CronTickerOccurrence)
                && await StatusAsync(failed.Result.Id) == TickerStatus.Failed;
        }, "every job reported as completed");
        await crons.DeleteAsync(cron.Result.Id);
        // A late call would show up within this time.
        await Task.Delay(1500);

        void AssertJob(Guid id, string function, TickerStatus status, int retryCount, Guid? parentId = null)
        {
            var starting = Assert.Single(CallsOf(id, "starting"));
            var completed = Assert.Single(CallsOf(id, "completed"));
            Assert.True(starting.At <= completed.At, Report());
            Assert.Equal(TickerStatus.InProgress, starting.Context.Status);
            Assert.Equal(function, completed.Context.FunctionName);
            Assert.Equal(TickerType.TimeTicker, completed.Context.Type);
            Assert.Equal(status, completed.Context.Status);
            Assert.Equal(retryCount, completed.Context.RetryCount);
            Assert.Equal(parentId, completed.Context.ParentId);
        }

        AssertJob(done.Result.Id, Succeeds, TickerStatus.Done, 0);
        AssertJob(childId, Child, TickerStatus.Done, 0, done.Result.Id);
        AssertJob(retried.Result.Id, FailsOnce, TickerStatus.Done, 1);
        AssertJob(failed.Result.Id, Fails, TickerStatus.Failed, 1);
        AssertJob(cancelled.Result.Id, WaitsForCancel, TickerStatus.Cancelled, 0);
        AssertJob(skipped.Result.Id, SkipsItself, TickerStatus.Skipped, 0);

        var failedCall = CallsOf(failed.Result.Id, "completed").Single().Context;
        Assert.Contains(Failure, failedCall.ExceptionMessage);
        Assert.Null(failedCall.SkippedReason);
        Assert.True(failedCall.Elapsed >= TimeSpan.FromMilliseconds(900), $"elapsed {failedCall.Elapsed}");
        var skippedCall = CallsOf(skipped.Result.Id, "completed").Single().Context;
        Assert.Equal(SkipReason, skippedCall.SkippedReason);
        Assert.Null(skippedCall.ExceptionMessage);
        Assert.Null(CallsOf(done.Result.Id, "completed").Single().Context.ExceptionMessage);
        Assert.NotNull(CallsOf(cancelled.Result.Id, "completed").Single().Context.ExceptionMessage);

        // Each cron occurrence is reported once, after it started.
        var occurrences = RecordingLifecycleHandler.Calls.Where(c => c.Context.Type == TickerType.CronTickerOccurrence)
            .GroupBy(c => c.Context.Id).ToArray();
        Assert.NotEmpty(occurrences);
        foreach (var occurrence in occurrences.Where(o => o.Any(c => c.Kind == "completed")))
        {
            Assert.Single(occurrence, c => c.Kind == "starting");
            var completed = Assert.Single(occurrence, c => c.Kind == "completed");
            Assert.Equal(Cron, completed.Context.FunctionName);
            Assert.Equal(cron.Result.Id, completed.Context.ParentId);
            Assert.True(completed.Context.Status is TickerStatus.Done or TickerStatus.DueDone, Report());
        }

        var ownIds = new[] { done.Result.Id, retried.Result.Id, failed.Result.Id, cancelled.Result.Id, skipped.Result.Id }
            .Select(id => id.ToString()).ToArray();
        AssertNoWarningBut(e => ownIds.Any(e.Message.Contains) || e.Message.Contains(Failure)
            || e.Message.StartsWith("Retrying in 1s (retry 1 of 1)"));
    }

    [Theory]
    [InlineData(Mode.Throw)]
    [InlineData(Mode.Hang)]
    public async Task A_Handler_That_Throws_Or_Hangs_Leaves_The_Jobs_As_They_Would_Be_Without_It(Mode mode)
    {
        RecordingLifecycleHandler.Mode = mode;
        var timeout = TimeSpan.FromMilliseconds(300);
        await StartAsync(o => o.SetLifecycleHandlerTimeout(timeout));
        var times = _app!.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();

        var first = await times.AddAsync(new TimeTickerEntity { Function = Succeeds, ExecutionTime = DateTime.UtcNow.AddSeconds(1) });
        var failing = await times.AddAsync(new TimeTickerEntity { Function = Fails, ExecutionTime = DateTime.UtcNow.AddSeconds(1) });
        Assert.True(first.IsSucceeded && failing.IsSucceeded, Report());
        await WaitUntilAsync(async () => await StatusAsync(first.Result.Id) is TickerStatus.Done or TickerStatus.DueDone
            && await StatusAsync(failing.Result.Id) == TickerStatus.Failed, "the first jobs ended");

        // The next job still starts, and runs once.
        var next = await times.AddAsync(new TimeTickerEntity { Function = Succeeds, ExecutionTime = DateTime.UtcNow.AddSeconds(1) });
        Assert.True(next.IsSucceeded, Report());
        await WaitUntilAsync(async () => await StatusAsync(next.Result.Id) is TickerStatus.Done or TickerStatus.DueDone, "the next job ended");
        Assert.Equal(1, Started.Count(id => id == next.Result.Id));

        // Each job was told once of its start and once of its end; each of those calls failed and was logged once.
        foreach (var id in new[] { first.Result.Id, failing.Result.Id, next.Result.Id })
        {
            await WaitUntilAsync(() => Task.FromResult(CallsOf(id, "completed").Length == 1), $"the completed call of {id}");
            Assert.Single(CallsOf(id, "starting"));
        }

        await WaitUntilAsync(() => Task.FromResult(_logs.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("lifecycle handler")) >= 6),
            "a Warning for each failed call");
        Assert.All(_logs.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("lifecycle handler")), e =>
        {
            if (mode == Mode.Throw)
                Assert.Equal(HandlerFailure, e.Exception?.Message);
            else
                Assert.Contains("did not end within", e.Message);
        });

        AssertNoWarningBut(e => e.Message.Contains("lifecycle handler") || e.Message.Contains(failing.Result.Id.ToString())
            || e.Message.Contains(Failure));
    }

    [Fact]
    public async Task A_Job_Recorded_As_Cancelled_By_The_Stop_Is_Reported_Before_The_Stop_Returns()
    {
        await StartAsync();
        var times = _app!.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
        var running = await times.AddAsync(new TimeTickerEntity { Function = WaitsForCancel, ExecutionTime = DateTime.UtcNow.AddSeconds(1) });
        Assert.True(running.IsSucceeded, Report());
        await WaitUntilAsync(() => Task.FromResult(Started.Contains(running.Result.Id)), "the job started");

        await _app.StopAsync();

        var completed = Assert.Single(CallsOf(running.Result.Id, "completed"));
        Assert.Equal(TickerStatus.Cancelled, completed.Context.Status);
        Assert.Single(CallsOf(running.Result.Id, "starting"));
        AssertNoWarningBut(e => e.Message.Contains(running.Result.Id.ToString()) || e.Message.Contains("stopping"));
    }

    [Fact]
    public async Task A_Job_That_The_Stop_Records_As_Cancelled_Itself_Is_Reported_Before_The_Stop_Returns()
    {
        await StartAsync(shutdownTimeout: TimeSpan.FromSeconds(5));
        var times = _app!.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
        var running = await times.AddAsync(new TimeTickerEntity { Function = IgnoresCancel, ExecutionTime = DateTime.UtcNow.AddSeconds(1) });
        Assert.True(running.IsSucceeded, Report());
        await WaitUntilAsync(() => Task.FromResult(Started.Contains(running.Result.Id)), "the job started");

        await _app.StopAsync();

        // The job is still running (it waits 10 s, the stop gives it about 4 s): its Cancelled record is the stop's own.
        var completed = Assert.Single(CallsOf(running.Result.Id, "completed"));
        Assert.Equal(TickerStatus.Cancelled, completed.Context.Status);
        AssertNoWarningBut(e => e.Message.Contains(running.Result.Id.ToString()) || e.Message.Contains("stopping"));
    }

    [Fact]
    public async Task Chain_Children_That_The_Parent_Outcome_Skips_Are_Reported_As_Skipped()
    {
        await StartAsync();
        var times = _app!.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
        var added = await times.AddAsync(new TimeTickerEntity
        {
            Function = Fails,
            ExecutionTime = DateTime.UtcNow.AddSeconds(1),
            Children =
            {
                new TimeTickerEntity
                {
                    Function = Child,
                    RunCondition = RunCondition.OnSuccess,
                    Children = { new TimeTickerEntity { Function = Child, RunCondition = RunCondition.OnSuccess } }
                },
                new TimeTickerEntity { Function = Succeeds, RunCondition = RunCondition.OnFailure },
            }
        });
        Assert.True(added.IsSucceeded, Report());
        var parent = added.Result;
        var skippedChild = parent.Children.Single(c => c.Function == Child);
        var grandchild = skippedChild.Children.Single();
        var ranChild = parent.Children.Single(c => c.Function == Succeeds);

        await WaitUntilAsync(() => Task.FromResult(CallsOf(skippedChild.Id, "completed").Length > 0
            && CallsOf(grandchild.Id, "completed").Length > 0 && CallsOf(ranChild.Id, "completed").Length > 0), "the children were reported");
        // A late or second call would show up within this time.
        await Task.Delay(1500);

        foreach (var (id, parentId) in new[] { (skippedChild.Id, parent.Id), (grandchild.Id, skippedChild.Id) })
        {
            var completed = Assert.Single(CallsOf(id, "completed")).Context;
            Assert.Equal(TickerStatus.Skipped, completed.Status);
            Assert.Equal(parentId, completed.ParentId);
            Assert.False(string.IsNullOrEmpty(completed.SkippedReason), Report());
            Assert.Null(completed.ExceptionMessage);
            // A skipped child never started.
            Assert.Empty(CallsOf(id, "starting"));
            Assert.Equal(TickerStatus.Skipped, await StatusAsync(id));
        }

        Assert.Equal(TickerStatus.Failed, Assert.Single(CallsOf(parent.Id, "completed")).Context.Status);
        var ran = Assert.Single(CallsOf(ranChild.Id, "completed")).Context;
        Assert.True(ran.Status is TickerStatus.Done or TickerStatus.DueDone, Report());
        Assert.Single(CallsOf(ranChild.Id, "starting"));
        AssertNoWarningBut(e => e.Message.Contains(parent.Id.ToString()) || e.Message.Contains(Failure));
    }
}
