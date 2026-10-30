using System;

namespace SharpTicker.Utilities.Interfaces
{
    public interface ITickerClock
    {
        DateTime UtcNow { get; }
    }
}
