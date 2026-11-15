using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpTicker.Dashboard.DependencyInjection;
using SharpTicker.DependencyInjection;
using SharpTicker.EntityFrameworkCore.DbContextFactory;
using SharpTicker.EntityFrameworkCore.DependencyInjection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Base;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>An application's own time ticker type, with a column of its own (#95).</summary>
public class AppTimeTicker : TimeTickerEntity<AppTimeTicker>
{
    public string? Tenant { get; set; }
}

/// <summary>An application's own cron ticker type, with a column of its own (#95).</summary>
public class AppCronTicker : CronTickerEntity
{
    public string? Tenant { get; set; }
}

/// <summary>
/// A real application that registers its own ticker types with <c>AddSharpTicker&lt;AppTimeTicker, AppCronTicker&gt;</c>
/// and the dashboard, served by a TestServer. The scheduler is switched off, so the data stays as seeded.
/// </summary>
public abstract class OwnEntityTypeDashboardHost : IAsyncLifetime
{
    public const string Api = "/ticker/dashboard/api";

    public static readonly Guid TimeId = Guid.Parse("00000000-0000-0000-0000-0000000095a1");
    public static readonly Guid CronId = Guid.Parse("00000000-0000-0000-0000-0000000095c1");
    public static readonly Guid OccurrenceId = Guid.Parse("00000000-0000-0000-0000-0000000095e1");

    private WebApplication? _app;

    public CapturedLogs Logs { get; } = new();
    public HttpClient Client { get; private set; } = null!;
    private ITickerPersistenceProvider<AppTimeTicker, AppCronTicker> Store { get; set; } = null!;

    protected abstract void ConfigureStore(TickerOptionsBuilder<AppTimeTicker, AppCronTicker> options);

    /// <summary>Whether the store gives back the columns of the application's own type. The in-memory store copies only the
    /// columns of the plain entity, so the dashboard has nothing more to write there.</summary>
    public virtual bool KeepsOwnColumns => true;
    protected virtual void DisposeStore() { }

    public async Task InitializeAsync()
    {
        TickerFunctionDelegate noOp = (_, _, _) => Task.CompletedTask;
        TickerFunctionProvider.RegisterFunctions(new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>
        {
            ["OwnTypeJob"] = ("", TickerTaskPriority.Normal, noOp, 0),
            ["OwnTypeCron"] = ("", TickerTaskPriority.Normal, noOp, 0),
        });
        TickerFunctionProvider.Build();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(Logs);
        builder.Services.AddSharpTicker<AppTimeTicker, AppCronTicker>(options =>
        {
            options.DisableBackgroundServices();
            ConfigureStore(options);
            options.AddDashboard(dashboard => dashboard.WithNoAuth());
        });

        _app = builder.Build();
        _app.UseSharpTicker();
        await _app.StartAsync();

        var startup = _app.Services.GetRequiredService<TickerExecutionContext>().ExternalProviderStartup;
        if (startup != null) await startup;

        Client = _app.GetTestClient();
        Store = _app.Services.GetRequiredService<ITickerPersistenceProvider<AppTimeTicker, AppCronTicker>>();

        // The in-memory store is shared by the whole process: start from an empty one.
        var occurrences = await Store.GetAllCronTickerOccurrences(null, default);
        await Store.RemoveCronTickerOccurrences(occurrences.Select(o => o.Id).ToArray(), default);
        var crons = await Store.GetCronTickers(null, default);
        await Store.RemoveCronTickers(crons.Select(c => c.Id).ToArray(), default);
        var times = await Store.GetTimeTickers(null, default);
        await Store.RemoveTimeTickers(times.Select(t => t.Id).ToArray());

        var today = DateTime.UtcNow.Date;
        await Store.AddTimeTickers(new[]
        {
            new AppTimeTicker
            {
                Id = TimeId, Function = "OwnTypeJob", Status = TickerStatus.Idle, ExecutionTime = today.AddDays(2).AddHours(12),
                Description = "own type", Tenant = "tenant-a", CreatedAt = today, UpdatedAt = today,
            },
        });
        await Store.InsertCronTickers(new[]
        {
            new AppCronTicker
            {
                Id = CronId, Function = "OwnTypeCron", Expression = "0 0 * * * *", IsEnabled = true, Tenant = "tenant-a",
                CreatedAt = today, UpdatedAt = today,
            },
        }, default);
        await Store.InsertCronTickerOccurrences(new[]
        {
            new CronTickerOccurrenceEntity<AppCronTicker>
            {
                Id = OccurrenceId, CronTickerId = CronId, Status = TickerStatus.Done, ExecutionTime = today.AddHours(12),
                ExecutedAt = today.AddHours(12), CreatedAt = today, UpdatedAt = today,
            },
        }, default);
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        DisposeStore();
    }

