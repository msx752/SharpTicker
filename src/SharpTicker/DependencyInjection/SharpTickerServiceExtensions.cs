using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpTicker.BackgroundServices;
using SharpTicker.Dispatcher;
using SharpTicker.Provider;
using SharpTicker.SharpTickerThreadPool;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Infrastructure;
using SharpTicker.Utilities.Instrumentation;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Managers;
using SharpTicker.Utilities.Temps;

namespace SharpTicker.DependencyInjection
{
    public static class SharpTickerServiceExtensions
    {
        // The logger the task scheduler writes to, or none when the host has no logging.
        private static ILogger SchedulerLogger(IServiceProvider serviceProvider)
            => serviceProvider.GetService<ILoggerFactory>()?.CreateLogger("SharpTicker.TaskScheduler");

        public static IServiceCollection AddSharpTicker(this IServiceCollection services, Action<TickerOptionsBuilder<TimeTickerEntity, CronTickerEntity>> optionsBuilder = null)
            => AddSharpTicker<TimeTickerEntity, CronTickerEntity>(services, optionsBuilder);

        public static IServiceCollection AddSharpTicker<TTimeTicker, TCronTicker>(this IServiceCollection services, Action<TickerOptionsBuilder<TTimeTicker, TCronTicker>> optionsBuilder = null)
            where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
            where TCronTicker : CronTickerEntity, new()
        {
            var tickerExecutionContext = new TickerExecutionContext();
            var schedulerOptionsBuilder = new SchedulerOptionsBuilder();
            var optionInstance = new TickerOptionsBuilder<TTimeTicker,TCronTicker>(tickerExecutionContext, schedulerOptionsBuilder);
            optionsBuilder?.Invoke(optionInstance);
            CronScheduleCache.TimeZoneInfo = schedulerOptionsBuilder.SchedulerTimeZone;

            // Apply JSON serializer options for ticker requests if configured during service registration
            if (optionInstance.RequestJsonSerializerOptions != null)
            {
                TickerHelper.RequestJsonSerializerOptions = optionInstance.RequestJsonSerializerOptions;
            }

            // Configure whether ticker request payloads should use GZip compression
            TickerHelper.UseGZipCompression = optionInstance.RequestGZipCompressionEnabled;
            services.AddSingleton<ITimeTickerManager<TTimeTicker>, TickerManager<TTimeTicker, TCronTicker>>();
            services.AddSingleton<ICronTickerManager<TCronTicker>, TickerManager<TTimeTicker, TCronTicker>>();
            services.AddSingleton<IInternalTickerManager, InternalTickerManager<TTimeTicker, TCronTicker>>();
            services.AddSingleton<ISharpTickerRedisContext, NoOpSharpTickerRedisContext>();
            services.AddSingleton<ISharpTickerNotificationHubSender, NoOpSharpTickerNotificationHubSender>();
            services.AddSingleton<ITickerClock, TickerSystemClock>();

            // Register the initializer hosted service BEFORE scheduler services
            // to guarantee seeding completes before the scheduler starts polling.
            // Registered as a singleton so UseSharpTicker can resolve it to set the initialization flag.
            services.AddSingleton<SharpTickerInitializerHostedService>();
            services.AddHostedService(sp => sp.GetRequiredService<SharpTickerInitializerHostedService>());

            // Only register background services if enabled (default is true)
            if (optionInstance.RegisterBackgroundServices)
            {
                services.AddSingleton<SharpTickerSchedulerBackgroundService>();
                services.AddSingleton<ISharpTickerHostScheduler>(provider =>
                    provider.GetRequiredService<SharpTickerSchedulerBackgroundService>());
                services.AddHostedService(provider =>
                    provider.GetRequiredService<SharpTickerSchedulerBackgroundService>());
                services.AddHostedService(provider => provider.GetRequiredService<SharpTickerFallbackBackgroundService>());
                services.AddSingleton<SharpTickerFallbackBackgroundService>();
                services.AddSingleton<ISharpTickerDispatcher, SharpTickerDispatcher>();
                services.AddSingleton<ISharpTickerTaskScheduler>(sp =>
                {
                    var notification = sp.GetRequiredService<ISharpTickerNotificationHubSender>();
                    var notifyDebounce = new SoftSchedulerNotifyDebounce((value) => notification.UpdateActiveThreads(value));
                    return new SharpTickerTaskScheduler(schedulerOptionsBuilder.MaxConcurrency, schedulerOptionsBuilder.IdleWorkerTimeOut, notifyDebounce, SchedulerLogger(sp));
                });
            }
            else
            {
                services.AddSingleton<ISharpTickerTaskScheduler>(sp => new SharpTickerTaskScheduler(schedulerOptionsBuilder.MaxConcurrency, schedulerOptionsBuilder.IdleWorkerTimeOut, null, SchedulerLogger(sp)));
                // Register NoOp implementations when background services are disabled
                services.AddSingleton<ISharpTickerHostScheduler, NoOpSharpTickerHostScheduler>();
                services.AddSingleton<ISharpTickerDispatcher, NoOpSharpTickerDispatcher>();
            }
            services.AddSingleton<ITickerFunctionConcurrencyGate, TickerFunctionConcurrencyGate>();
            services.AddSingleton<ITickerExecutionTaskHandler, TickerExecutionTaskHandler>();
            services.AddSingleton<ISharpTickerInstrumentation, LoggerInstrumentation>();
            services.AddSingleton<ITickerDashboardDataService<TTimeTicker, TCronTicker>, TickerDashboardDataService<TTimeTicker, TCronTicker>>();

            optionInstance.ExternalProviderConfigServiceAction?.Invoke(services);

            // Register in-memory persistence as fallback — only used when no external provider
            // (EF Core, MongoDB, Redis) has been configured via ExternalProviderConfigServiceAction above.
            services.TryAddSingleton<ITickerPersistenceProvider<TTimeTicker, TCronTicker>, TickerInMemoryPersistenceProvider<TTimeTicker, TCronTicker>>();

            optionInstance.DashboardServiceAction?.Invoke(services);

            if (optionInstance.TickerExceptionHandlerType != null)
                services.AddSingleton(typeof(ITickerExceptionHandler), optionInstance.TickerExceptionHandlerType);

            services.AddSingleton(_ => optionInstance);
            services.AddSingleton(_ => tickerExecutionContext);
            services.AddSingleton(_ => schedulerOptionsBuilder);

            // Register AFTER initializer and scheduler to ensure it runs last
            services.AddHostedService<SharpTickerStartupValidator>();

            return services;
        }

