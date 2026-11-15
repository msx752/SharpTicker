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

        public SharpTickerEfCoreOptionBuilder<TTimeTicker, TCronTicker> UseApplicationDbContext<TDbContext>(ConfigurationType configurationType) where TDbContext : DbContext
        {
            ServiceBuilder.UseApplicationDbContext<TDbContext, TTimeTicker, TCronTicker>(this, configurationType);
            return this;
        }
        
        public SharpTickerEfCoreOptionBuilder<TTimeTicker, TCronTicker> UseSharpTickerDbContext<TDbContext>(Action<DbContextOptionsBuilder> optionsAction, string schema = null) where TDbContext : SharpTickerDbContext<TTimeTicker, TCronTicker>
        {
            Schema = schema ?? Schema;
            
            ServiceBuilder.UseSharpTickerDbContext<TDbContext, TTimeTicker, TCronTicker>(this, optionsAction);
            return this;
        }

        public SharpTickerEfCoreOptionBuilder<TTimeTicker, TCronTicker> SetDbContextPoolSize(int poolSize)
        {
            PoolSize = poolSize;
            return this;
        }

        public SharpTickerEfCoreOptionBuilder<TTimeTicker, TCronTicker> SetSchema(string schema)
        {
            Schema = schema;
            return this;
        }

        /// <summary>
        /// How long a job that is <c>InProgress</c> stays locked to an instance that no longer renews its lock. A running
        /// instance renews the lock of the jobs it holds every fifth of this time, and releases the jobs of other instances
        /// whose lock was not renewed for this time, so that they run again. The default is 5 minutes, which is the
        /// recommended value; <see cref="TimeSpan.Zero"/> turns this off, and a job left <c>InProgress</c> is then released
        /// only when an instance starts under the name that holds it.
        /// <para>
        /// Accepted: <see cref="TimeSpan.Zero"/>, or a value from 1 second up to 30 days; any other value throws
        /// <see cref="ArgumentOutOfRangeException"/>. Keep the lease well above the time a renewal pass can take on your
        /// database, or the jobs of a live instance can be released. Set the same lease on every instance that runs the
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
