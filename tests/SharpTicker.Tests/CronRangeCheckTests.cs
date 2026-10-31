using SharpTicker.Utilities;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>Finding a range that runs backwards, so a new cron with one can be refused.</summary>
public class CronRangeCheckTests
{
    [Theory]
    [InlineData("0 0 22-2 * * *")]
    [InlineData("22-2 0 0 * * *")]
    [InlineData("0 30-10 * * * *")]
    [InlineData("0 0 0 25-5 * *")]
    [InlineData("0 0 0 * 11-2 *")]
    [InlineData("0 0 0 * NOV-FEB *")]
    [InlineData("0 0 0 * * 5-1")]
    [InlineData("0 0 0 * * FRI-MON")]
    [InlineData("0 0 0 * * sat-sun")]
    [InlineData("0 0 0 * * Friday-Monday")]
    [InlineData("0 0 0 * * 1,5-2")]
    [InlineData("0 0 0 * * 5-1/2")]
    [InlineData("0 0 22-2 * *")]
    [InlineData("0 22-2 * * MON")]
    public void ABackwardsRange_IsFound(string expression)
        => Assert.True(CronRangeCheck.HasBackwardsRange(expression));

    [Theory]
    [InlineData("0 0 9-17 * * *")]
    [InlineData("0 0 0 * * MON-FRI")]
    [InlineData("0 0 0 * * SUN-SAT")]
    [InlineData("0 0 0 * JAN-DEC *")]
    [InlineData("0 0 0 * * 1-5")]
    [InlineData("0 0 0 * * 3-3")]
    [InlineData("0 0 22-23,0-2 * * *")]
    [InlineData("0 0 0 * * mon,wed,fri")]
    [InlineData("0 */5 * * * *")]
    [InlineData("0 0-30/5 * * * *")]
    [InlineData("0 0 0 1,15 * *")]
    [InlineData("30 8 * * MON-FRI")]
    [InlineData("* * * * * *")]
    public void AForwardRangeOrNoRange_IsNotFound(string expression)
        => Assert.False(CronRangeCheck.HasBackwardsRange(expression));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a cron")]
    [InlineData("%Jobs:Cron%")]
    public void NothingToCheck_IsNotFound(string? expression)
        => Assert.False(CronRangeCheck.HasBackwardsRange(expression!));
}
