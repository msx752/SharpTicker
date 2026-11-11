using System.Text.Json;

namespace SharpTicker.Caching.StackExchangeRedis;

/// <summary>
/// The heartbeat entries of the nodes that share one Redis: <c>hb:{node}</c> with a TTL, and the registry of known nodes.
/// </summary>
internal interface INodeHeartbeatRegistry
{
    /// <summary>Writes this node's entry, then adds the node to the registry. Returns the payload for the dashboard.</summary>
    Task<JsonElement> WriteHeartbeatAsync();

    /// <summary>The registered nodes, this node excluded, that have no heartbeat entry.</summary>
    Task<string[]> GetMissingNodesAsync();

    Task<bool> IsNodeAliveAsync(string node);

    Task RemoveNodeFromRegistryAsync(string node);
}
