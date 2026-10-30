using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SharpTicker.Provider;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Models;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// How the scheduler records what happened to a job on the in-memory store: status, execution time, exception, retry
/// count and the release of the lock. Only the properties the scheduler marked for update may change. The entity types
/// are private to this class, so its store is separate from every other test's.
/// </summary>
public class InMemoryProviderExecutionResultTests
{
    private sealed class ResultTimeTicker : TimeTickerEntity<ResultTimeTicker> { }
    private sealed class ResultCronTicker : CronTickerEntity { }

    private static readonly DateTime Now = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Earlier = Now.AddHours(-3);

    private readonly TickerInMemoryPersistenceProvider<ResultTimeTicker, ResultCronTicker> _store;

    public InMemoryProviderExecutionResultTests()
    {
        var clock = Substitute.For<ITickerClock>();
        clock.UtcNow.Returns(Now);
        var services = new ServiceCollection();
        services.AddSingleton(clock);
        services.AddSingleton(new SchedulerOptionsBuilder { NodeIdentifier = "node-1" });
        _store = new TickerInMemoryPersistenceProvider<ResultTimeTicker, ResultCronTicker>(services.BuildServiceProvider());
    }

    private async Task<ResultTimeTicker> AddLockedTimeTickerAsync(string? request = null)
    {
        var ticker = new ResultTimeTicker
        {
            Id = Guid.NewGuid(),
            Function = "Job",
            Status = TickerStatus.InProgress,
            ExecutionTime = Earlier,
            LockHolder = "node-1",
            LockedAt = Earlier,
            Description = "keep me",
            Retries = 3,
            CreatedAt = Earlier,
            UpdatedAt = Earlier,
            Request = request == null ? null : Encoding.UTF8.GetBytes(request),
        };
        await _store.AddTimeTickers(new[] { ticker });
        return ticker;
    }

    private async Task<CronTickerOccurrenceEntity<ResultCronTicker>> AddLockedOccurrenceAsync(Guid? cronId = null)
    {
        var occurrence = new CronTickerOccurrenceEntity<ResultCronTicker>
        {
            Id = Guid.NewGuid(),
            CronTickerId = cronId ?? Guid.NewGuid(),
            Status = TickerStatus.InProgress,
            ExecutionTime = Earlier,
            LockHolder = "node-1",
            LockedAt = Earlier,
            CreatedAt = Earlier,
            UpdatedAt = Earlier,
        };
        await _store.InsertCronTickerOccurrences(new[] { occurrence }, default);
        return occurrence;
    }

    private static InternalFunctionContext Result(Guid id) => new() { TickerId = id };

    #region A finished time ticker

    [Fact]
    public async Task ADoneResult_IsRecordedWithItsTimingAndTheLockIsReleased()
    {
        var ticker = await AddLockedTimeTickerAsync();
        var context = Result(ticker.Id)
            .SetProperty(x => x.Status, TickerStatus.Done)
            .SetProperty(x => x.ExecutedAt, Now.AddSeconds(-2))
            .SetProperty(x => x.ElapsedTime, 1500L)
            .SetProperty(x => x.ReleaseLock, true);

        var rows = await _store.UpdateTimeTicker(context);

        Assert.Equal(1, rows);
        var stored = await _store.GetTimeTickerById(ticker.Id);
        Assert.Equal(TickerStatus.Done, stored.Status);
        Assert.Equal(Now.AddSeconds(-2), stored.ExecutedAt);
        Assert.Equal(1500, stored.ElapsedTime);
        Assert.Null(stored.LockHolder);
        Assert.Null(stored.LockedAt);
        Assert.Equal(Now, stored.UpdatedAt);
    }

    [Fact]
    public async Task AFailedResult_KeepsTheExceptionAndTheRetryCount()
    {
        var ticker = await AddLockedTimeTickerAsync();
        var context = Result(ticker.Id)
            .SetProperty(x => x.Status, TickerStatus.Failed)
            .SetProperty(x => x.ExceptionDetails, "System.InvalidOperationException: boom")
            .SetProperty(x => x.RetryCount, 3);

        await _store.UpdateTimeTicker(context);

        var stored = await _store.GetTimeTickerById(ticker.Id);
        Assert.Equal(TickerStatus.Failed, stored.Status);
        Assert.Equal("System.InvalidOperationException: boom", stored.ExceptionMessage);
        Assert.Equal(3, stored.RetryCount);
        Assert.Null(stored.SkippedReason);
    }

    [Fact]
    public async Task ASkippedResult_KeepsTheReasonAsTheSkipReason_NotAsAnException()
    {
        var ticker = await AddLockedTimeTickerAsync();
        var context = Result(ticker.Id)
            .SetProperty(x => x.Status, TickerStatus.Skipped)
            .SetProperty(x => x.ExceptionDetails, "Skipped: a previous run was still active");

        await _store.UpdateTimeTicker(context);

        var stored = await _store.GetTimeTickerById(ticker.Id);
        Assert.Equal(TickerStatus.Skipped, stored.Status);
        Assert.Equal("Skipped: a previous run was still active", stored.SkippedReason);
        Assert.Null(stored.ExceptionMessage);
    }

