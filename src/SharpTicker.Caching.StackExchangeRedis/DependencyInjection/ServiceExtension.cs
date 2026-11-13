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
            // The same condition: with another job store this heartbeat does not fence the node.
            services.AddSingleton(sp => new NodeSelfFence(
                Enabled: options.StopJobsWhenHeartbeatIsLost && sp.GetRequiredService<EarlierRunRelease>().StoreIsRedis,
                Fence: sp.GetService<NodeFence>(),
                Owner: sp.GetService<ITickerExecutionTaskHandler>()));
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
        /// With Redis as the job store: whether this node asks the jobs it is running to stop, and starts no job, once it
        /// could not write its heartbeat for one heartbeat TTL (<see cref="NodeHeartbeatInterval"/> plus 20 seconds). The
        /// other nodes may release the jobs of a node whose heartbeat stayed missing for one more TTL and start them again;
        /// a job that stops within about one TTL of being asked does not overlap that second run. A job stopped this way
        /// is not recorded as cancelled and the application's exception handler is not called for it: its row is put back
        /// and the job runs again from the start, so it has to be safe to run twice. Jobs start again with the next
        /// heartbeat that is written.
        /// <para>
        /// Not asked to stop: a job that ignores its cancellation token, and a chain child that started together with its
        /// parent (<c>RunCondition.InProgress</c>).
        /// </para>
        /// <para>
        /// The default is true. With it, a Redis outage longer than one TTL stops the running jobs of every node that sees
        /// it, and they run again afterwards. Set it to false to let the jobs run on, as versions before 1.5.0 did: such an
        /// outage then stops no job, and a node that alone cannot reach Redis may have its running jobs started a second
        /// time while they still run. It has no effect with another job store.
        /// </para>
        /// </summary>
        public bool StopJobsWhenHeartbeatIsLost { get; set; } = true;

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
