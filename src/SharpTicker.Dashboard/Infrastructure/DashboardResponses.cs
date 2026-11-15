using System;
using SharpTicker.Utilities.Enums;

namespace SharpTicker.Dashboard.Infrastructure;

internal class AuthInfoResponse
{
    public string Mode { get; set; }
    public bool Enabled { get; set; }
    public int SessionTimeout { get; set; }
}

internal class AuthValidateResponse
{
    public bool Authenticated { get; set; }
    public string Username { get; set; }
    public string Message { get; set; }
}

/// <summary>Body of the 400 answer to a cancel request that found no running job on this node.</summary>
internal class CancelRefusedResponse
{
    public string Reason { get; set; }
    public string Message { get; set; }
}

/// <summary>
/// What the Cron Tickers table shows about a cron beyond its stored row: when it fires next and how its latest run ended.
/// Both are looked up for the crons on the page that is shown, not for every cron.
/// </summary>
internal class CronRunSummary
{
    public Guid Id { get; set; }

    /// <summary>The next occurrence of the expression, in UTC. Null when the cron is disabled or the expression has none.</summary>
    public DateTime? NextRun { get; set; }

    /// <summary>When the latest run that started did so, in UTC. Null when it has not run.</summary>
    public DateTime? LastRun { get; set; }

    public TickerStatus? LastStatus { get; set; }
    public Guid? LastOccurrenceId { get; set; }
}

internal class DashboardOptionsResponse
{
    public int MaxConcurrency { get; set; }
    public TimeSpan IdleWorkerTimeOut { get; set; }
    public string CurrentMachine { get; set; }
    public string LastHostExceptionMessage { get; set; }
    public string SchedulerTimeZone { get; set; }
}

internal class ActionResponse
{
    public bool Success { get; set; }
    public string Message { get; set; }
}

internal class ActionResponseWithId
{
    public bool Success { get; set; }
    public string Message { get; set; }
    public Guid? TickerId { get; set; }
}

internal class TickerRequestResponse
{
    public string Result { get; set; }
    public int MatchType { get; set; }
}

internal class TickerFunctionResponse
{
    public string FunctionName { get; set; }
    public string FunctionRequestNamespace { get; set; }
    public string FunctionRequestType { get; set; }
    public int Priority { get; set; }
}

internal class NextTickerResponse
{
    public DateTime? NextOccurrence { get; set; }
}

internal class HostStatusResponse
{
    public bool IsRunning { get; set; }
}

internal class TupleResponse<T1, T2>
{
    public T1 Item1 { get; set; }
    public T2 Item2 { get; set; }
}

/// <summary>
/// One name in the Lock holders panel (Machines up to 1.5.2): a node identifier that locked jobs still in the store. A job keeps that name after it
/// ended, so the name of a node that is gone stays here. <see cref="Item1"/> and <see cref="Item2"/> keep the names the
/// answer had up to 1.5.2.
/// </summary>
internal class LockHolderResponse
{
    /// <summary>The node identifier, or "unknown" for locked jobs without one.</summary>
    public string Item1 { get; set; }

    /// <summary>How many jobs in the store carry the name, finished ones included.</summary>
    public int Item2 { get; set; }

    /// <summary>The latest time one of those jobs was locked, in UTC.</summary>
    public DateTime LastLockedAt { get; set; }

    /// <summary>
    /// With Redis: whether the node has a heartbeat entry now. Null when there is no heartbeat to look at (no Redis), for
    /// "unknown", and when Redis could not be read.
    /// </summary>
    public bool? HasHeartbeat { get; set; }
}

internal class FrontendConfigResponse
{
    public string BasePath { get; set; }
    public string BackendDomain { get; set; }
    public AuthInfoResponse Auth { get; set; }
}
