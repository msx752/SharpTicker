using System;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpTicker.Dashboard.Authentication;
using SharpTicker.Dashboard.Infrastructure;

namespace SharpTicker.Dashboard.Hubs
{
    /// <summary>
    /// What <c>GetStatus</c> sends back to the caller. A named type, not an anonymous one, so the source-generated
    /// serializer context can describe it for applications that run without reflection-based serialization.
    /// </summary>
    internal sealed record HubConnectionStatus(
        [property: JsonPropertyName("connectionId")] string ConnectionId,
        [property: JsonPropertyName("authenticated")] bool Authenticated,
        [property: JsonPropertyName("username")] string Username,
        [property: JsonPropertyName("timestamp")] DateTime Timestamp);

    public class SharpTickerNotificationHub : Hub
    {
        private readonly IConfiguration _config;
        private readonly ILogger<SharpTickerNotificationHub> _logger;
        private readonly IAuthService _authService;

        public SharpTickerNotificationHub(
            IConfiguration config, 
            ILogger<SharpTickerNotificationHub> logger,
            IAuthService authService)
        {
            _config = config;
            _logger = logger;
            _authService = authService;
        }

        public override async Task OnConnectedAsync()
        {
            var connectionId = Context.ConnectionId;
            _logger.LogDebug("SignalR connection attempt: {ConnectionId}", connectionId);

            // Authenticate the connection using new auth service
            var authResult = await _authService.AuthenticateAsync(Context.GetHttpContext()!);
            
            if (!authResult.IsAuthenticated)
            {
                _logger.LogWarning("SignalR authentication failed: {ConnectionId} - {Error}", 
                    connectionId, authResult.ErrorMessage);
                Context.Abort();
                return;
            }

            _logger.LogInformation("SignalR connection established: {ConnectionId} - User: {Username}", 
                connectionId, authResult.Username);

            // Store user info in connection
            Context.Items["username"] = authResult.Username;
            Context.Items["authenticated"] = true;

            // Only kept when a visibility rule is registered: the notifications are then sent per connection
            Services?.GetService<HubConnections>()?.Add(connectionId, Context.User ?? new ClaimsPrincipal());

            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            var connectionId = Context.ConnectionId;
            var username = Username ?? "unknown";
            
            _logger.LogInformation("SignalR connection disconnected: {ConnectionId} - User: {Username}", 
                connectionId, username);

            Services?.GetService<HubConnections>()?.Remove(connectionId);

            await base.OnDisconnectedAsync(exception);
        }

        public async Task JoinGroup(string groupName)
        {
            if (!IsAuthenticated())
            {
                await Clients.Caller.SendAsync("Error", "Authentication required");
                return;
            }

            // The groups are named after a cron ticker and carry its occurrence updates, so with a visibility rule a
            // user only joins the group of a cron they may see. The answer is the same as for a cron that does not
            // exist, which has a group that nothing is ever sent to.
            var gate = Services?.GetService<IVisibilityGate>();
            if (gate != null && Guid.TryParse(groupName, out var cronTickerId)
                && !await gate.CanFollowCronAsync(Context.User ?? new ClaimsPrincipal(), cronTickerId))
            {
                _logger.LogDebug("User {Username} was not added to group {GroupName}: the cron ticker is not visible to them", Username, groupName);
                await Clients.Caller.SendAsync("GroupJoined", groupName);
                return;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
            var username = Username;
            
            _logger.LogDebug("User {Username} joined group {GroupName}", username, groupName);
            await Clients.Caller.SendAsync("GroupJoined", groupName);
        }
        
        public async Task LeaveGroup(string groupName)
        {
            if (!IsAuthenticated())
            {
                await Clients.Caller.SendAsync("Error", "Authentication required");
                return;
            }

            await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
            var username = Username;
            
            _logger.LogDebug("User {Username} left group {GroupName}", username, groupName);
            await Clients.Caller.SendAsync("GroupLeft", groupName);
        }
        
        public async Task GetStatus()
        {
            var status = new HubConnectionStatus(
                Context.ConnectionId,
                IsAuthenticated(),
                Username ?? "anonymous",
                DateTime.UtcNow);

            await Clients.Caller.SendAsync("Status", status);
        }

        private IServiceProvider? Services => Context.GetHttpContext()?.RequestServices;

        // A connection that never authenticated has no "username" item, and the Items indexer throws for a missing key.
        private string? Username => Context.Items.TryGetValue("username", out var value) ? value?.ToString() : null;

        private bool IsAuthenticated()
        {
            return Context.Items.ContainsKey("authenticated") && 
                   (bool)Context.Items["authenticated"]!;
        }
    }
}