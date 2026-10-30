namespace SharpTicker.Utilities.Enums
{
    public enum SharpTickerStartMode
    {
        /// <summary>
        /// Start job processing immediately when UseSharpTicker is called.
        /// Background services are registered and start automatically.
        /// </summary>
        Immediate,
        
        /// <summary>
        /// Background services are registered but skip the first run.
        /// Job processing needs to be started manually via ISharpTickerHostScheduler.
        /// </summary>
        Manual
    }
}