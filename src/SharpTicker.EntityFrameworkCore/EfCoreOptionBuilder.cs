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
    }
}
