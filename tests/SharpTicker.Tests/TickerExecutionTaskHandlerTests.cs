using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SharpTicker.Exceptions;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Instrumentation;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Tests;

[Collection("TickerCancellationTokenState")]
public class TickerExecutionTaskHandlerTests : IDisposable
{
    public void Dispose()
    {
        TickerCancellationTokenManager.CleanUpTickerCancellationTokens();
    }

    private readonly ITickerClock _clock;
    private readonly IInternalTickerManager _internalManager;
    private readonly ISharpTickerInstrumentation _instrumentation;
    private readonly ServiceProvider _serviceProvider;
    private readonly TickerExecutionTaskHandler _handler;

    public TickerExecutionTaskHandlerTests()
    {
        _clock = Substitute.For<ITickerClock>();
        _clock.UtcNow.Returns(DateTime.UtcNow);
        _internalManager = Substitute.For<IInternalTickerManager>();
        // The store has every write, unless a test says otherwise.
        _internalManager.UpdateTickerAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>()).Returns(true);
        _instrumentation = Substitute.For<ISharpTickerInstrumentation>();

        var services = new ServiceCollection();
        services.AddSingleton(_internalManager);
        services.AddSingleton(_instrumentation);
        _serviceProvider = services.BuildServiceProvider();

        _handler = new TickerExecutionTaskHandler(_serviceProvider, _clock, _instrumentation, _internalManager);
    }

    #region Success Path

    [Fact]
    public async Task ExecuteTaskAsync_SetsStatusDone_WhenSucceeds_NotDue()
    {
        var context = CreateContext(ct: (_, _, _) => Task.CompletedTask);

        await _handler.ExecuteTaskAsync(context, isDue: false);

        Assert.Equal(TickerStatus.Done, context.Status);
    }

    [Fact]
    public async Task ExecuteTaskAsync_SetsStatusDueDone_WhenSucceeds_IsDue()
    {
        var context = CreateContext(ct: (_, _, _) => Task.CompletedTask);

        await _handler.ExecuteTaskAsync(context, isDue: true);

        Assert.Equal(TickerStatus.DueDone, context.Status);
    }

    [Fact]
    public async Task ExecuteTaskAsync_CallsUpdateTickerAsync_OnSuccess()
    {
        var context = CreateContext(ct: (_, _, _) => Task.CompletedTask);

        await _handler.ExecuteTaskAsync(context, isDue: false);

        await _internalManager.Received(1).UpdateTickerAsync(
            Arg.Is<InternalFunctionContext>(c => c.TickerId == context.TickerId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteTaskAsync_SetsElapsedTime_OnSuccess()
    {
        var context = CreateContext(ct: async (_, _, _) => await Task.Delay(10));

        await _handler.ExecuteTaskAsync(context, isDue: false);

        Assert.True(context.ElapsedTime >= 0);
    }

    [Fact]
    public async Task ExecuteTaskAsync_SetsExecutedAt_OnSuccess()
    {
        var now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        _clock.UtcNow.Returns(now);
        var context = CreateContext(ct: (_, _, _) => Task.CompletedTask);

        await _handler.ExecuteTaskAsync(context, isDue: false);

        Assert.Equal(now, context.ExecutedAt);
    }

    #endregion

    #region Failure Path

    [Fact]
    public async Task ExecuteTaskAsync_SetsStatusFailed_WhenDelegateThrows()
    {
        var context = CreateContext(ct: (_, _, _) => throw new InvalidOperationException("boom"));

        await _handler.ExecuteTaskAsync(context, isDue: false);

        Assert.Equal(TickerStatus.Failed, context.Status);
    }

    [Fact]
    public async Task ExecuteTaskAsync_RecordsExceptionDetails_WhenDelegateThrows()
    {
        var context = CreateContext(ct: (_, _, _) => throw new InvalidOperationException("boom"));

        await _handler.ExecuteTaskAsync(context, isDue: false);

        Assert.False(string.IsNullOrWhiteSpace(context.ExceptionDetails));
        Assert.Contains("boom", context.ExceptionDetails);
    }

    [Fact]
    public async Task ExecuteTaskAsync_CallsExceptionHandler_WhenRegistered_AndDelegateThrows()
    {
        var exceptionHandler = Substitute.For<ITickerExceptionHandler>();
        var services = new ServiceCollection();
        services.AddSingleton(_internalManager);
        services.AddSingleton(_instrumentation);
        services.AddSingleton(exceptionHandler);
        var sp = services.BuildServiceProvider();
        var handler = new TickerExecutionTaskHandler(sp, _clock, _instrumentation, _internalManager);

        var context = CreateContext(ct: (_, _, _) => throw new InvalidOperationException("boom"));

        await handler.ExecuteTaskAsync(context, isDue: false);

        await exceptionHandler.Received(1).HandleExceptionAsync(
            Arg.Any<Exception>(),
            Arg.Is(context.TickerId),
            Arg.Is(context.Type));
    }

    [Fact]
    public async Task ExecuteTaskAsync_CallsExceptionHandler_ForEachFailedAttempt()
    {
        var exceptionHandler = Substitute.For<ITickerExceptionHandler>();
        var services = new ServiceCollection();
        services.AddSingleton(_internalManager);
        services.AddSingleton(_instrumentation);
        services.AddSingleton(exceptionHandler);
        var sp = services.BuildServiceProvider();
        var handler = new TickerExecutionTaskHandler(sp, _clock, _instrumentation, _internalManager);

        var context = CreateContext(ct: (_, _, _) => throw new InvalidOperationException("boom"));
        context.Retries = 2;
        context.RetryIntervals = [0, 0];

        await handler.ExecuteTaskAsync(context, isDue: false);

        await exceptionHandler.Received(3).HandleExceptionAsync(
            Arg.Any<Exception>(),
            Arg.Is(context.TickerId),
            Arg.Is(context.Type));
    }

    [Fact]
    public async Task ExecuteTaskAsync_UpdatesExceptionDetails_OnFailedAttemptBeforeRetriesComplete()
    {
        var context = CreateContext(ct: (_, _, _) => throw new InvalidOperationException("boom"));
        context.Retries = 1;
        context.RetryIntervals = [0];
        var observedUpdates = new List<(TickerStatus Status, string ExceptionDetails)>();

        _internalManager
            .When(x => x.UpdateTickerAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>()))
            .Do(callInfo =>
            {
                var ctx = callInfo.Arg<InternalFunctionContext>();
                observedUpdates.Add((ctx.Status, ctx.ExceptionDetails));
            });

        await _handler.ExecuteTaskAsync(context, isDue: false);

        Assert.Contains(observedUpdates, x =>
            x.Status == TickerStatus.InProgress &&
            !string.IsNullOrWhiteSpace(x.ExceptionDetails));
    }

    #endregion

    #region Cancellation Path

    [Fact]
    public async Task ExecuteTaskAsync_SetsStatusCancelled_WhenTaskCancelledExceptionThrown()
    {
        var context = CreateContext(ct: (_, _, _) => throw new TaskCanceledException("cancelled"));

        await _handler.ExecuteTaskAsync(context, isDue: false);

        Assert.Equal(TickerStatus.Cancelled, context.Status);
    }

    /// <summary>The token each UpdateTickerAsync call was given, in order.</summary>
    private List<CancellationToken> RecordUpdateTokens()
    {
        var tokens = new List<CancellationToken>();
        _internalManager.UpdateTickerAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>())
            .Returns(call => { tokens.Add(call.ArgAt<CancellationToken>(1)); return Task.FromResult(true); });
        return tokens;
    }

    [Fact]
    public async Task ACancelledJob_IsRecordedAsCancelled_WithAWriteThatTheCancellationCannotStop()
    {
        // The write is what keeps a cancelled job from staying InProgress: with the cancelled token it fails on EF Core.
        var tokens = RecordUpdateTokens();
        using var cancelled = new CancellationTokenSource();
        var context = CreateContext(ct: async (token, _, _) =>
        {
            cancelled.Cancel();
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        });

        await _handler.ExecuteTaskAsync(context, isDue: false, cancelled.Token);

        Assert.Equal(TickerStatus.Cancelled, context.Status);
        var written = Assert.Single(tokens);
        Assert.False(written.IsCancellationRequested);
        Assert.False(written.CanBeCanceled);
    }

    [Fact]
    public async Task APlainOperationCanceledException_IsCancelled_WhenTheJobsOwnTokenWasCancelled()
    {
        var tokens = RecordUpdateTokens();
        using var cancelled = new CancellationTokenSource();
        var context = CreateContext(ct: (token, _, _) =>
        {
            cancelled.Cancel();
            token.ThrowIfCancellationRequested();     // throws OperationCanceledException, not TaskCanceledException
            return Task.CompletedTask;
        });

        await _handler.ExecuteTaskAsync(context, isDue: false, cancelled.Token);

        Assert.Equal(TickerStatus.Cancelled, context.Status);
        Assert.False(Assert.Single(tokens).IsCancellationRequested);
    }

    [Fact]
    public async Task APlainOperationCanceledException_IsAFailure_WhenNobodyCancelledTheJob()
    {
        var context = CreateContext(ct: (_, _, _) => throw new OperationCanceledException("a timeout inside the job"));

        await _handler.ExecuteTaskAsync(context, isDue: false);

        Assert.Equal(TickerStatus.Failed, context.Status);
    }

    /// <summary>
    /// HttpClient reports its own timeout as a TaskCanceledException that carries a TimeoutException, although nobody cancelled
    /// the job. That is a failure like any other, so it is retried and recorded as Failed, and the exception handler hears about
    /// it as one. A real HttpClient and a server that never answers make sure the test follows what HttpClient does.
    /// </summary>
    [Fact]
    public async Task AnHttpClientTimeoutInsideTheJob_IsAFailureThatIsRetried()
    {
        var exceptionHandler = Substitute.For<ITickerExceptionHandler>();
        var services = new ServiceCollection();
        services.AddSingleton(_internalManager);
        services.AddSingleton(_instrumentation);
        services.AddSingleton(exceptionHandler);
        var handler = new TickerExecutionTaskHandler(services.BuildServiceProvider(), _clock, _instrumentation, _internalManager);

        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;   // accepts connections and never answers
            var attempts = 0;
            var context = CreateContext(ct: async (token, _, _) =>
            {
                Interlocked.Increment(ref attempts);
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(150) };
                await http.GetAsync($"http://127.0.0.1:{port}/", token);
            });
            context.Retries = 1;
            context.RetryIntervals = [0];

            await handler.ExecuteTaskAsync(context, isDue: false);

            Assert.Equal(TickerStatus.Failed, context.Status);
            Assert.Equal(2, attempts);
            await exceptionHandler.Received().HandleExceptionAsync(Arg.Any<Exception>(), context.TickerId, context.Type);
            await exceptionHandler.DidNotReceive().HandleCanceledExceptionAsync(Arg.Any<Exception>(), Arg.Any<Guid>(), Arg.Any<TickerType>());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ATaskCanceledExceptionWithATimeoutInside_IsStillCancelled_WhenTheJobsOwnTokenWasCancelled()
    {
        using var cancelled = new CancellationTokenSource();
        var context = CreateContext(ct: (_, _, _) =>
        {
            cancelled.Cancel();
            throw new TaskCanceledException("cancelled", new TimeoutException());
        });

        await _handler.ExecuteTaskAsync(context, isDue: false, cancelled.Token);

        Assert.Equal(TickerStatus.Cancelled, context.Status);
    }

    [Fact]
    public async Task ATaskCanceledExceptionWithoutATimeoutInside_IsStillCancelled()
    {
        // What Task.Delay(delay, token) throws when the token is cancelled: a cancelled task counts as a cancellation.
        var context = CreateContext(ct: (_, _, _) => throw new TaskCanceledException("cancelled", new InvalidOperationException()));

        await _handler.ExecuteTaskAsync(context, isDue: false);

        Assert.Equal(TickerStatus.Cancelled, context.Status);
    }

    [Fact]
    public async Task ASucceededJob_IsRecordedAsDone_EvenWhenTheTokenWasCancelledMeanwhile()
    {
        var tokens = RecordUpdateTokens();
        using var cancelled = new CancellationTokenSource();
        var context = CreateContext(ct: (_, _, _) => { cancelled.Cancel(); return Task.CompletedTask; });

        await _handler.ExecuteTaskAsync(context, isDue: false, cancelled.Token);

        Assert.Equal(TickerStatus.Done, context.Status);
        Assert.False(Assert.Single(tokens).IsCancellationRequested);
    }

    [Fact]
    public async Task AFailedJob_IsRecordedAsFailed_EvenWhenTheTokenWasCancelledMeanwhile()
    {
        var tokens = RecordUpdateTokens();
        using var cancelled = new CancellationTokenSource();
        var context = CreateContext(ct: (_, _, _) => { cancelled.Cancel(); throw new InvalidOperationException("boom"); });

        await _handler.ExecuteTaskAsync(context, isDue: false, cancelled.Token);

        Assert.Equal(TickerStatus.Failed, context.Status);
        Assert.All(tokens.TakeLast(1), t => Assert.False(t.IsCancellationRequested));
    }

    [Fact]
    public async Task ExecuteTaskAsync_CallsCancelledExceptionHandler_WhenRegistered()
    {
        var exceptionHandler = Substitute.For<ITickerExceptionHandler>();
        var services = new ServiceCollection();
        services.AddSingleton(_internalManager);
        services.AddSingleton(_instrumentation);
        services.AddSingleton(exceptionHandler);
        var sp = services.BuildServiceProvider();
        var handler = new TickerExecutionTaskHandler(sp, _clock, _instrumentation, _internalManager);

        var context = CreateContext(ct: (_, _, _) => throw new TaskCanceledException("cancelled"));

        await handler.ExecuteTaskAsync(context, isDue: false);

        await exceptionHandler.Received(1).HandleCanceledExceptionAsync(
            Arg.Any<Exception>(),
            Arg.Is(context.TickerId),
            Arg.Is(context.Type));
    }

    #endregion

    #region TerminateExecutionException Path

    [Fact]
    public async Task ExecuteTaskAsync_SetsStatusSkipped_WhenTerminateExecutionExceptionThrown()
    {
        var context = CreateContext(ct: (_, _, _) => throw new TerminateExecutionException("skip me"));

        await _handler.ExecuteTaskAsync(context, isDue: false);

        Assert.Equal(TickerStatus.Skipped, context.Status);
    }

    [Fact]
    public async Task ExecuteTaskAsync_SetsCustomStatus_WhenTerminateExecutionWithStatusThrown()
    {
        var context = CreateContext(ct: (_, _, _) =>
            throw new TerminateExecutionException(TickerStatus.Cancelled, "terminate as cancelled"));

        await _handler.ExecuteTaskAsync(context, isDue: false);

        Assert.Equal(TickerStatus.Cancelled, context.Status);
    }

    [Fact]
    public async Task ExecuteTaskAsync_RecordsExceptionMessage_WhenTerminateExecutionThrown()
    {
        var context = CreateContext(ct: (_, _, _) =>
            throw new TerminateExecutionException("skip reason"));

        await _handler.ExecuteTaskAsync(context, isDue: false);

        Assert.Contains("skip reason", context.ExceptionDetails);
    }

    [Fact]
    public async Task ExecuteTaskAsync_RecordsInnerExceptionMessage_WhenTerminateExecutionHasInner()
    {
        var inner = new InvalidOperationException("inner reason");
        var context = CreateContext(ct: (_, _, _) =>
            throw new TerminateExecutionException("outer", inner));

        await _handler.ExecuteTaskAsync(context, isDue: false);

        Assert.Contains("inner reason", context.ExceptionDetails);
    }

    #endregion

    #region Null CachedDelegate

    [Fact]
    public async Task ExecuteTaskAsync_SetsStatusFailed_WhenCachedDelegateIsNull()
    {
        var context = CreateContext(ct: null);

        await _handler.ExecuteTaskAsync(context, isDue: false);

        Assert.Equal(TickerStatus.Failed, context.Status);
        Assert.Contains("was not found", context.ExceptionDetails);
    }

    #endregion

    #region Parent-Child Execution (TimeTicker)

    [Fact]
    public async Task ExecuteTaskAsync_RunsInProgressChildren_ConcurrentlyWithParent()
    {
        var childExecuted = false;
        var parentContext = CreateContext(
            type: TickerType.TimeTicker,
            ct: (_, _, _) => Task.CompletedTask);

        var childContext = CreateContext(
            type: TickerType.TimeTicker,
            ct: (_, _, _) =>
            {
                childExecuted = true;
                return Task.CompletedTask;
            });
        childContext.RunCondition = RunCondition.InProgress;
        parentContext.TimeTickerChildren.Add(childContext);

        await _handler.ExecuteTaskAsync(parentContext, isDue: false);

        Assert.True(childExecuted);
    }

    [Fact]
    public async Task ExecuteTaskAsync_RunsOnSuccessChild_AfterParentSucceeds()
    {
        var childExecuted = false;
        var parentContext = CreateContext(
            type: TickerType.TimeTicker,
            ct: (_, _, _) => Task.CompletedTask);

        var childContext = CreateContext(
            type: TickerType.TimeTicker,
            ct: (_, _, _) =>
            {
                childExecuted = true;
                return Task.CompletedTask;
            });
        childContext.RunCondition = RunCondition.OnSuccess;
        parentContext.TimeTickerChildren.Add(childContext);

        await _handler.ExecuteTaskAsync(parentContext, isDue: false);

        Assert.True(childExecuted);
    }

    [Fact]
    public async Task ExecuteTaskAsync_SkipsOnSuccessChild_WhenParentFails()
    {
        var childExecuted = false;
        var parentContext = CreateContext(
            type: TickerType.TimeTicker,
            ct: (_, _, _) => throw new InvalidOperationException("parent fail"));

        var childContext = CreateContext(
            type: TickerType.TimeTicker,
            ct: (_, _, _) =>
            {
                childExecuted = true;
                return Task.CompletedTask;
            });
        childContext.RunCondition = RunCondition.OnSuccess;
        parentContext.TimeTickerChildren.Add(childContext);

        await _handler.ExecuteTaskAsync(parentContext, isDue: false);

        Assert.False(childExecuted);
    }

    [Fact]
    public async Task ExecuteTaskAsync_RunsOnFailureChild_WhenParentFails()
    {
        var childExecuted = false;
        var parentContext = CreateContext(
            type: TickerType.TimeTicker,
            ct: (_, _, _) => throw new InvalidOperationException("parent fail"));

        var childContext = CreateContext(
            type: TickerType.TimeTicker,
            ct: (_, _, _) =>
            {
                childExecuted = true;
                return Task.CompletedTask;
            });
        childContext.RunCondition = RunCondition.OnFailure;
        parentContext.TimeTickerChildren.Add(childContext);

        await _handler.ExecuteTaskAsync(parentContext, isDue: false);

        Assert.True(childExecuted);
    }

    [Fact]
    public async Task ExecuteTaskAsync_SkipsOnFailureChild_WhenParentSucceeds()
    {
        var childExecuted = false;
        var parentContext = CreateContext(
            type: TickerType.TimeTicker,
            ct: (_, _, _) => Task.CompletedTask);

        var childContext = CreateContext(
            type: TickerType.TimeTicker,
            ct: (_, _, _) =>
            {
                childExecuted = true;
                return Task.CompletedTask;
            });
        childContext.RunCondition = RunCondition.OnFailure;
        parentContext.TimeTickerChildren.Add(childContext);

        await _handler.ExecuteTaskAsync(parentContext, isDue: false);

        Assert.False(childExecuted);
    }

    [Fact]
    public async Task ExecuteTaskAsync_RunsOnAnyCompletedStatus_RegardlessOfParentOutcome()
    {
        var childExecutedOnSuccess = false;
        var childExecutedOnFail = false;

        // Test with parent success
        var parentSuccess = CreateContext(
            type: TickerType.TimeTicker,
            ct: (_, _, _) => Task.CompletedTask);
        var childSuccess = CreateContext(
            type: TickerType.TimeTicker,
            ct: (_, _, _) => { childExecutedOnSuccess = true; return Task.CompletedTask; });
        childSuccess.RunCondition = RunCondition.OnAnyCompletedStatus;
        parentSuccess.TimeTickerChildren.Add(childSuccess);
        await _handler.ExecuteTaskAsync(parentSuccess, isDue: false);

        // Test with parent failure
        var parentFail = CreateContext(
            type: TickerType.TimeTicker,
            ct: (_, _, _) => throw new InvalidOperationException("fail"));
        var childFail = CreateContext(
            type: TickerType.TimeTicker,
            ct: (_, _, _) => { childExecutedOnFail = true; return Task.CompletedTask; });
        childFail.RunCondition = RunCondition.OnAnyCompletedStatus;
        parentFail.TimeTickerChildren.Add(childFail);
        await _handler.ExecuteTaskAsync(parentFail, isDue: false);

        Assert.True(childExecutedOnSuccess);
        Assert.True(childExecutedOnFail);
    }

    [Fact]
    public async Task ExecuteTaskAsync_RunsOnFailureOrCancelled_WhenParentFails()
    {
        var childExecuted = false;
        var parentContext = CreateContext(
            type: TickerType.TimeTicker,
            ct: (_, _, _) => throw new InvalidOperationException("fail"));

        var childContext = CreateContext(
            type: TickerType.TimeTicker,
            ct: (_, _, _) => { childExecuted = true; return Task.CompletedTask; });
        childContext.RunCondition = RunCondition.OnFailureOrCancelled;
        parentContext.TimeTickerChildren.Add(childContext);

        await _handler.ExecuteTaskAsync(parentContext, isDue: false);

        Assert.True(childExecuted);
    }

    [Fact]
    public async Task ExecuteTaskAsync_BulkSkipsDescendants_WhenChildConditionNotMet()
    {
        var parentContext = CreateContext(
            type: TickerType.TimeTicker,
            ct: (_, _, _) => Task.CompletedTask);

        var childContext = CreateContext(
            type: TickerType.TimeTicker,
            ct: (_, _, _) => Task.CompletedTask);
        childContext.RunCondition = RunCondition.OnFailure;

        var grandChild = CreateContext(
            type: TickerType.TimeTicker,
            ct: (_, _, _) => Task.CompletedTask);
        grandChild.RunCondition = RunCondition.OnSuccess;
        childContext.TimeTickerChildren.Add(grandChild);

        parentContext.TimeTickerChildren.Add(childContext);

        await _handler.ExecuteTaskAsync(parentContext, isDue: false);

        // The skipped children should be bulk-updated
        await _internalManager.Received().UpdateSkipTimeTickersWithUnifiedContextAsync(
            Arg.Any<InternalFunctionContext[]>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteTaskAsync_ChildWithNullDelegate_IsSkipped()
    {
        var parentContext = CreateContext(
            type: TickerType.TimeTicker,
            ct: (_, _, _) => Task.CompletedTask);

        var nullDelegateChild = new InternalFunctionContext
        {
            TickerId = Guid.NewGuid(),
            FunctionName = "NullChild",
            Type = TickerType.TimeTicker,
            CachedDelegate = null,
            RunCondition = RunCondition.OnSuccess,
            TimeTickerChildren = []
        };
        parentContext.TimeTickerChildren.Add(nullDelegateChild);

        // Should not throw
        await _handler.ExecuteTaskAsync(parentContext, isDue: false);
    }

    #endregion

    #region CronTickerOccurrence - direct path

    [Fact]
    public async Task ExecuteTaskAsync_CronTicker_GoesDirectlyToRunContextFunction()
    {
        var executed = false;
        var context = CreateContext(
            type: TickerType.CronTickerOccurrence,
            ct: (_, _, _) => { executed = true; return Task.CompletedTask; });

        await _handler.ExecuteTaskAsync(context, isDue: false);

        Assert.True(executed);
        Assert.Equal(TickerStatus.Done, context.Status);
    }

    #endregion

    #region Instrumentation

    [Fact]
    public async Task ExecuteTaskAsync_LogsJobEnqueued_OnExecution()
    {
        var context = CreateContext(ct: (_, _, _) => Task.CompletedTask);

        await _handler.ExecuteTaskAsync(context, isDue: false);

        _instrumentation.Received().LogJobEnqueued(
            Arg.Any<string>(),
            Arg.Is(context.FunctionName),
            Arg.Is(context.TickerId),
            Arg.Any<string>());
    }

    [Fact]
    public async Task ExecuteTaskAsync_LogsJobCompleted_OnSuccess()
    {
        var context = CreateContext(ct: (_, _, _) => Task.CompletedTask);

        await _handler.ExecuteTaskAsync(context, isDue: false);

        _instrumentation.Received().LogJobCompleted(
            Arg.Is(context.TickerId),
            Arg.Is(context.FunctionName),
            Arg.Any<long>(),
            Arg.Is(true));
    }

    [Fact]
    public async Task ExecuteTaskAsync_LogsJobFailed_OnFailure()
    {
        var context = CreateContext(ct: (_, _, _) => throw new InvalidOperationException("fail"));

        await _handler.ExecuteTaskAsync(context, isDue: false);

        _instrumentation.Received().LogJobFailed(
            Arg.Is(context.TickerId),
            Arg.Is(context.FunctionName),
            Arg.Any<Exception>(),
            Arg.Any<int>());
    }

    [Fact]
    public async Task ExecuteTaskAsync_LogsJobCancelled_OnCancellation()
    {
        var context = CreateContext(ct: (_, _, _) => throw new TaskCanceledException());

        await _handler.ExecuteTaskAsync(context, isDue: false);

        _instrumentation.Received().LogJobCancelled(
            Arg.Is(context.TickerId),
            Arg.Is(context.FunctionName),
            Arg.Any<string>());
    }

    [Fact]
    public async Task ExecuteTaskAsync_LogsJobSkipped_OnTerminateExecution()
    {
        var context = CreateContext(ct: (_, _, _) =>
            throw new TerminateExecutionException("skipped reason"));

        await _handler.ExecuteTaskAsync(context, isDue: false);

        _instrumentation.Received().LogJobSkipped(
            Arg.Is(context.TickerId),
            Arg.Is(context.FunctionName),
            Arg.Any<string>());
    }

    #endregion

    #region Outcome not stored

    // Each final outcome a job can end with, and a function that ends with it.
    public static TheoryData<TickerStatus> FinalOutcomes => new()
    {
        TickerStatus.Done, TickerStatus.Failed, TickerStatus.Cancelled, TickerStatus.Skipped
    };

    private static TickerFunctionDelegate EndingWith(TickerStatus outcome) => outcome switch
    {
        TickerStatus.Done => (_, _, _) => Task.CompletedTask,
        TickerStatus.Failed => (_, _, _) => throw new InvalidOperationException("the job failed"),
        TickerStatus.Cancelled => (_, _, _) => throw new TaskCanceledException("the job was cancelled"),
        TickerStatus.Skipped => (_, _, _) => throw new TerminateExecutionException(TickerStatus.Skipped, "the job skipped itself"),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    private void TheStoreRefusesEveryWrite() =>
        _internalManager.UpdateTickerAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new InvalidOperationException("the store is down")));

    private (TickerExecutionTaskHandler Handler, CapturedLogs Logs) HandlerWithLogs()
    {
        var logs = new CapturedLogs();
        var services = new ServiceCollection();
        services.AddSingleton(_internalManager);
        services.AddSingleton(_instrumentation);
        services.AddLogging(b => b.AddProvider(logs));
        return (new TickerExecutionTaskHandler(services.BuildServiceProvider(), _clock, _instrumentation, _internalManager), logs);
    }

    // The final status write can fail, for example while the store is unreachable. The job has ended all the same, so it must
    // leave the registry of running jobs: a stop would otherwise wait for it and record it as Cancelled, and with a parent id
    // every later occurrence of its cron ticker would see it as running and skip itself.
    [Theory]
    [MemberData(nameof(FinalOutcomes))]
    public async Task ExecuteTaskAsync_WhenTheFinalWriteFails_TheJobIsNoLongerListedAsRunning(TickerStatus outcome)
    {
        var parentId = Guid.NewGuid();
        var context = CreateContext(ct: EndingWith(outcome));
        context.ParentId = parentId;
        TheStoreRefusesEveryWrite();

        var failure = await Record.ExceptionAsync(() => _handler.ExecuteTaskAsync(context, isDue: false));

        Assert.Null(failure);
        Assert.Equal(outcome, context.Status);
        Assert.Empty(TickerCancellationTokenManager.GetRunningJobs(_handler));
        Assert.False(TickerCancellationTokenManager.IsParentRunning(parentId),
            "the ended job still counts as a running occurrence of its cron ticker");
    }

    // A lost outcome leaves the row InProgress in the store, so it is logged where an operator looks: as an error that names
    // the job and the outcome that was lost.
    [Theory]
    [MemberData(nameof(FinalOutcomes))]
    public async Task ExecuteTaskAsync_WhenTheFinalWriteFails_TheLostOutcomeIsLoggedAsAnError(TickerStatus outcome)
    {
        var (handler, logs) = HandlerWithLogs();
        var context = CreateContext(ct: EndingWith(outcome));
        TheStoreRefusesEveryWrite();

        await Record.ExceptionAsync(() => handler.ExecuteTaskAsync(context, isDue: false));

        var error = Assert.Single(logs.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(context.TickerId.ToString(), error.Message);
        Assert.Contains(context.FunctionName, error.Message);
        Assert.Contains($"The {outcome} outcome", error.Message);
        Assert.Equal("the store is down", error.Exception?.Message);
    }

    // A final write that matched no row (the row was deleted while the job ran) stored nothing either. That is logged as a
    // warning that names the job and the outcome, not as an error: there is no row left to be stuck.
    [Fact]
    public async Task ExecuteTaskAsync_WhenTheFinalWriteMatchesNoRow_TheOutcomeNotStoredIsLoggedAsAWarning()
    {
        var (handler, logs) = HandlerWithLogs();
        var context = CreateContext(type: TickerType.TimeTicker, ct: (_, _, _) => Task.CompletedTask);
        _internalManager.UpdateTickerAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>()).Returns(false);

        await handler.ExecuteTaskAsync(context, isDue: false);

        Assert.DoesNotContain(logs.Entries, e => e.Level == LogLevel.Error);
        var warning = Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(context.TickerId.ToString(), warning.Message);
        Assert.Contains(context.FunctionName, warning.Message);
        Assert.Contains("The Done outcome", warning.Message);
    }

    // The stop waits for the jobs that are still running and records each as Cancelled. A job whose final write failed has
    // ended, so the stop neither waits for it nor writes Cancelled over its row.
    [Fact]
    public async Task AStop_AfterAJobsFinalWriteFailed_NeitherWaitsForItNorRecordsItAsCancelled()
    {
        var context = CreateContext(ct: (_, _, _) => Task.CompletedTask);
        TheStoreRefusesEveryWrite();
        await Record.ExceptionAsync(() => _handler.ExecuteTaskAsync(context, isDue: false));

        var serviceManager = Substitute.For<IInternalTickerManager>();
        serviceManager.GetNextTickers(Arg.Any<CancellationToken>())
            .Returns((Timeout.InfiniteTimeSpan, Array.Empty<InternalFunctionContext>()));
        var service = new SharpTicker.BackgroundServices.SharpTickerSchedulerBackgroundService(
            new TickerExecutionContext(), _handler, Substitute.For<ISharpTickerTaskScheduler>(), serviceManager,
            new SchedulerOptionsBuilder { MinPollingInterval = TimeSpan.FromMilliseconds(50) }, new TickerFunctionConcurrencyGate())
        {
            ShutdownWaitLimit = TimeSpan.FromSeconds(5)
        };
        await service.StartAsync(CancellationToken.None);

        var stopping = System.Diagnostics.Stopwatch.StartNew();
        await service.StopAsync(CancellationToken.None);
        stopping.Stop();
        service.Dispose();

        await serviceManager.DidNotReceive().UpdateTickerAsync(
            Arg.Is<InternalFunctionContext>(c => c.TickerId == context.TickerId), Arg.Any<CancellationToken>());
        Assert.True(stopping.Elapsed < TimeSpan.FromSeconds(4), $"the stop waited {stopping.Elapsed} for a job that had ended");
    }

    // The write that records a failed attempt only keeps the dashboard current. When it fails, the retry still runs.
    [Fact]
    public async Task ExecuteTaskAsync_WhenRecordingAFailedAttemptFails_TheRetryStillRuns()
    {
        var calls = 0;
        var context = CreateContext(ct: (_, _, _) => ++calls == 1 ? throw new InvalidOperationException("first attempt fails") : Task.CompletedTask);
        context.Retries = 1;
        context.RetryIntervals = [0];
        var finalWrites = new List<TickerStatus>();
        _internalManager.UpdateTickerAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var written = call.Arg<InternalFunctionContext>();
                if (written.Status == TickerStatus.InProgress)
                    return Task.FromException<bool>(new InvalidOperationException("the store is down for a moment"));
                finalWrites.Add(written.Status);
                return Task.FromResult(true);
            });

        await _handler.ExecuteTaskAsync(context, isDue: false);

        Assert.Equal(2, calls);
        Assert.Equal([TickerStatus.Done], finalWrites);
    }

    // The write of the next retry's number is not an attempt of the job. When it fails, the retry still runs instead of the
    // failed write being counted as the retry having failed.
    [Fact]
    public async Task ExecuteTaskAsync_WhenRecordingTheNextRetryFails_TheRetryStillRuns()
    {
        var calls = 0;
        var context = CreateContext(ct: (_, _, _) => ++calls == 1 ? throw new InvalidOperationException("first attempt fails") : Task.CompletedTask);
        context.Retries = 1;
        context.RetryIntervals = [0];
        _internalManager.UpdateTickerAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var written = call.Arg<InternalFunctionContext>();
                return written.Status == TickerStatus.InProgress && written.RetryCount == 1
                    ? Task.FromException<bool>(new InvalidOperationException("the store is down for a moment"))
                    : Task.FromResult(true);
            });

        await _handler.ExecuteTaskAsync(context, isDue: false);

        Assert.Equal(2, calls);
        Assert.Equal(TickerStatus.Done, context.Status);
    }

    // The children follow the parent's outcome as the store has it. When the parent's final write fails, the store does not
    // have it, so no child runs and none is skipped: a child that runs on success, and one that would be skipped on success.
    [Fact]
    public async Task ExecuteTaskAsync_WhenTheParentsFinalWriteFails_NoChildRunsAndNoneIsSkipped()
    {
        var childRan = false;
        var parent = CreateContext(type: TickerType.TimeTicker, ct: (_, _, _) => Task.CompletedTask);
        var onSuccess = CreateContext(type: TickerType.TimeTicker, ct: (_, _, _) => { childRan = true; return Task.CompletedTask; });
        onSuccess.RunCondition = RunCondition.OnSuccess;
        var onFailure = CreateContext(type: TickerType.TimeTicker, ct: (_, _, _) => { childRan = true; return Task.CompletedTask; });
        onFailure.RunCondition = RunCondition.OnFailure;
        parent.TimeTickerChildren.Add(onSuccess);
        parent.TimeTickerChildren.Add(onFailure);
        _internalManager.UpdateTickerAsync(Arg.Is<InternalFunctionContext>(c => c.TickerId == parent.TickerId), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new InvalidOperationException("the store is down")));

        await _handler.ExecuteTaskAsync(parent, isDue: false);

        Assert.False(childRan, "a child ran although its parent's outcome was never stored");
        await _internalManager.DidNotReceive().UpdateSkipTimeTickersWithUnifiedContextAsync(
            Arg.Any<InternalFunctionContext[]>(), Arg.Any<CancellationToken>());
    }

    // The same when the write went through but matched no row: the store has no outcome to follow.
    [Fact]
    public async Task ExecuteTaskAsync_WhenTheParentsFinalWriteMatchesNoRow_NoChildRunsAndNoneIsSkipped()
    {
        var childRan = false;
        var parent = CreateContext(type: TickerType.TimeTicker, ct: (_, _, _) => Task.CompletedTask);
        var onSuccess = CreateContext(type: TickerType.TimeTicker, ct: (_, _, _) => { childRan = true; return Task.CompletedTask; });
        onSuccess.RunCondition = RunCondition.OnSuccess;
        var onFailure = CreateContext(type: TickerType.TimeTicker, ct: (_, _, _) => { childRan = true; return Task.CompletedTask; });
        onFailure.RunCondition = RunCondition.OnFailure;
        parent.TimeTickerChildren.Add(onSuccess);
        parent.TimeTickerChildren.Add(onFailure);
        _internalManager.UpdateTickerAsync(Arg.Is<InternalFunctionContext>(c => c.TickerId == parent.TickerId), Arg.Any<CancellationToken>())
            .Returns(false);

        await _handler.ExecuteTaskAsync(parent, isDue: false);

        Assert.False(childRan, "a child ran although its parent's outcome was never stored");
        await _internalManager.DidNotReceive().UpdateSkipTimeTickersWithUnifiedContextAsync(
            Arg.Any<InternalFunctionContext[]>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region Run marker

    // The store fences a run's writes with its RunMarker (#74): a claimed job's run uses the marker its claim wrote, and an
    // inline child the marker its start was recorded with. Without it every write would go out unfenced.
    [Fact]
    public async Task ExecuteTaskAsync_FencesTheWritesOfAClaimedJob_WithItsClaimMarker()
    {
        var claimMarker = new DateTime(2025, 6, 15, 12, 0, 0, 123, DateTimeKind.Utc);
        var context = CreateContext(type: TickerType.TimeTicker, ct: (_, _, _) => Task.CompletedTask);
        context.ClaimMarker = claimMarker;
        var markers = new List<DateTime?>();
        _internalManager.When(x => x.UpdateTickerAsync(Arg.Any<InternalFunctionContext>(), Arg.Any<CancellationToken>()))
            .Do(call => markers.Add(call.Arg<InternalFunctionContext>().RunMarker));

        await _handler.ExecuteTaskAsync(context, isDue: false);

        Assert.Equal([claimMarker], markers);
    }

    [Fact]
    public async Task ExecuteTaskAsync_FencesTheWritesOfAnInlineChild_WithTheMarkerOfItsStart()
    {
        var startMarker = new DateTime(2025, 6, 15, 12, 0, 1, 456, DateTimeKind.Utc);
        var parent = CreateContext(type: TickerType.TimeTicker, ct: (_, _, _) => Task.CompletedTask);
        parent.ClaimMarker = startMarker.AddSeconds(-1);
        var child = CreateContext(type: TickerType.TimeTicker, ct: (_, _, _) => Task.CompletedTask);
        child.RunCondition = RunCondition.OnSuccess;
        parent.TimeTickerChildren.Add(child);
        _internalManager.When(x => x.RecordChildStartedAsync(Arg.Is<InternalFunctionContext>(c => c.TickerId == child.TickerId), Arg.Any<CancellationToken>()))
            .Do(call => call.Arg<InternalFunctionContext>().RunMarker = startMarker);
        var childMarkers = new List<DateTime?>();
        _internalManager.When(x => x.UpdateTickerAsync(Arg.Is<InternalFunctionContext>(c => c.TickerId == child.TickerId), Arg.Any<CancellationToken>()))
            .Do(call => childMarkers.Add(call.Arg<InternalFunctionContext>().RunMarker));

        await _handler.ExecuteTaskAsync(parent, isDue: false);

        Assert.Equal([startMarker], childMarkers);
    }

    #endregion

    #region Helpers

    private static InternalFunctionContext CreateContext(
        TickerFunctionDelegate? ct = null,
        TickerType type = TickerType.CronTickerOccurrence)
    {
        return new InternalFunctionContext
        {
            TickerId = Guid.NewGuid(),
            FunctionName = "TestFunction",
            Type = type,
            ExecutionTime = DateTime.UtcNow,
            RetryIntervals = [],
            Retries = 0,
            RetryCount = 0,
            Status = TickerStatus.Idle,
            CachedDelegate = ct,
            TimeTickerChildren = []
        };
    }

    #endregion
}
