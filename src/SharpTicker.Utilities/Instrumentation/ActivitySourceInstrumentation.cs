using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Utilities.Instrumentation;

public class ActivitySourceInstrumentation(ILogger<ActivitySourceInstrumentation> logger, SchedulerOptionsBuilder optionsBuilder)
  : SharpTickerBaseLoggerInstrumentation(logger, optionsBuilder.NodeIdentifier)
  , ISharpTickerInstrumentation
{
  private static readonly ActivitySource ActivitySource = new("SharpTicker", "1.0.0");

  public override Activity StartJobActivity(string activityName, InternalFunctionContext context)
  {
    if (ActivitySource.StartActivity(activityName) is not { } activity)
      return null;

    activity.SetTag("sharpticker.job.id", context.TickerId.ToString());
    activity.SetTag("sharpticker.job.type", context.Type.ToString());
    activity.SetTag("sharpticker.job.function", context.FunctionName);
    activity.SetTag("sharpticker.job.priority", context.CachedPriority.ToString());
    activity.SetTag("sharpticker.job.machine", InstanceIdentifier);
    activity.SetTag("sharpticker.job.retries", context.Retries);

    if (context.ParentId.HasValue)
      activity.SetTag("sharpticker.job.parent_id", context.ParentId.Value.ToString());

    if (context.Type == TickerType.TimeTicker && context.ParentId.HasValue)
      activity.SetTag("sharpticker.job.run_condition", context.RunCondition.ToString());

    return activity;
  }

  public override void LogJobEnqueued(string jobType, string functionName, Guid jobId, string enqueuedFrom = null)
  {
    // Get detailed caller information for OpenTelemetry
    var callerInfo = string.IsNullOrEmpty(enqueuedFrom) ? CallerInfoHelper.GetCallerInfo(6) : enqueuedFrom;
    base.LogJobEnqueued(jobType, functionName, jobId, callerInfo);

    using var activity = ActivitySource.StartActivity("sharpticker.job.enqueued");
    if (activity == null) return;

    activity.SetTag("sharpticker.job.id", jobId.ToString());
    activity.SetTag("sharpticker.job.type", jobType);
    activity.SetTag("sharpticker.job.function", functionName);
    activity.SetTag("sharpticker.job.enqueued_from", callerInfo);
  }

  public override void LogJobCompleted(Guid jobId, string functionName, long executionTimeMs, bool success)
  {
    base.LogJobCompleted(jobId, functionName, executionTimeMs, success);

    using var activity = ActivitySource.StartActivity("sharpticker.job.completed");
    if (activity == null) return;

    activity.SetStatus(success ? ActivityStatusCode.Ok : ActivityStatusCode.Error);

    activity.SetTag("sharpticker.job.id", jobId.ToString());
    activity.SetTag("sharpticker.job.function", functionName);
    activity.SetTag("sharpticker.job.execution_time_ms", executionTimeMs);
    activity.SetTag("sharpticker.job.success", success);
  }

  public override void LogJobFailed(Guid jobId, string functionName, Exception exception, int retryCount)
  {
    base.LogJobFailed(jobId, functionName, exception, retryCount);

    using var activity = ActivitySource.StartActivity("sharpticker.job.failed");
    if (activity == null) return;

    activity.SetStatus(ActivityStatusCode.Error, exception.Message);

    activity.SetTag("sharpticker.job.id", jobId.ToString());
    activity.SetTag("sharpticker.job.function", functionName);
    activity.SetTag("sharpticker.job.retry_count", retryCount);
    activity.SetTag("sharpticker.job.error_type", exception.GetType().Name);
    activity.SetTag("sharpticker.job.error_message", exception.Message);

    // Record exception information in tags instead of RecordException (not available in all .NET versions)
    if (exception.StackTrace != null)
      activity.SetTag("sharpticker.job.error_stack_trace", exception.StackTrace);
  }

  public override void LogJobCancelled(Guid jobId, string functionName, string reason)
  {
    base.LogJobCancelled(jobId, functionName, reason);

    using var activity = ActivitySource.StartActivity("sharpticker.job.cancelled");
    if (activity == null) return;

    activity.SetStatus(ActivityStatusCode.Error, reason);

    activity.SetTag("sharpticker.job.id", jobId.ToString());
    activity.SetTag("sharpticker.job.function", functionName);
    activity.SetTag("sharpticker.job.cancellation_reason", reason);
  }

  public override void LogJobSkipped(Guid jobId, string functionName, string reason)
  {
    base.LogJobSkipped(jobId, functionName, reason);

    using var activity = ActivitySource.StartActivity("sharpticker.job.skipped");
    if (activity == null) return;

    activity.SetTag("sharpticker.job.id", jobId.ToString());
    activity.SetTag("sharpticker.job.function", functionName);
    activity.SetTag("sharpticker.job.skip_reason", reason);
  }

  public override void LogSeedingDataStarted(string seedingDataType)
  {
    base.LogSeedingDataStarted(seedingDataType);

    using var activity = ActivitySource.StartActivity("sharpticker.seeding.started");
    if (activity == null) return;

    activity.SetTag("sharpticker.seeding.type", seedingDataType);
    activity.SetTag("sharpticker.seeding.environment", InstanceIdentifier);
  }

  public override void LogSeedingDataCompleted(string seedingDataType)
  {
    base.LogSeedingDataCompleted(seedingDataType);

    using var activity = ActivitySource.StartActivity("sharpticker.seeding.completed");
    if (activity == null) return;

    activity.SetTag("sharpticker.seeding.type", seedingDataType);
    activity.SetTag("sharpticker.seeding.environment", InstanceIdentifier);
  }

  public override void LogRequestDeserializationFailure(string requestType, string functionName, Guid tickerId, TickerType type, Exception exception)
  {
    base.LogRequestDeserializationFailure(requestType, functionName, tickerId, type, exception);

    using var activity = ActivitySource.StartActivity("sharpticker.job_request_serialization.failed");
    if (activity == null) return;

    activity.SetTag("sharpticker.job.id", tickerId.ToString());
    activity.SetTag("sharpticker.job.function", functionName);
    activity.SetTag("sharpticker.job.cancellation_reason", exception.Message);
  }
}