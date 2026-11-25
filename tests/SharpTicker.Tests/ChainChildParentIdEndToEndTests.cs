using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpTicker.DependencyInjection;
using SharpTicker.Provider;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces.Managers;

namespace SharpTicker.Tests;

/// <summary>
/// The <see cref="TickerFunctionContext.ParentId"/> a chain child's function sees, as an application sees it:
/// <c>AddSharpTicker</c> on the in-memory store, a chain added through <see cref="ITimeTickerManager{TTimeTicker}"/> and run
/// by the scheduler. The docs say it is the parent's id for a chained child. Each kind of child is checked: one that starts
/// with its parent, one without a time that runs after its parent ended, one with a time of its own, and a grandchild. The
/// root sees no parent. The test also fails on any log entry at Warning or above.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class ChainChildParentIdEndToEndTests : IAsyncLifetime
{
    // Functions with no cron: they stay in the process-wide registry after the test, and no later host seeds or runs them.
    private const string Root = "ChainChildParentId.Root";
    private const string WithParent = "ChainChildParentId.WithParent";
    private const string AfterParent = "ChainChildParentId.AfterParent";
    private const string WithItsOwnTime = "ChainChildParentId.WithItsOwnTime";
    private const string Grandchild = "ChainChildParentId.Grandchild";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<Guid, Guid?> SeenParent = new();

    private readonly CapturedLogs _logs = new();
    private WebApplication? _app;

    public async Task InitializeAsync()
    {
        SeenParent.Clear();
        await ClearStoreAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        await ClearStoreAsync();
    }

    private static async Task ClearStoreAsync()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var store = new TickerInMemoryPersistenceProvider<TimeTickerEntity, CronTickerEntity>(services);
        var times = await store.GetTimeTickers(null, default);
        await store.RemoveTimeTickers(times.SelectMany(t => t.Children.SelectMany(c => c.Children.Select(g => g.Id).Prepend(c.Id)).Prepend(t.Id)).ToArray());
    }

    private string Report()
        => $"seen: {string.Join(", ", SeenParent.Select(p => $"{p.Key} -> {p.Value?.ToString() ?? "null"}"))}\nlog:\n{string.Join("\n", _logs.Entries.Select(e => $"{e.Level} {e.Category}: {e.Message}"))}";

    [Fact]
    public async Task Each_Chain_Child_Sees_Its_Parent_As_ParentId()
    {
        var services = new ServiceCollection();
        foreach (var function in new[] { Root, WithParent, AfterParent, WithItsOwnTime, Grandchild })
            services.MapTicker(function, (context, _) =>
            {
                SeenParent[context.Id] = context.ParentId;
                return Task.CompletedTask;
            });

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_logs);
        builder.Services.AddSharpTicker();
        _app = builder.Build();
        _app.UseSharpTicker();
        await _app.StartAsync();

        var manager = _app.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
        var at = DateTime.UtcNow.AddSeconds(1);
        var added = await manager.AddAsync(new TimeTickerEntity
        {
            Function = Root,
            ExecutionTime = at,
            Children =
            {
                new TimeTickerEntity { Function = WithParent, RunCondition = RunCondition.InProgress },
                new TimeTickerEntity
                {
                    Function = AfterParent,
                    RunCondition = RunCondition.OnSuccess,
                    Children = { new TimeTickerEntity { Function = Grandchild, RunCondition = RunCondition.OnSuccess } }
                },
                new TimeTickerEntity { Function = WithItsOwnTime, RunCondition = RunCondition.OnSuccess, ExecutionTime = at.AddSeconds(1) },
            }
        });
        Assert.True(added.IsSucceeded, added.Exception?.Message);
        var root = added.Result;
        var withParent = root.Children.Single(c => c.Function == WithParent).Id;
        var afterParent = root.Children.Single(c => c.Function == AfterParent);
        var withItsOwnTime = root.Children.Single(c => c.Function == WithItsOwnTime).Id;
        var grandchild = afterParent.Children.Single().Id;

        var deadline = DateTime.UtcNow + Limit;
        while (SeenParent.Count < 5)
        {
            Assert.True(DateTime.UtcNow < deadline, $"not within {Limit}: {SeenParent.Count} of 5 jobs ran\n{Report()}");
            await Task.Delay(100);
        }

        Assert.True(SeenParent[root.Id] == null, Report());
        Assert.True(SeenParent[withParent] == root.Id, Report());
        Assert.True(SeenParent[afterParent.Id] == root.Id, Report());
        Assert.True(SeenParent[withItsOwnTime] == root.Id, Report());
        Assert.True(SeenParent[grandchild] == afterParent.Id, Report());
        Assert.True(!_logs.Entries.Any(e => e.Level >= LogLevel.Warning), Report());
    }
}
