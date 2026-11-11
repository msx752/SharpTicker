namespace SharpTicker.Caching.StackExchangeRedis;

/// <summary>
/// What the heartbeat service needs to release the jobs an earlier run of this node left locked.
/// </summary>
/// <param name="StoreIsRedis">
/// True when the jobs are stored in Redis. With another store (EF Core with the Redis heartbeat, a custom provider) that
/// store's own startup step applies, and this one does nothing.
/// </param>
/// <param name="Node">This node's identifier, the lock holder on its jobs.</param>
/// <param name="RunStartedAt">The moment this run began, before any of its claims; null when it was not recorded.</param>
/// <param name="ApplicationStarted">Signalled once the application has started, when the start of the run is recorded.</param>
internal sealed record EarlierRunRelease(bool StoreIsRedis, string Node, Func<DateTime?> RunStartedAt, CancellationToken ApplicationStarted);
