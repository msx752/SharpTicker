using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SharpTicker.DependencyInjection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Base;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// Registering ticker functions with MapTicker: the name each function gets, the priority, concurrency and cron
/// it is configured with, and that the registered delegate runs the function from the container.
/// The registry is process-wide, so every test uses names no other test uses, and the tests run on their own.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class MapTickerTests
{
    public sealed class PlainJob : ITickerFunction
    {
        public static int Runs;
        public Task ExecuteAsync(TickerFunctionContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Runs);
            return Task.CompletedTask;
        }
    }

    public sealed record MapOrder(string Id);

    public sealed class TypedJob : ITickerFunction<MapOrder>
    {
        public Task ExecuteAsync(TickerFunctionContext<MapOrder> context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    public sealed class GroupedJob : ITickerFunction
    {
        public Task ExecuteAsync(TickerFunctionContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    public sealed class GroupedTypedJob : ITickerFunction<MapOrder>
    {
        public Task ExecuteAsync(TickerFunctionContext<MapOrder> context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static (string Cron, TickerTaskPriority Priority, TickerFunctionDelegate Delegate, int MaxConcurrency) Built(string name)
    {
        TickerFunctionProvider.Build();
        Assert.True(TickerFunctionProvider.TickerFunctions.TryGetValue(name, out var function), $"{name} was not registered");
        return function;
    }

    [Fact]
    public void AFunctionClass_IsRegisteredUnderItsClassName_WithDefaults()
    {
        new ServiceCollection().MapTicker<PlainJob>();

        var function = Built(nameof(PlainJob));

        Assert.Equal(TickerTaskPriority.Normal, function.Priority);
        Assert.Equal(0, function.MaxConcurrency);
        Assert.Equal(string.Empty, function.Cron);
    }

    [Fact]
    public void AFunctionClass_IsAddedToTheContainer_WithTheRequestedLifetime()
    {
        var services = new ServiceCollection();

        services.MapTicker<PlainJob>(ServiceLifetime.Singleton);

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(PlainJob));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void ARegistrationDoesNotReplaceAServiceTheApplicationAlreadyAdded()
    {
        var services = new ServiceCollection();
        services.AddTransient<PlainJob>();

        services.MapTicker<PlainJob>();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(PlainJob));
        Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
    }

    [Fact]
    public async Task TheRegisteredDelegate_RunsTheFunctionFromTheContainer()
    {
        var services = new ServiceCollection();
        services.MapTicker<PlainJob>();
        using var provider = services.BuildServiceProvider();
        var before = PlainJob.Runs;

        await Built(nameof(PlainJob)).Delegate(CancellationToken.None, provider, new TickerFunctionContext { FunctionName = nameof(PlainJob) });

        Assert.Equal(before + 1, PlainJob.Runs);
    }

    [Fact]
    public void ATypedFunction_RegistersItsRequestType()
    {
        new ServiceCollection().MapTicker<TypedJob, MapOrder>();

        Built(nameof(TypedJob));

        var (typeName, type) = TickerFunctionProvider.TickerFunctionRequestTypes[nameof(TypedJob)];
        Assert.Equal(typeof(MapOrder), type);
        Assert.Equal(typeof(MapOrder).FullName, typeName);
    }

    [Fact]
    public void AFunctionClass_CanBeLookedUpByTypeToGetItsName()
    {
        new ServiceCollection().MapTicker<TypedJob, MapOrder>();

        Assert.Equal(nameof(TypedJob), TickerFunctionProvider.GetFunctionName<TypedJob>());
    }

    [Fact]
    public void AnUnregisteredType_FallsBackToItsTypeName()
    {
        Assert.Equal(nameof(MapOrder), TickerFunctionProvider.GetFunctionName<MapOrder>());
    }

    [Fact]
    public void TheBuilder_AppliesCronPriorityAndConcurrency()
    {
        var services = new ServiceCollection();
        services.MapTicker("MapTicker.Configured", (_, _) => Task.CompletedTask)
            .WithCron("*/5 * * * *")
            .WithPriority(TickerTaskPriority.High)
            .WithMaxConcurrency(3);

        var function = Built("MapTicker.Configured");

        Assert.Equal("0 */5 * * * *", function.Cron);
        Assert.Equal(TickerTaskPriority.High, function.Priority);
        Assert.Equal(3, function.MaxConcurrency);
    }

    [Fact]
    public void TheBuilder_RefusesAnInvalidCronExpression_RightAway()
    {
        var builder = new ServiceCollection().MapTicker("MapTicker.BadCron", (_, _) => Task.CompletedTask);

        Assert.Throws<ArgumentException>(() => builder.WithCron("not a cron"));
    }

    [Fact]
    public async Task ALambdaFunction_RunsTheHandlerWithTheContext()
    {
        TickerFunctionContext? seen = null;
        new ServiceCollection().MapTicker("MapTicker.Lambda", (ctx, _) => { seen = ctx; return Task.CompletedTask; });
        var context = new TickerFunctionContext { FunctionName = "MapTicker.Lambda", Id = Guid.NewGuid() };

        await Built("MapTicker.Lambda").Delegate(CancellationToken.None, new ServiceCollection().BuildServiceProvider(), context);

        Assert.Same(context, seen);
    }

    [Fact]
    public async Task ALambdaFunctionWithServices_ReceivesTheServiceProvider()
    {
        IServiceProvider? seen = null;
        new ServiceCollection().MapTicker("MapTicker.LambdaSp", (_, sp, _) => { seen = sp; return Task.CompletedTask; });
        using var provider = new ServiceCollection().BuildServiceProvider();

        await Built("MapTicker.LambdaSp").Delegate(CancellationToken.None, provider, new TickerFunctionContext());

        Assert.Same(provider, seen);
    }

    [Fact]
    public void ATypedLambdaFunction_RegistersItsRequestType()
    {
        new ServiceCollection().MapTicker<MapOrder>("MapTicker.TypedLambda", (_, _) => Task.CompletedTask);
        new ServiceCollection().MapTicker<MapOrder>("MapTicker.TypedLambdaSp", (_, _, _) => Task.CompletedTask);

        Built("MapTicker.TypedLambda");
        Built("MapTicker.TypedLambdaSp");

        Assert.Equal(typeof(MapOrder), TickerFunctionProvider.TickerFunctionRequestTypes["MapTicker.TypedLambda"].Item2);
        Assert.Equal(typeof(MapOrder), TickerFunctionProvider.TickerFunctionRequestTypes["MapTicker.TypedLambdaSp"].Item2);
    }

    [Fact]
    public void AGroup_PrefixesTheNamesOfItsFunctions()
    {
        new ServiceCollection().MapTickerGroup("Billing").MapTicker<GroupedJob>();

        Built("Billing." + nameof(GroupedJob));
    }

    [Fact]
    public void AGroupFunction_CanOverrideItsName()
    {
        new ServiceCollection().MapTickerGroup("Billing").MapTicker<GroupedJob>("Renamed");

        Built("Billing.Renamed");
        Assert.Equal("Billing.Renamed", TickerFunctionProvider.GetFunctionName<GroupedJob>());
    }

    [Fact]
    public void AGroup_GivesItsFunctionsTheDefaultPriorityAndConcurrency()
    {
        new ServiceCollection().MapTickerGroup("Reports")
            .WithPriority(TickerTaskPriority.Low)
            .WithMaxConcurrency(2)
            .MapTicker<GroupedJob>("Defaults");

        var function = Built("Reports.Defaults");

        Assert.Equal(TickerTaskPriority.Low, function.Priority);
        Assert.Equal(2, function.MaxConcurrency);
    }

    [Fact]
    public void AGroupFunction_CanOverrideTheGroupDefaults_ThroughItsBuilder()
    {
        new ServiceCollection().MapTickerGroup("Reports")
            .WithPriority(TickerTaskPriority.Low)
            .MapTicker<GroupedJob>("Overridden")
            .WithPriority(TickerTaskPriority.High);

        Assert.Equal(TickerTaskPriority.High, Built("Reports.Overridden").Priority);
    }

    [Fact]
    public void AGroup_UsesItsLifetimeForTheFunctionClasses()
    {
        var services = new ServiceCollection();

        services.MapTickerGroup("Lifetimes").WithLifetime(ServiceLifetime.Transient).MapTicker<GroupedJob>("Transient");

        Assert.Equal(ServiceLifetime.Transient, Assert.Single(services, d => d.ServiceType == typeof(GroupedJob)).Lifetime);
    }

    [Fact]
    public void ATypedGroupFunction_GetsTheGroupPrefixAndDefaults()
    {
        new ServiceCollection().MapTickerGroup("Typed").WithMaxConcurrency(4).MapTicker<GroupedTypedJob, MapOrder>("Order");

        var function = Built("Typed.Order");

        Assert.Equal(4, function.MaxConcurrency);
        Assert.Equal(typeof(MapOrder), TickerFunctionProvider.TickerFunctionRequestTypes["Typed.Order"].Item2);
    }

    [Fact]
    public void AGroupWithACallback_ReturnsTheServiceCollection_AndRunsTheCallback()
    {
        var services = new ServiceCollection();

        var returned = services.MapTickerGroup("Callback", group => group.MapTicker<GroupedJob>("Inline"));

        Assert.Same(services, returned);
        Built("Callback.Inline");
    }
}
