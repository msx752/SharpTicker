using System.Collections.Frozen;
using System.Diagnostics;
using NSubstitute;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Managers;
using Xunit.Abstractions;

namespace SharpTicker.Tests;

/// <summary>
/// The managers refuse a ticker whose function is not mapped, before anything is stored. The check is a lookup in the
/// function registry, the one the scheduler makes when it runs the job: an exact (ordinal) name, and a null name is not
/// mapped. Up to 1.7.0 the check walked every mapped function, so its cost grew with the number of functions.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class TickerManagerFunctionLookupTests
{
    // Stays in the process-wide registry after the test, with no cron: no later host seeds or runs it.
    private const string Mapped = "FunctionLookup.Mapped";
    private const string Cron = "0 0 * * * *";

    private readonly ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> _store =
        Substitute.For<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();
    private readonly TickerManager<TimeTickerEntity, CronTickerEntity> _manager;

    private readonly ITestOutputHelper _output;

    public TickerManagerFunctionLookupTests(ITestOutputHelper output)
    {
        _output = output;
        TickerFunctionProvider.RegisterFunctions(
            new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>
            {
                [Mapped] = ("", TickerTaskPriority.Normal, (_, _, _) => Task.CompletedTask, 0)
            });
        TickerFunctionProvider.Build();

        var clock = Substitute.For<ITickerClock>();
        clock.UtcNow.Returns(new DateTime(2026, 3, 13, 12, 0, 0, DateTimeKind.Utc));
        var dispatcher = Substitute.For<ISharpTickerDispatcher>();
        dispatcher.IsEnabled.Returns(false);
        _manager = new TickerManager<TimeTickerEntity, CronTickerEntity>(
            _store, Substitute.For<ISharpTickerHostScheduler>(), clock, Substitute.For<ISharpTickerNotificationHubSender>(),
            new TickerExecutionContext(), dispatcher);
    }

    public enum Path { AddTime, AddCron, UpdateCron, AddCronBatch, UpdateCronBatch }

    private async Task<(bool Succeeded, Exception? Exception)> CallAsync(Path path, string? function)
    {
        switch (path)
        {
            case Path.AddTime:
            {
                var r = await ((ITimeTickerManager<TimeTickerEntity>)_manager).AddAsync(new TimeTickerEntity { Function = function! });
                return (r.IsSucceeded, r.Exception);
            }
            case Path.AddCron:
            {
                var r = await ((ICronTickerManager<CronTickerEntity>)_manager).AddAsync(new CronTickerEntity { Function = function!, Expression = Cron });
                return (r.IsSucceeded, r.Exception);
            }
            case Path.UpdateCron:
            {
                var r = await ((ICronTickerManager<CronTickerEntity>)_manager).UpdateAsync(new CronTickerEntity { Id = Guid.NewGuid(), Function = function!, Expression = Cron });
                return (r.IsSucceeded, r.Exception);
            }
            case Path.AddCronBatch:
            {
                var r = await ((ICronTickerManager<CronTickerEntity>)_manager).AddBatchAsync([new CronTickerEntity { Function = function!, Expression = Cron }]);
                return (r.IsSucceeded, r.Exception);
            }
            case Path.UpdateCronBatch:
            {
                var r = await ((ICronTickerManager<CronTickerEntity>)_manager).UpdateBatchAsync([new CronTickerEntity { Id = Guid.NewGuid(), Function = function!, Expression = Cron }]);
                return (r.IsSucceeded, r.Exception);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(path), path, null);
        }
    }

    public static TheoryData<Path, string?> NotMapped()
    {
        var data = new TheoryData<Path, string?>();
        foreach (var path in Enum.GetValues<Path>())
        {
            data.Add(path, "FunctionLookup.Missing");
            data.Add(path, Mapped.ToUpperInvariant());
            data.Add(path, null);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(NotMapped))]
    public async Task A_Ticker_Whose_Function_Is_Not_Mapped_Is_Refused_And_Not_Stored(Path path, string? function)
    {
        var (succeeded, exception) = await CallAsync(path, function);

        Assert.False(succeeded);
        Assert.Contains("Cannot find TickerFunction with name", exception?.Message);
        Assert.Empty(_store.ReceivedCalls());
    }

    [Theory]
    [InlineData(Path.AddTime)]
    [InlineData(Path.AddCron)]
    [InlineData(Path.AddCronBatch)]
    public async Task A_Ticker_Whose_Function_Is_Mapped_Is_Not_Refused_For_Its_Function(Path path)
    {
        var (_, exception) = await CallAsync(path, Mapped);

        Assert.DoesNotContain("Cannot find TickerFunction", exception?.Message ?? "");
    }

    // With many functions mapped, refusing a ticker takes about as long as with one: the check is a lookup. A walk over a
    // hundred thousand names took 0.3-1 ms a call; the bound is far above a lookup and far below such a walk.
    [Fact]
    public async Task The_Check_Does_Not_Grow_With_The_Number_Of_Mapped_Functions()
    {
        var original = TickerFunctionProvider.TickerFunctions;
        try
        {
            TickerFunctionProvider.TickerFunctions = Enumerable.Range(0, 100_000)
                .Select(i => $"FunctionLookup.Many.Function{i}")
                .ToDictionary(name => name, _ => ("", TickerTaskPriority.Normal, (TickerFunctionDelegate)((_, _, _) => Task.CompletedTask), 0))
                .ToFrozenDictionary();
            ITimeTickerManager<TimeTickerEntity> manager = _manager;

            for (var i = 0; i < 20; i++)
                await manager.AddAsync(new TimeTickerEntity { Function = "FunctionLookup.Missing" });

            var times = new List<double>();
            for (var i = 0; i < 51; i++)
            {
                var watch = Stopwatch.StartNew();
                var result = await manager.AddAsync(new TimeTickerEntity { Function = "FunctionLookup.Missing" });
                watch.Stop();
                Assert.False(result.IsSucceeded);
                times.Add(watch.Elapsed.TotalMicroseconds);
            }
            times.Sort();
            var median = times[times.Count / 2];
            _output.WriteLine($"median refusal with 100,000 functions mapped: {median:0.0} us");

            Assert.True(median < 50, $"refusing a ticker took {median:0.0} us (median) with 100,000 functions mapped");
        }
        finally
        {
            TickerFunctionProvider.TickerFunctions = original;
        }
    }
}
