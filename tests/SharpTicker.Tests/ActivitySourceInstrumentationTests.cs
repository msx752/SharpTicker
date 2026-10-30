using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Instrumentation;
using SharpTicker.Utilities.Models;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// The OpenTelemetry side of job execution: which activities the "SharpTicker" source emits and which tags they carry,
/// so a trace backend can find a job by id, function and outcome. A listener records what the source emits.
/// </summary>
public class ActivitySourceInstrumentationTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _activities = new();
    private readonly ActivitySourceInstrumentation _instrumentation;

    public ActivitySourceInstrumentationTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "SharpTicker",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (_activities) _activities.Add(activity); },
        };
        ActivitySource.AddActivityListener(_listener);

        _instrumentation = new ActivitySourceInstrumentation(
            NullLogger<ActivitySourceInstrumentation>.Instance,
            new SchedulerOptionsBuilder { NodeIdentifier = "node-7" });
    }

    public void Dispose() => _listener.Dispose();

    private Activity Single(string name)
    {
        lock (_activities)
            return Assert.Single(_activities, a => a.OperationName == name);
    }

    private static string? Tag(Activity activity, string key) => activity.GetTagItem(key)?.ToString();

    [Fact]
    public void AJobActivity_CarriesTheIdentityOfTheJob()
    {
        var id = Guid.NewGuid();
        var context = new InternalFunctionContext
        {
            TickerId = id,
            Type = TickerType.CronTickerOccurrence,
            FunctionName = "SendInvoices",
            CachedPriority = TickerTaskPriority.High,
            Retries = 3,
        };

        using var activity = _instrumentation.StartJobActivity("job", context);

        Assert.NotNull(activity);
        Assert.Equal(id.ToString(), Tag(activity, "sharpticker.job.id"));
        Assert.Equal("CronTickerOccurrence", Tag(activity, "sharpticker.job.type"));
        Assert.Equal("SendInvoices", Tag(activity, "sharpticker.job.function"));
        Assert.Equal("High", Tag(activity, "sharpticker.job.priority"));
        Assert.Equal("node-7", Tag(activity, "sharpticker.job.machine"));
        Assert.Equal("3", Tag(activity, "sharpticker.job.retries"));
        Assert.Null(activity.GetTagItem("sharpticker.job.parent_id"));
        Assert.Null(activity.GetTagItem("sharpticker.job.run_condition"));
    }

    [Fact]
    public void AChainedTimeTickersActivity_AlsoNamesItsParentAndRunCondition()
    {
        var parent = Guid.NewGuid();
        var context = new InternalFunctionContext
        {
            TickerId = Guid.NewGuid(),
            ParentId = parent,
            Type = TickerType.TimeTicker,
            FunctionName = "Child",
            RunCondition = RunCondition.OnFailure,
        };

        using var activity = _instrumentation.StartJobActivity("job", context);

        Assert.Equal(parent.ToString(), Tag(activity, "sharpticker.job.parent_id"));
        Assert.Equal("OnFailure", Tag(activity, "sharpticker.job.run_condition"));
    }

    [Fact]
    public void ACronOccurrenceWithAParent_DoesNotGetARunCondition()
    {
        var context = new InternalFunctionContext
        {
            TickerId = Guid.NewGuid(), ParentId = Guid.NewGuid(), Type = TickerType.CronTickerOccurrence, FunctionName = "Cron",
        };

        using var activity = _instrumentation.StartJobActivity("job", context);

        Assert.NotNull(Tag(activity, "sharpticker.job.parent_id"));
        Assert.Null(activity.GetTagItem("sharpticker.job.run_condition"));
    }

    [Fact]
    public void ACompletedJob_IsRecordedAsOkWithItsDuration()
    {
        var id = Guid.NewGuid();

        _instrumentation.LogJobCompleted(id, "Job", 250, success: true);

        var activity = Single("sharpticker.job.completed");
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
        Assert.Equal(id.ToString(), Tag(activity, "sharpticker.job.id"));
        Assert.Equal("250", Tag(activity, "sharpticker.job.execution_time_ms"));
        Assert.Equal("True", Tag(activity, "sharpticker.job.success"));
    }

    [Fact]
    public void ACompletedJobThatWasNotSuccessful_IsRecordedAsAnError()
    {
        _instrumentation.LogJobCompleted(Guid.NewGuid(), "Job", 10, success: false);

        Assert.Equal(ActivityStatusCode.Error, Single("sharpticker.job.completed").Status);
    }

    [Fact]
    public void AFailedJob_RecordsTheErrorTypeMessageAndStackTrace()
    {
        Exception exception;
        try { throw new InvalidOperationException("the order was rejected"); }
        catch (Exception e) { exception = e; }

        _instrumentation.LogJobFailed(Guid.NewGuid(), "Job", exception, retryCount: 2);

        var activity = Single("sharpticker.job.failed");
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("the order was rejected", activity.StatusDescription);
        Assert.Equal("InvalidOperationException", Tag(activity, "sharpticker.job.error_type"));
        Assert.Equal("the order was rejected", Tag(activity, "sharpticker.job.error_message"));
        Assert.Equal("2", Tag(activity, "sharpticker.job.retry_count"));
        Assert.Contains(nameof(AFailedJob_RecordsTheErrorTypeMessageAndStackTrace), Tag(activity, "sharpticker.job.error_stack_trace"));
    }

    [Fact]
    public void AFailedJobWithoutAStackTrace_RecordsNoStackTrace()
    {
        _instrumentation.LogJobFailed(Guid.NewGuid(), "Job", new InvalidOperationException("never thrown"), 0);

        Assert.Null(Single("sharpticker.job.failed").GetTagItem("sharpticker.job.error_stack_trace"));
    }

    [Fact]
    public void ACancelledJob_IsRecordedAsAnErrorWithTheReason()
    {
        _instrumentation.LogJobCancelled(Guid.NewGuid(), "Job", "cancelled from the dashboard");

        var activity = Single("sharpticker.job.cancelled");
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("cancelled from the dashboard", Tag(activity, "sharpticker.job.cancellation_reason"));
    }

    [Fact]
    public void ASkippedJob_IsRecordedWithTheReason_AndIsNotAnError()
    {
        _instrumentation.LogJobSkipped(Guid.NewGuid(), "Job", "a previous run is still active");

        var activity = Single("sharpticker.job.skipped");
        Assert.NotEqual(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("a previous run is still active", Tag(activity, "sharpticker.job.skip_reason"));
    }

    [Fact]
    public void Seeding_IsRecordedAtItsStartAndItsEnd_WithTheNode()
    {
        _instrumentation.LogSeedingDataStarted("CronTickers");
        _instrumentation.LogSeedingDataCompleted("CronTickers");

        var started = Single("sharpticker.seeding.started");
        var completed = Single("sharpticker.seeding.completed");
        Assert.Equal("CronTickers", Tag(started, "sharpticker.seeding.type"));
        Assert.Equal("node-7", Tag(started, "sharpticker.seeding.environment"));
        Assert.Equal("CronTickers", Tag(completed, "sharpticker.seeding.type"));
    }

    [Fact]
    public void ARequestThatCouldNotBeDeserialized_IsRecordedWithTheJobAndTheReason()
    {
        var id = Guid.NewGuid();

        _instrumentation.LogRequestDeserializationFailure("Order", "Job", id, TickerType.TimeTicker, new FormatException("bad json"));

        var activity = Single("sharpticker.job_request_serialization.failed");
        Assert.Equal(id.ToString(), Tag(activity, "sharpticker.job.id"));
        Assert.Equal("Job", Tag(activity, "sharpticker.job.function"));
        Assert.Equal("bad json", Tag(activity, "sharpticker.job.cancellation_reason"));
    }

    [Fact]
    public void AnEnqueuedJob_RecordsWhereItWasEnqueuedFrom()
    {
        var id = Guid.NewGuid();

        _instrumentation.LogJobEnqueued("TimeTicker", "Job", id, enqueuedFrom: "OrdersController.Create");

        var activity = Single("sharpticker.job.enqueued");
        Assert.Equal(id.ToString(), Tag(activity, "sharpticker.job.id"));
        Assert.Equal("TimeTicker", Tag(activity, "sharpticker.job.type"));
        Assert.Equal("OrdersController.Create", Tag(activity, "sharpticker.job.enqueued_from"));
    }

    [Fact]
    public void AnEnqueuedJobWithoutACaller_FindsOneInTheStackInsteadOfLeavingItEmpty()
    {
        _instrumentation.LogJobEnqueued("TimeTicker", "Job", Guid.NewGuid());

        Assert.False(string.IsNullOrEmpty(Tag(Single("sharpticker.job.enqueued"), "sharpticker.job.enqueued_from")));
    }

    [Fact]
    public void WithoutAListener_NothingIsRecordedAndNothingFails()
    {
        _listener.Dispose();

        _instrumentation.LogJobCompleted(Guid.NewGuid(), "Job", 1, true);
        _instrumentation.LogJobFailed(Guid.NewGuid(), "Job", new Exception("x"), 0);
        _instrumentation.LogJobSkipped(Guid.NewGuid(), "Job", "r");

        Assert.Null(_instrumentation.StartJobActivity("job", new InternalFunctionContext()));
        Assert.Empty(_activities);
    }
}

