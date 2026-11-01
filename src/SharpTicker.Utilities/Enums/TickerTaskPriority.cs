namespace SharpTicker.Utilities.Enums
{
    public enum TickerTaskPriority
    {
        /// <summary>
        /// Long running tasks are executed in a separate thread.
        /// Using Default TaskScheduler.
        /// </summary>
        LongRunning,
        /// <summary>
        /// When jobs are waiting for a free worker, High priority tasks start first.
        /// Using TickerTaskScheduler
        /// </summary>
        High,
        /// <summary>
        /// When jobs are waiting for a free worker, Normal priority tasks start after High and before Low priority tasks.
        /// Using TickerTaskScheduler
        /// </summary>
        Normal,
        /// <summary>
        /// When jobs are waiting for a free worker, Low priority tasks start after High and Normal ones. A Low task is not held back for ever: after ten higher priority tasks have started in a row while it waits, it goes next.
        /// Using TickerTaskScheduler
        /// </summary>
        Low
    }
}
