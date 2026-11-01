using System.Net;
using System.Text.RegularExpressions;
using SharpTicker.Dashboard.DependencyInjection;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// The page of the dashboard asks for a small script that holds the names of the hashed JS and CSS files of the build.
/// Its file name stays the same while its content changes with every dashboard build, so a proxy or a browser that kept
/// the old script after an upgrade loaded the old dashboard although the package was new. The page now asks for the
/// script with a fingerprint of its content in the address, so a new build is a new address.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class DashboardPreloadScriptTests : IClassFixture<InMemoryDashboardApiFixture>
{
    private const string Base = "/ticker/dashboard";

    private readonly InMemoryDashboardApiFixture _api;

    public DashboardPreloadScriptTests(InMemoryDashboardApiFixture api) => _api = api;

    private async Task<string> GetAsync(string path)
    {
        var response = await _api.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task ThePageAsksForThePreloadScriptWithTheFingerprintOfItsContent()
    {
        var page = await GetAsync(Base + "/time-tickers");
        var script = await GetAsync(Base + "/__sharpticker-preload.js");

        var asked = Regex.Match(page, @"__sharpticker-preload\.js\?v=([0-9a-f]{12})""");
        Assert.True(asked.Success, page);
        Assert.Equal(ServiceCollectionExtensions.VersionOf(script), asked.Groups[1].Value);
    }

    [Fact]
    public async Task TheScriptIsServedWhateverTheQuery()
    {
        var withQuery = await GetAsync(Base + "/__sharpticker-preload.js?v=anything");
        var without = await GetAsync(Base + "/__sharpticker-preload.js");

        Assert.Equal(without, withQuery);
        Assert.Contains("index-", without); // the names of the hashed files of this build
    }

    [Fact]
    public void TheFingerprintChangesWithTheContentAndOnlyWithIt()
    {
        var old = "src=\"/assets/index-DVF8oRIG.js\" href=\"/assets/index-0zCv2v9a.css\"";
        var current = "src=\"/assets/index-HAuLUDoJ.js\" href=\"/assets/index-BeFe0rmL.css\"";

        Assert.NotEqual(ServiceCollectionExtensions.VersionOf(old), ServiceCollectionExtensions.VersionOf(current));
        Assert.Equal(ServiceCollectionExtensions.VersionOf(current), ServiceCollectionExtensions.VersionOf(current));
        Assert.Matches("^[0-9a-f]{12}$", ServiceCollectionExtensions.VersionOf(current));
    }
}
