namespace SharpTicker.Tests;

/// <summary>
/// Test classes that replace process-wide static settings (TickerHelper serializer options and
/// compression, CronScheduleCache time zone) run in this collection. xUnit executes it on its own
/// instead of in parallel with other tests, so no test observes another test's settings mid-run.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedStaticStateCollection
{
    public const string Name = "Shared static state";
}
