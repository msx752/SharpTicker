using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// The heartbeat lookups of the Lock holders panel (#31) with many names: they are started together, not one after
/// another, and each gets the request's cancellation token. The heartbeat source stands for Redis: every lookup waits
/// until all of them have started, so lookups made one after another never get there, and the panel then shows no
/// heartbeat at all.
/// </summary>
public class LockHolderHeartbeatFixture : DashboardApiFixture
{
    public const int Names = 20;

    public sealed class WaitingHeartbeats : ISharpTickerRedisContext
    {
        private int _started;
        private readonly TaskCompletionSource _allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _tokensThatCanCancel;

        public int TokensThatCanCancel => Volatile.Read(ref _tokensThatCanCancel);
        public IDistributedCache DistributedCache => null!;
        public bool HasRedisConnection => true;

        public async Task<bool?> HasHeartbeatAsync(string node, CancellationToken cancellationToken)
        {
            if (cancellationToken.CanBeCanceled)
                Interlocked.Increment(ref _tokensThatCanCancel);
            if (Interlocked.Increment(ref _started) == Names)
                _allStarted.TrySetResult();
            await _allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return node.EndsWith("-0", StringComparison.Ordinal);
        }

        public Task<TResult[]> GetOrSetArrayAsync<TResult>(string cacheKey, Func<CancellationToken, Task<TResult[]>> factory,
            TimeSpan? expiration = null, CancellationToken cancellationToken = default) where TResult : class
            => factory(cancellationToken);
    }

    public WaitingHeartbeats Heartbeats { get; } = new();

    protected override void ConfigureStore(TickerOptionsBuilder<TimeTickerEntity, CronTickerEntity> options)
        => options.ExternalProviderConfigServiceAction += services => services.AddSingleton<ISharpTickerRedisContext>(Heartbeats);

    protected override async Task SeedMoreAsync()
    {
        var tickers = Enumerable.Range(0, Names).Select(i =>
        {
            var ticker = Time(Guid.NewGuid(), "SeedJob", TickerStatus.Done, Today);
            ticker.LockHolder = $"node-{i}";
            ticker.LockedAt = DateTime.UtcNow.AddMinutes(-i);
            return ticker;
        }).ToArray();
        await Store.AddTimeTickers(tickers);
    }
}

[Collection(SharedStaticStateCollection.Name)]
public class DashboardLockHolderHeartbeatTests : IClassFixture<LockHolderHeartbeatFixture>
{
    private readonly LockHolderHeartbeatFixture _api;

    public DashboardLockHolderHeartbeatTests(LockHolderHeartbeatFixture api) => _api = api;

    [Fact]
    public async Task MachineJobs_StartTheHeartbeatLookupsTogether_WithTheRequestsToken()
    {
        var logsBefore = _api.Logs.Entries.Length;

        var jobs = (await _api.GetAsync("/ticker/machine/jobs")).EnumerateArray().ToArray();

        Assert.Equal(LockHolderHeartbeatFixture.Names, jobs.Length);
        foreach (var job in jobs)
        {
            Assert.True(job.TryGetProperty("hasHeartbeat", out var heartbeat) && heartbeat.ValueKind != JsonValueKind.Null, job.GetRawText());
            Assert.Equal(job.GetProperty("item1").GetString() == "node-0", heartbeat.GetBoolean());
        }
        Assert.Equal(LockHolderHeartbeatFixture.Names, _api.Heartbeats.TokensThatCanCancel);

        var problems = _api.Logs.Entries.Skip(logsBefore).Where(e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Warning).ToArray();
        Assert.True(problems.Length == 0, string.Join(Environment.NewLine, problems.Select(p => $"{p.Level} {p.Category}: {p.Message}")));
    }
}
