using System;
using SharpTicker.Utilities.Enums;

namespace SharpTicker.Utilities.DashboardDtos
{
    public class TickerGraphData
    {
        public DateTime Date { get; set; }
        public Tuple<int, int>[] Results { get; set; }
    }
}