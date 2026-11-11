using System;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using SharpTicker.Caching.StackExchangeRedis.Infrastructure;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Interfaces;

namespace SharpTicker.Caching.StackExchangeRedis.DependencyInjection;

public static class ServiceExtension
{
    public static TickerOptionsBuilder<TTimeTicker, TCronTicker> AddStackExchangeRedis<TTimeTicker, TCronTicker>(
        this TickerOptionsBuilder<TTimeTicker, TCronTicker> tickerConfiguration, Action<SharpTickerRedisOptionBuilder> setupAction)
        where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
        where TCronTicker : CronTickerEntity, new()
    {
        tickerConfiguration.ExternalProviderConfigServiceAction += services =>
        {
            var options = new SharpTickerRedisOptionBuilder
            {
                InstanceName = "sharpticker:"
            };
            
            setupAction?.Invoke(options);
            if (string.IsNullOrWhiteSpace(options.Configuration)
                && options.ConfigurationOptions == null
                && options.ConnectionMultiplexer == null
                && options.ConnectionMultiplexerFactory == null)
                throw new InvalidOperationException("Redis configuration or connection must be provided when enabling StackExchange.Redis persistence.");

            services.AddKeyedSingleton<IConnectionMultiplexer>("sharpticker", (_, _) =>
                options.ConnectionMultiplexer
                ?? (options.ConnectionMultiplexerFactory != null
                    ? options.ConnectionMultiplexerFactory().GetAwaiter().GetResult()
                    : (options.ConfigurationOptions != null
                        ? ConnectionMultiplexer.Connect(options.ConfigurationOptions)
                        : ConnectionMultiplexer.Connect(options.Configuration))));
            services.AddKeyedSingleton<IDatabase>("sharpticker", (sp, _) =>
                sp.GetRequiredKeyedService<IConnectionMultiplexer>("sharpticker").GetDatabase());
            services.AddHostedService<NodeHeartBeatBackgroundService>();
            // Redis is the store only when no other store was registered (TryAdd below); the startup release of an earlier
            // run's jobs belongs to the store that holds them.
            services.AddSingleton(sp => new EarlierRunRelease(
                StoreIsRedis: sp.GetService<ITickerPersistenceProvider<TTimeTicker, TCronTicker>>() is TickerRedisPersistenceProvider<TTimeTicker, TCronTicker>,
                Node: sp.GetRequiredService<SchedulerOptionsBuilder>().NodeIdentifier,
                RunStartedAt: () => sp.GetService<TickerExecutionContext>()?.RunStartedAt,
                ApplicationStarted: sp.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted));
            services.AddSingleton<SharpTickerRedisContext>();
            services.AddSingleton<ISharpTickerRedisContext>(sp => sp.GetRequiredService<SharpTickerRedisContext>());
            services.AddSingleton<INodeHeartbeatRegistry>(sp => sp.GetRequiredService<SharpTickerRedisContext>());
            services.AddKeyedSingleton<IDistributedCache>("sharpticker", (sp, _) =>
                new RedisCache(new RedisCacheOptions
                {
                    InstanceName = options.InstanceName,
                    ConnectionMultiplexerFactory = () => Task.FromResult(
                        sp.GetRequiredKeyedService<IConnectionMultiplexer>("sharpticker")),
                }));
            services.AddSingleton(_ => options);
            services.TryAddSingleton<ITickerPersistenceProvider<TTimeTicker, TCronTicker>, TickerRedisPersistenceProvider<TTimeTicker, TCronTicker>>();
        };

        return tickerConfiguration;
    }

    public class SharpTickerRedisOptionBuilder : RedisCacheOptions
    {
        public TimeSpan NodeHeartbeatInterval { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// An existing <see cref="IConnectionMultiplexer"/> to use for all Redis operations.
        /// When set, <see cref="RedisCacheOptions.Configuration"/> and
        /// <see cref="RedisCacheOptions.ConfigurationOptions"/> are ignored.
        /// The caller is responsible for the lifetime and disposal of the multiplexer.
        /// </summary>
        public IConnectionMultiplexer ConnectionMultiplexer { get; set; }

        /// <summary>
        /// Optional source-generated JsonSerializerContext for AOT compatibility.
        /// When provided, enables trimming-safe JSON serialization for ticker entities.
        /// The context should include [JsonSerializable] attributes for your TTimeTicker,
        /// TCronTicker, and CronTickerOccurrenceEntity&lt;TCronTicker&gt; types.
        /// </summary>
        public JsonSerializerContext JsonSerializerContext { get; set; }
    }
}
