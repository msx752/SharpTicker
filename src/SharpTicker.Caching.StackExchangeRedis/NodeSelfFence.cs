using SharpTicker.Utilities;

namespace SharpTicker.Caching.StackExchangeRedis;

/// <summary>
/// What the heartbeat service needs to fence this node when its heartbeat cannot be written (see
/// <see cref="NodeHeartBeatBackgroundService"/>).
/// </summary>
/// <param name="Enabled">
/// Redis is the job store and the option is on. With another job store the rows are not released by this heartbeat's
/// dead-node check alone, and a job put back there would be claimed again at once: nothing is fenced then.
/// </param>
/// <param name="Fence">The node's fence; null when the core did not register one.</param>
/// <param name="Owner">The host's execution handler: the fence stops the jobs that handler runs, not those of another host in the process.</param>
internal sealed record NodeSelfFence(bool Enabled, NodeFence Fence, object Owner);
