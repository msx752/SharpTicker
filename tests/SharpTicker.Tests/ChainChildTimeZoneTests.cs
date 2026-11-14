using NSubstitute;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Managers;

namespace SharpTicker.Tests;

// #81: the times of delayed children and grandchildren are converted to UTC on add, as the root's is (Local to UTC,
// Unspecified read in the scheduler time zone).
[Collection(SharedStaticStateCollection.Name)]
public class ChainChildTimeZoneTests : IDisposable
{
    private const string Function = "ChainTimeZoneFunction";
    private static readonly DateTime FixedUtcNow = new(2026, 3, 13, 12, 0, 0, DateTimeKind.Utc);

    private readonly ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity> _provider =
        Substitute.For<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();
    private readonly ITimeTickerManager<TimeTickerEntity> _tickers;
    private readonly List<TimeTickerEntity> _added = [];

    public ChainChildTimeZoneTests()
    {
        var clock = Substitute.For<ITickerClock>();
        clock.UtcNow.Returns(FixedUtcNow);
        var dispatcher = Substitute.For<ISharpTickerDispatcher>();
        dispatcher.IsEnabled.Returns(false);
        _provider.AddTimeTickers(Arg.Any<TimeTickerEntity[]>(), Arg.Any<CancellationToken>())
            .Returns(c => { _added.AddRange((TimeTickerEntity[])c[0]); return 1; });

        TickerFunctionProvider.RegisterFunctions(
            new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>
            {
                [Function] = ("", TickerTaskPriority.Normal, (_, _, _) => Task.CompletedTask, 0)
            });
        TickerFunctionProvider.Build();

        _tickers = new TickerManager<TimeTickerEntity, CronTickerEntity>(
            _provider, Substitute.For<ISharpTickerHostScheduler>(), clock, Substitute.For<ISharpTickerNotificationHubSender>(),
            new TickerExecutionContext(), dispatcher);
    }

    public void Dispose() => TickerFunctionProvider.Build();

    private static TimeTickerEntity Chain(DateTime rootTime, DateTime childTime, DateTime grandchildTime)
        => new()
        {
            Function = Function,
            ExecutionTime = rootTime,
            Children =
            [
                new TimeTickerEntity
                {
                    Function = Function,
                    RunCondition = RunCondition.OnSuccess,
                    ExecutionTime = childTime,
                    Children = [new TimeTickerEntity { Function = Function, RunCondition = RunCondition.OnSuccess, ExecutionTime = grandchildTime }]
                }
            ]
        };

    private static void AssertUtc(DateTime expected, DateTime? actual, string what)
    {
        Assert.True(actual.HasValue, what);
        Assert.True(expected == actual!.Value && actual.Value.Kind == DateTimeKind.Utc,
            $"{what}: expected {expected:O} (Utc), stored {actual.Value:O} ({actual.Value.Kind})");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheTimesOfDelayedChildrenAndGrandchildren_AreConvertedToUtc_AsTheRootsIs(bool batch)
    {
        var rootLocal = new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Local);
        var childLocal = new DateTime(2026, 6, 15, 11, 0, 0, DateTimeKind.Local);
        var grandchildUnspecified = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Unspecified);
        var chain = Chain(rootLocal, childLocal, grandchildUnspecified);

        var succeeded = batch
            ? (await _tickers.AddBatchAsync([chain], CancellationToken.None)).IsSucceeded
            : (await _tickers.AddAsync(chain, CancellationToken.None)).IsSucceeded;

        Assert.True(succeeded);
        var root = Assert.Single(_added);
        AssertUtc(rootLocal.ToUniversalTime(), root.ExecutionTime, "root");
        var child = Assert.Single(root.Children);
        AssertUtc(childLocal.ToUniversalTime(), child.ExecutionTime, "child");
        AssertUtc(TimeZoneInfo.ConvertTimeToUtc(grandchildUnspecified, CronScheduleCache.TimeZoneInfo), Assert.Single(child.Children).ExecutionTime, "grandchild");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AChildTimeGivenInUtc_IsKept_AndAChildWithoutATime_StaysWithoutOne(bool batch)
    {
        var childUtc = new DateTime(2026, 6, 15, 8, 30, 0, DateTimeKind.Utc);
        var chain = Chain(new DateTime(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc), childUtc, childUtc);
        Assert.Single(chain.Children).Children.Single().ExecutionTime = null;

        var succeeded = batch
            ? (await _tickers.AddBatchAsync([chain], CancellationToken.None)).IsSucceeded
            : (await _tickers.AddAsync(chain, CancellationToken.None)).IsSucceeded;

        Assert.True(succeeded);
        var child = Assert.Single(Assert.Single(_added).Children);
        AssertUtc(childUtc, child.ExecutionTime, "child");
        Assert.Null(Assert.Single(child.Children).ExecutionTime);
    }
}
