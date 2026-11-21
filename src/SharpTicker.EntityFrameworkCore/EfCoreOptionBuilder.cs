using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpTicker.EntityFrameworkCore.Customizer;
using SharpTicker.EntityFrameworkCore.DbContextFactory;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Interfaces.Managers;

namespace SharpTicker.EntityFrameworkCore
{
    /// <summary>
    /// Configures the Entity Framework Core store, given to <c>AddOperationalStore</c>. Choose one of
    /// <see cref="UseApplicationDbContext{TDbContext}"/> and <see cref="UseSharpTickerDbContext{TDbContext}"/>.
    /// </summary>
    public class SharpTickerEfCoreOptionBuilder<TTimeTicker, TCronTicker>
        where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
        where TCronTicker : CronTickerEntity, new()
    {
        internal Action<IServiceCollection> ConfigureServices { get; set; }
        internal int PoolSize { get; set; } = 1024;
        internal string Schema { get; set; } = "ticker";
        internal TimeSpan InProgressLease { get; set; } = DefaultInProgressLease;

        internal static readonly TimeSpan DefaultInProgressLease = TimeSpan.FromMinutes(5);
        internal static readonly TimeSpan MinInProgressLease = TimeSpan.FromSeconds(1);
        internal static readonly TimeSpan MaxInProgressLease = TimeSpan.FromDays(30);

        /// <summary>
        /// Keeps the store's tables in the application's own context, which the application registers itself, for
        /// example with <c>AddDbContext</c>. The tables are then part of the application's model and migrations.
        /// </summary>
        /// <param name="configurationType">
        /// <see cref="ConfigurationType.UseModelCustomizer"/> adds the tables to the context's model;
        /// <see cref="ConfigurationType.IgnoreModelCustomizer"/> leaves that to the context's <c>OnModelCreating</c>, which
        /// applies the configurations from <c>SharpTicker.EntityFrameworkCore.Configurations</c> itself.
        /// </param>
        public SharpTickerEfCoreOptionBuilder<TTimeTicker, TCronTicker> UseApplicationDbContext<TDbContext>(ConfigurationType configurationType) where TDbContext : DbContext
        {
            ServiceBuilder.UseApplicationDbContext<TDbContext, TTimeTicker, TCronTicker>(this, configurationType);
            return this;
        }
        
        /// <summary>
        /// Keeps the store's tables in a context of their own, <see cref="SharpTickerDbContext{TTimeTicker, TCronTicker}"/>
        /// or a type derived from it, created through a pooled factory (see <see cref="SetDbContextPoolSize"/>).
        /// </summary>
        /// <param name="optionsAction">Configures the context's options, such as the database provider and connection string.</param>
        /// <param name="schema">The schema of the tables; <see langword="null"/> keeps the current one (default <c>ticker</c>, see <see cref="SetSchema"/>).</param>
        public SharpTickerEfCoreOptionBuilder<TTimeTicker, TCronTicker> UseSharpTickerDbContext<TDbContext>(Action<DbContextOptionsBuilder> optionsAction, string schema = null) where TDbContext : SharpTickerDbContext<TTimeTicker, TCronTicker>
        {
            Schema = schema ?? Schema;
            
            ServiceBuilder.UseSharpTickerDbContext<TDbContext, TTimeTicker, TCronTicker>(this, optionsAction);
            return this;
        }

        /// <summary>
        /// The size of the context pool used with <see cref="UseSharpTickerDbContext{TDbContext}"/>. The default is 1024.
        /// A value of 0 or less makes <c>AddOperationalStore</c> throw <see cref="ArgumentOutOfRangeException"/>.
        /// </summary>
        public SharpTickerEfCoreOptionBuilder<TTimeTicker, TCronTicker> SetDbContextPoolSize(int poolSize)
        {
            PoolSize = poolSize;
            return this;
        }

        /// <summary>
        /// The schema of the store's tables. The default is <c>ticker</c>. It applies to
        /// <see cref="UseSharpTickerDbContext{TDbContext}"/> and to <see cref="ConfigurationType.UseModelCustomizer"/>; with
        /// <see cref="ConfigurationType.IgnoreModelCustomizer"/> the schema is the one given to the configurations.
        /// </summary>
        public SharpTickerEfCoreOptionBuilder<TTimeTicker, TCronTicker> SetSchema(string schema)
        {
            Schema = schema;
            return this;
        }

        /// <summary>
        /// How long a job that is <c>InProgress</c> stays locked to an instance that no longer renews its lock. A running
        /// instance renews the lock of the jobs it holds every fifth of this time, and records the jobs of other instances
        /// whose lock was not renewed for this time as <c>Cancelled</c>; they do not run again (up to 1.6.1 they were
        /// released and ran again). The default is 5 minutes, which is the
        /// recommended value; <see cref="TimeSpan.Zero"/> turns this off, and a job left <c>InProgress</c> is then released
        /// only when an instance starts under the name that holds it.
        /// <para>
        /// Accepted: <see cref="TimeSpan.Zero"/>, or a value from 1 second up to 30 days; any other value throws
        /// <see cref="ArgumentOutOfRangeException"/>. Keep the lease well above the time a renewal pass can take on your
        /// database, or the jobs of a live instance can be recorded as <c>Cancelled</c>. Set the same lease on every instance that runs the
        /// background services, and turn it off on all of them or on none.
        /// </para>
        /// </summary>
        public SharpTickerEfCoreOptionBuilder<TTimeTicker, TCronTicker> SetInProgressLease(TimeSpan lease)
        {
            if (lease != TimeSpan.Zero && (lease < MinInProgressLease || lease > MaxInProgressLease))
                throw new ArgumentOutOfRangeException(nameof(lease), lease, "The lease must be TimeSpan.Zero (off) or from 1 second up to 30 days.");

            InProgressLease = lease;
            return this;
        }
    }
}
