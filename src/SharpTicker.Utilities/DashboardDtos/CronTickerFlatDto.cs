using System;
using SharpTicker.Utilities.Enums;

namespace SharpTicker.Utilities.DashboardDtos
{
    public class CronTickerFlatDto
    {
        public Guid Id { get; set; }
        public string FunctionName { get; set; }
        public string Expression { get; set; }
        public string Description { get; set; }
        public int Retries { get; set; }
        public TickerTaskPriority Priority { get; set; }
        public DateTime CreatedAt { get; set; }
        public int OccurrenceCount { get; set; }
        public TickerStatus? LastRunStatus { get; set; }
        public DateTime? LastRunAt { get; set; }
        public bool IsEnabled { get; set; }
    }
}
