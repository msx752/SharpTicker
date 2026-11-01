using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpTicker.SharpTickerThreadPool;
using SharpTicker.Utilities.Enums;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// The order in which a saturated pool takes its waiting jobs: High before Normal before Low, first in first out within
/// one priority, and a Low job is not held back for ever by a steady stream of High ones.
/// With one worker the order is exact, so the tests use one: the worker is held by a job that waits on a gate, the jobs
/// queue up behind it, and the order they run in after the gate opens is recorded.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class SharpTickerTaskSchedulerPriorityTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private sealed class Run : IAsyncDisposable
    {
        public SharpTickerTaskScheduler Scheduler { get; } = new(1);
        public ConcurrentQueue<string> Order { get; } = new();
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _holding = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Holds the only worker until <see cref="Open"/> is called, and returns once it holds it.</summary>
        public async Task HoldTheWorkerAsync()
        {
            await Scheduler.QueueAsync(async _ =>
            {
                _holding.SetResult();
                await _gate.Task;
            }, TickerTaskPriority.Normal);
            await _holding.Task.WaitAsync(Wait);
        }

        public ValueTask Queue(string name, TickerTaskPriority priority)
            => Scheduler.QueueAsync(_ => { Order.Enqueue(name); return Task.CompletedTask; }, priority);

        public void Open() => _gate.SetResult();

        public async Task WaitForAsync(int count)
        {
            var deadline = DateTime.UtcNow + Wait;
            while (Order.Count < count && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert.True(Order.Count >= count, $"only {Order.Count} of {count} jobs ran: {string.Join(",", Order)}");
        }

        public ValueTask DisposeAsync() => Scheduler.DisposeAsync();
    }

    [Fact]
    public async Task WaitingJobs_RunHighBeforeNormalBeforeLow_WhateverTheOrderTheyWereQueuedIn()
    {
        await using var run = new Run();
        await run.HoldTheWorkerAsync();

        await run.Queue("low", TickerTaskPriority.Low);
        await run.Queue("normal", TickerTaskPriority.Normal);
        await run.Queue("high", TickerTaskPriority.High);

        run.Open();
        await run.WaitForAsync(3);

        Assert.Equal(new[] { "high", "normal", "low" }, run.Order.ToArray());
    }

    [Fact]
    public async Task JobsOfOnePriority_RunInTheOrderTheyWereQueued()
    {
        await using var run = new Run();
        await run.HoldTheWorkerAsync();

        foreach (var name in new[] { "a", "b", "c", "d" })
            await run.Queue(name, TickerTaskPriority.Normal);

        run.Open();
        await run.WaitForAsync(4);

        Assert.Equal(new[] { "a", "b", "c", "d" }, run.Order.ToArray());
    }

    [Fact]
    public async Task ALowJob_IsNotHeldBackForEver_ByAStreamOfHighJobs()
    {
        await using var run = new Run();
        await run.HoldTheWorkerAsync();

        await run.Queue("low", TickerTaskPriority.Low);
        for (var i = 0; i < 25; i++)
            await run.Queue($"high-{i}", TickerTaskPriority.High);

        run.Open();
        await run.WaitForAsync(26);

        var order = run.Order.ToArray();
        var position = Array.IndexOf(order, "low");
        Assert.True(position > 0, "a Low job should not jump ahead of the High jobs that were queued first");
        Assert.True(position <= 10, $"the Low job ran at position {position}, after more than 10 higher priority jobs");
    }

    [Fact]
    public async Task EveryJobRunsExactlyOnce_WhateverItsPriority()
    {
        await using var run = new Run();
        await run.HoldTheWorkerAsync();

        var priorities = new[] { TickerTaskPriority.High, TickerTaskPriority.Normal, TickerTaskPriority.Low };
        var expected = new List<string>();
        for (var i = 0; i < 30; i++)
        {
            var name = $"job-{i}";
            expected.Add(name);
            await run.Queue(name, priorities[i % 3]);
        }

        run.Open();
        await run.WaitForAsync(30);
        await Task.Delay(100);

        Assert.Equal(expected.OrderBy(x => x), run.Order.OrderBy(x => x));
        Assert.Equal(0, run.Scheduler.TotalQueuedTasks);
    }
}
