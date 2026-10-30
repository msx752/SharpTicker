using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpTicker.EntityFrameworkCore.DbContextFactory;
using SharpTicker.EntityFrameworkCore.Infrastructure;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Interfaces;

namespace SharpTicker.EntityFrameworkCore.Customizer;

public static class ServiceBuilder
{
    internal static void UseApplicationDbContext<TContext, TTimeTicker, TCronTicker>(SharpTickerEfCoreOptionBuilder<TTimeTicker, TCronTicker> builder, ConfigurationType configurationType) 
        where TContext : DbContext
        where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
        where TCronTicker : CronTickerEntity, new()
    {
        builder.ConfigureServices = (services) =>
        {
            if (configurationType == ConfigurationType.UseModelCustomizer)
            {
#if NET9_0_OR_GREATER
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IDbContextOptionsConfiguration<TContext>, SharpTickerOptionsConfiguration<TContext, TTimeTicker, TCronTicker>>());
#else
                // EF Core 8 has no IDbContextOptionsConfiguration<TContext>: wrap the registered
                // DbContextOptions<TContext> factory instead, so AddDbContext must be called first
                var originalDescriptor = services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(DbContextOptions<TContext>));

                if (originalDescriptor?.ImplementationFactory == null)
                    throw new InvalidOperationException($"Cannot use UseModelCustomizer: register {typeof(TContext).Name} with AddDbContext before configuring the scheduler");

                services.Remove(originalDescriptor);
                services.Add(new ServiceDescriptor(
                    typeof(DbContextOptions<TContext>),
                    provider => WithTickerModelCustomizer<TContext, TTimeTicker, TCronTicker>(
                        (DbContextOptions<TContext>)originalDescriptor.ImplementationFactory(provider)),
                    originalDescriptor.Lifetime));
#endif
            }

            services.AddSingleton<ITickerPersistenceProvider<TTimeTicker, TCronTicker>, TickerEfCorePersistenceProvider<TContext, TTimeTicker, TCronTicker>>();
        };
    }

    internal static void UseSharpTickerDbContext<TContext, TTimeTicker, TCronTicker>(SharpTickerEfCoreOptionBuilder<TTimeTicker, TCronTicker> builder, Action<DbContextOptionsBuilder> optionsAction)
        where TContext : SharpTickerDbContext<TTimeTicker, TCronTicker>
        where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
        where TCronTicker : CronTickerEntity, new()
    {
        builder.ConfigureServices = (services) =>
        {
            services.TryAddSingleton<IDbContextFactory<TContext>>(sp =>
            {
                var optionsBuilder = new DbContextOptionsBuilder<TContext>();
                optionsAction.Invoke(optionsBuilder);
                optionsBuilder.UseApplicationServiceProvider(sp);
                return new PooledDbContextFactory<TContext>(optionsBuilder.Options, builder.PoolSize);
            });
            services.TryAddScoped<TContext>(sp => sp.GetRequiredService<IDbContextFactory<TContext>>().CreateDbContext());
            services.AddSingleton<ITickerPersistenceProvider<TTimeTicker, TCronTicker>, TickerEfCorePersistenceProvider<TContext, TTimeTicker, TCronTicker>>();
        };
    }

#if NET9_0_OR_GREATER
    public class SharpTickerOptionsConfiguration<TContext, TTimeTicker, TCronTicker>
        : IDbContextOptionsConfiguration<TContext>
        where TContext : DbContext
        where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
        where TCronTicker : CronTickerEntity, new()
    {
        public void Configure(IServiceProvider serviceProvider, DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder
                .ReplaceService<IModelCustomizer, TickerModelCustomizer<TTimeTicker, TCronTicker>>();
        }
    }
#else
    private static DbContextOptions<TContext> WithTickerModelCustomizer<TContext, TTimeTicker, TCronTicker>(DbContextOptions<TContext> options)
        where TContext : DbContext
        where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
        where TCronTicker : CronTickerEntity, new()
        => new DbContextOptionsBuilder<TContext>(options)
            .ReplaceService<IModelCustomizer, TickerModelCustomizer<TTimeTicker, TCronTicker>>()
            .Options;
#endif
}