/// <summary>Finding who enqueued a job from the call stack, used when the caller does not say.</summary>
public class CallerInfoHelperTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string DetailedFromHere() => CallerInfoHelper.GetCallerInfo(1);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string SimpleFromHere() => CallerInfoHelper.GetSimpleCallerInfo(1);

    [Fact]
    public void TheDetailedCaller_NamesTheMethodThatCalledIt()
    {
        var caller = DetailedFromHere();

        Assert.Contains(nameof(DetailedFromHere), caller);
        Assert.Contains(nameof(CallerInfoHelperTests), caller);
    }

    [Fact]
    public void TheSimpleCaller_NamesTheMethodWithoutAFile()
    {
        var caller = SimpleFromHere();

        Assert.Contains(nameof(SimpleFromHere), caller);
        Assert.DoesNotContain("(", caller);
    }

    [Fact]
    public void AFrameBeyondTheStack_IsUnknown()
    {
        Assert.Equal("Unknown", CallerInfoHelper.GetCallerInfo(10_000));
        Assert.Equal("Unknown", CallerInfoHelper.GetSimpleCallerInfo(10_000));
    }

    [Fact]
    public async System.Threading.Tasks.Task ACompilerGeneratedFrame_IsReplacedByTheMethodAroundIt()
    {
        await System.Threading.Tasks.Task.Yield();

        var caller = DetailedFromHere();

        Assert.DoesNotContain("<", caller);
        Assert.DoesNotContain(">", caller);
    }
}
