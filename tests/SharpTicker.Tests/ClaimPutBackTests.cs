using SharpTicker.Utilities;
using SharpTicker.Utilities.Enums;

namespace SharpTicker.Tests;

public class ClaimPutBackTests
{
    private static readonly DateTime MarkerA = new(2025, 6, 15, 12, 0, 0, 1, DateTimeKind.Utc);
    private static readonly DateTime MarkerB = new(2025, 6, 15, 12, 0, 0, 2, DateTimeKind.Utc);

    [Fact]
    public async Task EveryReleaseRuns_ReturnsSoTheCallerRethrows()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var released = new List<(Guid, DateTime, CancellationToken)>();

        await ClaimPutBack.PutBackOrThrowAsync(
            new Dictionary<Guid, DateTime> { [a] = MarkerA, [b] = MarkerB },
            TickerType.TimeTicker,
            new InvalidOperationException("store failed"),
            (id, marker, token) =>
            {
                released.Add((id, marker, token));
                return Task.FromResult(id == a);
            });

        Assert.Equal([(a, MarkerA, CancellationToken.None), (b, MarkerB, CancellationToken.None)], released);
    }

    [Fact]
    public async Task AReleaseThrows_TriesTheRestAndThrowsTheFailureFirstThenTheReleaseError()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var failure = new InvalidOperationException("store failed");
        var releaseError = new TimeoutException("release failed");
        var released = new List<Guid>();

        var thrown = await Assert.ThrowsAsync<ClaimPutBackFailedException>(() => ClaimPutBack.PutBackOrThrowAsync(
            new Dictionary<Guid, DateTime> { [a] = MarkerA, [b] = MarkerB },
            TickerType.TimeTicker,
            failure,
            (id, _, _) =>
            {
                released.Add(id);
                return id == a ? Task.FromException<bool>(releaseError) : Task.FromResult(true);
            }));

        Assert.Equal([a, b], released);
        Assert.Equal([failure, releaseError], thrown.InnerExceptions);
    }

    // The rows whose release threw are still InProgress for this node: the exception names each one with its marker and
    // type, so it can be put back later. A row whose release ran (put back, or moved on) is not listed.
    [Theory]
    [InlineData(TickerType.TimeTicker)]
    [InlineData(TickerType.CronTickerOccurrence)]
    public async Task AReleaseThrows_TheExceptionListsOnlyTheRowsNotPutBack(TickerType type)
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        var thrown = await Assert.ThrowsAsync<ClaimPutBackFailedException>(() => ClaimPutBack.PutBackOrThrowAsync(
            new Dictionary<Guid, DateTime> { [a] = MarkerA, [b] = MarkerB, [c] = MarkerA },
            type,
            new InvalidOperationException("store failed"),
            (id, _, _) => id == b ? Task.FromResult(false) : id == a ? Task.FromException<bool>(new TimeoutException()) : Task.FromResult(true)));

        Assert.Equal([new NotPutBackClaim(a, MarkerA, type)], thrown.NotPutBack);
    }

    [Fact]
    public void FindIn_FindsTheFailureItselfOrWrappedInAnotherOrInAnAggregate()
    {
        var first = new ClaimPutBackFailedException(1, [new InvalidOperationException()], [new NotPutBackClaim(Guid.NewGuid(), MarkerA, TickerType.TimeTicker)]);
        var second = new ClaimPutBackFailedException(1, [new InvalidOperationException()], [new NotPutBackClaim(Guid.NewGuid(), MarkerB, TickerType.CronTickerOccurrence)]);

        Assert.Equal([first], ClaimPutBackFailedException.FindIn(first));
        Assert.Equal([first], ClaimPutBackFailedException.FindIn(new InvalidOperationException("wrapped", new Exception("again", first))));
        Assert.Equal([first, second], ClaimPutBackFailedException.FindIn(new AggregateException(first, new Exception("x", second))));
        Assert.Empty(ClaimPutBackFailedException.FindIn(new InvalidOperationException("no put-back failed")));
        Assert.Empty(ClaimPutBackFailedException.FindIn(null));
    }

    [Fact]
    public void RetryList_TakesTheRowsOfEveryFailureFound_ThenIsEmpty()
    {
        var row = new NotPutBackClaim(Guid.NewGuid(), MarkerA, TickerType.TimeTicker);
        var list = new ClaimPutBackRetryList();

        Assert.Equal(1, list.AddFrom(new Exception("wrapped", new ClaimPutBackFailedException(1, [new InvalidOperationException()], [row]))));
        Assert.Equal(0, list.AddFrom(new InvalidOperationException()));
        Assert.False(list.IsEmpty);
        Assert.Equal([row], list.TakeAll());
        Assert.True(list.IsEmpty);
        Assert.Empty(list.TakeAll());
    }
}
