using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Utilities.Temps
{
    internal class NoOpSharpTickerNotificationHubSender : ISharpTickerNotificationHubSender
    {
        public Task AddCronTickerNotifyAsync(object cronTicker)
        {
            return Task.CompletedTask;
        }

        public Task UpdateCronTickerNotifyAsync(object cronTicker)
        {
            return Task.CompletedTask;
        }

        public Task RemoveCronTickerNotifyAsync(Guid id)
        {
            return Task.CompletedTask;
        }

        public Task AddTimeTickerNotifyAsync(Guid id)
        {
            return Task.CompletedTask;
        }
        
        public Task AddTimeTickersBatchNotifyAsync()
        {
            return Task.CompletedTask;
        }

        public Task UpdateTimeTickerNotifyAsync(object timeTicker)
        {
            return Task.CompletedTask;
        }

        public Task RemoveTimeTickerNotifyAsync(Guid id)
        {
            return Task.CompletedTask;
        }

        public void UpdateActiveThreads(string activeThreads)
        {
        }

        public void UpdateNextOccurrence(DateTime? nextOccurrence)
        {
        }

        public void UpdateHostStatus(bool active)
        {
        }

        public void UpdateHostException(string exceptionMessage)
        {
        }

        public Task UpdateNodeHeartBeatAsync(JsonElement nodeHeartBeat)
        {
            return Task.CompletedTask;
        }

        public Task AddCronOccurrenceAsync(Guid groupId, object occurrence)
        { 
            return Task.CompletedTask;
        }

        public Task UpdateCronOccurrenceAsync(Guid groupId, object occurrence)
        {
            return Task.CompletedTask;
        }

        public Task UpdateTimeTickerFromInternalFunctionContext<TTimeTickerEntity>(InternalFunctionContext internalFunctionContext) where TTimeTickerEntity : TimeTickerEntity<TTimeTickerEntity>, new()
        {
            return Task.CompletedTask;
        }

        public Task UpdateCronOccurrenceFromInternalFunctionContext<TCronTickerEntity>(
            InternalFunctionContext internalFunctionContext) where TCronTickerEntity : CronTickerEntity, new()
        {
            return Task.CompletedTask;
        }

        public Task CanceledTickerNotifyAsync(Guid id)
        {
            return Task.CompletedTask;
        }
    }
}
