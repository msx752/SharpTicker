using System.Net;
using Microsoft.AspNetCore.Http;
using SharpTicker.Dashboard.DependencyInjection;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// The page of the dashboard needs a small script that holds the names of the hashed JS and CSS files of the build. Its
/// content changes with every dashboard build, so it must not be something a proxy or a browser can keep: after an
/// upgrade the page would load the old dashboard. It is part of the configuration script, which is made for each request
/// and is never stored, and what the dashboard answers in place of a file that does not exist is never stored either.
/// </summary>
public class DashboardPreloadScriptTests
{
    private const string Page = "<html><head><title>x</title></head><body></body></html>";
    private const string Config = "(function(){window.__dynamic_base__='/ticker/dashboard';})();";
    private const string Preload = "(function(){var preloads=[{src:'/assets/index-HAuLUDoJ.js'}];})();";

    [Fact]
    public void ThePreloadScript_FollowsTheConfiguration_ThatItNeeds()
    {
        var script = ServiceCollectionExtensions.WithPreload(Config, Preload);

        Assert.StartsWith(Config, script);
        Assert.EndsWith(Preload, script);
        Assert.True(script.IndexOf("__dynamic_base__", StringComparison.Ordinal) < script.IndexOf("preloads", StringComparison.Ordinal));
        // On a line of its own, so a comment at the end of one script cannot swallow the other
        Assert.Contains(Config + "\n" + Preload, script);
    }

    [Fact]
    public void WithoutAPreloadScript_TheConfigurationIsLeftAsItIs()
    {
        Assert.Equal(Config, ServiceCollectionExtensions.WithPreload(Config, null!));
        Assert.Equal(Config, ServiceCollectionExtensions.WithPreload(Config, ""));
    }

    [Fact]
    public void ThePageAsksForOneScript_NotForTheOldPreloadAddress()
    {
        var page = ServiceCollectionExtensions.InjectExternalScripts(Page, new DefaultHttpContext(), "/ticker/dashboard");

        Assert.Contains("<script src=\"__sharpticker-config.js\"></script>", page);
        Assert.DoesNotContain("__sharpticker-preload", page);
        Assert.Contains("<base href=\"/ticker/dashboard/\" />", page);
        // In the head, before anything else, as before
        Assert.True(page.IndexOf("<base", StringComparison.Ordinal) < page.IndexOf("<title>", StringComparison.Ordinal));
    }

    [Fact]
    public void TheOldPreloadAddress_AnswersNothingToDo()
    {
        // Doing it twice would add every tag twice
        Assert.DoesNotContain("preloads", ServiceCollectionExtensions.LegacyPreloadNote);
        Assert.StartsWith("/*", ServiceCollectionExtensions.LegacyPreloadNote);
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

        private async Task<HttpResponseMessage> GetAsync(string path)
        {
            var response = await _api.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return response;
        }

        private static string[] CacheControl(HttpResponseMessage response)
            => response.Headers.CacheControl?.ToString().Split(", ") ?? Array.Empty<string>();

        [FactWithTheDashboardBuild]
        public async Task TheConfigurationScript_CarriesThePreloadScript_AndIsNeverStored()
        {
            var response = await GetAsync(Base + "/__sharpticker-config.js");
            var script = await response.Content.ReadAsStringAsync();

            Assert.Contains("window.__dynamic_base__", script);
            Assert.Contains("assets/index-", script); // the hashed files of this build
            Assert.True(script.IndexOf("window.__dynamic_base__", StringComparison.Ordinal) < script.IndexOf("assets/index-", StringComparison.Ordinal));
            Assert.Contains("no-cache", CacheControl(response));
        }

        [FactWithTheDashboardBuild]
        public async Task TheOldPreloadAddress_StillAnswers_WithNothingToDo_AndIsNeverStored()
        {
            var response = await GetAsync(Base + "/__sharpticker-preload.js");

            Assert.Equal(ServiceCollectionExtensions.LegacyPreloadNote, await response.Content.ReadAsStringAsync());
            Assert.Contains("no-cache", CacheControl(response));
        }

        [FactWithTheDashboardBuild]
        public async Task ThePage_AndTheAnswerForAFileThatDoesNotExist_AreNeverStored()
        {
            var page = await GetAsync(Base + "/time-tickers");
            var missing = await GetAsync(Base + "/assets/index-doesNotExist.js");

            Assert.Contains("no-cache", CacheControl(page));
            Assert.Contains("no-cache", CacheControl(missing));
            Assert.DoesNotContain("__sharpticker-preload", await page.Content.ReadAsStringAsync());
        }
    }
}
