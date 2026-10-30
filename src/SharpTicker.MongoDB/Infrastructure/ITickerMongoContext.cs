using MongoDB.Driver;
using SharpTicker.Utilities.Entities;

namespace SharpTicker.MongoDB.Infrastructure
{
    internal interface ITickerMongoContext<TTimeTicker, TCronTicker>
        where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
        where TCronTicker : CronTickerEntity, new()
    {
        IMongoDatabase Database { get; }
        IMongoCollection<TTimeTicker> TimeTickers { get; }
        IMongoCollection<TCronTicker> CronTickers { get; }
        IMongoCollection<CronTickerOccurrenceEntity<TCronTicker>> CronTickerOccurrences { get; }
    }
}
