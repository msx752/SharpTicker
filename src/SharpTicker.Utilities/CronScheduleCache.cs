namespace SharpTicker.Utilities;

using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using NCrontab;

internal static partial class CronScheduleCache
{
    public static TimeZoneInfo TimeZoneInfo { get; internal set; } = TimeZoneInfo.Local;
    
    private static readonly ConcurrentDictionary<string, CrontabSchedule> Cache = new(StringComparer.Ordinal);

    private static readonly CrontabSchedule.ParseOptions Opts = new()
    {
        IncludingSeconds = true
    };

    private static string Normalize(string expr)
    {
        ArgumentNullException.ThrowIfNull(expr);

        // The scheduler loop asks for every cron on each pass, nearly always with an expression already in this form; the
        // replace below would build a new string for it every time.
        if (IsNormalized(expr))
            return expr;

        return ReplaceRegex().Replace(expr.Trim(), " ");
    }

    // True when the replace would give the expression back unchanged: no white space at either end and none between the
    // fields but single spaces. char.IsWhiteSpace is the set that Trim removes and that \s matches.
    private static bool IsNormalized(string expr)
    {
        if (expr.Length == 0)
            return true;

        if (char.IsWhiteSpace(expr[0]) || char.IsWhiteSpace(expr[^1]))
            return false;

        for (var i = 1; i < expr.Length; i++)
        {
            var c = expr[i];
            if (c == ' ')
            {
                if (expr[i - 1] == ' ')
                    return false;
            }
            else if (char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return true;
    }

    public static CrontabSchedule Get(string expression)
    {
        var key = Normalize(expression);

        return Cache.GetOrAdd(key, exp => CrontabSchedule.TryParse(exp, Opts));
    }

    public static DateTime? GetNextOccurrenceOrDefault(string expression, DateTime dateTime)
    {
        var parsed = Get(expression);

        if (parsed == null)
            return null;

        // Read the configured zone once: if it is replaced while this call runs, mixing two zones
        // would make ConvertTimeToUtc throw on a DateTimeKind mismatch, which the DST-gap
        // handling below would misread and turn into a null result
        var timeZone = TimeZoneInfo;

        var localTime = TimeZoneInfo.ConvertTimeFromUtc(dateTime, timeZone);

        DateTime nextOccurrence;
        try
        {
            nextOccurrence = parsed.GetNextOccurrence(localTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            // An expression that parses but matches no date (the 30th of February): the cron library runs past the last
            // year it can search and throws. Such an expression has no next occurrence (#194).
            return null;
        }

        try
        {
            var utcDateTime = TimeZoneInfo.ConvertTimeToUtc(nextOccurrence, timeZone);
            return utcDateTime;
        }
        catch (ArgumentException)
        {
            // DST gap: the local time produced by NCrontab doesn't exist
            // (e.g., spring-forward skips 2:00→3:00). Advance past the gap
            // by finding the next valid UTC instant after the invalid time,
            // then ask NCrontab for the next occurrence from that point.
            // Loop handles the (unlikely) case of consecutive gaps.
            var candidate = nextOccurrence;
            for (var i = 0; i < 24; i++)
            {
                candidate = candidate.AddHours(1);
                try
                {
                    var utc = TimeZoneInfo.ConvertTimeToUtc(candidate, timeZone);
                    // candidate is valid — get the real next occurrence from here
                    var local = TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone);
                    var retryOccurrence = parsed.GetNextOccurrence(local);
                    return TimeZoneInfo.ConvertTimeToUtc(retryOccurrence, timeZone);
                }
                catch (ArgumentException)
                {
                    // Still in a gap — keep advancing
                }
            }

            // Exhausted retries — should never happen in practice
            return null;
        }
    }

    public static bool Invalidate(string expression) =>
        Cache.TryRemove(Normalize(expression), out _);
    
    [GeneratedRegex(@"\s+")]
    private static partial Regex ReplaceRegex();
}