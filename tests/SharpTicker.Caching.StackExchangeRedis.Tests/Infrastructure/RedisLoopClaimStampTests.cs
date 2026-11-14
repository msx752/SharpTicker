using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpTicker.Caching.StackExchangeRedis.DependencyInjection;
using SharpTicker.Caching.StackExchangeRedis.Infrastructure;
using SharpTicker.DependencyInjection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>
/// The claim of the scheduler loop on Redis takes a row only when its UpdatedAt is the one that was read (#176). It
/// compares the value as text. A row the application wrote holds UpdatedAt as System.Text.Json writes it, with the
/// trailing zeros of the fraction trimmed; the value the claim compared with always had seven digits. A job whose
/// UpdatedAt ended in a zero was never claimed by the loop and was started by the fallback, up to its interval late.
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class RedisLoopClaimStampTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private const string HostFunction = "LoopClaimStamp.EndToEnd";
    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ConcurrentDictionary<Guid, DateTime> Started = new();

    static RedisLoopClaimStampTests()
    {
        TickerFunctionProvider.RegisterFunctions(new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>
        {
            [HostFunction] = (string.Empty, TickerTaskPriority.Normal, (_, _, context) =>
            {
                Started[context.Id] = DateTime.UtcNow;
                return Task.CompletedTask;
            }, 0)
        });
        TickerFunctionProvider.Build();
    }

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => fixture.ResetAsync();

    private static TimeTickerEntity Job(DateTime stamp) => new()
    {
        Id = Guid.NewGuid(), Function = "LoopClaimStamp.Job", ExecutionTime = T0.AddSeconds(2), Status = TickerStatus.Idle,
        CreatedAt = stamp, UpdatedAt = stamp, Request = []
    };

    private static async Task<List<TimeTickerEntity>> ClaimAsync(ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> store, TimeTickerEntity[] read)
    {
        var claimed = new List<TimeTickerEntity>();
        await foreach (var row in store.QueueTimeTickers(read, CancellationToken.None))
            claimed.Add(row);
        return claimed;
    }

    // What the scheduler loop does: read the earliest jobs, then claim them. The stamp of the job is the only thing
    // that differs between the cases.
    [Theory]
    [InlineData(1234567)] // seven digits, none trimmed
    [InlineData(1234560)] // ends in a zero
    [InlineData(1230000)] // a whole millisecond
    [InlineData(0)]       // a whole second
    public async Task TheLoopsClaim_TakesAJob_WhateverTheLastDigitsOfItsStampAre(int ticksOfTheSecond)
    {
        var store = fixture.CreateProvider("loop-claim", new FakeTickerClock(T0));
        var job = Job(T0.AddMinutes(-5).AddTicks(ticksOfTheSecond));
        await store.AddTimeTickers([job], CancellationToken.None);

        var read = await store.GetEarliestTimeTickers(CancellationToken.None);
        Assert.Equal(job.Id, Assert.Single(read).Id);
        var claimed = await ClaimAsync(store, read);

        Assert.Equal(job.Id, Assert.Single(claimed).Id);
        Assert.Equal(TickerStatus.Queued, (await store.GetTimeTickerById(job.Id, CancellationToken.None)).Status);
    }

    // The comparison is still there: a job that was changed between the read and the claim is not taken with what was
    // read, whichever way its stamp is spelled.
    [Theory]
    [InlineData(1234567)]
    [InlineData(1230000)]
    public async Task AJobChangedBetweenTheReadAndTheClaim_IsNotTaken(int ticksOfTheSecond)
    {
        var store = fixture.CreateProvider("loop-claim", new FakeTickerClock(T0));
        var job = Job(T0.AddMinutes(-5).AddTicks(ticksOfTheSecond));
        await store.AddTimeTickers([job], CancellationToken.None);
        var read = await store.GetEarliestTimeTickers(CancellationToken.None);

        var changed = await store.GetTimeTickerById(job.Id, CancellationToken.None);
        changed.UpdatedAt = T0.AddMinutes(-1);
        Assert.Equal(1, await store.UpdateTimeTickers([changed], CancellationToken.None));

        Assert.Empty(await ClaimAsync(store, read));
        Assert.Equal(TickerStatus.Idle, (await store.GetTimeTickerById(job.Id, CancellationToken.None)).Status);
    }

    // The second spelling the claim sends is the serializer's own.
    [Theory]
    [InlineData(1234567, DateTimeKind.Utc)]
    [InlineData(1234560, DateTimeKind.Utc)]
    [InlineData(1230000, DateTimeKind.Utc)]
    [InlineData(1000000, DateTimeKind.Utc)]
    [InlineData(0, DateTimeKind.Utc)]
    [InlineData(1230000, DateTimeKind.Unspecified)]
    [InlineData(0, DateTimeKind.Unspecified)]
    [InlineData(1230000, DateTimeKind.Local)]
    [InlineData(0, DateTimeKind.Local)]
    public void TheSecondSpelling_IsTheTextTheSerializerWrites(int ticksOfTheSecond, DateTimeKind kind)
    {
        var value = new DateTime(2025, 6, 15, 11, 55, 0, kind).AddTicks(ticksOfTheSecond);

        var spelled = BaseRedisPersistenceProvider<TimeTickerEntity, CronTickerEntity>.AsTheSerializerWrites(value);

        Assert.Equal(JsonSerializer.Serialize(value).Trim('"'), spelled);
    }

    // A clock that gives whole milliseconds, as some do: every stamp ends in zeros.
    private sealed class MillisecondClock : ITickerClock
    {
        public DateTime UtcNow
        {
            get
            {
                var now = DateTime.UtcNow;
                return new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
            }
        }
    }

    // As an application meets it: a started host whose clock gives whole milliseconds, jobs added two seconds before
    // they are due. The fallback would take them only after six seconds, so a job that starts within a second of its
    // time was started by the scheduler loop.
    [Fact]
    public async Task JobsAddedShortlyBeforeTheyAreDue_StartAtTheirTime_WithAClockOfWholeMilliseconds()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSharpTicker(options =>
        {
            options.ConfigureScheduler(scheduler =>
            {
                scheduler.NodeIdentifier = "loop-claim-host";
                scheduler.FallbackIntervalChecker = TimeSpan.FromSeconds(6);
            });
            options.AddStackExchangeRedis(redis => redis.Configuration = fixture.Container.GetConnectionString());
        });
        builder.Services.AddSingleton<ITickerClock, MillisecondClock>();
        using var host = builder.Build();
        host.UseSharpTicker();
        await host.StartAsync();
        var manager = host.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
        // Not in the first moments of the host: what is measured is the loop's claim, not how fast the application starts.
        await Task.Delay(TimeSpan.FromSeconds(1));

        var lateness = new List<double>();
        for (var i = 0; i < 3; i++)
        {
            var due = DateTime.UtcNow.AddSeconds(2);
            var added = await manager.AddAsync(new TimeTickerEntity { Function = HostFunction, ExecutionTime = due });
            Assert.True(added.IsSucceeded, added.Exception?.ToString());
            Assert.Equal(0, added.Result.UpdatedAt.Ticks % TimeSpan.TicksPerMillisecond);
            var id = added.Result.Id;

            var deadline = DateTime.UtcNow.AddSeconds(12);
            while (!Started.ContainsKey(id) && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            Assert.True(Started.TryGetValue(id, out var startedAt), $"job {i} did not start within 12 s of being added");
            lateness.Add((startedAt - due).TotalMilliseconds);
        }

        await host.StopAsync();
        Assert.All(lateness, late => Assert.True(late < 1000, $"started {string.Join(", ", lateness.Select(l => $"{l:F0} ms"))} after its time"));
    }
}
