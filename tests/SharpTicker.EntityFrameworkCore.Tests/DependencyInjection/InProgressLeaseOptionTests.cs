using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharpTicker.DependencyInjection;
using SharpTicker.EntityFrameworkCore.Customizer;
using SharpTicker.EntityFrameworkCore.DependencyInjection;
using SharpTicker.Utilities.Entities;

namespace SharpTicker.EntityFrameworkCore.Tests.DependencyInjection;

/// <summary>
/// <c>SetInProgressLease</c> (#24): the lease is on by default with 5 minutes, <see cref="TimeSpan.Zero"/> turns it off,
/// a negative value is refused, and a host without the background services has no lease.
/// </summary>
public class InProgressLeaseOptionTests
{
    private static int LeaseServices(Action<SharpTickerEfCoreOptionBuilder<TimeTickerEntity, CronTickerEntity>>? ef = null, bool disableBackgroundServices = false)
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestDbContext>(options => options.UseSqlite("Data Source=:memory:"));
        services.AddSharpTicker(options =>
        {
            options.AddOperationalStore(store =>
            {
                store.UseApplicationDbContext<TestDbContext>(ConfigurationType.UseModelCustomizer);
                ef?.Invoke(store);
            });
            // After the store on purpose: the lease follows the whole configuration.
            if (disableBackgroundServices)
                options.DisableBackgroundServices();
        });

        return services.Count(d => d.ServiceType == typeof(IHostedService)
                                   && d.ImplementationType == typeof(InProgressLeaseBackgroundService<TimeTickerEntity, CronTickerEntity>));
    }

    [Fact]
    public void ByDefault_TheLeaseRuns_WithFiveMinutes()
    {
        Assert.Equal(1, LeaseServices());
        Assert.Equal(TimeSpan.FromMinutes(5), new SharpTickerEfCoreOptionBuilder<TimeTickerEntity, CronTickerEntity>().InProgressLease);
    }

    [Fact]
    public void Zero_TurnsTheLeaseOff()
        => Assert.Equal(0, LeaseServices(ef => ef.SetInProgressLease(TimeSpan.Zero)));

    [Fact]
    public void AHostWithoutTheBackgroundServices_HasNoLease()
        => Assert.Equal(0, LeaseServices(disableBackgroundServices: true));

    [Fact]
    public void ANegativeLease_IsRefused()
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SharpTickerEfCoreOptionBuilder<TimeTickerEntity, CronTickerEntity>().SetInProgressLease(TimeSpan.FromSeconds(-1)));
}
