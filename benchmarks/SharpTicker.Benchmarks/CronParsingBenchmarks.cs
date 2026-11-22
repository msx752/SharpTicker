using BenchmarkDotNet.Attributes;
using NCrontab;
using SharpTicker.Utilities;

namespace SharpTicker.Benchmarks;

/// <summary>
/// Cost of SharpTicker's cron expression parsing through the public CronExpression.Parse: a 5-part expression is turned
/// into a 6-part one ("0 " is prepended), the expression is normalized and looked up in SharpTicker's schedule cache, which
/// parses it once with seconds included. The expressions are parsed once in setup, so the rows measure the steady state
/// (cache hit), which is what every later parse of the same expression costs.
///
/// Not measured here: the next-occurrence calculation with the scheduler's time zone. It is internal to SharpTicker.Utilities
/// and reachable from outside only through the ticker managers, which also write to a store.
///
/// The NCrontab row is a baseline for comparison only: the cron library itself, parsing without SharpTicker's cache.
/// </summary>
[MemoryDiagnoser]
public class CronParsingBenchmarks
{
    private const string FivePart = "*/5 * * * *";
    private const string SixPart = "*/30 * * * * *";
    private const string SixPartComplex = "0 0 9-17 * * 1-5";

    private static readonly CrontabSchedule.ParseOptions IncludingSeconds = new() { IncludingSeconds = true };

    [GlobalSetup]
    public void Setup()
    {
        // Fill the schedule cache, as the first parse of each expression in an application does
        CronExpression.Parse(FivePart);
        CronExpression.Parse(SixPart);
        CronExpression.Parse(SixPartComplex);
    }

    [Benchmark(Description = "CronExpression.Parse: 5-part (*/5 * * * *), cached")]
    public CronExpression Parse_FivePart() => CronExpression.Parse(FivePart);

    [Benchmark(Description = "CronExpression.Parse: 6-part (*/30 * * * * *), cached")]
    public CronExpression Parse_SixPart() => CronExpression.Parse(SixPart);

    [Benchmark(Description = "CronExpression.Parse: 6-part (0 0 9-17 * * 1-5), cached")]
    public CronExpression Parse_SixPartComplex() => CronExpression.Parse(SixPartComplex);

    [Benchmark(Baseline = true, Description = "Baseline, NCrontab only (not SharpTicker): parse 6-part (*/30 * * * * *), no cache")]
    public CrontabSchedule NCrontab_Parse_SixPart() => CrontabSchedule.Parse(SixPart, IncludingSeconds);
}
