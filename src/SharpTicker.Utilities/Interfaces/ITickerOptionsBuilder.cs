using System;

namespace SharpTicker.Utilities.Interfaces;

internal interface ITickerOptionsBuilder
{
    internal TimeSpan TimeOutChecker { get; }
}