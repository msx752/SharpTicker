using System;
using SharpTicker.Utilities;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>The state the scheduler and the dashboard share: when the scheduler plans to wake up next.</summary>
public class TickerExecutionContextTests
{
    [Fact]
    public void BeforeAnythingIsPlanned_ThereIsNoNextOccurrence()
    {
        Assert.Null(new TickerExecutionContext().GetNextPlannedOccurrence());
    }

    [Fact]
    public void APlannedOccurrence_IsReadBackAsUtc()
    {
        var context = new TickerExecutionContext();
        var planned = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        context.SetNextPlannedOccurrence(planned);

        Assert.Equal(planned, context.GetNextPlannedOccurrence());
        Assert.Equal(DateTimeKind.Utc, context.GetNextPlannedOccurrence()!.Value.Kind);
    }

    [Fact]
    public void ClearingThePlan_GoesBackToNoNextOccurrence()
    {
        var context = new TickerExecutionContext();
        context.SetNextPlannedOccurrence(DateTime.UtcNow.AddHours(1));

        context.SetNextPlannedOccurrence(null);

        Assert.Null(context.GetNextPlannedOccurrence());
    }
}
