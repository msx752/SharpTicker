using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SharpTicker.Dashboard;
using SharpTicker.Dashboard.Authentication;
using SharpTicker.Dashboard.Hubs;
using SharpTicker.Dashboard.Infrastructure;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Interfaces;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// The live updates with a visibility rule: a notification that carries a ticker only reaches the connections whose
/// user may see it, and a user only joins the group of a cron they may see. The rule is the team prefix rule of the
/// other visibility tests; without a rule everything goes to everyone, as it always did.
/// </summary>
public class DashboardVisibilityHubTests
{
    private static ClaimsPrincipal Team(string team, bool admin = false)
    {
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim("team", team));
        if (admin) identity.AddClaim(new Claim(ClaimTypes.Role, "admin"));
        return new ClaimsPrincipal(identity);
    }

    private sealed class SenderHarness
    {
        public IClientProxy All { get; } = Substitute.For<IClientProxy>();
        public IClientProxy Selected { get; } = Substitute.For<IClientProxy>();
        public IReadOnlyList<string>? SelectedIds { get; private set; }
        public HubConnections Connections { get; } = new();
        public SharpTickerNotificationHubSender Sender { get; }

        public SenderHarness(bool withRule = true)
        {
            var clients = Substitute.For<IHubClients>();
            clients.All.Returns(All);
            clients.Clients(Arg.Any<IReadOnlyList<string>>()).Returns(call =>
            {
                SelectedIds = call.Arg<IReadOnlyList<string>>();
                return Selected;
            });
            var context = Substitute.For<IHubContext<SharpTickerNotificationHub>>();
            context.Clients.Returns(clients);

            var services = new ServiceCollection();
            if (withRule)
            {
                services.AddSingleton(Connections);
                services.AddScoped<ITickerVisibilityRule, DashboardVisibilityTests.TeamPrefixRule>();
            }

            Sender = new SharpTickerNotificationHubSender(context, services.BuildServiceProvider());
        }

        public static bool Sent(IClientProxy proxy, string method)
            => proxy.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IClientProxy.SendCoreAsync) && (string)c.GetArguments()[0]! == method);
    }

    [Fact]
    public async Task ACronNotification_ReachesOnlyTheConnectionsOfTheTeamAndAdmins()
    {
        var h = new SenderHarness();
        h.Connections.Add("billing", Team("billing"));
        h.Connections.Add("ops", Team("ops"));
        h.Connections.Add("admin", Team("none", admin: true));

        await h.Sender.AddCronTickerNotifyAsync(new CronTickerEntity { Id = Guid.NewGuid(), Function = "billing.Nightly", Expression = "0 0 2 * * *" });

        Assert.Equal(new[] { "admin", "billing" }, h.SelectedIds!.OrderBy(x => x));
        Assert.True(SenderHarness.Sent(h.Selected, "AddCronTickerNotification"));
        Assert.False(SenderHarness.Sent(h.All, "AddCronTickerNotification"));
    }

    [Fact]
    public async Task ACronUpdate_AndATimeTickerUpdate_AreFilteredTheSameWay()
    {
        var h = new SenderHarness();
        h.Connections.Add("billing", Team("billing"));
        h.Connections.Add("ops", Team("ops"));

        await h.Sender.UpdateCronTickerNotifyAsync(new CronTickerEntity { Id = Guid.NewGuid(), Function = "ops.Nightly", Expression = "0 0 2 * * *" });
        Assert.Equal(new[] { "ops" }, h.SelectedIds);

        await h.Sender.UpdateTimeTickerNotifyAsync(new TimeTickerEntity { Id = Guid.NewGuid(), Function = "billing.Invoices" });
        Assert.Equal(new[] { "billing" }, h.SelectedIds);
        Assert.True(SenderHarness.Sent(h.Selected, "UpdateTimeTickerNotification"));
    }

    [Fact]
    public async Task ANotificationNobodyMaySee_IsSentToNobody()
    {
        var h = new SenderHarness();
        h.Connections.Add("billing", Team("billing"));

        await h.Sender.AddCronTickerNotifyAsync(new CronTickerEntity { Id = Guid.NewGuid(), Function = "ops.Nightly", Expression = "0 0 2 * * *" });

        Assert.Null(h.SelectedIds);
        Assert.False(SenderHarness.Sent(h.All, "AddCronTickerNotification"));
    }

    [Fact]
    public async Task WithoutARule_ANotificationGoesToEveryone()
    {
        var h = new SenderHarness(withRule: false);

        await h.Sender.AddCronTickerNotifyAsync(new CronTickerEntity { Id = Guid.NewGuid(), Function = "ops.Nightly", Expression = "0 0 2 * * *" });

        Assert.True(SenderHarness.Sent(h.All, "AddCronTickerNotification"));
        Assert.Null(h.SelectedIds);
    }

    [Fact]
    public async Task ARuleThatThrowsForATicker_MeansNotVisible_NotACrash()
    {
        var h = new SenderHarness();
        h.Connections.Add("billing", Team("billing"));

        // A null Function makes the rule's StartsWith call throw when it is evaluated on the ticker
        await h.Sender.AddCronTickerNotifyAsync(new CronTickerEntity { Id = Guid.NewGuid(), Function = null!, Expression = "0 0 2 * * *" });

        Assert.Null(h.SelectedIds);
    }

    private sealed class FakeGate : IVisibilityGate
    {
        public HashSet<Guid> Allowed { get; } = new();

        public Task<bool> CanFollowCronAsync(ClaimsPrincipal user, Guid cronTickerId) => Task.FromResult(Allowed.Contains(cronTickerId));
    }

    private sealed class HubHarness
    {
        public SharpTickerNotificationHub Hub { get; }
        public IGroupManager Groups { get; } = Substitute.For<IGroupManager>();
        public ISingleClientProxy Caller { get; } = Substitute.For<ISingleClientProxy>();
        public FakeGate Gate { get; } = new();
        public HubConnections Connections { get; } = new();
        public IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public HubHarness(bool withGate = true)
        {
            var auth = Substitute.For<IAuthService>();
            auth.AuthenticateAsync(Arg.Any<HttpContext>()).Returns(AuthResult.Success("someone"));
            var context = Substitute.For<HubCallerContext>();
            context.ConnectionId.Returns("connection-1");
            context.Items.Returns(Items!);
            context.User.Returns(Team("billing"));

            var services = new ServiceCollection();
            if (withGate)
            {
                services.AddSingleton(Connections);
                services.AddSingleton<IVisibilityGate>(Gate);
            }

            context.GetHttpContext().Returns(new DefaultHttpContext { RequestServices = services.BuildServiceProvider() });
            var clients = Substitute.For<IHubCallerClients>();
            clients.Caller.Returns(Caller);

            Hub = new SharpTickerNotificationHub(new ConfigurationBuilder().Build(), NullLogger<SharpTickerNotificationHub>.Instance, auth)
            {
                Context = context,
                Groups = Groups,
                Clients = clients,
            };
        }
    }

    [Fact]
    public async Task AConnectionIsKnownWhileItIsOpen_AndForgottenWhenItCloses()
    {
        var h = new HubHarness();

        await h.Hub.OnConnectedAsync();
        Assert.Single(h.Connections.Snapshot());

        await h.Hub.OnDisconnectedAsync(null);
        Assert.Empty(h.Connections.Snapshot());
    }

    [Fact]
    public async Task JoinGroup_OfACronTheUserMayNotSee_AddsNobodyAndAnswersLikeForACronThatDoesNotExist()
    {
        var h = new HubHarness();
        await h.Hub.OnConnectedAsync();
        var cron = Guid.NewGuid();
        
        await h.Hub.JoinGroup(cron.ToString());

        await h.Groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
        await h.Caller.Received(1).SendCoreAsync("GroupJoined", Arg.Is<object?[]>(a => (string)a[0]! == cron.ToString()), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinGroup_OfACronTheUserMaySee_AddsTheConnection()
    {
        var h = new HubHarness();
        await h.Hub.OnConnectedAsync();
        var cron = Guid.NewGuid();
        h.Gate.Allowed.Add(cron);

        await h.Hub.JoinGroup(cron.ToString());

        await h.Groups.Received(1).AddToGroupAsync("connection-1", cron.ToString(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheGate_AsksTheStoreForTheCronWithTheRule()
    {
        var provider = Substitute.For<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();
        var services = new ServiceCollection();
        services.AddScoped<ITickerVisibilityRule, DashboardVisibilityTests.TeamPrefixRule>();
        services.AddSingleton(provider);
        var gate = new VisibilityGate<TimeTickerEntity, CronTickerEntity>(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
        var cron = Guid.NewGuid();

        provider.GetCronTickers(default!, default).ReturnsForAnyArgs(Task.FromResult(Array.Empty<CronTickerEntity>()));
        Assert.False(await gate.CanFollowCronAsync(Team("billing"), cron));

        provider.GetCronTickers(default!, default).ReturnsForAnyArgs(Task.FromResult(new[] { new CronTickerEntity { Id = cron } }));
        Assert.True(await gate.CanFollowCronAsync(Team("billing"), cron));
    }

    [Fact]
    public async Task TheGate_LetsAnAdminInWithoutAskingTheStore()
    {
        var provider = Substitute.For<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();
        var services = new ServiceCollection();
        services.AddScoped<ITickerVisibilityRule, DashboardVisibilityTests.TeamPrefixRule>();
        services.AddSingleton(provider);
        var gate = new VisibilityGate<TimeTickerEntity, CronTickerEntity>(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());

        Assert.True(await gate.CanFollowCronAsync(Team("x", admin: true), Guid.NewGuid()));
        await provider.DidNotReceiveWithAnyArgs().GetCronTickers(default!, default);
    }
}
