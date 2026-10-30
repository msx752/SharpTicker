using System;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;

namespace SharpTicker.MongoDB.DependencyInjection
{
    public static class ServiceExtension
    {
        /// <summary>
        /// Registers the MongoDB persistence provider for SharpTicker. Call this from
        /// inside <c>AddSharpTicker(options =&gt; ...)</c>.
        /// </summary>
        public static TickerOptionsBuilder<TimeTickerEntity, CronTickerEntity> AddOperationalStore(
            this TickerOptionsBuilder<TimeTickerEntity, CronTickerEntity> tickerConfiguration,
            Action<SharpTickerMongoOptionBuilder<TimeTickerEntity, CronTickerEntity>> mongoConfiguration = null)
            => AddOperationalStore<TimeTickerEntity, CronTickerEntity>(tickerConfiguration, mongoConfiguration);

        public static TickerOptionsBuilder<TTimeTicker, TCronTicker> AddOperationalStore<TTimeTicker, TCronTicker>(
            this TickerOptionsBuilder<TTimeTicker, TCronTicker> tickerConfiguration,
            Action<SharpTickerMongoOptionBuilder<TTimeTicker, TCronTicker>> mongoConfiguration = null)
            where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
            where TCronTicker : CronTickerEntity, new()
        {
            var optionBuilder = new SharpTickerMongoOptionBuilder<TTimeTicker, TCronTicker>();
            mongoConfiguration?.Invoke(optionBuilder);

            if (optionBuilder.ConfigureServices is null)
                throw new InvalidOperationException(
                    "SharpTicker.MongoDB: you must call UseSharpTickerMongoClient(...) or UseExistingMongoClient(...) on the option builder.");

            tickerConfiguration.ExternalProviderConfigServiceAction += optionBuilder.ConfigureServices;
            return tickerConfiguration;
        }
    }
}
