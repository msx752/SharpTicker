using System;
using System.Runtime.CompilerServices;
using System.Threading;
using SharpTicker.Utilities.Interfaces;

namespace SharpTicker.Utilities;

/// <summary>
/// The value a claim writes into UpdatedAt when it moves a row to InProgress. A claim that has to be put back, or whose
/// call failed after the write may have landed, is found again by this value, so two claims of one node must never write
/// the same one: every marker is the clock truncated to whole milliseconds (the finest precision every store keeps) and
/// strictly later than the previous marker of the same clock. Another node writes its own lock holder, so markers only
/// need to be unique per node; a node's provider and manager share its clock, and therefore its markers.
/// </summary>
internal static class ClaimMarkers
{
    private sealed class LastMarker
    {
        public long Ticks = long.MinValue;
    }

    private static readonly ConditionalWeakTable<ITickerClock, LastMarker> Last = new();

    internal static DateTime NextClaimMarker(this ITickerClock clock)
    {
        if (clock == null)
            throw new ArgumentNullException(nameof(clock));

        var last = Last.GetValue(clock, _ => new LastMarker());
        var now = clock.UtcNow;
        var truncated = now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond;

        while (true)
        {
            var previous = Interlocked.Read(ref last.Ticks);
            var next = previous == long.MinValue || truncated > previous
                ? truncated
                : previous + TimeSpan.TicksPerMillisecond;

            if (Interlocked.CompareExchange(ref last.Ticks, next, previous) == previous)
                return new DateTime(next, DateTimeKind.Utc);
        }
    }
}
