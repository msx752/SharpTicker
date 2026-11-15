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
        /// whose lock was not renewed for this time, so that they run again. The default is 5 minutes;
        /// <see cref="TimeSpan.Zero"/> turns this off, and a job left <c>InProgress</c> is then released only when an
        /// instance starts under the name that holds it.
        /// </summary>
        public SharpTickerEfCoreOptionBuilder<TTimeTicker, TCronTicker> SetInProgressLease(TimeSpan lease)
        {
            if (lease < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(lease), lease, "The lease must not be negative; TimeSpan.Zero turns it off.");

            InProgressLease = lease;
            return this;
        }
    }
}
