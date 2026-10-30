using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.Core;
using SharpTicker.Dashboard.Authentication;
using SharpTicker.Dashboard.Hubs;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// The SignalR hub the dashboard connects to. A connection has to pass authentication, and only an authenticated
/// connection can join or leave the groups that carry the occurrence updates.
/// </summary>
public class DashboardHubTests
{
    private const string ConnectionId = "connection-1";

    private sealed class Harness
    {
        public SharpTickerNotificationHub Hub { get; }
        public IAuthService Auth { get; } = Substitute.For<IAuthService>();
        public HubCallerContext Context { get; } = Substitute.For<HubCallerContext>();
        public IGroupManager Groups { get; } = Substitute.For<IGroupManager>();
        public ISingleClientProxy Caller { get; } = Substitute.For<ISingleClientProxy>();
        public IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public Harness(AuthResult? authResult = null)
        {
            Auth.AuthenticateAsync(Arg.Any<HttpContext>()).Returns(authResult ?? AuthResult.Success("mustafa"));
            Context.ConnectionId.Returns(ConnectionId);
            Context.Items.Returns(Items!);
            Context.GetHttpContext().Returns(new DefaultHttpContext());

            var clients = Substitute.For<IHubCallerClients>();
            clients.Caller.Returns(Caller);

            Hub = new SharpTickerNotificationHub(new ConfigurationBuilder().Build(), NullLogger<SharpTickerNotificationHub>.Instance, Auth)
            {
                Context = Context,
                Groups = Groups,
                Clients = clients,
            };
        }

        public void AlreadyAuthenticated(string username = "mustafa")
        {
            Items["authenticated"] = true;
            Items["username"] = username;
        }

        /// <summary>The single argument of the one call the hub made to the caller with this method name.</summary>
        public object SentTo(string method)
        {
            var calls = Caller.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IClientProxy.SendCoreAsync)).ToArray();
            var call = Assert.Single(calls, c => (string)c.GetArguments()[0]! == method);
            return ((object[])call.GetArguments()[1]!)[0];
        }

        public Task ReceivedAsync(string method, params object[] args)
            => Caller.Received(1).SendCoreAsync(method, Arg.Is<object[]>(a => a.Length == args.Length && ArgsEqual(a, args)), Arg.Any<CancellationToken>());

        private static bool ArgsEqual(object[] actual, object[] expected)
        {
            for (var i = 0; i < actual.Length; i++)
                if (!Equals(actual[i], expected[i])) return false;
            return true;
        }
    }

    #region Connecting

    [Fact]
    public async Task OnConnected_RemembersTheUser_WhenAuthenticationSucceeds()
    {
        var h = new Harness(AuthResult.Success("mustafa"));

        await h.Hub.OnConnectedAsync();

        Assert.Equal("mustafa", h.Items["username"]);
        Assert.Equal(true, h.Items["authenticated"]);
        h.Context.DidNotReceive().Abort();
    }

    [Fact]
    public async Task OnConnected_AbortsTheConnection_WhenAuthenticationFails()
    {
        var h = new Harness(AuthResult.Failure("bad credentials"));

        await h.Hub.OnConnectedAsync();

        h.Context.Received(1).Abort();
        Assert.False(h.Items.ContainsKey("authenticated"));
        Assert.False(h.Items.ContainsKey("username"));
    }

    [Fact]
    public async Task OnDisconnected_CompletesForAConnectionThatNeverAuthenticated()
    {
        var h = new Harness();

        await h.Hub.OnDisconnectedAsync(null);
    }

    #endregion

    #region Groups

    [Fact]
    public async Task JoinGroup_AddsTheConnectionToTheGroup_AndTellsTheCaller()
    {
        var h = new Harness();
        h.AlreadyAuthenticated();

        await h.Hub.JoinGroup("cron-1");

        await h.Groups.Received(1).AddToGroupAsync(ConnectionId, "cron-1", Arg.Any<CancellationToken>());
        await h.ReceivedAsync("GroupJoined", "cron-1");
    }

    [Fact]
    public async Task JoinGroup_IsRefused_ForAConnectionThatIsNotAuthenticated()
    {
        var h = new Harness();

        await h.Hub.JoinGroup("cron-1");

        await h.Groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
        await h.ReceivedAsync("Error", "Authentication required");
    }

    [Fact]
    public async Task LeaveGroup_RemovesTheConnectionFromTheGroup_AndTellsTheCaller()
    {
        var h = new Harness();
        h.AlreadyAuthenticated();

        await h.Hub.LeaveGroup("cron-1");

        await h.Groups.Received(1).RemoveFromGroupAsync(ConnectionId, "cron-1", Arg.Any<CancellationToken>());
        await h.ReceivedAsync("GroupLeft", "cron-1");
    }

    [Fact]
    public async Task LeaveGroup_IsRefused_ForAConnectionThatIsNotAuthenticated()
    {
        var h = new Harness();

        await h.Hub.LeaveGroup("cron-1");

        await h.Groups.DidNotReceiveWithAnyArgs().RemoveFromGroupAsync(default!, default!, default);
        await h.ReceivedAsync("Error", "Authentication required");
    }

    [Fact]
    public async Task AConnectionThatWasAuthenticatedByTheHubCanJoinAGroup()
    {
        var h = new Harness(AuthResult.Success("mustafa"));
        await h.Hub.OnConnectedAsync();

        await h.Hub.JoinGroup("cron-1");

        await h.Groups.Received(1).AddToGroupAsync(ConnectionId, "cron-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AConnectionTheHubAborted_CannotJoinAGroup()
    {
        var h = new Harness(AuthResult.Failure());
        await h.Hub.OnConnectedAsync();

        await h.Hub.JoinGroup("cron-1");

        await h.Groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    #endregion

    #region Status

    [Fact]
    public async Task GetStatus_ReportsTheConnectionAndTheUser()
    {
        var h = new Harness();
        h.AlreadyAuthenticated("mustafa");

        await h.Hub.GetStatus();

        var json = JsonSerializer.SerializeToElement(h.SentTo("Status"));
        Assert.Equal(ConnectionId, json.GetProperty("connectionId").GetString());
        Assert.True(json.GetProperty("authenticated").GetBoolean());
        Assert.Equal("mustafa", json.GetProperty("username").GetString());
    }

    [Fact]
    public async Task GetStatus_SaysAnonymousAndNotAuthenticated_BeforeAuthentication()
    {
        var h = new Harness();

        await h.Hub.GetStatus();

        var json = JsonSerializer.SerializeToElement(h.SentTo("Status"));
        Assert.False(json.GetProperty("authenticated").GetBoolean());
        Assert.Equal("anonymous", json.GetProperty("username").GetString());
    }

    #endregion
}