        /// <summary>
        /// Initializes SharpTicker for generic host applications (Console, MAUI, WPF, Worker Services, etc.).
        ///
        /// This method configures middleware and scheduler settings. All I/O-bound
        /// initialization (function discovery, database seeding, external provider startup)
        /// is deferred to <see cref="SharpTickerInitializerHostedService"/> which runs when
        /// the host starts. This means design-time tools that build the host without
        /// starting it (OpenAPI generators, EF migration tools, etc.) will not trigger
        /// database operations.
        /// </summary>
        public static IHost UseSharpTicker(this IHost host, SharpTickerStartMode qStartMode = SharpTickerStartMode.Immediate)
        {
            var serviceProvider = host.Services;
            var tickerExecutionContext = serviceProvider.GetService<TickerExecutionContext>();
            var notificationHubSender = serviceProvider.GetService<ISharpTickerNotificationHubSender>();
            var backgroundScheduler = serviceProvider.GetService<SharpTickerSchedulerBackgroundService>();

            // Signal the initializer hosted service that UseSharpTicker was called,
            // so it should perform startup I/O when the host starts.
            var initializer = serviceProvider.GetService<SharpTickerInitializerHostedService>();
            if (initializer != null)
                initializer.InitializationRequested = true;

            // If background services are registered, configure them
            if (backgroundScheduler != null)
            {
                backgroundScheduler.SkipFirstRun = qStartMode == SharpTickerStartMode.Manual;

                tickerExecutionContext.NotifyCoreAction += (value, type) =>
                {
                    if (type == CoreNotifyActionType.NotifyHostExceptionMessage)
                    {
                        notificationHubSender.UpdateHostException((string)value);
                        tickerExecutionContext.LastHostExceptionMessage = (string)value;
                    }
                    else if (type == CoreNotifyActionType.NotifyNextOccurence)
                        notificationHubSender.UpdateNextOccurrence(value is DateTime dt ? (DateTime?)dt : null);
                    else if (type == CoreNotifyActionType.NotifyHostStatus)
                        notificationHubSender.UpdateHostStatus(value is bool b && b);
                    else if (type == CoreNotifyActionType.NotifyThreadCount)
                        notificationHubSender.UpdateActiveThreads((string)value);
                };
            }
            // If background services are not registered (due to DisableBackgroundServices()),
            // silently skip background service configuration. This is expected behavior.

            if (tickerExecutionContext?.DashboardApplicationAction != null)
            {
                // Cast object back to IApplicationBuilder for Dashboard middleware
                tickerExecutionContext.DashboardApplicationAction(host);
                tickerExecutionContext.DashboardApplicationAction = null;
            }

            return host;
        }
    }
}
