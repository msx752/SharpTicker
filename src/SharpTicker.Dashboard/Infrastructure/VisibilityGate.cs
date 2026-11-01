using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Interfaces;

namespace SharpTicker.Dashboard.Infrastructure;

/// <summary>
/// The signed-in user of every open dashboard connection, kept only when a visibility rule is registered, so a live
/// notification can be sent to the connections whose user may see it.
/// </summary>
internal sealed class HubConnections
{
    private readonly ConcurrentDictionary<string, ClaimsPrincipal> _users = new();

    public void Add(string connectionId, ClaimsPrincipal user) => _users[connectionId] = user;

    public void Remove(string connectionId) => _users.TryRemove(connectionId, out _);

    public KeyValuePair<string, ClaimsPrincipal>[] Snapshot() => _users.ToArray();
}

/// <summary>
/// Answers the questions the SignalR hub has about the visibility rule. The hub itself does not know the ticker types
/// of the application, so this is registered with them.
/// </summary>
internal interface IVisibilityGate
{
    /// <summary>Whether the user may receive the live updates of the occurrences of this cron ticker.</summary>
    Task<bool> CanFollowCronAsync(ClaimsPrincipal user, Guid cronTickerId);
}

internal sealed class VisibilityGate<TTimeTicker, TCronTicker> : IVisibilityGate
    where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
    where TCronTicker : CronTickerEntity, new()
{
    private readonly IServiceScopeFactory _scopes;

    public VisibilityGate(IServiceScopeFactory scopes) => _scopes = scopes;

    public async Task<bool> CanFollowCronAsync(ClaimsPrincipal user, Guid cronTickerId)
    {
        using var scope = _scopes.CreateScope();
        var visibility = VisibilityScope<TTimeTicker, TCronTicker>.For(scope.ServiceProvider, user);
        if (visibility.CronTickers == null)
            return true;

        var provider = scope.ServiceProvider.GetRequiredService<ITickerPersistenceProvider<TTimeTicker, TCronTicker>>();
        return await visibility.VisibleCronTickerIdAsync(provider, cronTickerId, default) != VisibilityScope<TTimeTicker, TCronTicker>.NoTicker;
    }
}
