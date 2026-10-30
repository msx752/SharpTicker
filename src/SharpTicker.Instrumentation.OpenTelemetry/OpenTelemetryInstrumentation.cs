using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Instrumentation;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Instrumentation.OpenTelemetry;

internal class OpenTelemetryInstrumentation : SharpTickerBaseLoggerInstrumentation , ISharpTickerInstrumentation
{
    private readonly ILogger<OpenTelemetryInstrumentation> _logger;
    private static readonly ActivitySource ActivitySource = new("SharpTicker", "1.0.0");

    public OpenTelemetryInstrumentation(ILogger<OpenTelemetryInstrumentation> logger, SchedulerOptionsBuilder optionsBuilder) : base(logger, optionsBuilder.NodeIdentifier)
    {
        _logger = logger;
    }

    public override Activity? StartJobActivity(string activityName, InternalFunctionContext context)
    {
        var activity = ActivitySource.StartActivity(activityName);
            
        if (activity != null)
        {
            activity.SetTag("sharpticker.job.id", context.TickerId.ToString());
            activity.SetTag("sharpticker.job.type", context.Type.ToString());
            activity.SetTag("sharpticker.job.function", context.FunctionName);
            activity.SetTag("sharpticker.job.priority", context.CachedPriority.ToString());
            activity.SetTag("sharpticker.job.machine", InstanceIdentifier);
            activity.SetTag("sharpticker.job.retries", context.Retries);
                
            if (context.ParentId.HasValue)
            {
                activity.SetTag("sharpticker.job.parent_id", context.ParentId.Value.ToString());
            }

            if (context.Type == TickerType.TimeTicker && context.ParentId.HasValue)
            {
                activity.SetTag("sharpticker.job.run_condition", context.RunCondition.ToString());
            }
        }
            
        return activity;
    }

    public override void LogJobEnqueued(string jobType, string functionName, Guid jobId, string? enqueuedFrom = null)
    {
        using var activity = ActivitySource.StartActivity("sharpticker.job.enqueued");
        activity?.SetTag("sharpticker.job.id", jobId.ToString());
        activity?.SetTag("sharpticker.job.type", jobType);
        activity?.SetTag("sharpticker.job.function", functionName);
            
        // Get detailed caller information for OpenTelemetry
        var callerInfo = string.IsNullOrEmpty(enqueuedFrom) ? CallerInfoHelper.GetCallerInfo(6) : enqueuedFrom;
        activity?.SetTag("sharpticker.job.enqueued_from", callerInfo);
            
        _logger.LogInformation("SharpTicker Job enqueued: {JobType} - {Function} ({JobId}) from {EnqueuedFrom}", 
            jobType, functionName, jobId, callerInfo);
    }

    public override void LogJobCompleted(Guid jobId, string functionName, long executionTimeMs, bool success)
    {
        using var activity = ActivitySource.StartActivity("sharpticker.job.completed");
        activity?.SetTag("sharpticker.job.id", jobId.ToString());
        activity?.SetTag("sharpticker.job.function", functionName);
        activity?.SetTag("sharpticker.job.execution_time_ms", executionTimeMs);
        activity?.SetTag("sharpticker.job.success", success);
            
        // Set activity status based on success
        if (activity != null)
        {
            activity.SetStatus(success ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
        }
            
        base.LogJobCompleted(jobId, functionName, executionTimeMs, success);
    }

    public override void LogJobFailed(Guid jobId, string functionName, Exception exception, int retryCount)
    {
        using var activity = ActivitySource.StartActivity("sharpticker.job.failed");
        activity?.SetTag("sharpticker.job.id", jobId.ToString());
        activity?.SetTag("sharpticker.job.function", functionName);
        activity?.SetTag("sharpticker.job.retry_count", retryCount);
        activity?.SetTag("sharpticker.job.error_type", exception.GetType().Name);
        activity?.SetTag("sharpticker.job.error_message", exception.Message);
            
        if (activity != null)
        {
            activity.SetStatus(ActivityStatusCode.Error, exception.Message);
            // Record exception information in tags instead of RecordException (not available in all .NET versions)
            if (exception.StackTrace != null)
            {
                activity.SetTag("sharpticker.job.error_stack_trace", exception.StackTrace);
            }
        }
            
        base.LogJobFailed(jobId, functionName, exception, retryCount);
    }

    public override void LogJobCancelled(Guid jobId, string functionName, string reason)
    {
        using var activity = ActivitySource.StartActivity("sharpticker.job.cancelled");
        activity?.SetTag("sharpticker.job.id", jobId.ToString());
        activity?.SetTag("sharpticker.job.function", functionName);
        activity?.SetTag("sharpticker.job.cancellation_reason", reason);
            
        if (activity != null)
        {
            activity.SetStatus(ActivityStatusCode.Error, reason);
        }
            
        base.LogJobCancelled(jobId, functionName, reason);
    }

    public override void LogJobSkipped(Guid jobId, string functionName, string reason)
    {
        using var activity = ActivitySource.StartActivity("sharpticker.job.skipped");
        activity?.SetTag("sharpticker.job.id", jobId.ToString());
        activity?.SetTag("sharpticker.job.function", functionName);
        activity?.SetTag("sharpticker.job.skip_reason", reason);
            
        base.LogJobSkipped(jobId, functionName, reason);
    }

    public override void LogSeedingDataStarted(string seedingDataType)
    {
        using var activity = ActivitySource.StartActivity("sharpticker.seeding.started");
        activity?.SetTag("sharpticker.seeding.type", seedingDataType);
        activity?.SetTag("sharpticker.seeding.environment", InstanceIdentifier);
            
        base.LogSeedingDataStarted(seedingDataType);
    }

    public override void LogSeedingDataCompleted(string seedingDataType)
    {
        using var activity = ActivitySource.StartActivity("sharpticker.seeding.completed");
        activity?.SetTag("sharpticker.seeding.type", seedingDataType);
        activity?.SetTag("sharpticker.seeding.environment", InstanceIdentifier);
            
        base.LogSeedingDataCompleted(seedingDataType);
    }

    public override void LogRequestDeserializationFailure(string requestType, string functionName, Guid tickerId, TickerType type, Exception exception)
    {
        using var activity = ActivitySource.StartActivity("sharpticker.job_request_serialization.failed");
        activity?.SetTag("sharpticker.job.id", tickerId.ToString());
        activity?.SetTag("sharpticker.job.function", functionName);
        activity?.SetTag("sharpticker.job.cancellation_reason", exception.Message);
        base.LogRequestDeserializationFailure(requestType, functionName, tickerId, type, exception);
    }
}