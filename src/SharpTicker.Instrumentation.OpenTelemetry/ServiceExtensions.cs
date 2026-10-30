using Microsoft.Extensions.DependencyInjection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Instrumentation;

namespace SharpTicker.Instrumentation.OpenTelemetry
{
    public static class ServiceExtensions
    {
        /// <summary>
        /// Adds OpenTelemetry instrumentation with activity tracing for SharpTicker jobs.
        /// Also includes standard logging through ILogger.
        /// </summary>
        public static TickerOptionsBuilder<TTimeTicker, TCronTicker> AddOpenTelemetryInstrumentation<TTimeTicker, TCronTicker>(
            this TickerOptionsBuilder<TTimeTicker, TCronTicker> tickerConfiguration)
            where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
            where TCronTicker : CronTickerEntity, new()
        {
            tickerConfiguration.ExternalProviderConfigServiceAction += services =>
            {
                // Replace any existing instrumentation with OpenTelemetry version
                services.AddSingleton<ISharpTickerInstrumentation, OpenTelemetryInstrumentation>();
            };

            return tickerConfiguration;
        }
    }
}