    public async Task<(int Status, string Body)> SendAsync(HttpMethod method, string path, string? json = null)
    {
        using var request = new HttpRequestMessage(method, Api + path);
        if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await Client.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}

public sealed class InMemoryOwnEntityTypeDashboardHost : OwnEntityTypeDashboardHost
{
    protected override void ConfigureStore(TickerOptionsBuilder<AppTimeTicker, AppCronTicker> options) { }

    public override bool KeepsOwnColumns => false;
}

public sealed class EfCoreOwnEntityTypeDashboardHost : OwnEntityTypeDashboardHost
{
    private SqliteConnection? _connection;

    protected override void ConfigureStore(TickerOptionsBuilder<AppTimeTicker, AppCronTicker> options)
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var schema = new DbContextOptionsBuilder<SharpTickerDbContext<AppTimeTicker, AppCronTicker>>().UseSqlite(_connection).Options;
        using (var context = new SharpTickerDbContext<AppTimeTicker, AppCronTicker>(schema))
            context.Database.EnsureCreated();

        options.AddOperationalStore(ef => ef.UseSharpTickerDbContext<SharpTickerDbContext<AppTimeTicker, AppCronTicker>>(db => db.UseSqlite(_connection)));
    }

    protected override void DisposeStore() => _connection?.Dispose();
}

/// <summary>
/// The dashboard API with the application's own ticker types (#95). Up to 1.5.2 every endpoint that reads or writes one
/// of those types failed, because the dashboard's JSON options only knew the plain entity types.
/// </summary>
public abstract class DashboardOwnEntityTypeTests<THost> : IAsyncLifetime where THost : OwnEntityTypeDashboardHost, new()
{
    private readonly THost _host = new();

    public Task InitializeAsync() => _host.InitializeAsync();
    public Task DisposeAsync() => _host.DisposeAsync();

    private static string InTwoDays => DateTime.UtcNow.AddDays(2).ToString("yyyy-MM-ddTHH:mm:ss'Z'");

    private void AssertNothingLoggedAtWarningOrAbove()
    {
        var entries = _host.Logs.Entries.Where(e => e.Level >= LogLevel.Warning).ToArray();
        Assert.True(entries.Length == 0,
            "Logged at Warning or above:\n" + string.Join("\n", entries.Select(e => $"{e.Level} {e.Category}: {e.Message} {e.Exception?.GetType().Name}: {e.Exception?.Message}")));
    }

