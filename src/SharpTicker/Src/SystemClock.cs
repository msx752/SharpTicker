using System;
using SharpTicker.Utilities.Interfaces;

namespace SharpTicker
{
    internal class TickerSystemClock : ITickerClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }
}
