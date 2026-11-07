using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SharpTicker.Dashboard.Infrastructure.Dashboard;
using SharpTicker.Provider;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Managers;
using SharpTicker.Utilities.Models;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// Every path that marks a row InProgress and hands its job on (the fallback, run-now from the API, run-now of a cron from
/// the dashboard) gives the job's context the marker its claim wrote. Without it a job that never starts cannot be put back.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class ClaimMarkerHandOffTests : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FakeClock(DateTime start) : ITickerClock
    {
        public DateTime UtcNow { get; set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    private readonly FakeClock _clock = new(T0);
    private readonly List<Guid> _timeTickerIds = [];
    private readonly List<Guid> _cronTickerIds = [];
    private readonly List<Guid> _occurrenceIds = [];

    public Task InitializeAsync()
    {
        TickerFunctionProvider.RegisterFunctions(new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>
        {
            ["HandOff.Fn"] = ("", TickerTaskPriority.Normal, (_, _, _) => Task.CompletedTask, 0)
        });
        TickerFunctionProvider.Build();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        var node = CreateProvider("cleanup");
        var occurrences = await node.GetAllCronTickerOccurrences(x => _cronTickerIds.Contains(x.CronTickerId), CancellationToken.None);
        await node.RemoveCronTickerOccurrences(occurrences.Select(x => x.Id).Concat(_occurrenceIds).ToArray(), CancellationToken.None);
        await node.RemoveTimeTickers(_timeTickerIds.ToArray(), CancellationToken.None);
        await node.RemoveCronTickers(_cronTickerIds.ToArray(), CancellationToken.None);
    }

    private TickerInMemoryPersistenceProvider<TimeTickerEntity, CronTickerEntity> CreateProvider(string nodeIdentifier)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITickerClock>(_clock);
        services.AddSingleton(new SchedulerOptionsBuilder { NodeIdentifier = nodeIdentifier });
        return new TickerInMemoryPersistenceProvider<TimeTickerEntity, CronTickerEntity>(services.BuildServiceProvider());
    }

    private static ISharpTickerNotificationHubSender Hub()
    {
        var hub = Substitute.For<ISharpTickerNotificationHubSender>();
        hub.UpdateTimeTickerNotifyAsync(Arg.Any<object>()).Returns(Task.CompletedTask);
        hub.UpdateCronOccurrenceFromInternalFunctionContext<CronTickerEntity>(Arg.Any<InternalFunctionContext>()).Returns(Task.CompletedTask);
        return hub;
    }

    private IInternalTickerManager InternalManager(TickerInMemoryPersistenceProvider<TimeTickerEntity, CronTickerEntity> provider)
        => new InternalTickerManager<TimeTickerEntity, CronTickerEntity>(provider, _clock, Hub());

    private static (ISharpTickerDispatcher Dispatcher, List<InternalFunctionContext> Dispatched) RecordingDispatcher()
    {
        var dispatched = new List<InternalFunctionContext>();
        var dispatcher = Substitute.For<ISharpTickerDispatcher>();
        dispatcher.IsEnabled.Returns(true);
        dispatcher.DispatchAsync(Arg.Any<InternalFunctionContext[]>(), Arg.Any<CancellationToken>())
            .Returns(call => { dispatched.AddRange(call.Arg<InternalFunctionContext[]>()); return Task.CompletedTask; });
        return (dispatcher, dispatched);
    }

    private async Task<Guid> SeedTimeTicker()
    {
        var ticker = new TimeTickerEntity
        {
            Id = Guid.NewGuid(), Function = "HandOff.Fn", ExecutionTime = T0, Status = TickerStatus.Idle,
            CreatedAt = T0.AddHours(-2), UpdatedAt = T0.AddHours(-1), Request = []
        };
        _timeTickerIds.Add(ticker.Id);
        await CreateProvider("seed").AddTimeTickers([ticker], CancellationToken.None);
        return ticker.Id;
    }

    private async Task<Guid> SeedCronTicker()
    {
        var cron = new CronTickerEntity
        {
            Id = Guid.NewGuid(), Function = "HandOff.Fn", Expression = "* * * * *", Request = [],
            CreatedAt = T0.AddHours(-2), UpdatedAt = T0.AddHours(-2)
        };
        _cronTickerIds.Add(cron.Id);
        Assert.Equal(1, await CreateProvider("seed").InsertCronTickers([cron], CancellationToken.None));
        return cron.Id;
    }

    [Fact]
    public async Task Fallback_GivesEachJobTheMarkerOfItsClaim()
    {
        var node = CreateProvider("node-B");
        var timeId = await SeedTimeTicker();
        var cronId = await SeedCronTicker();
        var slot = (T0, new[] { new InternalManagerContext(cronId) { FunctionName = "HandOff.Fn", Expression = "* * * * *" } });
        var occurrenceId = Assert.Single(await Drain(CreateProvider("node-B").QueueCronTickerOccurrences(slot, CancellationToken.None))).Id;
        _occurrenceIds.Add(occurrenceId);
        _clock.Advance(TimeSpan.FromSeconds(2));

        var functions = await InternalManager(node).RunTimedOutTickers(CancellationToken.None);

        var time = Assert.Single(functions, x => x.TickerId == timeId);
        Assert.Equal((await node.GetTimeTickerById(timeId, CancellationToken.None)).UpdatedAt, time.ClaimMarker);
        var occurrence = Assert.Single(functions, x => x.TickerId == occurrenceId);
        var stored = (await node.GetAllCronTickerOccurrences(x => x.Id == occurrenceId, CancellationToken.None)).Single();
        Assert.Equal(stored.UpdatedAt, occurrence.ClaimMarker);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunNow_GivesTheJobTheMarkerOfItsClaim_AndNoneToItsChildren(bool batch)
    {
        var node = CreateProvider("node-A");
        var (dispatcher, dispatched) = RecordingDispatcher();
        ITimeTickerManager<TimeTickerEntity> manager = new TickerManager<TimeTickerEntity, CronTickerEntity>(
            node, Substitute.For<ISharpTickerHostScheduler>(), _clock, Hub(), new TickerExecutionContext(), dispatcher);
        var child = new TimeTickerEntity { Id = Guid.NewGuid(), Function = "HandOff.Fn", Request = [] };
        var ticker = new TimeTickerEntity { Id = Guid.NewGuid(), Function = "HandOff.Fn", Request = [], Children = [child] };
        _timeTickerIds.Add(ticker.Id);
        _timeTickerIds.Add(child.Id);

        if (batch)
            Assert.True((await manager.AddBatchAsync([ticker])).IsSucceeded);
        else
            Assert.True((await manager.AddAsync(ticker)).IsSucceeded);

        var context = Assert.Single(dispatched);
        Assert.Equal(TickerStatus.InProgress, (await node.GetTimeTickerById(ticker.Id, CancellationToken.None)).Status);
        Assert.Equal((await node.GetTimeTickerById(ticker.Id, CancellationToken.None)).UpdatedAt, context.ClaimMarker);
        // A child is not a claimed row of its own: it runs inside its parent, so there is nothing of its own to put back.
        Assert.Null(Assert.Single(context.TimeTickerChildren).ClaimMarker);
    }

    [Fact]
    public async Task DashboardRunNowOfACron_GivesTheJobTheMarkerOfItsClaim()
    {
        var node = CreateProvider("node-A");
        var cronId = await SeedCronTicker();
        var (dispatcher, dispatched) = RecordingDispatcher();
        var repository = new TickerDashboardRepository<TimeTickerEntity, CronTickerEntity>(new TickerExecutionContext(), node,
            Substitute.For<ISharpTickerHostScheduler>(), Substitute.For<ISharpTickerNotificationHubSender>(), new SharpTicker.Dashboard.DashboardOptionsBuilder(), dispatcher);

        await repository.AddOnDemandCronTickerOccurrenceAsync(cronId, CancellationToken.None);

        var context = Assert.Single(dispatched);
        var stored = (await node.GetAllCronTickerOccurrences(x => x.Id == context.TickerId, CancellationToken.None)).Single();
        Assert.Equal(TickerStatus.InProgress, stored.Status);
        Assert.Equal(stored.UpdatedAt, context.ClaimMarker);
    }

    private static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }
}
