using BenchmarkDotNet.Attributes;
using NCrontab;
using QuartzCron = Quartz.CronExpression;

namespace SharpTicker.Benchmarks.Comparisons;

/// <summary>
/// Library level, not SharpTicker's own path: NCrontab, the cron library SharpTicker parses with (6 fields, seconds included,
/// as SharpTicker always parses), against Quartz's CronExpression, for the same two schedules. Each parse row parses without
/// any cache. SharpTicker's own path (CronExpression.Parse with its schedule cache) is in CronParsingBenchmarks of the
/// SharpTicker.Benchmarks project.
/// Next occurrence: NCrontab has no time zone (SharpTicker converts the time around it, which is not measured here); Quartz
/// is set to UTC, so it does its own time-zone handling with UTC.
/// Hangfire has no row: its cron parser is internal, and its public Cron class only builds expression strings.
/// </summary>
[MemoryDiagnoser]
public class CronExpressionComparison
{
    // Every 30 seconds
    private const string EveryThirtySecondsNcrontab = "*/30 * * * * *";
    private const string EveryThirtySecondsQuartz = "0/30 * * * * ?";

    // Every hour from 9 to 17 on weekdays
    private const string BusinessHoursNcrontab = "0 0 9-17 * * 1-5";
    private const string BusinessHoursQuartz = "0 0 9-17 ? * MON-FRI";

    private static readonly CrontabSchedule.ParseOptions IncludingSeconds = new() { IncludingSeconds = true };

    private CrontabSchedule _ncrontabThirty = null!;
    private CrontabSchedule _ncrontabBusiness = null!;
    private QuartzCron _quartzThirty = null!;
    private QuartzCron _quartzBusiness = null!;

    private readonly DateTime _base = new(2026, 3, 16, 12, 0, 0, DateTimeKind.Utc);
    private readonly DateTimeOffset _baseOffset = new(2026, 3, 16, 12, 0, 0, TimeSpan.Zero);

    [GlobalSetup]
    public void Setup()
    {
        _ncrontabThirty = CrontabSchedule.Parse(EveryThirtySecondsNcrontab, IncludingSeconds);
        _ncrontabBusiness = CrontabSchedule.Parse(BusinessHoursNcrontab, IncludingSeconds);
        _quartzThirty = new QuartzCron(EveryThirtySecondsQuartz) { TimeZone = TimeZoneInfo.Utc };
        _quartzBusiness = new QuartzCron(BusinessHoursQuartz) { TimeZone = TimeZoneInfo.Utc };
    }

    // ── Parse ──

    [Benchmark(Description = "NCrontab: parse every 30 s")]
    public CrontabSchedule NCrontab_Parse_Thirty() => CrontabSchedule.Parse(EveryThirtySecondsNcrontab, IncludingSeconds);

    [Benchmark(Description = "Quartz: parse every 30 s")]
    public QuartzCron Quartz_Parse_Thirty() => new(EveryThirtySecondsQuartz);

    [Benchmark(Description = "NCrontab: parse weekday business hours")]
    public CrontabSchedule NCrontab_Parse_Business() => CrontabSchedule.Parse(BusinessHoursNcrontab, IncludingSeconds);

    [Benchmark(Description = "Quartz: parse weekday business hours")]
    public QuartzCron Quartz_Parse_Business() => new(BusinessHoursQuartz);

    // ── Next occurrence ──

    [Benchmark(Description = "NCrontab: next occurrence, every 30 s")]
    public DateTime NCrontab_Next_Thirty() => _ncrontabThirty.GetNextOccurrence(_base);

    [Benchmark(Description = "Quartz: next occurrence, every 30 s")]
    public DateTimeOffset? Quartz_Next_Thirty() => _quartzThirty.GetNextValidTimeAfter(_baseOffset);

    [Benchmark(Description = "NCrontab: next occurrence, weekday business hours")]
    public DateTime NCrontab_Next_Business() => _ncrontabBusiness.GetNextOccurrence(_base);

    [Benchmark(Description = "Quartz: next occurrence, weekday business hours")]
    public DateTimeOffset? Quartz_Next_Business() => _quartzBusiness.GetNextValidTimeAfter(_baseOffset);

    // ── 100 next occurrences in a row ──

    [Benchmark(Description = "NCrontab: 100 next occurrences, every 30 s")]
    public DateTime NCrontab_Next100()
    {
        var current = _base;
        for (var i = 0; i < 100; i++)
            current = _ncrontabThirty.GetNextOccurrence(current);
        return current;
    }

    [Benchmark(Description = "Quartz: 100 next occurrences, every 30 s")]
    public DateTimeOffset Quartz_Next100()
    {
        var current = _baseOffset;
        for (var i = 0; i < 100; i++)
            current = _quartzThirty.GetNextValidTimeAfter(current)!.Value;
        return current;
    }
}
