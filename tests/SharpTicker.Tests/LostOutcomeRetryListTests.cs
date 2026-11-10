using System;
using System.Linq;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Models;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>The list of outcomes whose final write threw (#74 PR-C), and the copy it keeps of each.</summary>
public class LostOutcomeRetryListTests
{
    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private static InternalFunctionContext Outcome(Guid? id = null, DateTime? marker = null, TickerStatus status = TickerStatus.Done)
    {
        var context = new InternalFunctionContext
        {
            TickerId = id ?? Guid.NewGuid(),
            FunctionName = "lost-fn",
            Type = TickerType.TimeTicker,
            RunMarker = marker ?? T0
        };
        context.SetProperty(x => x.Status, status)
            .SetProperty(x => x.ExecutedAt, T0.AddSeconds(5))
            .SetProperty(x => x.ElapsedTime, 42L);
        return context;
    }

    [Fact]
    public void TryAdd_ListsACopy_ThatLaterChangesToTheRunsContextDoNotReach()
    {
        var list = new LostOutcomeRetryList();
        var outcome = Outcome();

        Assert.True(list.TryAdd(outcome, isChild: false, T0));
        outcome.SetProperty(x => x.Status, TickerStatus.Failed).SetProperty(x => x.ReleaseLock, true);
        outcome.RunMarker = T0.AddDays(1);

        var listed = Assert.Single(list.Snapshot());
        Assert.NotSame(outcome, listed.Context);
        Assert.Equal(TickerStatus.Done, listed.Context.Status);
        Assert.Equal(T0, listed.Context.RunMarker);
        Assert.Equal(T0, listed.RunMarker);
        Assert.Equal(new[] { "ElapsedTime", "ExecutedAt", "Status" }, listed.Context.GetPropsToUpdate().OrderBy(p => p));
    }

    [Fact]
    public void TryAdd_RefusesAnOutcomeWithoutARunMarker()
    {
        var outcome = Outcome();
        outcome.RunMarker = null;

        Assert.Throws<ArgumentException>(() => new LostOutcomeRetryList().TryAdd(outcome, isChild: false, T0));
    }

    [Fact]
    public void TryAdd_ReplacesTheListedOutcomeOfTheSameTicker_AndKeepsTheOthers()
    {
        var list = new LostOutcomeRetryList();
        var id = Guid.NewGuid();
        list.TryAdd(Outcome(id, T0), isChild: false, T0);
        var other = Outcome();
        list.TryAdd(other, isChild: false, T0);
        list.TryAdd(Outcome(id, T0.AddMinutes(1), TickerStatus.Failed), isChild: false, T0.AddMinutes(1));

        var listed = list.Snapshot();
        Assert.Equal(2, listed.Count);
        var replaced = listed.Single(o => o.Context.TickerId == id);
        Assert.Equal(T0.AddMinutes(1), replaced.RunMarker);
        Assert.Equal(TickerStatus.Failed, replaced.Context.Status);
        // Oldest first: the other ticker was listed before the replacement.
        Assert.Equal(new[] { other.TickerId, id }, listed.Select(o => o.Context.TickerId));
    }

    [Fact]
    public void TryRemove_RemovesOnlyThatEntry_AndOnlyOnce()
    {
        var list = new LostOutcomeRetryList();
        var id = Guid.NewGuid();
        list.TryAdd(Outcome(id, T0), isChild: false, T0);
        var first = Assert.Single(list.Snapshot());
        list.TryAdd(Outcome(id, T0.AddMinutes(1)), isChild: false, T0);
        var second = Assert.Single(list.Snapshot());

        Assert.False(list.TryRemove(first));   // replaced: the newer run's entry stays
        Assert.Same(second, Assert.Single(list.Snapshot()));
        Assert.True(list.TryRemove(second));
        Assert.False(list.TryRemove(second));  // a second caller does not act on it again
        Assert.True(list.IsEmpty);
    }

    [Fact]
    public void Close_ReturnsWhatIsListed_RefusesLaterAdds_AndOpenAcceptsThemAgain()
    {
        var list = new LostOutcomeRetryList();
        var before = Outcome();
        list.TryAdd(before, isChild: false, T0);

        var atClose = list.Close();

        Assert.Equal(before.TickerId, Assert.Single(atClose).Context.TickerId);
        Assert.False(list.TryAdd(Outcome(), isChild: false, T0));
        Assert.Single(list.Snapshot()); // still listed until removed

        list.Open();
        Assert.True(list.TryAdd(Outcome(), isChild: false, T0));
        Assert.Equal(2, list.Snapshot().Count);
    }

    [Fact]
    public void TryAdd_RaisesAdded_AndIgnoresAHandlerThatThrows()
    {
        var list = new LostOutcomeRetryList();
        var raised = 0;
        list.Added += () => raised++;
        list.Added += () => throw new InvalidOperationException("handler");

        Assert.True(list.TryAdd(Outcome(), isChild: false, T0));
        Assert.Equal(1, raised);

        list.Close();
        Assert.False(list.TryAdd(Outcome(), isChild: false, T0));
        Assert.Equal(1, raised); // nothing listed, nothing raised
    }

    [Fact]
    public void FailedAttempts_Counts()
    {
        var list = new LostOutcomeRetryList();
        list.TryAdd(Outcome(), isChild: true, T0);
        var entry = Assert.Single(list.Snapshot());

        Assert.True(entry.IsChild);
        Assert.Equal(0, entry.FailedAttempts);
        Assert.Equal(1, entry.RecordFailedAttempt());
        Assert.Equal(2, entry.RecordFailedAttempt());
        Assert.Equal(2, entry.FailedAttempts);
    }
}
