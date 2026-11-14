using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpTicker.EntityFrameworkCore.Infrastructure;
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

            // The initializer records when this run began, before any job could be claimed. Without it (a host that skipped
            // the initializer) this moment is the latest one still before the scheduler and the fallback start.
            var runStartedAt = executionContext?.RunStartedAt
                               ?? serviceProvider.GetService<ITickerClock>()?.UtcNow
                               ?? DateTime.UtcNow;

            // With Redis next to the store, its heartbeat releases the jobs of a node that is gone; without it nothing
            // does, so the jobs under other identifiers are reported once (#24).
            var jobsOfOtherNodes = serviceProvider.GetService<ISharpTickerRedisContext>()?.HasRedisConnection == true
                ? null
                : serviceProvider.GetService<ITickerPersistenceProvider<TTimeTicker, TCronTicker>>() as IJobsOfOtherNodes;

            hostLifetime.ApplicationStarted.Register(() =>
            {
                // The context keeps the task so that whoever needs to know the release is over, a test for one, can wait for it.
                var release = Task.Run(() => ReleaseDeadNodeResourcesOnStartAsync(internalTickerManager, schedulerOptions.NodeIdentifier, runStartedAt, hostScheduler, logger, jobsOfOtherNodes));
                if (executionContext != null)
                    executionContext.ExternalProviderStartup = release;
            });
        });
    }

    private const string StartupReleaseCategory = "SharpTicker.EntityFrameworkCore.StartupRelease";

    /// <summary>
    /// Releases the jobs an earlier run of this node left locked, then restarts the host scheduler so it picks up what was
    /// seeded in the meantime. It runs once the application has started, when the scheduler and the fallback may already
    /// have claimed jobs of this run, so only rows locked before <paramref name="runStartedAt"/> are released.
    /// </summary>
    internal static async Task ReleaseDeadNodeResourcesOnStartAsync(
        IInternalTickerManager internalTickerManager,
        string nodeIdentifier,
        DateTime runStartedAt,
        ISharpTickerHostScheduler hostScheduler,
        ILogger logger,
        IJobsOfOtherNodes jobsOfOtherNodes = null)
    {
        try
        {
            await internalTickerManager.ReleaseEarlierRunResources(nodeIdentifier, runStartedAt);

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

        if (jobsOfOtherNodes == null)
            return;

        try
        {
            var others = await jobsOfOtherNodes.CountInProgressOfOtherNodesAsync(nodeIdentifier);
            if (others.Count == 0)
                return;

            // Every container that was replaced can have left an identifier, so the list is cut.
            var listed = others.OrderByDescending(x => x.Jobs).ThenBy(x => x.NodeIdentifier, StringComparer.Ordinal).Take(MaxNodesInLog)
                .Select(x => $"{x.NodeIdentifier} ({x.Jobs})");
            var nodes = string.Join(", ", listed) + (others.Count > MaxNodesInLog ? $" and {others.Count - MaxNodesInLog} more" : "");
            logger.LogInformation("{JobCount} job(s) are InProgress under other node identifiers: {Nodes}. With the EF Core store and no Redis heartbeat, " +
                                  "such a job is released when a node with the same identifier starts. A job of a node that is still running ends as usual. " +
                                  "If no node runs or starts under that identifier again, the job stays InProgress until it is removed.", others.Sum(x => x.Jobs), nodes);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Counting the jobs that are InProgress under other node identifiers failed at startup.");
        }
    }

    private const int MaxNodesInLog = 10;
}
