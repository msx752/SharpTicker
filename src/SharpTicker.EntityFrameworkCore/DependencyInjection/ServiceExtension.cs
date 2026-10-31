using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Instrumentation;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Managers;
using SharpTicker.Utilities.Interfaces;

namespace SharpTicker.EntityFrameworkCore.DependencyInjection;

public static class ServiceExtension
{
        
    public static TickerOptionsBuilder<TTimeTicker, TCronTicker> AddOperationalStore<TTimeTicker, TCronTicker>(this TickerOptionsBuilder<TTimeTicker,TCronTicker> tickerConfiguration, Action<SharpTickerEfCoreOptionBuilder<TTimeTicker, TCronTicker>> efConfiguration = null)
        where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
        where TCronTicker : CronTickerEntity, new()
    {
        var efCoreOptionBuilder = new SharpTickerEfCoreOptionBuilder<TTimeTicker, TCronTicker>();

        efConfiguration?.Invoke(efCoreOptionBuilder);
            
        if (efCoreOptionBuilder.PoolSize <= 0) 
            throw new ArgumentOutOfRangeException(nameof(efCoreOptionBuilder.PoolSize), "Pool size must be greater than 0");
            
        tickerConfiguration.ExternalProviderConfigServiceAction += (services) 
            => services.AddSingleton(_ => efCoreOptionBuilder);
            
        tickerConfiguration.ExternalProviderConfigServiceAction += efCoreOptionBuilder.ConfigureServices;
            
        UseApplicationService(tickerConfiguration, efCoreOptionBuilder);
            
        return tickerConfiguration;
    }
        
    private static void UseApplicationService<TTimeTicker, TCronTicker>(TickerOptionsBuilder<TTimeTicker, TCronTicker> tickerConfiguration, SharpTickerEfCoreOptionBuilder<TTimeTicker, TCronTicker> options)
        where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
        where TCronTicker : CronTickerEntity, new()
    {
        tickerConfiguration.UseExternalProviderApplication((serviceProvider) =>
        {
            var internalTickerManager = serviceProvider.GetRequiredService<IInternalTickerManager>();
            var hostLifetime = serviceProvider.GetService<IHostApplicationLifetime>();
            var schedulerOptions = serviceProvider.GetService<SchedulerOptionsBuilder>();
            var hostScheduler = serviceProvider.GetService<ISharpTickerHostScheduler>();

            var logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger(StartupReleaseCategory) ?? NullLogger.Instance;
            var executionContext = serviceProvider.GetService<TickerExecutionContext>();

            hostLifetime.ApplicationStarted.Register(() =>
            {
                // The context keeps the task so that whoever needs to know the release is over, a test for one, can wait for it.
                var release = Task.Run(() => ReleaseDeadNodeResourcesOnStartAsync(internalTickerManager, schedulerOptions.NodeIdentifier, hostScheduler, logger));
                if (executionContext != null)
                    executionContext.ExternalProviderStartup = release;
            });
        });
    }

    private const string StartupReleaseCategory = "SharpTicker.EntityFrameworkCore.StartupRelease";

    /// <summary>
    /// Releases the jobs a dead node or an earlier run of this node left locked, then restarts the host scheduler so it picks
    /// up what was seeded in the meantime.
    /// </summary>
    internal static async Task ReleaseDeadNodeResourcesOnStartAsync(
        IInternalTickerManager internalTickerManager,
        string nodeIdentifier,
        ISharpTickerHostScheduler hostScheduler,
        ILogger logger)
    {
        try
        {
            // Release resources held by dead nodes before the scheduler starts processing.
            await internalTickerManager.ReleaseDeadNodeResources(nodeIdentifier);

            // After cleanup, restart the host scheduler so it immediately
            // picks up newly seeded cron tickers and jobs configured via the core pipeline.
            if (hostScheduler != null && hostScheduler.IsRunning)
            {
                hostScheduler.Restart();
            }
        }
        catch (Exception ex)
        {
            // Nobody awaits this task, so an exception would be lost. The host scheduler is not restarted after a failure,
            // as before, but now the failure is on the server log.
            logger.LogError(ex, "Releasing the jobs held by dead nodes and by an earlier run of node {NodeIdentifier} failed at startup. " +
                                "Jobs that were left InProgress were not released by this step.", nodeIdentifier);
        }
    }
}
