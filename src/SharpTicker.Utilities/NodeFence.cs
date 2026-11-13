using System;

namespace SharpTicker.Utilities;

/// <summary>
/// Raised when this node can no longer show the other nodes that it is alive, so they may release its jobs and start them
/// again while it still runs them. While it is raised the node starts no job, and the jobs it was running when it was
/// raised have been asked to stop; such a run stores no outcome, and its claim is put back so the job runs again.
/// One instance per service provider. Nothing in this assembly raises it: the store package that knows the node's
/// heartbeat does.
/// </summary>
internal sealed class NodeFence
{
    private volatile bool _raised;

    /// <summary>Whether the node must not start a job right now.</summary>
    internal bool IsRaised => _raised;

    /// <summary>
    /// Raises the fence and asks the jobs <paramref name="owner"/> is running to stop (see
    /// <see cref="TickerCancellationTokenManager.CancelForFence"/>). Returns how many were asked. It does not throw for a
    /// job whose cancellation callback throws: <paramref name="onCancelFailure"/> gets that job's id and the exception.
    /// </summary>
    internal int Raise(object owner, Action<Guid, Exception> onCancelFailure = null)
    {
        _raised = true;
        return TickerCancellationTokenManager.CancelForFence(owner, onCancelFailure);
    }

    /// <summary>The node is seen again: jobs may start. Runs that were asked to stop stay stopped.</summary>
    internal void Clear() => _raised = false;
}
