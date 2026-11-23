using SharpTicker.EntityFrameworkCore.Configurations;

namespace SharpTicker.EntityFrameworkCore.Tests.Infrastructure;

/// <summary>
/// #257: the read side of the store's DateTime converter. A provider returns Unspecified (SQLite, SQL Server, MySQL), Utc
/// (Npgsql on timestamptz) or Local (Npgsql with its legacy timestamp behaviour on timestamptz); each comes out as the
/// same instant with <see cref="DateTimeKind.Utc"/>. The write side passes the value on unchanged.
/// </summary>
public class UtcDateTimeConversionTests
{
    private static readonly DateTime Utc = new(2026, 6, 15, 6, 30, 15, 123, DateTimeKind.Utc);

    private static DateTime Read(DateTime fromProvider) => (DateTime)UtcDateTimeConversion.UtcOnRead.ConvertFromProvider(fromProvider)!;

    [Fact]
    public void AnUnspecifiedValue_KeepsItsTicks_AndBecomesUtc()
    {
        var read = Read(DateTime.SpecifyKind(Utc, DateTimeKind.Unspecified));

        Assert.Equal(DateTimeKind.Utc, read.Kind);
        Assert.Equal(Utc.Ticks, read.Ticks);
    }

    [Fact]
    public void AUtcValue_IsKept()
    {
        var read = Read(Utc);

        Assert.Equal(DateTimeKind.Utc, read.Kind);
        Assert.Equal(Utc.Ticks, read.Ticks);
    }

    [Fact]
    public void ALocalValue_IsTheSameInstantInUtc()
    {
        var local = Utc.ToLocalTime();

        var read = Read(local);

        Assert.Equal(DateTimeKind.Utc, read.Kind);
        Assert.True(read.Ticks == Utc.Ticks, $"a Local {local:O} reads as {read:O}, the instant is {Utc:O}");
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void TheWriteSide_PassesTheValueOnUnchanged(DateTimeKind kind)
    {
        var value = DateTime.SpecifyKind(Utc, kind);

        var written = (DateTime)UtcDateTimeConversion.UtcOnRead.ConvertToProvider(value)!;

        Assert.Equal(value.Ticks, written.Ticks);
        Assert.Equal(kind, written.Kind);
    }
}
