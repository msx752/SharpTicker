using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using SharpTicker.Dashboard.DependencyInjection;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// The page of the dashboard asks for a small script that holds the names of the hashed JS and CSS files of the build.
/// Its file name stays the same while its content changes with every dashboard build, so a proxy or a browser that kept
/// the old script after an upgrade loaded the old dashboard although the package was new. The page now asks for the
/// script with a fingerprint of its content in the address, so a new build is a new address.
/// </summary>
public class DashboardPreloadScriptTests
{
    private const string Page = "<html><head><title>x</title></head><body></body></html>";

    private static string Inject(string? version, string basePath = "/ticker/dashboard")
        => ServiceCollectionExtensions.InjectExternalScripts(Page, new DefaultHttpContext(), basePath, version!);

    [Fact]
    public void ThePageAsksForThePreloadScriptWithTheFingerprintInTheAddress()
    {
        var page = Inject("0123456789ab");

        Assert.Contains("<script src=\"__sharpticker-preload.js?v=0123456789ab\"></script>", page);
        // The config script is not cached by the dashboard, so its address stays as it was
        Assert.Contains("<script src=\"__sharpticker-config.js\"></script>", page);
    }

    [Fact]
    public void WithoutAFingerprint_ThePageAsksForThePlainAddress()
    {
        var page = Inject(null);

        Assert.Contains("<script src=\"__sharpticker-preload.js\"></script>", page);
        Assert.DoesNotContain("?v=", page);
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

    /// <summary>
    /// A test that needs the dashboard UI inside the assembly, which is there only when the UI was built before the
    /// .NET build (the test job of the CI does not build it). Without it the test is skipped, not passed.
    /// </summary>
    public sealed class FactWithTheDashboardBuildAttribute : FactAttribute
    {
        public FactWithTheDashboardBuildAttribute()
        {
            var built = typeof(ServiceCollectionExtensions).Assembly.GetManifestResourceNames()
                .Any(name => name.EndsWith("wwwroot.dist.index.html", StringComparison.Ordinal));
            if (!built)
                Skip = "The dashboard UI is not built into SharpTicker.Dashboard in this run (npm run build comes first).";
        }
    }

    [Collection(SharedStaticStateCollection.Name)]
    public class WithTheBuiltDashboard : IClassFixture<InMemoryDashboardApiFixture>
    {
        private const string Base = "/ticker/dashboard";

        private readonly InMemoryDashboardApiFixture _api;

        public WithTheBuiltDashboard(InMemoryDashboardApiFixture api) => _api = api;

        private async Task<string> GetAsync(string path)
        {
            var response = await _api.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsStringAsync();
        }

        [FactWithTheDashboardBuild]
        public async Task ThePageAsksForTheFingerprintOfTheScriptItServes()
        {
            var page = await GetAsync(Base + "/time-tickers");
            var script = await GetAsync(Base + "/__sharpticker-preload.js");

            var asked = Regex.Match(page, @"__sharpticker-preload\.js\?v=([0-9a-f]{12})""");
            Assert.True(asked.Success, page);
            Assert.Equal(ServiceCollectionExtensions.VersionOf(script), asked.Groups[1].Value);
        }

        [FactWithTheDashboardBuild]
        public async Task TheScriptIsServedWhateverTheQuery()
        {
            var withQuery = await GetAsync(Base + "/__sharpticker-preload.js?v=anything");
            var without = await GetAsync(Base + "/__sharpticker-preload.js");

            Assert.Equal(without, withQuery);
            Assert.Contains("index-", without); // the names of the hashed files of this build
        }
    }
}
