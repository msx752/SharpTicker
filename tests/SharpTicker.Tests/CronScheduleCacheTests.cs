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

    // An expression written in other white space is the same cron: it reads the same cached schedule as its normal form.
    [Theory]
    [InlineData("0 */7 * * * *")]
    [InlineData(" 0 */7 * * * *")]
    [InlineData("0 */7 * * * * ")]
    [InlineData("0  */7 * * * *")]
    [InlineData("0\t*/7 * * * *")]
    [InlineData("0 */7 *\n* * *")]
    [InlineData("0 */7 * *\u00A0* *")]
    [InlineData("0 */7 * * *\u2003*")]
    [InlineData("\r\n0 */7 * * * *\t")]
    public void An_Expression_In_Other_White_Space_Reads_The_Schedule_Of_Its_Normal_Form(string expression)
    {
        Assert.Same(CronScheduleCache.Get("0 */7 * * * *"), CronScheduleCache.Get(expression));
    }

    // The scheduler loop asks for the next occurrence of every cron on each pass. An expression already in its normal form
    // is read from the cache as it is: up to 1.7.0 the white space was replaced in a new string at every call (48-56 B).
    [Theory]
    [InlineData("0 */5 * * * *")]
    [InlineData("0 0 9-17 * * 1-5")]
    [InlineData("*/30 * * * * *")]
    public void The_Next_Occurrence_Of_A_Cached_Expression_In_Normal_Form_Allocates_Nothing(string expression)
    {
        CronScheduleCache.TimeZoneInfo = TimeZoneInfo.Utc;
        var now = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.NotNull(CronScheduleCache.GetNextOccurrenceOrDefault(expression, now));

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
            CronScheduleCache.GetNextOccurrenceOrDefault(expression, now);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    // The shortcut takes an expression as it is when it has no white space but single spaces. That gives the replace's
    // result only if every character the replace (\s) and Trim treat as white space is one char.IsWhiteSpace reports.
    [Fact]
    public void Every_Character_The_Normalization_Treats_As_White_Space_Is_Char_IsWhiteSpace()
    {
        var regex = new System.Text.RegularExpressions.Regex(@"^\s$");
        for (var c = char.MinValue; c < char.MaxValue; c++)
        {
            var s = c.ToString();
            if (regex.IsMatch(s) || s.Trim().Length == 0)
                Assert.True(char.IsWhiteSpace(c), $"U+{(int)c:X4} is white space to the normalization, not to char.IsWhiteSpace");
        }
    }
}
