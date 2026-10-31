using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;
using SharpTicker.Dashboard.Hubs;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Models;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// What the dashboard sends to the browser over SignalR, to whom, and in what shape. The occurrence updates go to the
/// group named after the cron ticker; everything else goes to every client. The browser reads these payloads by name,
/// so a renamed field or a changed status format breaks the live updates.
/// </summary>
public class DashboardHubSenderTests
{
    private sealed class Harness
    {
        public IClientProxy All { get; } = Substitute.For<IClientProxy>();
        public IClientProxy Group { get; } = Substitute.For<IClientProxy>();
        public string? GroupName { get; private set; }
        public SharpTickerNotificationHubSender Sender { get; }

        public Harness()
        {
            var clients = Substitute.For<IHubClients>();
            clients.All.Returns(All);
            clients.Group(Arg.Any<string>()).Returns(call =>
            {
                GroupName = call.Arg<string>();
                return Group;
            });
            var context = Substitute.For<IHubContext<SharpTickerNotificationHub>>();
            context.Clients.Returns(clients);
            Sender = new SharpTickerNotificationHubSender(context);
        }

        public static object[] ArgumentsOf(IClientProxy proxy, string method)
        {
            var call = Assert.Single(
                proxy.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IClientProxy.SendCoreAsync)),
                c => (string)c.GetArguments()[0]! == method);
            return (object[])call.GetArguments()[1]!;
        }

        public static JsonElement PayloadOf(IClientProxy proxy, string method)
            => JsonSerializer.SerializeToElement(Assert.Single(ArgumentsOf(proxy, method)));

        public static bool Sent(IClientProxy proxy, string method)
            => proxy.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IClientProxy.SendCoreAsync) && (string)c.GetArguments()[0]! == method);
    }

    [Fact]
    public void TheSender_RequiresAHubContext()
    {
        Assert.Throws<ArgumentNullException>(() => new SharpTickerNotificationHubSender(null!));
    }

    #region To every client

    [Fact]
    public async Task AddCronTicker_SendsTheTickerToEveryClient_WithCamelCaseFields()
    {
        var h = new Harness();
        var id = Guid.NewGuid();

        await h.Sender.AddCronTickerNotifyAsync(new CronTickerEntity { Id = id, Function = "Report", Expression = "0 * * * * *" });

        var payload = Harness.PayloadOf(h.All, "AddCronTickerNotification");
        Assert.Equal(id, payload.GetProperty("id").GetGuid());
        Assert.Equal("Report", payload.GetProperty("function").GetString());
        Assert.Equal("0 * * * * *", payload.GetProperty("expression").GetString());
    }

    [Fact]
    public async Task UpdateCronTicker_SendsTheTickerToEveryClient()
    {
        var h = new Harness();

        await h.Sender.UpdateCronTickerNotifyAsync(new CronTickerEntity { Id = Guid.NewGuid(), Function = "Report" });

        Assert.Equal("Report", Harness.PayloadOf(h.All, "UpdateCronTickerNotification").GetProperty("function").GetString());
    }

    [Fact]
    public async Task RemoveCronTicker_SendsTheId()
    {
        var h = new Harness();
        var id = Guid.NewGuid();

        await h.Sender.RemoveCronTickerNotifyAsync(id);

        Assert.Equal(id, Assert.Single(Harness.ArgumentsOf(h.All, "RemoveCronTickerNotification")));
    }

    [Fact]
    public async Task AddTimeTicker_SendsTheId()
    {
        var h = new Harness();
        var id = Guid.NewGuid();

        await h.Sender.AddTimeTickerNotifyAsync(id);

        Assert.Equal(id, Assert.Single(Harness.ArgumentsOf(h.All, "AddTimeTickerNotification")));
    }

    [Fact]
    public async Task AddTimeTickersBatch_SendsNoArguments()
    {
        var h = new Harness();

        await h.Sender.AddTimeTickersBatchNotifyAsync();

        Assert.Empty(Harness.ArgumentsOf(h.All, "AddTimeTickersBatchNotification"));
    }

    [Fact]
    public async Task UpdateTimeTicker_SendsTheTickerToEveryClient()
    {
        var h = new Harness();
        var id = Guid.NewGuid();

        await h.Sender.UpdateTimeTickerNotifyAsync(new TimeTickerEntity { Id = id, Function = "Import", Status = TickerStatus.Done });

        var payload = Harness.PayloadOf(h.All, "UpdateTimeTickerNotification");
        Assert.Equal(id, payload.GetProperty("id").GetGuid());
        Assert.Equal("Import", payload.GetProperty("function").GetString());
    }

    [Fact]
    public async Task RemoveTimeTicker_SendsTheId()
    {
        var h = new Harness();
        var id = Guid.NewGuid();

        await h.Sender.RemoveTimeTickerNotifyAsync(id);

        Assert.Equal(id, Assert.Single(Harness.ArgumentsOf(h.All, "RemoveTimeTickerNotification")));
    }

    [Fact]
    public async Task CanceledTicker_SendsTheId()
    {
        var h = new Harness();
        var id = Guid.NewGuid();

        await h.Sender.CanceledTickerNotifyAsync(id);

        Assert.Equal(id, Assert.Single(Harness.ArgumentsOf(h.All, "CanceledTickerNotification")));
    }

    [Fact]
    public void ActiveThreads_AreSentAsText()
    {
        var h = new Harness();

        h.Sender.UpdateActiveThreads("3/16");

        Assert.Equal("3/16", Harness.PayloadOf(h.All, "GetActiveThreadsNotification").GetString());
    }

    [Fact]
    public void HostStatus_IsSentAsABoolean()
    {
        var h = new Harness();

        h.Sender.UpdateHostStatus(true);
        h.Sender.UpdateHostStatus(false);

        var values = h.All.ReceivedCalls()
            .Where(c => (string)c.GetArguments()[0]! == "GetHostStatusNotification")
            .Select(c => JsonSerializer.SerializeToElement(((object[])c.GetArguments()[1]!).Single()).GetBoolean())
            .ToArray();
        Assert.Equal(new[] { true, false }, values);
    }

    [Fact]
    public void NextOccurrence_IsSentWhenThereIsOne()
    {
        var h = new Harness();
        var at = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        h.Sender.UpdateNextOccurrence(at);

        Assert.Equal(at, Harness.PayloadOf(h.All, "GetNextOccurrenceNotification").GetDateTime().ToUniversalTime());
    }

    [Fact]
    public void NextOccurrence_IsNotSent_WhenThereIsNone()
    {
        var h = new Harness();

        h.Sender.UpdateNextOccurrence(null);

        Assert.False(Harness.Sent(h.All, "GetNextOccurrenceNotification"));
    }

    [Fact]
    public async Task UpdateHostException_SendsTheMessage()
    {
        var h = new Harness();

        h.Sender.UpdateHostException("scheduler stopped");

        await Task.Yield();
        Assert.Equal("scheduler stopped", Harness.PayloadOf(h.All, "UpdateHostExceptionNotification").GetString());
    }

    #endregion

    #region Time ticker updates are collapsed

    [Fact]
    public async Task ManyTimeTickerUpdatesInARow_SendOneNotification()
    {
        var h = new Harness();
        var context = new InternalFunctionContext();

        for (var i = 0; i < 25; i++)
            await h.Sender.UpdateTimeTickerFromInternalFunctionContext<TimeTickerEntity>(context);

        await WaitUntil(() => Harness.Sent(h.All, "UpdateTimeTickerNotification"));
        await Task.Delay(300); // long enough for a second notification to show up if there were one

        var sent = h.All.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IClientProxy.SendCoreAsync)
                                                    && (string)c.GetArguments()[0]! == "UpdateTimeTickerNotification");
        Assert.Equal(1, sent);
        Assert.Empty(Harness.ArgumentsOf(h.All, "UpdateTimeTickerNotification"));
    }

    [Fact]
    public async Task ATimeTickerUpdateAfterThePauseIsSentAgain()
    {
        var h = new Harness();
        var context = new InternalFunctionContext();

        await h.Sender.UpdateTimeTickerFromInternalFunctionContext<TimeTickerEntity>(context);
        await WaitUntil(() => CountOf(h, "UpdateTimeTickerNotification") == 1);
        await h.Sender.UpdateTimeTickerFromInternalFunctionContext<TimeTickerEntity>(context);
        await WaitUntil(() => CountOf(h, "UpdateTimeTickerNotification") == 2);

        Assert.Equal(2, CountOf(h, "UpdateTimeTickerNotification"));
    }

    private static int CountOf(Harness h, string method)
        => h.All.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IClientProxy.SendCoreAsync) && (string)c.GetArguments()[0]! == method);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < until)
            await Task.Delay(20);
        Assert.True(condition(), "the notification was not sent in time");
    }

    #endregion

    #region Occurrence updates go to the cron ticker's group

    [Fact]
    public void AnOccurrenceUpdate_GoesToTheGroupOfItsCronTicker_WithTheFieldsTheBrowserReads()
    {
        var h = new Harness();
        var occurrenceId = Guid.NewGuid();
        var cronId = Guid.NewGuid();
        var executedAt = new DateTime(2030, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        h.Sender.UpdateCronOccurrenceFromInternalFunctionContext<CronTickerEntity>(new InternalFunctionContext
        {
            TickerId = occurrenceId,
            ParentId = cronId,
            Status = TickerStatus.Done,
            ExecutedAt = executedAt,
            ElapsedTime = 4000,
            RetryCount = 2,
            ExceptionDetails = null,
        });

        Assert.Equal(cronId.ToString(), h.GroupName);
        Assert.False(Harness.Sent(h.All, "UpdateCronOccurrenceNotification"));
        var payload = Harness.PayloadOf(h.Group, "UpdateCronOccurrenceNotification");
        Assert.Equal(occurrenceId, payload.GetProperty("id").GetGuid());
        Assert.Equal(cronId, payload.GetProperty("cronTickerId").GetGuid());
        Assert.Equal((int)TickerStatus.Done, payload.GetProperty("status").GetInt32());
        Assert.Equal(executedAt, payload.GetProperty("executedAt").GetDateTime().ToUniversalTime());
        Assert.Equal(4000, payload.GetProperty("elapsedTime").GetInt64());
        Assert.Equal(2, payload.GetProperty("retryCount").GetInt32());
    }

    [Theory]
    [InlineData(TickerStatus.Queued, 1)]
    [InlineData(TickerStatus.InProgress, 2)]
    [InlineData(TickerStatus.Done, 3)]
    [InlineData(TickerStatus.Failed, 5)]
    [InlineData(TickerStatus.Cancelled, 6)]
    public void TheStatusIsSentAsANumber_BecauseTheBrowserTurnsItIntoAName(TickerStatus status, int number)
    {
        var h = new Harness();

        h.Sender.UpdateCronOccurrenceFromInternalFunctionContext<CronTickerEntity>(
            new InternalFunctionContext { TickerId = Guid.NewGuid(), ParentId = Guid.NewGuid(), Status = status });

        Assert.Equal(number, Harness.PayloadOf(h.Group, "UpdateCronOccurrenceNotification").GetProperty("status").GetInt32());
    }

    [Fact]
    public void AFailedOccurrenceCarriesItsErrorMessage()
    {
        var h = new Harness();

        h.Sender.UpdateCronOccurrenceFromInternalFunctionContext<CronTickerEntity>(new InternalFunctionContext
        {
            TickerId = Guid.NewGuid(), ParentId = Guid.NewGuid(), Status = TickerStatus.Failed, ExceptionDetails = "boom",
        });

        Assert.Equal("boom", Harness.PayloadOf(h.Group, "UpdateCronOccurrenceNotification").GetProperty("exceptionMessage").GetString());
    }

    [Fact]
    public void AnOccurrenceWithoutACronTicker_GoesToTheEmptyGroup_InsteadOfFailing()
    {
        var h = new Harness();

        h.Sender.UpdateCronOccurrenceFromInternalFunctionContext<CronTickerEntity>(
            new InternalFunctionContext { TickerId = Guid.NewGuid(), ParentId = null });

        Assert.Equal(string.Empty, h.GroupName);
    }

    [Fact]
    public async Task UpdateCronOccurrence_SendsTheOccurrenceToTheGivenGroup()
    {
        var h = new Harness();
        var cronId = Guid.NewGuid();
        var occurrence = new CronTickerOccurrenceEntity<CronTickerEntity>
        {
            Id = Guid.NewGuid(), CronTickerId = cronId, Status = TickerStatus.InProgress,
        };

        await h.Sender.UpdateCronOccurrenceAsync(cronId, occurrence);

        Assert.Equal(cronId.ToString(), h.GroupName);
        var payload = Harness.PayloadOf(h.Group, "UpdateCronOccurrenceNotification");
        Assert.Equal(occurrence.Id, payload.GetProperty("id").GetGuid());
        Assert.Equal(cronId, payload.GetProperty("cronTickerId").GetGuid());
    }

    [Fact]
    public async Task AddCronOccurrence_SendsTheOccurrenceToTheGivenGroup()
    {
        var h = new Harness();
        var cronId = Guid.NewGuid();
        var occurrence = new CronTickerOccurrenceEntity<CronTickerEntity>
        {
            Id = Guid.NewGuid(), CronTickerId = cronId, Status = TickerStatus.Queued,
        };

        await h.Sender.AddCronOccurrenceAsync(cronId, occurrence);

        Assert.Equal(cronId.ToString(), h.GroupName);
        Assert.Equal(occurrence.Id, Harness.PayloadOf(h.Group, "AddCronOccurrenceNotification").GetProperty("id").GetGuid());
        Assert.False(Harness.Sent(h.All, "AddCronOccurrenceNotification"));
    }

    [Fact]
    public async Task AnOccurrenceOfAnotherShapeIsSentWithCamelCaseNames()
    {
        var h = new Harness();
        var cronId = Guid.NewGuid();

        await h.Sender.UpdateCronOccurrenceAsync(cronId, new { Id = 7, SomeValue = "x" });

        var payload = Harness.PayloadOf(h.Group, "UpdateCronOccurrenceNotification");
        Assert.Equal(7, payload.GetProperty("id").GetInt32());
        Assert.Equal("x", payload.GetProperty("someValue").GetString());
    }

    #endregion
}
