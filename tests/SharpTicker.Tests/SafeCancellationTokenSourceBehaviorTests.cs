using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// The cancellation source the scheduler loop and the restart logic use. Cancelling it, also after a delay, works, and
/// cancelling it after it was disposed is ignored instead of throwing, because a restart can race with shutdown.
/// </summary>
public class SafeCancellationTokenSourceBehaviorTests
{
    [Fact]
    public void ANewSource_IsNeitherCancelledNorDisposed()
    {
        using var source = new SafeCancellationTokenSource();

        Assert.False(source.IsCancellationRequested);
        Assert.False(source.Token.IsCancellationRequested);
        Assert.False(source.IsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellingWithEitherExceptionMode_CancelsTheToken(bool throwOnFirstException)
    {
        using var source = new SafeCancellationTokenSource();

        source.Cancel(throwOnFirstException);

        Assert.True(source.IsCancellationRequested);
        Assert.True(source.Token.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACallbackThatThrows_IsNotHidden_ButTheSourceIsStillCancelled(bool throwOnFirstException)
    {
        using var source = new SafeCancellationTokenSource();
        source.Token.Register(() => throw new InvalidOperationException("callback failed"));

        Assert.ThrowsAny<Exception>(() => source.Cancel(throwOnFirstException));

        Assert.True(source.IsCancellationRequested);
    }

    [Fact]
    public async Task CancelAfterATimeSpan_CancelsOnceTheDelayHasPassed()
    {
        using var source = new SafeCancellationTokenSource();
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Token.Register(() => cancelled.TrySetResult(true));

        source.CancelAfter(TimeSpan.FromMilliseconds(100));
        Assert.False(source.IsCancellationRequested);

        Assert.True(await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(source.IsCancellationRequested);
    }

    [Fact]
    public async Task CancelAfterMilliseconds_CancelsOnceTheDelayHasPassed()
    {
        using var source = new SafeCancellationTokenSource();
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Token.Register(() => cancelled.TrySetResult(true));

        source.CancelAfter(100);

        Assert.True(await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void CancellingWithAnExceptionMode_AfterDispose_IsIgnored()
    {
        var source = new SafeCancellationTokenSource();
        source.Dispose();

        source.Cancel(true);
        source.Cancel(false);

        Assert.True(source.IsDisposed);
    }

    [Fact]
    public void ALinkedSource_CancelsWhenAnyOfItsParentsCancels_AndDisposingItDoesNotCancelThem()
    {
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        var linked = SafeCancellationTokenSource.CreateLinked(first.Token, second.Token);

        second.Cancel();
        Assert.True(linked.IsCancellationRequested);

        linked.Dispose();
        Assert.False(first.IsCancellationRequested);
    }

    [Fact]
    public async Task CancellingAndDisposingAtTheSameTime_NeverThrows()
    {
        for (var round = 0; round < 200; round++)
        {
            var source = new SafeCancellationTokenSource();
            var cancel = Task.Run(() => { source.Cancel(false); source.CancelAfter(1); source.CancelAfter(TimeSpan.FromMilliseconds(1)); });
            var dispose = Task.Run(source.Dispose);

            await Task.WhenAll(cancel, dispose).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
