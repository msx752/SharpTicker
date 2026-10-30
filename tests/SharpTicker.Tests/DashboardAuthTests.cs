using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SharpTicker.Dashboard;
using Xunit;

namespace SharpTicker.Tests;

public sealed class BasicAuthDashboardFixture : DashboardApiFixture
{
    protected override void ConfigureDashboard(DashboardOptionsBuilder dashboard) => dashboard.WithBasicAuth("admin", "s3cret");
}

public sealed class ApiKeyDashboardFixture : DashboardApiFixture
{
    protected override void ConfigureDashboard(DashboardOptionsBuilder dashboard) => dashboard.WithApiKey("key-123");
}

public sealed class CustomAuthDashboardFixture : DashboardApiFixture
{
    protected override void ConfigureDashboard(DashboardOptionsBuilder dashboard) => dashboard.WithCustomAuth(value => value == "let-me-in");
}

/// <summary>
/// With authentication switched on, every dashboard API call has to carry valid credentials. Only the endpoints the
/// login screen needs stay open.
/// </summary>
public abstract class DashboardAuthTests<TFixture> : IClassFixture<TFixture> where TFixture : DashboardApiFixture
{
    private readonly TFixture _fixture;

    protected DashboardAuthTests(TFixture fixture) => _fixture = fixture;

    /// <summary>The Authorization header value that is accepted.</summary>
    protected abstract string ValidCredentials { get; }

    protected abstract string WrongCredentials { get; }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? authorization)
    {
        var request = new HttpRequestMessage(method, path);
        if (authorization != null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return _fixture.Client.SendAsync(request);
    }

    private const string Protected = DashboardApiFixture.Api + "/time-tickers";

    [Fact]
    public async Task AnApiCallWithoutCredentials_IsRefused()
    {
        var response = await SendAsync(HttpMethod.Get, Protected, null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Unauthorized", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnApiCallWithWrongCredentials_IsRefused()
    {
        var response = await SendAsync(HttpMethod.Get, Protected, WrongCredentials);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnApiCallWithValidCredentials_GetsTheData()
    {
        var response = await SendAsync(HttpMethod.Get, Protected, ValidCredentials);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/cron-tickers")]
    [InlineData("/time-tickers")]
    [InlineData("/options")]
    public async Task EveryReadEndpoint_IsProtected(string path)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, DashboardApiFixture.Api + path, null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, DashboardApiFixture.Api + path, ValidCredentials)).StatusCode);
    }

    [Fact]
    public async Task AWriteWithoutCredentials_IsRefusedAndChangesNothing()
    {
        var response = await SendAsync(HttpMethod.Delete, $"{Protected}/{DashboardApiFixture.DoneId}", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var still = await SendAsync(HttpMethod.Get, $"{Protected}/{DashboardApiFixture.DoneId}", ValidCredentials);
        Assert.NotEqual(HttpStatusCode.NotFound, still.StatusCode);
    }

    [Fact]
    public async Task TheLoginScreenCanStillReadTheAuthSettings_WithoutCredentials()
    {
        var response = await SendAsync(HttpMethod.Get, DashboardApiFixture.Api + "/auth/info", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(json.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task ValidatingCredentials_AcceptsTheRightOnesAndRefusesTheWrongOnes()
    {
        var ok = await SendAsync(HttpMethod.Post, DashboardApiFixture.Api + "/auth/validate", ValidCredentials);
        var no = await SendAsync(HttpMethod.Post, DashboardApiFixture.Api + "/auth/validate", WrongCredentials);
        var none = await SendAsync(HttpMethod.Post, DashboardApiFixture.Api + "/auth/validate", null);

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True(JsonDocument.Parse(await ok.Content.ReadAsStringAsync()).RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, no.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
    }

    [Fact]
    public async Task TheCredentialsAreAcceptedAsAnAccessTokenInTheQuery_ForTheSignalRConnection()
    {
        var token = ValidCredentials.Substring(ValidCredentials.IndexOf(' ') + 1);

        var response = await SendAsync(HttpMethod.Get, $"{Protected}?access_token={Uri.EscapeDataString(token)}", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

public class BasicAuthDashboardTests : DashboardAuthTests<BasicAuthDashboardFixture>
{
    public BasicAuthDashboardTests(BasicAuthDashboardFixture fixture) : base(fixture) { }

    protected override string ValidCredentials => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:s3cret"));
    protected override string WrongCredentials => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:wrong"));
}

public class ApiKeyDashboardTests : DashboardAuthTests<ApiKeyDashboardFixture>
{
    public ApiKeyDashboardTests(ApiKeyDashboardFixture fixture) : base(fixture) { }

    protected override string ValidCredentials => "Bearer key-123";
    protected override string WrongCredentials => "Bearer key-124";
}

public class CustomAuthDashboardTests : DashboardAuthTests<CustomAuthDashboardFixture>
{
    public CustomAuthDashboardTests(CustomAuthDashboardFixture fixture) : base(fixture) { }

    protected override string ValidCredentials => "let-me-in";
    protected override string WrongCredentials => "nope";
}
