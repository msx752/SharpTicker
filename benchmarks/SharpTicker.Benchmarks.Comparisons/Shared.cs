using System.Collections.Specialized;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Quartz.Impl;
using SharpTicker.DependencyInjection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Enums;

namespace SharpTicker.Benchmarks.Comparisons;

/// <summary>
/// The same no-op job for all three libraries.
/// </summary>
public static class NoopJobs
{
    public const string SharpTickerFunction = "Benchmarks.Noop";

    private static readonly object Lock = new();
    private static bool _registered;

    /// <summary>
    /// Registers the SharpTicker function once per process, as the source generator does for a [TickerFunction] method.
    /// </summary>
    public static void EnsureSharpTickerFunction()
    {
        lock (Lock)
        {
            if (_registered)
                return;

            TickerFunctionProvider.RegisterFunctions(new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>
            {
                [SharpTickerFunction] = (string.Empty, TickerTaskPriority.Normal, (_, _, _) => Task.CompletedTask, 0)
            });
            TickerFunctionProvider.Build();
            _registered = true;
        }
    }

    /// <summary>
    /// A SharpTicker container with the in-memory store and without background services (nothing runs the jobs).
    /// </summary>
    public static ServiceProvider CreateSharpTickerInMemory()
    {
        EnsureSharpTickerFunction();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSharpTicker(options => options.DisableBackgroundServices());
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// A Quartz scheduler with the default RAMJobStore, not started (nothing runs the jobs).
    /// </summary>
    public static IScheduler CreateQuartzScheduler() =>
        new StdSchedulerFactory(new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = "bench-" + Guid.NewGuid().ToString("N"),
            ["quartz.jobStore.type"] = "Quartz.Simpl.RAMJobStore, Quartz"
        }).GetScheduler().GetAwaiter().GetResult();

    /// <summary>
    /// Hangfire job method.
    /// </summary>
    public static void HangfireNoop() { }

    /// <summary>
    /// Quartz job.
    /// </summary>
    public sealed class QuartzNoopJob : IJob
    {
        public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
    }
}
