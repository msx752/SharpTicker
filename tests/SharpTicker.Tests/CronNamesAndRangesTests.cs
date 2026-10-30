using System;
using System.Collections.Generic;
using System.Linq;
using SharpTicker.Utilities;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// A cron written with month and day names, or with shortened names, has to fire at the same times as the
/// numeric cron it stands for, not only parse. The scheduler reads the times from <see cref="CronScheduleCache"/>.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class CronNamesAndRangesTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public CronNamesAndRangesTests() => CronScheduleCache.TimeZoneInfo = TimeZoneInfo.Utc;

    public void Dispose() => CronScheduleCache.TimeZoneInfo = TimeZoneInfo.Utc;

    /// <summary>The next <paramref name="count"/> times the expression fires after <see cref="Start"/>.</summary>
    private static List<DateTime> Times(string expression, int count)
    {
        // A five-field expression gets its seconds field the way CronExpression.Parse adds it.
        expression = CronExpression.Parse(expression).Value;
        var times = new List<DateTime>();
        var from = Start;
        for (var i = 0; i < count; i++)
        {
            var next = CronScheduleCache.GetNextOccurrenceOrDefault(expression, from);
            Assert.NotNull(next);
            times.Add(next!.Value);
            from = next.Value;
        }
        return times;
    }

    [Theory]
    [InlineData("0 0 9 * * MON-FRI", "0 0 9 * * 1-5")]
    [InlineData("0 0 9 * * mon,wed,fri", "0 0 9 * * 1,3,5")]
    [InlineData("0 0 9 * * SUN", "0 0 9 * * 0")]
    [InlineData("0 0 9 * * SAT", "0 0 9 * * 6")]
    [InlineData("0 0 0 1 JAN,JUL *", "0 0 0 1 1,7 *")]
    [InlineData("0 0 0 1 Jan-Mar *", "0 0 0 1 1-3 *")]
    [InlineData("0 0 0 1 DEC *", "0 0 0 1 12 *")]
    [InlineData("30 9 * * MON-FRI", "0 30 9 * * 1-5")]
    public void ANamedCron_FiresAtTheSameTimesAsItsNumericForm(string named, string numeric)
        => Assert.Equal(Times(numeric, 60), Times(named, 60));

    [Theory]
    [InlineData("0 0 9 * * Monday", "0 0 9 * * 1")]
    [InlineData("0 0 9 * * Mo", "0 0 9 * * 1")]
    [InlineData("0 0 0 1 Sept *", "0 0 0 1 9 *")]
    [InlineData("0 0 0 1 September *", "0 0 0 1 9 *")]
    [InlineData("0 0 9 * * Friday", "0 0 9 * * 5")]
    public void ALongOrShortenedName_FiresAtTheSameTimesAsItsNumber(string named, string numeric)
        => Assert.Equal(Times(numeric, 40), Times(named, 40));

    [Fact]
    public void NamedWeekdays_FireOnlyOnThoseDays()
    {
        var days = Times("0 0 9 * * MON-FRI", 30).Select(t => t.DayOfWeek).Distinct().OrderBy(d => d).ToArray();

        Assert.Equal(new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }, days);
    }

    [Fact]
    public void AStepLargerThanTheField_FiresOnlyAtTheStart()
    {
        var seconds = Times("*/60 * * * * *", 3).Select(t => t.Second).Distinct().ToArray();

        Assert.Equal(new[] { 0 }, seconds);
    }
}
