namespace SharpTicker.Tests;

/// <summary>
/// Test classes that share something the whole process shares run in this collection: static settings (TickerHelper
/// serializer options and compression, CronScheduleCache time zone), the function registry (TickerFunctionProvider), the
/// in-memory store, and the thread pool, which the task scheduler tests block on purpose. xUnit executes it on its own
/// instead of in parallel with other tests, so no test observes another test's state or starves for threads mid-run.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedStaticStateCollection
{
    public const string Name = "Shared static state";
}