    private async Task<JsonElement> GetAsync(string path)
    {
        var (status, body) = await _host.SendAsync(HttpMethod.Get, path);
        Assert.True(status == 200, $"GET {path} returned {status}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static JsonElement[] Rows(JsonElement answer)
        => (answer.ValueKind == JsonValueKind.Array ? answer : answer.GetProperty("items")).EnumerateArray().ToArray();

    [Theory]
    [InlineData("/time-tickers")]
    [InlineData("/time-tickers/paginated")]
    public async Task TheTimeTickerLists_AnswerWithTheAppsOwnType(string path)
    {
        var row = Assert.Single(Rows(await GetAsync(path)));

        Assert.Equal(OwnEntityTypeDashboardHost.TimeId, row.GetProperty("id").GetGuid());
        if (_host.KeepsOwnColumns)
            Assert.Equal("tenant-a", row.GetProperty("tenant").GetString());
        AssertNothingLoggedAtWarningOrAbove();
    }

    [Theory]
    [InlineData("")]
    [InlineData("/paginated")]
    public async Task TheOccurrenceLists_AnswerWithTheAppsOwnType(string suffix)
    {
        var row = Assert.Single(Rows(await GetAsync($"/cron-ticker-occurrences/{OwnEntityTypeDashboardHost.CronId}{suffix}")));

        Assert.Equal(OwnEntityTypeDashboardHost.OccurrenceId, row.GetProperty("id").GetGuid());
        AssertNothingLoggedAtWarningOrAbove();
    }

    [Fact]
    public async Task AddingAndUpdatingATimeTicker_ReadsTheAppsOwnType()
    {
        var (status, body) = await _host.SendAsync(HttpMethod.Post, "/time-ticker/add",
            $$"""{"function":"OwnTypeJob","executionTime":"{{InTwoDays}}","description":"added"}""");
        Assert.True(status == 200, body);
        var added = JsonDocument.Parse(body).RootElement;
        Assert.True(added.GetProperty("success").GetBoolean(), body);
        var id = added.GetProperty("tickerId").GetGuid();

        (status, body) = await _host.SendAsync(HttpMethod.Put, $"/time-ticker/update?id={id}",
            $$"""{"function":"OwnTypeJob","executionTime":"{{InTwoDays}}","description":"changed"}""");
        Assert.True(status == 200, body);
        Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("success").GetBoolean(), body);

        var row = Rows(await GetAsync("/time-tickers")).Single(t => t.GetProperty("id").GetGuid() == id);
        Assert.Equal("changed", row.GetProperty("description").GetString());
        AssertNothingLoggedAtWarningOrAbove();
    }

    [Fact]
    public async Task AddingAndUpdatingACronTicker_ReadsTheAppsOwnType()
    {
        var (status, body) = await _host.SendAsync(HttpMethod.Post, "/cron-ticker/add",
            """{"function":"OwnTypeCron","expression":"0 0 * * * *","description":"added"}""");
        Assert.True(status == 200, body);
        var added = JsonDocument.Parse(body).RootElement;
        Assert.True(added.GetProperty("success").GetBoolean(), body);
        var id = added.GetProperty("tickerId").GetGuid();

        (status, body) = await _host.SendAsync(HttpMethod.Put, $"/cron-ticker/update?id={id}",
            """{"function":"OwnTypeCron","expression":"0 30 * * * *","description":"changed"}""");
        Assert.True(status == 200, body);
        Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("success").GetBoolean(), body);

        var row = Rows(await GetAsync("/cron-tickers")).Single(c => c.GetProperty("id").GetGuid() == id);
        Assert.Equal("0 30 * * * *", row.GetProperty("expression").GetString());
        AssertNothingLoggedAtWarningOrAbove();
    }
}

/// <summary>The dashboard API with the application's own ticker types on the in-memory store.</summary>
[Collection(SharedStaticStateCollection.Name)]
public class InMemoryDashboardOwnEntityTypeTests : DashboardOwnEntityTypeTests<InMemoryOwnEntityTypeDashboardHost>
{
}

/// <summary>The dashboard API with the application's own ticker types on EF Core with SQLite.</summary>
[Collection(SharedStaticStateCollection.Name)]
public class EfCoreDashboardOwnEntityTypeTests : DashboardOwnEntityTypeTests<EfCoreOwnEntityTypeDashboardHost>
{
}

/// <summary>Which resolvers the dashboard's own JSON options get, with reflection-based serialization on and off (#95).</summary>
public sealed class DashboardJsonOptionsResolverTests
{
    private static JsonSerializerOptions DashboardOptions()
        => new() { TypeInfoResolverChain = { SharpTicker.Dashboard.Infrastructure.DashboardJsonSerializerContext.Default } };

    [Fact]
    public void WithReflectionOn_AReflectionResolverIsAddedAfterTheDashboardsContext()
    {
        var options = DashboardOptions();

        ServiceCollectionExtensions.AddReflectionResolverWhenReflectionIsOn(options, reflectionIsEnabled: true);

        Assert.Equal(2, options.TypeInfoResolverChain.Count);
        Assert.Same(SharpTicker.Dashboard.Infrastructure.DashboardJsonSerializerContext.Default, options.TypeInfoResolverChain[0]);
        Assert.IsType<System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver>(options.TypeInfoResolverChain[1]);
        Assert.NotNull(options.GetTypeInfo(typeof(SharpTicker.Utilities.Models.PaginationResult<AppTimeTicker>)));
    }

    [Fact]
    public void WithReflectionOff_TheOptionsAreLeftAlone()
    {
        var options = DashboardOptions();

        ServiceCollectionExtensions.AddReflectionResolverWhenReflectionIsOn(options, reflectionIsEnabled: false);

        Assert.Same(SharpTicker.Dashboard.Infrastructure.DashboardJsonSerializerContext.Default, Assert.Single(options.TypeInfoResolverChain));
    }
}
