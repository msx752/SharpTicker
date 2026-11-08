using System;
using System.Threading;
using System.Threading.Tasks;
using SharpTicker.SharpTickerThreadPool;
using SharpTicker.Utilities.Enums;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// Which thread a job starts on. A job runs up to its first await on one of the scheduler's own worker threads, so a job
/// starts even when the .NET thread pool has no free thread; what comes after the first await runs on the .NET thread pool.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class SharpTickerTaskSchedulerThreadTests
{
    private sealed record ThreadSeen(string? Name, bool IsThreadPoolThread, bool IsWorkerThread);

    private static ThreadSeen Current()
        => new(Thread.CurrentThread.Name, Thread.CurrentThread.IsThreadPoolThread, SharpTickerTaskScheduler.IsSharpTickerWorkerThread);

    private static async Task<T> Within<T>(Task<T> task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))));
        return await task;
    }

    [Fact]
    public async Task AJob_StartsOnAWorkerThreadOfTheScheduler_NotOnTheThreadPool()
    {
        await using var scheduler = new SharpTickerTaskScheduler(2);
        var seen = new TaskCompletionSource<ThreadSeen>(TaskCreationOptions.RunContinuationsAsynchronously);

        await scheduler.QueueAsync(_ => { seen.TrySetResult(Current()); return Task.CompletedTask; }, TickerTaskPriority.Normal);

        var thread = await Within(seen.Task);
        Assert.False(thread.IsThreadPoolThread);
        Assert.True(thread.IsWorkerThread);
        Assert.StartsWith("SharpTicker.Worker-", thread.Name);
    }

    // The scheduler installs no synchronization context, so a job's await continues on the .NET thread pool, not on a worker.
    [Fact]
    public async Task AfterItsFirstAwait_AJobContinuesOnTheThreadPool()
    {
        await using var scheduler = new SharpTickerTaskScheduler(1);
        var seen = new TaskCompletionSource<(ThreadSeen Before, ThreadSeen After)>(TaskCreationOptions.RunContinuationsAsynchronously);

        await scheduler.QueueAsync(async _ =>
        {
            var before = Current();
            await Task.Delay(1);
            seen.TrySetResult((before, Current()));
        }, TickerTaskPriority.Normal);

        var (before, after) = await Within(seen.Task);
        Assert.True(before.IsWorkerThread);
        Assert.True(after.IsThreadPoolThread);
        Assert.False(after.IsWorkerThread);
    }

    private static readonly AsyncLocal<string?> Ambient = new();

    private sealed class ForeignContext : SynchronizationContext;

    // A job that is not an async method can change the ambient state of the thread it runs on: an AsyncLocal (such as
    // Activity.Current) or the synchronization context. The next job on the same worker must not see either.
    [Fact]
    public async Task WhatAJobChangesOnItsThread_DoesNotReachTheNextJobOnThatWorker()
    {
        await using var scheduler = new SharpTickerTaskScheduler(1);
        var first = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<(string? Ambient, SynchronizationContext? Context, string? Thread)>(TaskCreationOptions.RunContinuationsAsynchronously);

        await scheduler.QueueAsync(_ =>
        {
            Ambient.Value = "set by the first job";
            SynchronizationContext.SetSynchronizationContext(new ForeignContext());
            first.TrySetResult(Thread.CurrentThread.Name);
            return Task.CompletedTask;
        }, TickerTaskPriority.Normal);
        var firstThread = await Within(first.Task);

        await scheduler.QueueAsync(_ =>
        {
            second.TrySetResult((Ambient.Value, SynchronizationContext.Current, Thread.CurrentThread.Name));
            return Task.CompletedTask;
        }, TickerTaskPriority.Normal);

        var seen = await Within(second.Task);
        Assert.Equal(firstThread, seen.Thread); // one worker, so the same thread ran both
        Assert.Null(seen.Ambient);
        Assert.Null(seen.Context);
    }
}
