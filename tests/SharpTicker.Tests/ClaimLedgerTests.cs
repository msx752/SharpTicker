using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpTicker.BackgroundServices;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Models;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// The ledger holds the claims this node won and has not started. A claim is either started by its worker or put back,
/// never both: putting back a job that also starts would let the fallback run it a second time.
/// </summary>
public class ClaimLedgerTests
{
    private static readonly DateTime Marker = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private static InternalFunctionContext Function() => new() { TickerId = Guid.NewGuid(), Type = TickerType.TimeTicker, FunctionName = "fn" };

    [Fact]
    public void AStartedClaim_CannotBePutBack_AndLeavesTheLedger()
    {
        var ledger = new ClaimLedger();
        var entry = ledger.Record(Function(), Marker);

        Assert.True(entry.TryStart());

        Assert.False(entry.TryBeginRevert());
        Assert.Empty(ledger.Pending());
    }

    [Fact]
    public void AClaimBeingPutBack_CannotStart()
    {
        var ledger = new ClaimLedger();
        var entry = ledger.Record(Function(), Marker);

        Assert.True(entry.TryBeginRevert());

        Assert.False(entry.TryStart());
        Assert.Empty(ledger.Pending());
    }

    [Fact]
    public void APutBackThatSucceeded_LeavesTheLedger()
    {
        var ledger = new ClaimLedger();
        var entry = ledger.Record(Function(), Marker);
        Assert.True(entry.TryBeginRevert());

        entry.RevertSucceeded();

        Assert.Empty(ledger.Pending());
        Assert.Equal(0, ledger.Count);
    }

    // A put-back whose call failed may still have landed (only its reply was lost): the row can be Idle and the fallback may
    // run the job. The claim is therefore never started afterwards; it stays pending until a put-back reaches the store.
    [Fact]
    public void APutBackThatFailed_StaysPending_ButNeverStarts()
    {
        var ledger = new ClaimLedger();
        var entry = ledger.Record(Function(), Marker);
        Assert.True(entry.TryBeginRevert());

        entry.RevertFailed();

        Assert.Same(entry, Assert.Single(ledger.Pending()));
        Assert.False(entry.TryStart());
        Assert.Same(entry, Assert.Single(ledger.PendingPutBacks()));
        Assert.True(entry.TryBeginRevert());
        entry.RevertSucceeded();
        Assert.Empty(ledger.Pending());
    }

    // Only a claim whose put-back failed is tried again by the loop; one that waits in the queue for its worker is not.
    [Fact]
    public void PendingPutBacks_HoldsOnlyClaimsWhosePutBackFailed()
    {
        var ledger = new ClaimLedger();
        var waiting = ledger.Record(Function(), Marker);
        var failed = ledger.Record(Function(), Marker);
        Assert.True(failed.TryBeginRevert());
        failed.RevertFailed();

        Assert.Same(failed, Assert.Single(ledger.PendingPutBacks()));
        Assert.Equal(2, ledger.Pending().Count);
        Assert.False(waiting.NeedsPutBack);
    }

    [Fact]
    public void ALostClaim_IsForgotten()
    {
        var ledger = new ClaimLedger();
        var entry = ledger.Record(Function(), Marker);

        entry.Forget();

        Assert.Empty(ledger.Pending());
        Assert.False(entry.TryStart());
        Assert.False(entry.TryBeginRevert());
    }

    [Fact]
    public void TheEntry_CarriesItsFunctionAndMarker()
    {
        var ledger = new ClaimLedger();
        var function = Function();

        var entry = ledger.Record(function, Marker);

        Assert.Same(function, entry.Function);
        Assert.Equal(Marker, entry.ClaimMarker);
    }

    [Fact]
    public async Task StartAndPutBack_Racing_HaveExactlyOneWinnerEveryTime()
    {
        var ledger = new ClaimLedger();

        for (var round = 0; round < 2000; round++)
        {
            var entry = ledger.Record(Function(), Marker);
            using var go = new ManualResetEventSlim();
            var start = Task.Run(() => { go.Wait(); return entry.TryStart(); });
            var revert = Task.Run(() => { go.Wait(); return entry.TryBeginRevert(); });
            go.Set();

            var results = await Task.WhenAll(start, revert);

            Assert.Equal(1, results.Count(won => won));
            if (results[1])
                entry.RevertSucceeded();
        }

        Assert.Equal(0, ledger.Count);
    }
}
