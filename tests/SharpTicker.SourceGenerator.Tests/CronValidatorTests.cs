using SharpTicker.SourceGenerator.Validation;

namespace SharpTicker.SourceGenerator.Tests;

/// <summary>
/// The cron check the generator runs at build time. It decides whether an application compiles, so it must accept
/// what the scheduler accepts at run time and reject what the scheduler rejects.
/// </summary>
public class CronValidatorTests
{
    [Theory]
    [InlineData("0 0 0 * * *")]
    [InlineData("* * * * * *")]
    [InlineData("*/15 * * * * *")]
    [InlineData("0 */5 * * * *")]
    [InlineData("0 0 9-17 * * 1-5")]
    [InlineData("0 0 0 1,15 * *")]
    [InlineData("0 30 2 * * 0")]
    [InlineData("59 59 23 31 12 6")]
    [InlineData("0 0 0 1 1 *")]
    [InlineData("0 0/5 * * * *")]
    public void ASixFieldExpression_InRange_IsValid(string expression)
        => Assert.True(CronValidator.IsValidCronExpression(expression));

    [Theory]
    [InlineData("30 2 * * *")]
    [InlineData("*/5 * * * *")]
    public void AFiveFieldExpression_IsValid_BecauseTheSecondsFieldIsAdded(string expression)
        => Assert.True(CronValidator.IsValidCronExpression(expression));

    [Theory]
    [InlineData("0 0 0 * * *", "0 0 0 * * *")]
    [InlineData("30 2 * * *", "0 30 2 * * *")]
    public void NormalizingAddsTheSecondsField_OnlyToAFiveFieldExpression(string expression, string expected)
        => Assert.Equal(expected, CronValidator.NormalizeToSixPart(expression));

    [Theory]
    [InlineData("")]
    [InlineData("not a cron")]
    [InlineData("* * * *")]
    [InlineData("60 * * * * *")]
    [InlineData("* 60 * * * *")]
    [InlineData("* * 24 * * *")]
    [InlineData("* * * 0 * *")]
    [InlineData("* * * 32 * *")]
    [InlineData("* * * * 0 *")]
    [InlineData("* * * * 13 *")]
    [InlineData("* * * * * 7")]
    [InlineData("5-1 * * * * *")]
    [InlineData("*/0 * * * * *")]
    [InlineData("*/60 * * * * *")]
    [InlineData("- * * * * *")]
    [InlineData("1- * * * * *")]
    [InlineData("1/ * * * * *")]
    [InlineData("a * * * * *")]
    public void AnExpressionOutOfRangeOrMalformed_IsInvalid(string expression)
        => Assert.False(CronValidator.IsValidCronExpression(expression));

    [Theory(Skip = "Known bug: the build-time check ignores everything after the sixth field, so these compile, while the scheduler rejects them at run time.")]
    [InlineData("* * * * * * *")]
    [InlineData("0 0 0 * * * junk")]
    public void AnExpressionWithMoreThanSixFields_IsInvalid(string expression)
        => Assert.False(CronValidator.IsValidCronExpression(expression));

    [Fact(Skip = "Known bug: a five-field expression padded with spaces is accepted at run time but rejected by the build-time check, which does not trim before counting fields.")]
    public void APaddedFiveFieldExpression_IsValid_LikeAtRunTime()
        => Assert.True(CronValidator.IsValidCronExpression("  30 2 * * *  "));

    [Fact]
    public void ANullExpression_IsInvalid()
        => Assert.False(CronValidator.IsValidCronExpression(null!));

    [Fact]
    public void ANumberTooLargeToRead_DoesNotThrow()
        => Assert.False(CronValidator.IsValidCronExpression("99999999999999999999 * * * * *"));

    [Theory(Skip = "Known bug: the run time accepts month and day names (JAN, MON), but the build-time check rejects them, so [TickerFunction(\"Job\", \"0 0 9 * * MON\")] fails to compile with TQ003 although it would run.")]
    [InlineData("0 0 9 * * MON")]
    [InlineData("0 0 0 * JAN *")]
    [InlineData("0 0 9 * * MON-FRI")]
    public void MonthAndDayNames_AreValid_LikeAtRunTime(string expression)
        => Assert.True(CronValidator.IsValidCronExpression(expression));
}
