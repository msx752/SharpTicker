using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using SharpTicker.Dashboard;
using SharpTicker.Dashboard.Infrastructure.Dashboard;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Models;
using Xunit;

namespace SharpTicker.Tests.Shared;

/// <summary>
/// The dashboard status counts and jobs per machine on one store, compared with what that store gives when its rows are
/// read and counted, the way the counts were made up to 1.5.2: GetTimeTickers (top-level rows only) and
/// GetAllCronTickerOccurrences, with and without a visibility rule. Compiled into the test project of each store.
/// </summary>
internal static class DashboardStatusCountCheck
{
    private static readonly Expression<Func<TimeTickerEntity, bool>> BillingTimes = t => t.Function.StartsWith("billing.");
    private static readonly Expression<Func<CronTickerEntity, bool>> BillingCrons = c => c.Function.StartsWith("billing.");

    public static async Task SeedAndCheckAsync(ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> store, DateTime now)
    {
        var ct = CancellationToken.None;
        var today = now.Date;

        TimeTickerEntity Time(string function, TickerStatus status, Guid? parentId = null, string? lockHolder = null) => new()
        {
            Id = Guid.NewGuid(), Function = function, Status = status, ExecutionTime = today.AddHours(12), ParentId = parentId,
            LockHolder = lockHolder, LockedAt = lockHolder == null ? null : today, Request = Array.Empty<byte>(),
            CreatedAt = today, UpdatedAt = today,
        };

        var invoices = Time("billing.Invoices", TickerStatus.Done);
        var reminders = Time("billing.Reminders", TickerStatus.Failed);
        var locked = Time("billing.Locked", TickerStatus.Done, lockHolder: "node-A");
        var cleanup = Time("ops.Cleanup", TickerStatus.Done);
        var queued = Time("ops.Queued", TickerStatus.Queued);
        await store.AddTimeTickers(new[] { invoices, reminders, locked, cleanup, queued }, ct);
        // Children are not counted on their own
        await store.AddTimeTickers(new[]
        {
            Time("billing.Child", TickerStatus.Idle, invoices.Id),
            Time("billing.Child", TickerStatus.Failed, locked.Id, lockHolder: "node-C"),
            Time("ops.Child", TickerStatus.Done, cleanup.Id),
        }, ct);

        var billingCron = new CronTickerEntity { Id = Guid.NewGuid(), Function = "billing.Nightly", Expression = "0 0 2 * * *", IsEnabled = true, Request = Array.Empty<byte>() };
        var opsCron = new CronTickerEntity { Id = Guid.NewGuid(), Function = "ops.Nightly", Expression = "0 0 3 * * *", IsEnabled = true, Request = Array.Empty<byte>() };
        await store.InsertCronTickers(new[] { billingCron, opsCron }, ct);

        var hour = 0;
        CronTickerOccurrenceEntity<CronTickerEntity> Occurrence(Guid cron, TickerStatus status, string? lockHolder = null) => new()
        {
            Id = Guid.NewGuid(), CronTickerId = cron, Status = status, ExecutionTime = today.AddHours(++hour),
            LockHolder = lockHolder, LockedAt = lockHolder == null ? null : today, CreatedAt = today, UpdatedAt = today,
        };

        await store.InsertCronTickerOccurrences(new[]
        {
            Occurrence(billingCron.Id, TickerStatus.Done),
            Occurrence(billingCron.Id, TickerStatus.Failed),
            Occurrence(billingCron.Id, TickerStatus.Done, lockHolder: "node-B"),
            Occurrence(opsCron.Id, TickerStatus.Done),
            Occurrence(opsCron.Id, TickerStatus.Queued, lockHolder: "node-D"),
        }, ct);

        var repository = new TickerDashboardRepository<TimeTickerEntity, CronTickerEntity>(new TickerExecutionContext(), store,
            Substitute.For<ISharpTickerHostScheduler>(), Substitute.For<ISharpTickerNotificationHubSender>(), new DashboardOptionsBuilder(),
            Substitute.For<ISharpTickerDispatcher>());
        var billing = new TickerVisibility<TimeTickerEntity, CronTickerEntity>(BillingTimes, BillingCrons);

        foreach (var visibility in new[] { null, billing })
        {
            var times = (await store.GetTimeTickers(visibility?.TimeTickers, ct)).Select(t => (t.Status, t.LockedAt, t.LockHolder)).ToArray();
            var visibleCrons = (await store.GetCronTickers(visibility?.CronTickers!, ct)).Select(c => c.Id).ToList();
            var occurrences = (await store.GetAllCronTickerOccurrences(o => visibleCrons.Contains(o.CronTickerId), ct))
                .Select(o => (o.Status, o.LockedAt, o.LockHolder)).ToArray();

            Assert.Equal(EveryStatus(times.Select(t => t.Status)), await repository.GetTimeTickerFullDataAsync(ct, visibility));
            Assert.Equal(EveryStatus(occurrences.Select(o => o.Status)), await repository.GetCronTickerFullDataAsync(ct, visibility));
            Assert.Equal(
                times.Concat(occurrences).GroupBy(x => x.Status).ToDictionary(g => g.Key, g => g.Count()),
                (await repository.GetOverallJobStatusesAsync(ct, visibility)).ToDictionary(x => x.Item1, x => x.Item2));
            Assert.Equal(
                times.Concat(occurrences).Where(x => x.LockedAt != null).GroupBy(x => x.LockHolder ?? "unknown").ToDictionary(g => g.Key, g => g.Count()),
                (await repository.GetMachineJobsAsync(ct, visibility)).ToDictionary(x => x.Item1, x => x.Item2));
        }

        // The numbers themselves, so the comparison above is not between two empty answers
        Assert.Equal(new Dictionary<TickerStatus, int> { [TickerStatus.Done] = 4, [TickerStatus.Failed] = 2 },
            (await repository.GetOverallJobStatusesAsync(ct, billing)).ToDictionary(x => x.Item1, x => x.Item2));
        Assert.Equal(new Dictionary<TickerStatus, int> { [TickerStatus.Done] = 6, [TickerStatus.Failed] = 2, [TickerStatus.Queued] = 2 },
            (await repository.GetOverallJobStatusesAsync(ct)).ToDictionary(x => x.Item1, x => x.Item2));
        Assert.Equal(new Dictionary<string, int> { ["node-A"] = 1, ["node-B"] = 1 },
            (await repository.GetMachineJobsAsync(ct, billing)).ToDictionary(x => x.Item1, x => x.Item2));
        Assert.Equal(new Dictionary<string, int> { ["node-A"] = 1, ["node-B"] = 1, ["node-D"] = 1 },
            (await repository.GetMachineJobsAsync(ct)).ToDictionary(x => x.Item1, x => x.Item2));
    }

    private static List<Tuple<TickerStatus, int>> EveryStatus(IEnumerable<TickerStatus> statuses)
    {
        var counts = statuses.GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        return Enum.GetValues<TickerStatus>().Select(s => Tuple.Create(s, counts.GetValueOrDefault(s, 0))).ToList();
    }
}
