using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using SharpTicker.Dashboard.Infrastructure.Dashboard;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Models;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// The dashboard's "Run now" on a cron stores an occurrence, claims it and runs it from the occurrence's cron. A claimed
/// occurrence that comes back without its cron must not throw while its row is InProgress: nothing would run it or put it back.
/// </summary>
public class DashboardRunNowCronTests
{
    private static readonly DateTime Marker = new(2025, 6, 15, 12, 0, 0, 1, DateTimeKind.Utc);

    private static (ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> Provider, CronTickerOccurrenceEntity<CronTickerEntity> Claimed)
        ProviderReturningAClaimWithoutItsCron(Guid cronId, CronTickerEntity? storedCron)
    {
        var provider = Substitute.For<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();
        var claimed = new CronTickerOccurrenceEntity<CronTickerEntity>
        {
            CronTickerId = cronId,
            Status = TickerStatus.InProgress,
            LockHolder = "node-A",
            UpdatedAt = Marker,
            CronTicker = null!
        };
        provider.InsertCronTickerOccurrences(Arg.Any<CronTickerOccurrenceEntity<CronTickerEntity>[]>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                claimed.Id = Assert.Single(call.Arg<CronTickerOccurrenceEntity<CronTickerEntity>[]>()).Id;
                return 1;
            });
        provider.AcquireImmediateCronOccurrencesAsync(Arg.Any<Guid[]>(), Arg.Any<CancellationToken>())
            .Returns(_ => new[] { claimed });
        provider.GetCronTickerById(cronId, Arg.Any<CancellationToken>()).Returns(storedCron!);
        provider.RemoveCronTickerOccurrences(Arg.Any<Guid[]>(), Arg.Any<CancellationToken>()).Returns(1);
        return (provider, claimed);
    }

    private static (ISharpTickerDispatcher Dispatcher, List<InternalFunctionContext> Dispatched) RecordingDispatcher()
    {
        var dispatched = new List<InternalFunctionContext>();
        var dispatcher = Substitute.For<ISharpTickerDispatcher>();
        dispatcher.IsEnabled.Returns(true);
        dispatcher.DispatchAsync(Arg.Any<InternalFunctionContext[]>(), Arg.Any<CancellationToken>())
            .Returns(call => { dispatched.AddRange(call.Arg<InternalFunctionContext[]>()); return Task.CompletedTask; });
        return (dispatcher, dispatched);
    }

    private static TickerDashboardRepository<TimeTickerEntity, CronTickerEntity> Repository(
        ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> provider, ISharpTickerDispatcher dispatcher)
        => new(new TickerExecutionContext(), provider, Substitute.For<ISharpTickerHostScheduler>(),
            Substitute.For<ISharpTickerNotificationHubSender>(), new SharpTicker.Dashboard.DashboardOptionsBuilder(), dispatcher);

    // A provider (a custom one, or a wrapper) that does not fill CronTicker: the cron is still stored, so the job runs.
    [Fact]
    public async Task TheClaimComesBackWithoutItsCron_TheCronIsRead_AndTheJobRuns()
    {
        var cron = new CronTickerEntity { Id = Guid.NewGuid(), Function = "RunNow.Fn", Retries = 2, RetryIntervals = [7, 9] };
        var (provider, claimed) = ProviderReturningAClaimWithoutItsCron(cron.Id, cron);
        var (dispatcher, dispatched) = RecordingDispatcher();

        await Repository(provider, dispatcher).AddOnDemandCronTickerOccurrenceAsync(cron.Id, CancellationToken.None);

        var context = Assert.Single(dispatched);
        Assert.Equal(claimed.Id, context.TickerId);
        Assert.Equal("RunNow.Fn", context.FunctionName);
        Assert.Equal(2, context.Retries);
        Assert.Equal([7, 9], context.RetryIntervals);
        Assert.Equal(Marker, context.ClaimMarker);
        await provider.DidNotReceiveWithAnyArgs().RemoveCronTickerOccurrences(default!, default);
    }

    // The cron was deleted after the endpoint checked it and before the occurrence was claimed. The occurrence this call
    // stored points at nothing: putting it back would leave it to a fallback that has no function to run, so it is removed.
    [Fact]
    public async Task TheCronIsGoneWhenTheClaimComesBack_TheOccurrenceIsRemoved_AndNothingRuns()
    {
        var cronId = Guid.NewGuid();
        var (provider, claimed) = ProviderReturningAClaimWithoutItsCron(cronId, storedCron: null);
        var (dispatcher, dispatched) = RecordingDispatcher();

        await Repository(provider, dispatcher).AddOnDemandCronTickerOccurrenceAsync(cronId, CancellationToken.None);

        Assert.Empty(dispatched);
        await provider.Received(1).RemoveCronTickerOccurrences(
            Arg.Is<Guid[]>(ids => ids.Length == 1 && ids[0] == claimed.Id), Arg.Any<CancellationToken>());
    }

    // The claim of the stored occurrence failed, and so did the put-back of the row: it is InProgress for this node with
    // nothing to run it. It goes to the retry list, so the scheduler loop puts it back once the store answers; the request
    // still fails, as before.
    [Fact]
    public async Task TheClaimFailsAndItsPutBackToo_TheRowGoesToTheRetryList_AndTheRequestFails()
    {
        var cronId = Guid.NewGuid();
        var (provider, _) = ProviderReturningAClaimWithoutItsCron(cronId, storedCron: null);
        var row = new NotPutBackClaim(Guid.NewGuid(), Marker, TickerType.CronTickerOccurrence);
        provider.AcquireImmediateCronOccurrencesAsync(Arg.Any<Guid[]>(), Arg.Any<CancellationToken>())
            .Returns<CronTickerOccurrenceEntity<CronTickerEntity>[]>(_ => throw new ClaimPutBackFailedException(1,
                [new InvalidOperationException("store failed"), new TimeoutException("release failed")], [row]));
        var (dispatcher, dispatched) = RecordingDispatcher();
        var retry = new ClaimPutBackRetryList();
        var repository = new TickerDashboardRepository<TimeTickerEntity, CronTickerEntity>(new TickerExecutionContext(), provider,
            Substitute.For<ISharpTickerHostScheduler>(), Substitute.For<ISharpTickerNotificationHubSender>(),
            new SharpTicker.Dashboard.DashboardOptionsBuilder(), dispatcher, retry);

        await Assert.ThrowsAsync<ClaimPutBackFailedException>(() => repository.AddOnDemandCronTickerOccurrenceAsync(cronId, CancellationToken.None));

        Assert.Empty(dispatched);
        Assert.Equal([row], retry.TakeAll());
    }
}
