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
    [InlineData("  30 2 * * *  ", "0 30 2 * * *")]
    [InlineData("30  2 * * *", "0 30  2 * * *")]
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
    [InlineData("*/0 * * * * *")]
    [InlineData("- * * * * *")]
    [InlineData("1- * * * * *")]
    [InlineData("1/ * * * * *")]
    [InlineData("a * * * * *")]
    public void AnExpressionOutOfRangeOrMalformed_IsInvalid(string expression)
        => Assert.False(CronValidator.IsValidCronExpression(expression));

    [Theory]
    [InlineData("* * * * * * *")]
    [InlineData("0 0 0 * * * junk")]
    public void AnExpressionWithMoreThanSixFields_IsInvalid(string expression)
        => Assert.False(CronValidator.IsValidCronExpression(expression));

    [Fact]
    public void APaddedFiveFieldExpression_IsValid_LikeAtRunTime()
        => Assert.True(CronValidator.IsValidCronExpression("  30 2 * * *  "));

    [Theory]
    [InlineData("*/60 * * * * *")]
    [InlineData("0 0 0 * * 0/24")]
    public void ALargeStep_IsValid_LikeAtRunTime(string expression)
        => Assert.True(CronValidator.IsValidCronExpression(expression));

    // A backwards range is not supported.
    [Theory]
    [InlineData("5-1 * * * * *")]
    [InlineData("22-2 0 0 * * *")]
    [InlineData("0 0 22-2 * * *")]
    [InlineData("0 0 0 25-5 * *")]
    [InlineData("0 0 0 * 11-2 *")]
    [InlineData("0 0 0 * NOV-FEB *")]
    [InlineData("0 0 0 * * 5-1")]
    [InlineData("0 0 0 * * FRI-MON")]
    [InlineData("0 0 0 * * Friday-Monday")]
    public void ABackwardsRange_IsInvalid(string expression)
        => Assert.False(CronValidator.IsValidCronExpression(expression));

    [Theory]
    [InlineData("1, * * * * *")]
    [InlineData(",1 * * * * *")]
    [InlineData("1,,2 * * * * *")]
    [InlineData("0 0 0 * * 1,")]
    [InlineData("*-5 * * * * *")]
    [InlineData("1-3-5 * * * * *")]
    [InlineData("*/2/3 * * * * *")]
    [InlineData("1-* * * * * *")]
    public void AnEmptyListItemOrAMalformedRange_IsInvalid(string expression)
        => Assert.False(CronValidator.IsValidCronExpression(expression));

    [Theory]
    [InlineData("0 0 0 * MON *")]
    [InlineData("0 0 0 * * JAN")]
    [InlineData("0 0 0 * * MONDAYS")]
    [InlineData("0 0 0 * * MONXYZ")]
    [InlineData("0 0 0 * JANUARYS *")]
    [InlineData("0 0 9 JAN * *")]
    [InlineData("MON * * * * *")]
    public void ANameInTheWrongFieldOrUnknown_IsInvalid(string expression)
        => Assert.False(CronValidator.IsValidCronExpression(expression));

    [Theory]
    [InlineData("0  0  0  *  *  *")]
    [InlineData(" 0 0 0 * * * ")]
    public void ExtraSpacesBetweenOrAroundFields_AreIgnored(string expression)
        => Assert.True(CronValidator.IsValidCronExpression(expression));

    [Fact]
    public void ANullExpression_IsInvalid()
        => Assert.False(CronValidator.IsValidCronExpression(null!));

    [Fact]
    public void ANumberTooLargeToRead_DoesNotThrow()
        => Assert.False(CronValidator.IsValidCronExpression("99999999999999999999 * * * * *"));

    [Theory]
    [InlineData("0 0 9 * * MON")]
    [InlineData("0 0 0 * JAN *")]
    [InlineData("0 0 9 * * MON-FRI")]
    [InlineData("0 0 9 * * mon,wed,fri")]
    [InlineData("0 0 9 * * Monday")]
    [InlineData("0 0 0 * September *")]
    [InlineData("0 0 0 * Sept *")]
    [InlineData("0 0 9 * * Mo")]
    [InlineData("0 0 0 1 Jan,Jul *")]
    [InlineData("0 0 0 * JAN-MAR *")]
    [InlineData("0 0 0 * * SUN")]
    [InlineData("0 0 0 * DEC SAT")]
    [InlineData("30 9 * * MON")]
    public void MonthAndDayNames_AreValid_LikeAtRunTime(string expression)
        => Assert.True(CronValidator.IsValidCronExpression(expression));
}
