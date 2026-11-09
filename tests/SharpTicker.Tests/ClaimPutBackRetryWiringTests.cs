using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SharpTicker.BackgroundServices;
using SharpTicker.DependencyInjection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Interfaces.Managers;

namespace SharpTicker.Tests;

/// <summary>
/// The rows whose put-back failed inside a claim call reach the scheduler loop through one retry list per service provider:
/// the managers write to the same list the queue of claimed work reads. Without the background services nothing would read
/// it, so it is not registered and the managers keep the old behaviour.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class ClaimPutBackRetryWiringTests
{
    private static object? RetryListOf(object instance)
        => instance.GetType().GetField("_putBackRetry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);

    [Fact]
    public async Task WithTheBackgroundServices_TheManagersAndTheQueueShareOneRetryList()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSharpTicker();
        await using var provider = services.BuildServiceProvider();

        var list = provider.GetRequiredService<ClaimPutBackRetryList>();
        Assert.Same(list, RetryListOf(provider.GetRequiredService<IInternalTickerManager>()));
        Assert.Same(list, RetryListOf(provider.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>()));
        Assert.Same(list, RetryListOf(provider.GetRequiredService<ICronTickerManager<CronTickerEntity>>()));
        Assert.Same(list, RetryListOf(provider.GetRequiredService<ClaimedWorkQueue>()));
    }

    [Fact]
    public async Task WithoutTheBackgroundServices_ThereIsNoRetryList()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSharpTicker(options => options.DisableBackgroundServices());
        await using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<ClaimPutBackRetryList>());
        Assert.Null(RetryListOf(provider.GetRequiredService<IInternalTickerManager>()));
        Assert.Null(RetryListOf(provider.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>()));
    }
}
