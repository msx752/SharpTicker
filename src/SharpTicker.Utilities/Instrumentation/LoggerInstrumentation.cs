using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Utilities.Instrumentation
{
    /// <summary>
    /// No-operation implementation of ISharpTickerInstrumentation
    /// </summary>
    public sealed class LoggerInstrumentation : SharpTickerBaseLoggerInstrumentation, ISharpTickerInstrumentation
    {
        public LoggerInstrumentation(ILogger<LoggerInstrumentation> logger, SchedulerOptionsBuilder optionsBuilder) : base(logger,  optionsBuilder.NodeIdentifier)
        {
        }

        public override Activity StartJobActivity(string activityName, InternalFunctionContext context)
            => null;
    }
}