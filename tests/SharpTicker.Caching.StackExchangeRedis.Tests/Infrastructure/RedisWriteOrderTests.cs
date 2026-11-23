using System.Reflection;
using StackExchange.Redis;
using SharpTicker.Caching.StackExchangeRedis.Helpers;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>
/// The Redis store writes a row and its index entries in separate commands (#94). These tests make one of the commands
/// fail and check that the row and its index entries still agree: the job is found at the time its row holds, and a row
/// that is still stored is still listed.
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class RedisWriteOrderTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeTickerClock _clock = new(T0);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> CreateProvider(string nodeIdentifier, IDatabase? database = null)
        => fixture.CreateProvider(nodeIdentifier, _clock, database);

    private ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> FailingOn(string method, int failAt)
        => CreateProvider("node-A", RedisClaimMultiNodeTests.FailingDatabase.Create(fixture.Database, method, failAt));

    private ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> FailingOnKey(string method, string key)
        => CreateProvider("node-A", FailOnKeyDatabase.Create(fixture.Database, method, key));

    private static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    private static TimeTickerEntity NewTimeTicker(DateTime executionTime, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Function = "write-order-fn",
        ExecutionTime = executionTime,
        Status = TickerStatus.Idle,
        CreatedAt = T0.AddHours(-2),
        UpdatedAt = T0.AddHours(-1),
        Request = []
    };

    private static CronTickerEntity NewCron() => new()
    {
        Id = Guid.NewGuid(), Function = "write-order-cron", Expression = "0 0 * * *", Request = []
    };

    private static CronTickerOccurrenceEntity<CronTickerEntity> NewOccurrence(Guid cronId) => new()
    {
        Id = Guid.NewGuid(), CronTickerId = cronId, ExecutionTime = T0.AddHours(1), Status = TickerStatus.Idle,
        CreatedAt = T0, UpdatedAt = T0
    };

    private async Task<string> PendingAsync(Guid id)
    {
        var score = await fixture.Database.SortedSetScoreAsync(RedisKeyBuilder.TimeTickerPendingKey, id.ToString());
        return score == null ? "no pending entry" : $"pending entry at {new DateTime((long)score.Value, DateTimeKind.Utc):O}";
    }

    private async Task<(bool Stored, bool Listed)> TimeTickerStateAsync(Guid id)
    {
        var node = CreateProvider("node-A");
        return (await node.GetTimeTickerById(id, CancellationToken.None) != null,
            (await node.GetTimeTickers(null, CancellationToken.None)).Any(x => x.Id == id));
    }

    private async Task<(bool Stored, bool Listed)> CronStateAsync(Guid id)
    {
        var node = CreateProvider("node-A");
        return (await node.GetCronTickerById(id, CancellationToken.None) != null,
            (await node.GetCronTickers(null, CancellationToken.None)).Any(x => x.Id == id));
    }

    private async Task<(bool Stored, bool Listed)> OccurrenceStateAsync(Guid id)
        => (await fixture.Database.KeyExistsAsync(RedisKeyBuilder.CronOccurrenceKey(id)),
            (await CreateProvider("node-A").GetAllCronTickerOccurrences(null, CancellationToken.None)).Any(x => x.Id == id));

    // --- Update: the job is found at the time its row holds. ---

    // Moved from an hour ahead to now, and the index write fails: the row says now, so the fallback takes it once due.
    [Fact]
    public async Task Update_MovedEarlier_IndexWriteFails_TheJobIsTakenAtItsNewTime()
    {
        var node = CreateProvider("node-A");
        var ticker = NewTimeTicker(T0.AddHours(1));
        await node.AddTimeTickers([ticker], CancellationToken.None);

        await Assert.ThrowsAsync<RedisConnectionException>(() =>
            FailingOn(nameof(IDatabase.CreateBatch), 1).UpdateTimeTickers([NewTimeTicker(T0, ticker.Id)], CancellationToken.None));

        Assert.Equal(T0, (await node.GetTimeTickerById(ticker.Id, CancellationToken.None))!.ExecutionTime);
        var pending = await PendingAsync(ticker.Id);
        _clock.Advance(TimeSpan.FromSeconds(2));
        var taken = await Drain(node.QueueTimedOutTimeTickers(CancellationToken.None));
        Assert.True(taken.Any(x => x.Id == ticker.Id), $"The row is due at {T0:O}, but the fallback did not take it ({pending}).");
    }

    // Moved from now to an hour ahead, and the index write fails: the row says an hour ahead, so the fallback does not
    // take it now, and its entry is written at the row's time.
    [Fact]
    public async Task Update_MovedLater_IndexWriteFails_TheFallbackDoesNotStartItAtItsOldTime()
    {
        var node = CreateProvider("node-A");
        var ticker = NewTimeTicker(T0);
        await node.AddTimeTickers([ticker], CancellationToken.None);

        await Assert.ThrowsAsync<RedisConnectionException>(() =>
            FailingOn(nameof(IDatabase.CreateBatch), 1).UpdateTimeTickers([NewTimeTicker(T0.AddHours(1), ticker.Id)], CancellationToken.None));

        Assert.Equal(T0.AddHours(1), (await node.GetTimeTickerById(ticker.Id, CancellationToken.None))!.ExecutionTime);
        var pending = await PendingAsync(ticker.Id);
        _clock.Advance(TimeSpan.FromSeconds(2));
        var taken = await Drain(node.QueueTimedOutTimeTickers(CancellationToken.None));
        Assert.False(taken.Any(x => x.Id == ticker.Id), $"The row is due at {T0.AddHours(1):O}, but the fallback took it at {_clock.UtcNow:O} ({pending}).");
        Assert.Equal($"pending entry at {T0.AddHours(1):O}", await PendingAsync(ticker.Id));
        Assert.Equal(TickerStatus.Idle, (await node.GetTimeTickerById(ticker.Id, CancellationToken.None))!.Status);
    }

    // The same, read by the scheduler loop: the row is not in the window of its old time.
    [Fact]
    public async Task Update_MovedLater_IndexWriteFails_TheLoopDoesNotReadItAtItsOldTime()
    {
        var node = CreateProvider("node-A");
        var ticker = NewTimeTicker(T0.AddSeconds(30));
        await node.AddTimeTickers([ticker], CancellationToken.None);

        await Assert.ThrowsAsync<RedisConnectionException>(() =>
            FailingOn(nameof(IDatabase.CreateBatch), 1).UpdateTimeTickers([NewTimeTicker(T0.AddHours(1), ticker.Id)], CancellationToken.None));

        var pending = await PendingAsync(ticker.Id);
        var window = await node.GetEarliestTimeTickers(CancellationToken.None);
        Assert.False(window.Any(x => x.Id == ticker.Id), $"The row is due at {T0.AddHours(1):O}, but the loop read it in the window of {pending}.");
        Assert.Equal($"pending entry at {T0.AddHours(1):O}", await PendingAsync(ticker.Id));

        // Its entry is at its time now, so the loop reads it then.
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Contains(await node.GetEarliestTimeTickers(CancellationToken.None), x => x.Id == ticker.Id);
    }

    // Moved from an hour ahead to now, and the row write fails: the row keeps its time, so nothing takes it now.
    [Fact]
    public async Task Update_MovedEarlier_RowWriteFails_TheJobKeepsItsStoredTime()
    {
        var node = CreateProvider("node-A");
        var ticker = NewTimeTicker(T0.AddHours(1));
        await node.AddTimeTickers([ticker], CancellationToken.None);

        await Assert.ThrowsAsync<RedisConnectionException>(() =>
            FailingOn(nameof(IDatabase.ScriptEvaluateAsync), 1).UpdateTimeTickers([NewTimeTicker(T0, ticker.Id)], CancellationToken.None));

        Assert.Equal(T0.AddHours(1), (await node.GetTimeTickerById(ticker.Id, CancellationToken.None))!.ExecutionTime);
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(await Drain(node.QueueTimedOutTimeTickers(CancellationToken.None)), x => x.Id == ticker.Id);
        // The loop reads the next window, however far ahead: the row is in it at its own time.
        Assert.All((await node.GetEarliestTimeTickers(CancellationToken.None)).Where(x => x.Id == ticker.Id),
            x => Assert.Equal(T0.AddHours(1), x.ExecutionTime));
        Assert.Equal($"pending entry at {T0.AddHours(1):O}", await PendingAsync(ticker.Id));
    }

    // --- Add and insert: a row that is stored is listed. ---

    [Fact]
    public async Task AddTimeTicker_IndexWriteFails_TheRowIsEitherGoneOrStillListed()
    {
        var ticker = NewTimeTicker(T0.AddHours(1));

        await Assert.ThrowsAsync<RedisConnectionException>(() =>
            FailingOn(nameof(IDatabase.CreateBatch), 1).AddTimeTickers([ticker], CancellationToken.None));

        var (stored, listed) = await TimeTickerStateAsync(ticker.Id);
        Assert.True(stored == listed, $"Row stored: {stored}, row listed: {listed}.");
        // A retry with the same job adds it.
        await CreateProvider("node-A").AddTimeTickers([ticker], CancellationToken.None);
        Assert.Equal((true, true), await TimeTickerStateAsync(ticker.Id));
        Assert.Equal($"pending entry at {T0.AddHours(1):O}", await PendingAsync(ticker.Id));
    }

    [Fact]
    public async Task InsertCronTicker_IndexWriteFails_TheRowIsEitherGoneOrStillListed()
    {
        var cron = NewCron();

        await Assert.ThrowsAsync<RedisConnectionException>(() =>
            FailingOnKey(nameof(IDatabase.SetAddAsync), RedisKeyBuilder.CronIdsKey).InsertCronTickers([cron], CancellationToken.None));

        var (stored, listed) = await CronStateAsync(cron.Id);
        Assert.True(stored == listed, $"Row stored: {stored}, row listed: {listed}.");
    }

    [Fact]
    public async Task InsertCronOccurrence_IndexWriteFails_TheRowIsEitherGoneOrStillListed()
    {
        var occurrence = NewOccurrence(Guid.NewGuid());

        await Assert.ThrowsAsync<RedisConnectionException>(() =>
            FailingOn(nameof(IDatabase.CreateBatch), 1).InsertCronTickerOccurrences([occurrence], CancellationToken.None));

        var (stored, listed) = await OccurrenceStateAsync(occurrence.Id);
        Assert.True(stored == listed, $"Row stored: {stored}, row listed: {listed}.");
    }

    // --- Remove: a row that is stored is listed, and a retry removes what is left. ---

    // Removed since #153 with the row first and its index entries last.
    [Fact]
    public async Task RemoveTimeTicker_KeyDeleteFails_TheRowIsEitherGoneOrStillListed()
    {
        var ticker = NewTimeTicker(T0.AddHours(1));
        await CreateProvider("node-A").AddTimeTickers([ticker], CancellationToken.None);

        await Assert.ThrowsAsync<RedisConnectionException>(() =>
            FailingOnKey(nameof(IDatabase.KeyDeleteAsync), RedisKeyBuilder.TimeTickerKey(ticker.Id)).RemoveTimeTickers([ticker.Id], CancellationToken.None));

        var (stored, listed) = await TimeTickerStateAsync(ticker.Id);
        Assert.True(stored == listed, $"Row stored: {stored}, row listed: {listed}.");
    }

    [Fact]
    public async Task RemoveCronTicker_KeyDeleteFails_TheRowIsEitherGoneOrStillListed_AndARetryRemovesIt()
    {
        var node = CreateProvider("node-A");
        var cron = NewCron();
        await node.InsertCronTickers([cron], CancellationToken.None);

        await Assert.ThrowsAsync<RedisConnectionException>(() =>
            FailingOnKey(nameof(IDatabase.KeyDeleteAsync), RedisKeyBuilder.CronKey(cron.Id)).RemoveCronTickers([cron.Id], CancellationToken.None));

        var (stored, listed) = await CronStateAsync(cron.Id);
        Assert.True(stored == listed, $"Row stored: {stored}, row listed: {listed}.");
        Assert.Equal(1, await node.RemoveCronTickers([cron.Id], CancellationToken.None));
        Assert.Equal((false, false), await CronStateAsync(cron.Id));
    }

    // The delete of one of its occurrences fails: the cron and that occurrence are each gone or still listed, and a
    // retry of the cron's remove removes the rest.
    [Fact]
    public async Task RemoveCronTicker_OccurrenceDeleteFails_EveryRowIsEitherGoneOrStillListed_AndARetryRemovesTheRest()
    {
        var node = CreateProvider("node-A");
        var cron = NewCron();
        await node.InsertCronTickers([cron], CancellationToken.None);
        var occurrence = NewOccurrence(cron.Id);
        await node.InsertCronTickerOccurrences([occurrence], CancellationToken.None);

        await Assert.ThrowsAsync<RedisConnectionException>(() =>
            FailingOnKey(nameof(IDatabase.KeyDeleteAsync), RedisKeyBuilder.CronOccurrenceKey(occurrence.Id)).RemoveCronTickers([cron.Id], CancellationToken.None));

        var cronState = await CronStateAsync(cron.Id);
        var occurrenceState = await OccurrenceStateAsync(occurrence.Id);
        Assert.True(cronState.Stored == cronState.Listed && occurrenceState.Stored == occurrenceState.Listed,
            $"Cron stored: {cronState.Stored}, listed: {cronState.Listed}. Occurrence stored: {occurrenceState.Stored}, listed: {occurrenceState.Listed}.");

        await node.RemoveCronTickers([cron.Id], CancellationToken.None);
        Assert.Equal((false, false), await CronStateAsync(cron.Id));
        Assert.Equal((false, false), await OccurrenceStateAsync(occurrence.Id));
        Assert.Null(await fixture.Database.SortedSetScoreAsync(RedisKeyBuilder.CronOccurrencePendingKey, occurrence.Id.ToString()));
    }

    // A removed occurrence that is still stored without its index entries would be found again by the scheduler, which
    // repairs the entries of a slot's row it cannot create.
    [Fact]
    public async Task RemoveCronOccurrence_KeyDeleteFails_TheRowIsEitherGoneOrStillListed()
    {
        var node = CreateProvider("node-A");
        var occurrence = NewOccurrence(Guid.NewGuid());
        await node.InsertCronTickerOccurrences([occurrence], CancellationToken.None);

        await Assert.ThrowsAsync<RedisConnectionException>(() =>
            FailingOnKey(nameof(IDatabase.KeyDeleteAsync), RedisKeyBuilder.CronOccurrenceKey(occurrence.Id)).RemoveCronTickerOccurrences([occurrence.Id], CancellationToken.None));

        var (stored, listed) = await OccurrenceStateAsync(occurrence.Id);
        Assert.True(stored == listed, $"Row stored: {stored}, row listed: {listed}.");
    }

    /// <summary>Fails one call of a method on one key, once; every other call goes to the real database.</summary>
    public class FailOnKeyDatabase : DispatchProxy
    {
        private IDatabase _inner = null!;
        private string _method = "";
        private string _key = "";
        private int _failed;

        public static IDatabase Create(IDatabase inner, string method, string key)
        {
            var proxy = DispatchProxy.Create<IDatabase, FailOnKeyDatabase>();
            var failing = (FailOnKeyDatabase)(object)proxy;
            failing._inner = inner;
            failing._method = method;
            failing._key = key;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == _method && args is { Length: > 0 } && args[0] is RedisKey key && key == (RedisKey)_key
                && Interlocked.Exchange(ref _failed, 1) == 0)
            {
                var failure = new RedisConnectionException(ConnectionFailureType.SocketFailure, $"{_method} on {_key} did not reach Redis.");
                var returnType = targetMethod.ReturnType;
                if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    var fromException = typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!
                        .MakeGenericMethod(returnType.GetGenericArguments()[0]);
                    return fromException.Invoke(null, [failure]);
                }
                throw failure;
            }

            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }
    }
}