    [Fact]
    public async Task OnlyThePropertiesMarkedForUpdate_AreChanged()
    {
        var ticker = await AddLockedTimeTickerAsync();
        var context = Result(ticker.Id)
            .SetProperty(x => x.Status, TickerStatus.Done);
        context.ElapsedTime = 999;
        context.RetryCount = 9;
        context.ExceptionDetails = "not marked";
        context.ReleaseLock = true;

        await _store.UpdateTimeTicker(context);

        var stored = await _store.GetTimeTickerById(ticker.Id);
        Assert.Equal(TickerStatus.Done, stored.Status);
        Assert.Equal(0, stored.ElapsedTime);
        Assert.Equal(0, stored.RetryCount);
        Assert.Null(stored.ExceptionMessage);
        Assert.Equal("node-1", stored.LockHolder);
        Assert.Equal("keep me", stored.Description);
        Assert.Equal(3, stored.Retries);
    }

    [Fact]
    public async Task ARunThatWillBeRetried_GoesBackToIdleAndKeepsTheLockReleased()
    {
        var ticker = await AddLockedTimeTickerAsync();
        var context = Result(ticker.Id)
            .SetProperty(x => x.Status, TickerStatus.Idle)
            .SetProperty(x => x.RetryCount, 1)
            .SetProperty(x => x.ReleaseLock, true);

        await _store.UpdateTimeTicker(context);

        var stored = await _store.GetTimeTickerById(ticker.Id);
        Assert.Equal(TickerStatus.Idle, stored.Status);
        Assert.Equal(1, stored.RetryCount);
        Assert.Null(stored.LockedAt);
    }

    [Fact]
    public async Task AResultForATickerThatNoLongerExists_ChangesNothing()
    {
        var context = Result(Guid.NewGuid()).SetProperty(x => x.Status, TickerStatus.Done);

        Assert.Equal(0, await _store.UpdateTimeTicker(context));
    }

    [Fact]
    public async Task AUnifiedResult_IsAppliedToEveryListedTicker_AndToNoOther()
    {
        var first = await AddLockedTimeTickerAsync();
        var second = await AddLockedTimeTickerAsync();
        var untouched = await AddLockedTimeTickerAsync();
        var context = new InternalFunctionContext()
            .SetProperty(x => x.Status, TickerStatus.Cancelled)
            .SetProperty(x => x.ReleaseLock, true);

        await _store.UpdateTimeTickersWithUnifiedContext(new[] { first.Id, second.Id, Guid.NewGuid() }, context);

        Assert.Equal(TickerStatus.Cancelled, (await _store.GetTimeTickerById(first.Id)).Status);
        Assert.Equal(TickerStatus.Cancelled, (await _store.GetTimeTickerById(second.Id)).Status);
        Assert.Null((await _store.GetTimeTickerById(second.Id)).LockHolder);
        Assert.Equal(TickerStatus.InProgress, (await _store.GetTimeTickerById(untouched.Id)).Status);
    }

    #endregion

    #region A finished cron occurrence

    [Fact]
    public async Task ADoneOccurrence_IsRecordedWithItsTimingAndTheLockIsReleased()
    {
        var occurrence = await AddLockedOccurrenceAsync();
        var context = Result(occurrence.Id)
            .SetProperty(x => x.Status, TickerStatus.Done)
            .SetProperty(x => x.ExecutedAt, Now.AddSeconds(-1))
            .SetProperty(x => x.ElapsedTime, 800L)
            .SetProperty(x => x.ReleaseLock, true);

        await _store.UpdateCronTickerOccurrence(context);

        var stored = Assert.Single(await _store.GetAllCronTickerOccurrences(x => x.Id == occurrence.Id, default));
        Assert.Equal(TickerStatus.Done, stored.Status);
        Assert.Equal(Now.AddSeconds(-1), stored.ExecutedAt);
        Assert.Equal(800, stored.ElapsedTime);
        Assert.Null(stored.LockHolder);
        Assert.Equal(Now, stored.UpdatedAt);
    }

    [Fact]
    public async Task AFailedOccurrence_KeepsTheExceptionAndTheRetryCount()
    {
        var occurrence = await AddLockedOccurrenceAsync();
        var context = Result(occurrence.Id)
            .SetProperty(x => x.Status, TickerStatus.Failed)
            .SetProperty(x => x.ExceptionDetails, "boom")
            .SetProperty(x => x.RetryCount, 2);

        await _store.UpdateCronTickerOccurrence(context);

        var stored = Assert.Single(await _store.GetAllCronTickerOccurrences(x => x.Id == occurrence.Id, default));
        Assert.Equal("boom", stored.ExceptionMessage);
        Assert.Equal(2, stored.RetryCount);
        Assert.Null(stored.SkippedReason);
    }

