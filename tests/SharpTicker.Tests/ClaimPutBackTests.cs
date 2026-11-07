using SharpTicker.Utilities;

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

        var thrown = await Assert.ThrowsAsync<AggregateException>(() => ClaimPutBack.PutBackOrThrowAsync(
            new Dictionary<Guid, DateTime> { [a] = MarkerA, [b] = MarkerB },
            failure,
            (id, _, _) =>
            {
                released.Add(id);
                return id == a ? Task.FromException<bool>(releaseError) : Task.FromResult(true);
            }));

        Assert.Equal([a, b], released);
        Assert.Equal([failure, releaseError], thrown.InnerExceptions);
    }
}
