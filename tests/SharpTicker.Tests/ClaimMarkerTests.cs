using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Interfaces;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// The claim marker is the UpdatedAt value a claim writes. A claim that must be put back later is matched on it, so two
/// claims of one node must never write the same value, on every store, including those that keep only milliseconds.
/// </summary>
public class ClaimMarkerTests
{
    private sealed class FixedClock(DateTime now) : ITickerClock
    {
        public DateTime UtcNow { get; set; } = now;
    }

    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Marker_IsTheClockTruncatedToWholeMilliseconds()
    {
        var clock = new FixedClock(T0.AddTicks(1234567));

        var marker = clock.NextClaimMarker();

        Assert.Equal(T0.AddMilliseconds(123), marker);
        Assert.Equal(DateTimeKind.Utc, marker.Kind);
    }

    [Fact]
    public void Markers_OfOneClock_StrictlyIncrease_WhenTheClockDoesNotMove()
    {
        var clock = new FixedClock(T0);

        var first = clock.NextClaimMarker();
        var second = clock.NextClaimMarker();
        var third = clock.NextClaimMarker();

        Assert.Equal(T0, first);
        Assert.Equal(T0.AddMilliseconds(1), second);
        Assert.Equal(T0.AddMilliseconds(2), third);
    }

    // A clock that goes back (an NTP step) must not hand out a marker it already gave.
    [Fact]
    public void Markers_OfOneClock_StillIncrease_WhenTheClockGoesBack()
    {
        var clock = new FixedClock(T0.AddSeconds(5));
        var before = clock.NextClaimMarker();

        clock.UtcNow = T0;
        var after = clock.NextClaimMarker();

        Assert.True(after > before);
    }

    [Fact]
    public void Markers_FollowTheClock_WhenItMovesAhead()
    {
        var clock = new FixedClock(T0);
        clock.NextClaimMarker();

        clock.UtcNow = T0.AddSeconds(3);

        Assert.Equal(T0.AddSeconds(3), clock.NextClaimMarker());
    }

    // Each node of a multi-node test has its own clock; one node's markers must not move another's.
    [Fact]
    public void Markers_OfDifferentClocks_AreIndependent()
    {
        var nodeA = new FixedClock(T0);
        var nodeB = new FixedClock(T0);

        nodeA.NextClaimMarker();
        nodeA.NextClaimMarker();

        Assert.Equal(T0, nodeB.NextClaimMarker());
    }

    [Fact]
    public async Task Markers_AreUnique_UnderConcurrentClaims()
    {
        var clock = new FixedClock(T0);
        var seen = new ConcurrentBag<DateTime>();

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
                seen.Add(clock.NextClaimMarker());
        })));

        Assert.Equal(16 * 500, seen.Distinct().Count());
    }
}