    [Fact]
    public async Task ASkippedOccurrence_KeepsTheReasonAsTheSkipReason()
    {
        var occurrence = await AddLockedOccurrenceAsync();
        var context = Result(occurrence.Id)
            .SetProperty(x => x.Status, TickerStatus.Skipped)
            .SetProperty(x => x.ExceptionDetails, "still running");

        await _store.UpdateCronTickerOccurrence(context);

        var stored = Assert.Single(await _store.GetAllCronTickerOccurrences(x => x.Id == occurrence.Id, default));
        Assert.Equal("still running", stored.SkippedReason);
        Assert.Null(stored.ExceptionMessage);
    }

    [Fact]
    public async Task OnlyThePropertiesMarkedForUpdate_AreChangedOnAnOccurrence()
    {
        var occurrence = await AddLockedOccurrenceAsync();
        var context = Result(occurrence.Id).SetProperty(x => x.Status, TickerStatus.Done);
        context.ElapsedTime = 999;
        context.ReleaseLock = true;

        await _store.UpdateCronTickerOccurrence(context);

        var stored = Assert.Single(await _store.GetAllCronTickerOccurrences(x => x.Id == occurrence.Id, default));
        Assert.Equal(0, stored.ElapsedTime);
        Assert.Equal("node-1", stored.LockHolder);
    }

    [Fact]
    public async Task AUnifiedResult_IsAppliedToEveryListedOccurrence_AndToNoOther()
    {
        var first = await AddLockedOccurrenceAsync();
        var second = await AddLockedOccurrenceAsync();
        var untouched = await AddLockedOccurrenceAsync();
        var context = new InternalFunctionContext()
            .SetProperty(x => x.Status, TickerStatus.Cancelled)
            .SetProperty(x => x.ReleaseLock, true);

        await _store.UpdateCronTickerOccurrencesWithUnifiedContext(new[] { first.Id, second.Id }, context);

        var all = (await _store.GetAllCronTickerOccurrences(null, default)).ToDictionary(o => o.Id);
        Assert.Equal(TickerStatus.Cancelled, all[first.Id].Status);
        Assert.Equal(TickerStatus.Cancelled, all[second.Id].Status);
        Assert.Null(all[first.Id].LockHolder);
        Assert.Equal(TickerStatus.InProgress, all[untouched.Id].Status);
    }

    [Fact]
    public async Task AResultForAnOccurrenceThatNoLongerExists_ChangesNothing()
    {
        await _store.UpdateCronTickerOccurrence(Result(Guid.NewGuid()).SetProperty(x => x.Status, TickerStatus.Done));

        Assert.DoesNotContain(await _store.GetAllCronTickerOccurrences(null, default), o => o.Status == TickerStatus.Done);
    }

    #endregion

    #region Reading the request a job runs with

    [Fact]
    public async Task ATimeTickersRequest_IsReadBack_OrNullWhenThereIsNone()
    {
        var withRequest = await AddLockedTimeTickerAsync("{\"a\":1}");
        var without = await AddLockedTimeTickerAsync();

        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString((await _store.GetTimeTickerRequest(withRequest.Id, default))!));
        Assert.Null(await _store.GetTimeTickerRequest(without.Id, default));
        Assert.Null(await _store.GetTimeTickerRequest(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task AnOccurrencesRequest_IsTheOneOfItsCronTicker()
    {
        var cron = new ResultCronTicker
        {
            Id = Guid.NewGuid(), Function = "Cron", Expression = "0 0 * * * *",
            Request = Encoding.UTF8.GetBytes("{\"b\":2}"), CreatedAt = Earlier, UpdatedAt = Earlier,
        };
        await _store.InsertCronTickers(new[] { cron }, default);
        var occurrence = await AddLockedOccurrenceAsync(cron.Id);

        var request = await _store.GetCronTickerOccurrenceRequest(occurrence.Id);

        Assert.Equal("{\"b\":2}", Encoding.UTF8.GetString(request!));
    }

    [Fact]
    public async Task AnOccurrencesRequest_IsNull_WhenItsCronTickerIsGoneOrTheOccurrenceIsUnknown()
    {
        var orphan = await AddLockedOccurrenceAsync();

        Assert.Null(await _store.GetCronTickerOccurrenceRequest(orphan.Id));
        Assert.Null(await _store.GetCronTickerOccurrenceRequest(Guid.NewGuid()));
    }

    #endregion

    [Fact]
    public async Task TheCronExpressionsTheSchedulerReads_LeaveOutDisabledCronTickers()
    {
        var enabled = new ResultCronTicker { Id = Guid.NewGuid(), Function = "On", Expression = "0 0 * * * *", IsEnabled = true, CreatedAt = Earlier, UpdatedAt = Earlier };
        var disabled = new ResultCronTicker { Id = Guid.NewGuid(), Function = "Off", Expression = "0 0 * * * *", IsEnabled = false, CreatedAt = Earlier, UpdatedAt = Earlier };
        await _store.InsertCronTickers(new[] { enabled, disabled }, default);

        var expressions = await _store.GetAllCronTickerExpressions(default);

        Assert.Contains(expressions, c => c.Id == enabled.Id);
        Assert.DoesNotContain(expressions, c => c.Id == disabled.Id);
    }
}
