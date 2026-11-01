using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpTicker.Dashboard.Infrastructure;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Dashboard.Hubs
{
    internal class SharpTickerNotificationHubSender : ISharpTickerNotificationHubSender
    {
        private readonly IHubContext<SharpTickerNotificationHub> _hubContext;
        private readonly Timer _timeTickerUpdateTimer;
        private int _hasPendingTimeTickerUpdate;
        private static readonly TimeSpan TimeTickerUpdateDebounce = TimeSpan.FromMilliseconds(100);
        private static readonly JsonSerializerOptions CamelCaseOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private readonly IServiceProvider? _services;

        public SharpTickerNotificationHubSender(IHubContext<SharpTickerNotificationHub> hubContext, IServiceProvider? services = null)
        {
            _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
            _services = services;
            _timeTickerUpdateTimer = new Timer(TimeTickerUpdateCallback, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        public async Task AddCronTickerNotifyAsync(object cronTicker)
        {
            var entity = (CronTickerEntity)cronTicker;
            var json = JsonSerializer.SerializeToElement(entity, DashboardJsonSerializerContext.Default.CronTickerEntity);
            await SendToThoseWhoMaySeeAsync("AddCronTickerNotification", json, (rule, user) => rule.CronTickers(user), entity);
        }

        public async Task UpdateCronTickerNotifyAsync(object cronTicker)
        {
            var entity = (CronTickerEntity)cronTicker;
            var json = JsonSerializer.SerializeToElement(entity, DashboardJsonSerializerContext.Default.CronTickerEntity);
            await SendToThoseWhoMaySeeAsync("UpdateCronTickerNotification", json, (rule, user) => rule.CronTickers(user), entity);
        }

        public async Task RemoveCronTickerNotifyAsync(Guid id)
        {
            await _hubContext.Clients.All.SendAsync("RemoveCronTickerNotification", id);
        }

        public async Task AddTimeTickerNotifyAsync(Guid id)
        {
            await _hubContext.Clients.All.SendAsync("AddTimeTickerNotification", id);
        }
        
        public async Task AddTimeTickersBatchNotifyAsync()
        {
            await _hubContext.Clients.All.SendAsync("AddTimeTickersBatchNotification");
        }

        public async Task UpdateTimeTickerNotifyAsync(object timeTicker)
        {
            var entity = (TimeTickerEntity)timeTicker;
            var json = JsonSerializer.SerializeToElement(entity, DashboardJsonSerializerContext.Default.TimeTickerEntity);
            if (_services?.GetService<HubConnections>() != null)
                json = WithoutChildren(json); // the children can be tickers the receiver may not see
            await SendToThoseWhoMaySeeAsync("UpdateTimeTickerNotification", json, (rule, user) => rule.TimeTickers(user), entity);
        }

        public async Task RemoveTimeTickerNotifyAsync(Guid id)
        {
            await _hubContext.Clients.All.SendAsync("RemoveTimeTickerNotification", id);
        }

        /// <summary>
        /// Sends a notification that carries a ticker to every connection, or, when a visibility rule is registered, only
        /// to the connections whose user may see that ticker. The rule is evaluated on the ticker in memory, so a
        /// predicate that cannot be evaluated for it (it throws) counts as not visible.
        /// </summary>
        private async Task SendToThoseWhoMaySeeAsync<TEntity>(string method, JsonElement payload,
            Func<ITickerVisibilityRule, ClaimsPrincipal, Expression<Func<TEntity, bool>>?> predicateOf, TEntity ticker)
        {
            var connections = _services?.GetService<HubConnections>();
            if (connections == null)
            {
                await _hubContext.Clients.All.SendAsync(method, payload);
                return;
            }

            using var scope = _services!.CreateScope();
            var rule = scope.ServiceProvider.GetService<ITickerVisibilityRule>();
            var receivers = new List<string>();
            foreach (var connection in connections.Snapshot())
            {
                if (rule == null || MaySee(rule, connection.Value, predicateOf, ticker, method))
                    receivers.Add(connection.Key);
            }

            if (receivers.Count > 0)
                await _hubContext.Clients.Clients(receivers).SendAsync(method, payload);
        }

        private static JsonElement WithoutChildren(JsonElement ticker)
        {
            if (ticker.ValueKind != JsonValueKind.Object)
                return ticker;

            var node = System.Text.Json.Nodes.JsonNode.Parse(ticker.GetRawText())!.AsObject();
            foreach (var key in node.Select(p => p.Key).Where(k => string.Equals(k, "children", StringComparison.OrdinalIgnoreCase)).ToList())
                node.Remove(key);

            return JsonSerializer.SerializeToElement(node);
        }

        private bool MaySee<TEntity>(ITickerVisibilityRule rule, ClaimsPrincipal user,
            Func<ITickerVisibilityRule, ClaimsPrincipal, Expression<Func<TEntity, bool>>?> predicateOf, TEntity ticker, string method)
        {
            try
            {
                var predicate = predicateOf(rule, user);
                return predicate == null || predicate.Compile()(ticker);
            }
            catch (Exception exception)
            {
                _services?.GetService<ILoggerFactory>()?.CreateLogger<SharpTickerNotificationHubSender>()
                    .LogWarning(exception, "The visibility rule could not be evaluated for the notification {Method}; it was not sent to that user", method);
                return false;
            }
        }

        public void UpdateActiveThreads(string activeThreads)
        {
            var json = JsonSerializer.SerializeToElement(activeThreads, DashboardJsonSerializerContext.Default.String);
            _ = _hubContext.Clients.All.SendAsync("GetActiveThreadsNotification", json);
        }

        public void UpdateNextOccurrence(DateTime? nextOccurrence)
        {
            if (nextOccurrence != null)
            {
                var json = JsonSerializer.SerializeToElement(nextOccurrence, DashboardJsonSerializerContext.Default.NullableDateTime);
                _ = _hubContext.Clients.All.SendAsync("GetNextOccurrenceNotification", json);
            }
        }

        public void UpdateHostStatus(bool active)
        {
            var json = JsonSerializer.SerializeToElement(active, DashboardJsonSerializerContext.Default.Boolean);
            _ = _hubContext.Clients.All.SendAsync("GetHostStatusNotification", json);
        }

        public void UpdateHostException(string exceptionMessage)
        {
            var json = JsonSerializer.SerializeToElement(exceptionMessage, DashboardJsonSerializerContext.Default.String);
            _ = _hubContext.Clients.All.SendAsync("UpdateHostExceptionNotification", json);
        }

        public async Task UpdateNodeHeartBeatAsync(JsonElement nodeHeartBeat)
        {
            await _hubContext.Clients.All.SendAsync("UpdateNodeHeartBeat", nodeHeartBeat);
        }

        public async Task AddCronOccurrenceAsync(Guid groupId, object occurrence)
        {
            var json = SerializeOccurrence(occurrence);
            await _hubContext.Clients.Group(groupId.ToString()).SendAsync("AddCronOccurrenceNotification", json);
        }

        public async Task UpdateCronOccurrenceAsync(Guid groupId, object occurrence)
        {
            var json = SerializeOccurrence(occurrence);
            await _hubContext.Clients.Group(groupId.ToString()).SendAsync("UpdateCronOccurrenceNotification", json);
        }

        private static JsonElement SerializeOccurrence(object occurrence) 
        {
            if (occurrence is CronTickerOccurrenceEntity<CronTickerEntity> typed) 
                return JsonSerializer.SerializeToElement(typed, DashboardJsonSerializerContext.Default.CronTickerOccurrenceEntityCronTickerEntity); 
            return JsonSerializer.SerializeToElement(occurrence, CamelCaseOptions);
        }

        public Task UpdateTimeTickerFromInternalFunctionContext<TTimeTicker>(InternalFunctionContext internalFunctionContext)
            where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
        {
            // Debounce high-frequency updates into a single notification
            if (Interlocked.Exchange(ref _hasPendingTimeTickerUpdate, 1) == 0)
            {
                _timeTickerUpdateTimer.Change(TimeTickerUpdateDebounce, Timeout.InfiniteTimeSpan);
            }

            return Task.CompletedTask;
        }

        private void TimeTickerUpdateCallback(object _)
        {
            if (Interlocked.Exchange(ref _hasPendingTimeTickerUpdate, 0) == 0)
                return;

            _ = _hubContext.Clients.All.SendAsync("UpdateTimeTickerNotification");
        }

        public Task UpdateCronOccurrenceFromInternalFunctionContext<TCronTicker>(InternalFunctionContext internalFunctionContext)
            where TCronTicker : CronTickerEntity, new()
        {
            var updatePayload = new CronOccurrenceUpdateNotification
            {
                Id = internalFunctionContext.TickerId,
                Status = internalFunctionContext.Status,
                CronTickerId = internalFunctionContext.ParentId,
                ExecutedAt = internalFunctionContext.ExecutedAt,
                ElapsedTime = internalFunctionContext.ElapsedTime,
                RetryCount = internalFunctionContext.RetryCount,
                ExceptionMessage = internalFunctionContext.ExceptionDetails
            };

            var json = JsonSerializer.SerializeToElement(updatePayload, DashboardJsonSerializerContext.Default.CronOccurrenceUpdateNotification);
            _ = _hubContext.Clients
                .Group(internalFunctionContext.ParentId?.ToString() ?? string.Empty)
                .SendAsync("UpdateCronOccurrenceNotification", json);

            return Task.CompletedTask;
        }

        public async Task CanceledTickerNotifyAsync(Guid id)
        {
            await _hubContext.Clients.All.SendAsync("CanceledTickerNotification", id);
        }
    }
}
