using System;
using System.Text.Json.Serialization;
using SharpTicker.Utilities.Entities.BaseEntity;

namespace SharpTicker.Utilities.Entities
{
    public class CronTickerEntity : BaseTickerEntity
    {
        public virtual string Expression { get; set; }
        public virtual byte[] Request { get; set; }
        public virtual int Retries { get; set; }
        public virtual int[] RetryIntervals { get; set; }
        public virtual bool IsEnabled { get; set; } = true;

        /// <summary>
        /// Deprecated. No longer read or written by the scheduler. The property
        /// stays mapped so existing databases keep their column and no schema
        /// migration is needed. Use <see cref="IsEnabled"/> to pause a cron.
        /// </summary>
        [Obsolete("Not used by the scheduler. Kept only so existing database columns stay mapped. Use IsEnabled instead.")]
        public virtual bool IsSystemPaused { get; set; }
    }
}