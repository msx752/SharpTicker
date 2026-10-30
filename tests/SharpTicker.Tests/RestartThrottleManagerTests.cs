namespace SharpTicker.Tests;

public class RestartThrottleManagerTests
{
    // Wide window so assertions keep a large margin even on a loaded machine
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RequestRestart_TriggersCallback_AfterDebounceWindow()
    {
        var triggerCount = 0;
        using var manager = new RestartThrottleManager(() => Interlocked.Increment(ref triggerCount), Window);

        manager.RequestRestart();

        Assert.True(await WaitUntilAsync(() => Volatile.Read(ref triggerCount) == 1));
    }

    [Fact]
    public async Task RequestRestart_DoesNotTrigger_BeforeDebounceWindow()
    {
        var triggerCount = 0;
        using var manager = new RestartThrottleManager(() => Interlocked.Increment(ref triggerCount), Window);

        manager.RequestRestart();
        await Task.Delay(Window / 4);

        Assert.Equal(0, Volatile.Read(ref triggerCount));
    }

    [Fact]
    public async Task MultipleRequests_CoalesceIntoSingleCallback()
    {
        var triggerCount = 0;
        using var manager = new RestartThrottleManager(() => Interlocked.Increment(ref triggerCount), Window);

        // Multiple rapid requests should coalesce
        manager.RequestRestart();
        manager.RequestRestart();
        manager.RequestRestart();

        Assert.True(await WaitUntilAsync(() => Volatile.Read(ref triggerCount) >= 1));
        await Task.Delay(Window / 2); // no late second callback

        Assert.Equal(1, Volatile.Read(ref triggerCount));
    }

    [Fact]
    public async Task RequestRestart_ResetsTimer_OnSubsequentCalls()
    {
        var triggerCount = 0;
        using var manager = new RestartThrottleManager(() => Interlocked.Increment(ref triggerCount), Window);

        manager.RequestRestart();
        await Task.Delay(Window * 0.6);
        manager.RequestRestart(); // Should push the deadline out to a full window from now
        await Task.Delay(Window * 0.6);

        // ~1.2 windows after the first request: without the reset it would have fired by now
        Assert.Equal(0, Volatile.Read(ref triggerCount));

        Assert.True(await WaitUntilAsync(() => Volatile.Read(ref triggerCount) >= 1));
        Assert.Equal(1, Volatile.Read(ref triggerCount));
    }

    [Fact]
    public void Dispose_CanBeCalledSafely()
    {
        var manager = new RestartThrottleManager(() => { });
        var exception = Record.Exception(() => manager.Dispose());

        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_BeforeAnyRequest_DoesNotThrow()
    {
        var manager = new RestartThrottleManager(() => { });

        // Timer hasn't been created yet (lazy creation)
        var exception = Record.Exception(() => manager.Dispose());
        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_AfterRequest_DoesNotThrow()
    {
        var manager = new RestartThrottleManager(() => { });
        manager.RequestRestart();

        var exception = Record.Exception(() => manager.Dispose());
        Assert.Null(exception);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(20);
        }

        return condition();
    }
}
