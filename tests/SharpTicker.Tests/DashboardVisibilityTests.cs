using System;
using System.Linq;
using System.Linq.Expressions;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SharpTicker.Dashboard;
using SharpTicker.Dashboard.Infrastructure;
using SharpTicker.Utilities.Entities;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// The visibility rule of the dashboard: its registration, the check that it is combined with host authentication, and the
/// translation of what a rule returns into the entity types the stores use.
/// </summary>
public class DashboardVisibilityTests
{
    /// <summary>A team prefix rule, as the documentation shows it. Admins see everything.</summary>
    public sealed class TeamPrefixRule : ITickerVisibilityRule
    {
        public Expression<Func<TimeTickerEntity, bool>>? TimeTickers(ClaimsPrincipal user)
            => IsAdmin(user) ? null : t => t.Function.StartsWith(Prefix(user));

        public Expression<Func<CronTickerEntity, bool>>? CronTickers(ClaimsPrincipal user)
            => IsAdmin(user) ? null : c => c.Function.StartsWith(Prefix(user));

        public bool CanSeeFunction(ClaimsPrincipal user, string functionName)
            => IsAdmin(user) || functionName.StartsWith(Prefix(user), StringComparison.Ordinal);

        private static bool IsAdmin(ClaimsPrincipal user) => user.IsInRole("admin");
        private static string Prefix(ClaimsPrincipal user) => user.FindFirst("team")?.Value + ".";
    }

    /// <summary>An application's own time ticker type, which does not derive from the plain TimeTickerEntity.</summary>
    public sealed class OwnTimeTicker : TimeTickerEntity<OwnTimeTicker> { }

    private static ClaimsPrincipal User(string? team = null, bool admin = false)
    {
        var identity = new ClaimsIdentity("test");
        if (team != null) identity.AddClaim(new Claim("team", team));
        if (admin) identity.AddClaim(new Claim(ClaimTypes.Role, "admin"));
        return new ClaimsPrincipal(identity);
    }

    private static HttpContext ContextFor(ClaimsPrincipal user, bool withRule)
    {
        var services = new ServiceCollection();
        if (withRule) services.AddScoped<ITickerVisibilityRule, TeamPrefixRule>();
        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider().CreateScope().ServiceProvider, User = user };
    }

    #region Registration

    [Fact]
    public void ARuleWithHostAuthentication_IsAccepted()
    {
        var options = new DashboardOptionsBuilder().WithHostAuthentication("Scheduler").WithVisibilityRule<TeamPrefixRule>();

        options.Validate();

        Assert.Equal(typeof(TeamPrefixRule), options.VisibilityRuleType);
    }

    [Fact]
    public void TheOrderOfTheTwoCalls_DoesNotMatter()
    {
        var options = new DashboardOptionsBuilder().WithVisibilityRule<TeamPrefixRule>().WithHostAuthentication();

        options.Validate();
    }

    [Theory]
    [InlineData("none")]
    [InlineData("basic")]
    [InlineData("apikey")]
    [InlineData("custom")]
    public void ARuleWithAnyOtherAuthenticationMode_FailsWithAMessageThatSaysWhatToDo(string mode)
    {
        var options = new DashboardOptionsBuilder();
        _ = mode switch
        {
            "none" => options.WithNoAuth(),
            "basic" => options.WithBasicAuth("user", "password"),
            "apikey" => options.WithApiKey("key"),
            _ => options.WithCustomAuth(_ => true),
        };
        options.WithVisibilityRule<TeamPrefixRule>();

        var error = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("WithHostAuthentication", error.Message);
        Assert.Contains(nameof(TeamPrefixRule), error.Message);
    }

    [Fact]
    public void WithoutARule_NothingIsRequired()
    {
        new DashboardOptionsBuilder().WithBasicAuth("user", "password").Validate();
        new DashboardOptionsBuilder().Validate();
    }

    #endregion

    #region Scope

    [Fact]
    public void WithoutARule_NothingIsRestricted_AndNoPredicateIsAdded()
    {
        var scope = VisibilityScope<TimeTickerEntity, CronTickerEntity>.For(ContextFor(User("billing"), withRule: false));

        Assert.False(scope.HasRule);
        Assert.Null(scope.TimeTickers);
        Assert.Null(scope.CronTickers);
        Assert.True(scope.CanSeeFunction("anything"));
    }

    [Fact]
    public void ARuleIsAppliedToTheUserOfTheRequest()
    {
        var billing = VisibilityScope<TimeTickerEntity, CronTickerEntity>.For(ContextFor(User("billing"), withRule: true));

        Assert.True(billing.HasRule);
        var visible = new[]
        {
            new TimeTickerEntity { Function = "billing.Invoices" }, new TimeTickerEntity { Function = "ops.Cleanup" },
        }.AsQueryable().Where(billing.TimeTickers!).Select(t => t.Function).ToArray();
        Assert.Equal(new[] { "billing.Invoices" }, visible);
        Assert.True(billing.CanSeeFunction("billing.Invoices"));
        Assert.False(billing.CanSeeFunction("ops.Cleanup"));
    }

    [Fact]
    public void ARuleThatAllowsEverything_AddsNoPredicate()
    {
        var admin = VisibilityScope<TimeTickerEntity, CronTickerEntity>.For(ContextFor(User(admin: true), withRule: true));

        Assert.True(admin.HasRule);
        Assert.Null(admin.TimeTickers);
        Assert.Null(admin.CronTickers);
        Assert.True(admin.CanSeeFunction("anything"));
    }

    #endregion

    #region Rebinding a predicate to the entity type of the store

    [Fact]
    public void APredicate_IsRebound_ToAnApplicationsOwnTickerType()
    {
        Expression<Func<TimeTickerEntity, bool>> plain = t => t.Function.StartsWith("billing.") && t.Description != null;

        var own = PredicateRebinder.To<TimeTickerEntity, OwnTimeTicker>(plain)!;

        var items = new[]
        {
            new OwnTimeTicker { Function = "billing.Invoices", Description = "x" },
            new OwnTimeTicker { Function = "billing.Reminders" },
            new OwnTimeTicker { Function = "ops.Cleanup", Description = "x" },
        };
        Assert.Equal(new[] { "billing.Invoices" }, items.AsQueryable().Where(own).Select(t => t.Function).ToArray());
    }

    [Fact]
    public void ANullPredicate_StaysNull()
    {
        Assert.Null(PredicateRebinder.To<TimeTickerEntity, OwnTimeTicker>(null));
    }

    [Fact]
    public void APredicateThatUsesAMemberTheTargetTypeDoesNotHave_IsRefusedWithAClearMessage()
    {
        Expression<Func<CronTickerEntity, bool>> cron = c => c.Expression != null;

        var error = Assert.Throws<InvalidOperationException>(() => PredicateRebinder.To<CronTickerEntity, OwnTimeTicker>(cron));

        Assert.Contains("Expression", error.Message);
    }

    [Fact]
    public void APredicateThatUsesTheTickerItself_IsRefused()
    {
        Expression<Func<TimeTickerEntity, bool>> plain = t => t == null;

        Assert.Throws<InvalidOperationException>(() => PredicateRebinder.To<TimeTickerEntity, OwnTimeTicker>(plain));
    }

    #endregion
}
