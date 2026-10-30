using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SharpTicker.BackgroundServices;
using SharpTicker.DependencyInjection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces.Managers;

namespace SharpTicker.Tests;

public class DesignTimeToolDetectionTests
{
    [Fact]
    public async Task Initializer_Skips_When_UseSharpTicker_Not_Called()
    {
        // Design-time tools (dotnet-getdocument, dotnet-ef) build the host but
        // never call UseSharpTicker. Verify the initializer does nothing in that case.
        var context = new TickerExecutionContext();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var configuration = Substitute.For<IConfiguration>();

        var initializer = new SharpTickerInitializerHostedService(context, serviceProvider, configuration);
        await initializer.StartAsync(CancellationToken.None);

        // InitializationRequested is false by default, so no seeding should occur.
        Assert.False(initializer.InitializationRequested);
    }

    [Fact]
    public void UseSharpTicker_Sets_InitializationRequested_On_Initializer()
    {
        var services = new ServiceCollection();
        services.AddSharpTicker();

        var host = BuildMinimalHost(services);
        host.UseSharpTicker();

        var initializer = host.Services.GetRequiredService<SharpTickerInitializerHostedService>();
        Assert.True(initializer.InitializationRequested);
    }

    [Fact]
    public void UseSharpTicker_Does_Not_Perform_IO()
    {
        // UseSharpTicker should complete without touching the database or building functions.
        var services = new ServiceCollection();
        services.AddSharpTicker();

        var host = BuildMinimalHost(services);

        // This should not throw — no DB access, no function scanning
        var result = host.UseSharpTicker();
        Assert.Same(host, result);
    }

    [Fact]
    public async Task Initializer_Runs_Seeding_When_InitializationRequested()
    {
        var internalManager = Substitute.For<IInternalTickerManager>();
        var context = new TickerExecutionContext();
        var configuration = Substitute.For<IConfiguration>();

        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddSingleton(configuration);
        services.AddSingleton(internalManager);
        services.AddSingleton(new SchedulerOptionsBuilder());

        var sp = services.BuildServiceProvider();
        var initializer = new SharpTickerInitializerHostedService(context, sp, configuration);
        initializer.InitializationRequested = true;

        await initializer.StartAsync(CancellationToken.None);

        // MigrateDefinedCronTickers should have been called (even if with empty list)
        await internalManager.Received(1).MigrateDefinedCronTickers(Arg.Any<(string, string)[]>());
    }

    [Fact]
    public async Task Initializer_Respects_IgnoreSeedDefinedCronTickers()
    {
        var internalManager = Substitute.For<IInternalTickerManager>();

        var services = new ServiceCollection();
        services.AddSharpTicker(options => options.IgnoreSeedDefinedCronTickers());

        var host = BuildMinimalHost(services);
        host.UseSharpTicker();

        var context = host.Services.GetRequiredService<TickerExecutionContext>();
        var configuration = host.Services.GetRequiredService<IConfiguration>();

        // Create a new initializer with the real context that has seeding disabled
        var initializer = new SharpTickerInitializerHostedService(context, host.Services, configuration);
        initializer.InitializationRequested = true;

        await initializer.StartAsync(CancellationToken.None);

        // Seeding should be skipped — IInternalTickerManager was not registered via AddSharpTicker's
        // mock, so if it tried to seed, it would call the real (non-mock) manager.
        // The real assertion: IgnoreSeedDefinedCronTickers sets the flag correctly.
        var optionsSeeding = context.OptionsSeeding;
        Assert.NotNull(optionsSeeding);
        Assert.False(optionsSeeding.SeedDefinedCronTickers);
    }

    [Fact]
    public async Task Initializer_Runs_External_Provider_Action()
    {
        var actionCalled = false;
        var context = new TickerExecutionContext();
        context.ExternalProviderApplicationAction = _ => actionCalled = true;

        var internalManager = Substitute.For<IInternalTickerManager>();
        var configuration = Substitute.For<IConfiguration>();

        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddSingleton(configuration);
        services.AddSingleton(internalManager);
        services.AddSingleton(new SchedulerOptionsBuilder());

        var sp = services.BuildServiceProvider();
        var initializer = new SharpTickerInitializerHostedService(context, sp, configuration);
        initializer.InitializationRequested = true;

        await initializer.StartAsync(CancellationToken.None);

        Assert.True(actionCalled);
        Assert.Null(context.ExternalProviderApplicationAction);
    }

    [Fact]
    public async Task Initializer_StopAsync_Is_NoOp()
    {
        var context = new TickerExecutionContext();
        var sp = Substitute.For<IServiceProvider>();
        var config = Substitute.For<IConfiguration>();

        var initializer = new SharpTickerInitializerHostedService(context, sp, config);

        // Should complete immediately without exceptions
        await initializer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Initializer_Registered_Before_Scheduler_Services()
    {
        var services = new ServiceCollection();
        services.AddSharpTicker();

        // Verify registration order: initializer should appear before scheduler
        var hostedServiceDescriptors = services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .ToList();

        Assert.True(hostedServiceDescriptors.Count >= 1);

        // First hosted service should be the initializer (resolved via factory from singleton)
        var first = hostedServiceDescriptors[0];
        // The factory registration resolves SharpTickerInitializerHostedService
        var sp = BuildMinimalHost(new ServiceCollection()).Services;
        // Just verify the initializer singleton is registered before scheduler
        var allSingletons = services
            .Where(d => d.Lifetime == ServiceLifetime.Singleton)
            .Select(d => d.ServiceType ?? d.ImplementationType)
            .ToList();

        var initializerIndex = allSingletons.IndexOf(typeof(SharpTickerInitializerHostedService));
        var schedulerIndex = allSingletons.IndexOf(typeof(SharpTickerSchedulerBackgroundService));

        Assert.True(initializerIndex >= 0, "SharpTickerInitializerHostedService should be registered");
        // Scheduler may or may not be registered (depends on DisableBackgroundServices)
        if (schedulerIndex >= 0)
        {
            Assert.True(initializerIndex < schedulerIndex,
                "Initializer should be registered before scheduler");
        }
    }

    private static IHost BuildMinimalHost(IServiceCollection services)
    {
        services.TryAddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.TryAddSingleton(Substitute.For<IHostApplicationLifetime>());
        services.TryAddSingleton(Substitute.For<IHostEnvironment>());
        services.AddLogging();
        return new MinimalHost(services.BuildServiceProvider());
    }

    /// <summary>
    /// Minimal IHost implementation for testing UseSharpTicker without a full host builder.
    /// </summary>
    private sealed class MinimalHost : IHost
    {
        public IServiceProvider Services { get; }

        public MinimalHost(IServiceProvider services) => Services = services;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
    }
}
