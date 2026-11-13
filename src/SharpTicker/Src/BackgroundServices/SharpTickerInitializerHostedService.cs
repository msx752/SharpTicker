using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharpTicker.Provider;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;

namespace SharpTicker.BackgroundServices;

/// <summary>
/// Performs SharpTicker startup initialization (function discovery, cron ticker seeding,
/// external provider setup) as part of the host lifecycle.
///
/// By running inside <see cref="IHostedService.StartAsync"/> instead of inline in
/// <c>UseSharpTicker</c>, this service is naturally skipped by design-time tools
/// (OpenAPI generators, EF migrations, etc.) that build the host but never start it.
/// Registered before the scheduler services to guarantee seeding completes first.
/// </summary>
internal sealed class SharpTickerInitializerHostedService : IHostedService
{
    private readonly TickerExecutionContext _executionContext;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Set to true by <c>UseSharpTicker</c> to signal that this hosted service
    /// should perform startup I/O when the host starts. When false (default),
    /// <see cref="StartAsync"/> is a no-op — this naturally prevents initialization
    /// in design-time tool contexts where UseSharpTicker is never called.
    /// </summary>
    internal bool InitializationRequested { get; set; }

    public SharpTickerInitializerHostedService(
        TickerExecutionContext executionContext,
        IServiceProvider serviceProvider,
        IConfiguration configuration)
    {
        _executionContext = executionContext;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!InitializationRequested)
            return;

        // No job of this run can be claimed before the functions are registered below, so this moment comes before every
        // claim of this run. The startup release of what an earlier run left locked is fenced by it.
        _executionContext.RunStartedAt ??= _serviceProvider.GetService<ITickerClock>()?.UtcNow ?? DateTime.UtcNow;

        var invalidCrons = TickerFunctionProvider.UpdateCronExpressionsFromIConfiguration(_configuration);
        TickerFunctionProvider.Build();

        if (invalidCrons.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, invalidCrons.Select(x =>
                $"The cron expression '{x.Value}' that the configuration key '{x.ConfigurationKey}' gives to the function '{x.Function}' is not valid.")));

        var options = _executionContext.OptionsSeeding;

        if (options == null || options.SeedDefinedCronTickers)
        {
            await SeedDefinedCronTickers(_serviceProvider);
        }

        if (options?.TimeSeederAction != null)
        {
            await options.TimeSeederAction(_serviceProvider);
        }

        if (options?.CronSeederAction != null)
        {
            await options.CronSeederAction(_serviceProvider);
        }

        // Skip stale cron occurrences that were pending before this restart.
        // This prevents the fallback service from catching up past-due occurrences
        // from a previous application lifecycle, which would cause unexpected
        // duplicate executions at startup.
        await SkipStaleCronOccurrencesAsync(_serviceProvider, cancellationToken);

        if (_executionContext.ExternalProviderApplicationAction != null)
        {
            _executionContext.ExternalProviderApplicationAction(_serviceProvider);
            _executionContext.ExternalProviderApplicationAction = null;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task SeedDefinedCronTickers(IServiceProvider serviceProvider)
    {
        var internalTickerManager = serviceProvider.GetRequiredService<IInternalTickerManager>();

        var functionsToSeed = TickerFunctionProvider.TickerFunctions
            .Where(x => !string.IsNullOrEmpty(x.Value.cronExpression))
            .Select(x => (x.Key, x.Value.cronExpression)).ToArray();

        await internalTickerManager.MigrateDefinedCronTickers(functionsToSeed);
    }

    private static async Task SkipStaleCronOccurrencesAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var schedulerOptions = serviceProvider.GetRequiredService<SchedulerOptionsBuilder>();
        if (schedulerOptions.StaleCronOccurrenceThreshold <= TimeSpan.Zero)
            return;

        var internalTickerManager = serviceProvider.GetRequiredService<IInternalTickerManager>();
        await internalTickerManager.SkipStaleCronOccurrencesAsync(schedulerOptions.StaleCronOccurrenceThreshold, cancellationToken);
    }
}
