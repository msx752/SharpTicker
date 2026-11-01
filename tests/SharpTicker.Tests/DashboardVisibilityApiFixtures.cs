using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharpTicker.Dashboard;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Base;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// The dashboard with host authentication and a team prefix visibility rule, on each store. A request says who it is
/// with the headers X-Team (the team the user belongs to) and X-Admin (a user who sees everything); a request with
/// neither is not signed in.
/// </summary>
internal static class VisibilityFixtureSetup
{
    public const string Billing = "billing", Ops = "ops";

    public static readonly Guid BillingInvoicesId = Guid.Parse("00000000-0000-0000-0000-00000000b001");
    public static readonly Guid BillingRemindersId = Guid.Parse("00000000-0000-0000-0000-00000000b002");
    public static readonly Guid OpsCleanupId = Guid.Parse("00000000-0000-0000-0000-00000000a001");
    public static readonly Guid BillingCronId = Guid.Parse("00000000-0000-0000-0000-00000000bc01");
    public static readonly Guid OpsCronId = Guid.Parse("00000000-0000-0000-0000-00000000ac01");
    public static readonly Guid BillingOccurrenceDoneId = Guid.Parse("00000000-0000-0000-0000-0000000b0d01");
    public static readonly Guid BillingOccurrenceFailedId = Guid.Parse("00000000-0000-0000-0000-0000000b0f01");
    public static readonly Guid OpsOccurrenceDoneId = Guid.Parse("00000000-0000-0000-0000-0000000a0d01");

    public static void ConfigureServices(IServiceCollection services)
    {
        services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", _ => { });
        services.AddAuthorization();
    }

    public static void UseAuthentication(WebApplication app) => app.UseAuthentication();

    public static void ConfigureDashboard(DashboardOptionsBuilder dashboard)
        => dashboard.WithHostAuthentication().WithVisibilityRule<DashboardVisibilityTests.TeamPrefixRule>();

    public static void RegisterFunctions()
    {
        TickerFunctionDelegate noOp = (_, _, _) => Task.CompletedTask;
        var functions = new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>();
        foreach (var name in new[] { "billing.Invoices", "billing.Reminders", "billing.Nightly", "ops.Cleanup", "ops.Nightly" })
            functions[name] = ("", TickerTaskPriority.Normal, noOp, 0);
        TickerFunctionProvider.RegisterFunctions(functions);
        TickerFunctionProvider.Build();
    }

    public static async Task SeedAsync(DashboardApiFixture api)
    {
        RegisterFunctions();
        await api.Store.AddTimeTickers(new[]
        {
            api.Time(BillingInvoicesId, "billing.Invoices", TickerStatus.Done, api.Today, request: "{\"Customer\":\"acme\"}"),
            api.Time(BillingRemindersId, "billing.Reminders", TickerStatus.Failed, api.Today, exception: "boom"),
            api.Time(OpsCleanupId, "ops.Cleanup", TickerStatus.Done, api.Today),
        }, default);

        await api.Store.InsertCronTickers(new[]
        {
            new CronTickerEntity { Id = BillingCronId, Function = "billing.Nightly", Expression = "0 0 2 * * *", IsEnabled = true, CreatedAt = api.Today, UpdatedAt = api.Today },
            new CronTickerEntity { Id = OpsCronId, Function = "ops.Nightly", Expression = "0 0 3 * * *", IsEnabled = true, CreatedAt = api.Today, UpdatedAt = api.Today },
        }, default);

        CronTickerOccurrenceEntity<CronTickerEntity> Occurrence(Guid id, Guid cron, TickerStatus status, DateTime day) => new()
        {
            Id = id, CronTickerId = cron, Status = status, ExecutionTime = day.Date.AddHours(12), ExecutedAt = day.Date.AddHours(12),
            CreatedAt = api.Today, UpdatedAt = api.Today,
        };

        await api.Store.InsertCronTickerOccurrences(new[]
        {
            Occurrence(BillingOccurrenceDoneId, BillingCronId, TickerStatus.Done, api.Yesterday),
            Occurrence(BillingOccurrenceFailedId, BillingCronId, TickerStatus.Failed, api.Today),
            Occurrence(OpsOccurrenceDoneId, OpsCronId, TickerStatus.Done, api.Today),
        }, default);
    }

    /// <summary>Sends a request as a user of the given team, or as an admin, or not signed in when both are null.</summary>
    public static async Task<(int Status, JsonElement? Body)> SendAsync(
        this DashboardApiFixture api, string? team, bool admin, HttpMethod method, string path, string? json = null)
    {
        using var request = new HttpRequestMessage(method, DashboardApiFixture.Api + path);
        if (team != null) request.Headers.Add("X-Team", team);
        if (admin) request.Headers.Add("X-Admin", "true");
        if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await api.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, string.IsNullOrWhiteSpace(body) ? null : JsonDocument.Parse(body).RootElement.Clone());
    }

    public static async Task<JsonElement> GetAsAsync(this DashboardApiFixture api, string? team, string path, bool admin = false)
    {
        var (status, body) = await api.SendAsync(team, admin, HttpMethod.Get, path);
        Assert.True(status == 200, $"GET {path} as {(admin ? "admin" : team)} returned {status}");
        return body!.Value;
    }

    private sealed class HeaderAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var team = Request.Headers["X-Team"].ToString();
            var admin = Request.Headers["X-Admin"].ToString() == "true";
            if (team == "" && !admin)
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity("Test");
            identity.AddClaim(new Claim(ClaimTypes.Name, admin ? "admin" : team));
            if (team != "") identity.AddClaim(new Claim("team", team));
            if (admin) identity.AddClaim(new Claim(ClaimTypes.Role, "admin"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }
}

public sealed class InMemoryVisibilityApiFixture : InMemoryDashboardApiFixture
{
    protected override void ConfigureHostServices(IServiceCollection services) => VisibilityFixtureSetup.ConfigureServices(services);
    protected override void UseBeforeSharpTicker(WebApplication app) => VisibilityFixtureSetup.UseAuthentication(app);
    protected override void ConfigureDashboard(DashboardOptionsBuilder dashboard) => VisibilityFixtureSetup.ConfigureDashboard(dashboard);
    protected override Task SeedMoreAsync() => VisibilityFixtureSetup.SeedAsync(this);
}

public sealed class EfCoreVisibilityApiFixture : EfCoreDashboardApiFixture
{
    protected override void ConfigureHostServices(IServiceCollection services) => VisibilityFixtureSetup.ConfigureServices(services);
    protected override void UseBeforeSharpTicker(WebApplication app) => VisibilityFixtureSetup.UseAuthentication(app);
    protected override void ConfigureDashboard(DashboardOptionsBuilder dashboard) => VisibilityFixtureSetup.ConfigureDashboard(dashboard);
    protected override Task SeedMoreAsync() => VisibilityFixtureSetup.SeedAsync(this);
}
