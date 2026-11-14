using System;
using SharpTicker.Utilities;
using Xunit;

namespace SharpTicker.Tests;

[Collection(SharedStaticStateCollection.Name)]
public class CronScheduleCacheTests
{
    [Fact]
    public void GetNextOccurrenceOrDefault_Returns_Null_For_Invalid_Expression()
    {
        CronScheduleCache.TimeZoneInfo = TimeZoneInfo.Utc;

        var next = CronScheduleCache.GetNextOccurrenceOrDefault("invalid cron", DateTime.UtcNow);

        Assert.Null(next);
    }

    // #194: an expression that parses but matches no date. The cron library throws for it; it has no next occurrence.
    [Theory]
    [InlineData("0 0 0 30 2 *")]
    [InlineData("0 0 0 31 4 *")]
    public void GetNextOccurrenceOrDefault_Returns_Null_For_An_Expression_That_Matches_No_Date(string expression)
    {
        CronScheduleCache.TimeZoneInfo = TimeZoneInfo.Utc;

        var next = CronScheduleCache.GetNextOccurrenceOrDefault(expression, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Null(next);
    }

    [Fact]
    public void GetNextOccurrenceOrDefault_Normalizes_Whitespace_And_Caches()
    {
        CronScheduleCache.TimeZoneInfo = TimeZoneInfo.Utc;
        var now = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var expr1 = "*/5 * * * * *";
        var expr2 = "*/5    *   *   *   *   *";

        var next1 = CronScheduleCache.GetNextOccurrenceOrDefault(expr1, now);
        var next2 = CronScheduleCache.GetNextOccurrenceOrDefault(expr2, now);

        Assert.NotNull(next1);
        Assert.NotNull(next2);
        Assert.Equal(next1, next2);

        var invalidated = CronScheduleCache.Invalidate(expr1);
        Assert.True(invalidated);
    }
}
