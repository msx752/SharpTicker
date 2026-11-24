using System;
using System.Linq;
using System.Reflection;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Models;
using Xunit;

namespace SharpTicker.Tests;

public class InternalFunctionContextTests
{
    [Fact]
    public void SetProperty_Tracks_Updated_Properties()
    {
        var context = new InternalFunctionContext();

        context
            .SetProperty(c => c.Status, TickerStatus.InProgress)
            .SetProperty(c => c.ElapsedTime, 123L)
            .SetProperty(c => c.ReleaseLock, true);

        var updated = context.GetPropsToUpdate();

        Assert.Contains(nameof(InternalFunctionContext.Status), updated);
        Assert.Contains(nameof(InternalFunctionContext.ElapsedTime), updated);
        Assert.Contains(nameof(InternalFunctionContext.ReleaseLock), updated);
        Assert.Equal(3, updated.Count);

        Assert.Equal(TickerStatus.InProgress, context.Status);
        Assert.Equal(123L, context.ElapsedTime);
        Assert.True(context.ReleaseLock);
    }

    [Fact]
    public void ResetUpdateProps_Clears_Tracked_Properties()
    {
        var context = new InternalFunctionContext();

        context
            .SetProperty(c => c.Status, TickerStatus.Done)
            .SetProperty(c => c.ElapsedTime, 500L);

        Assert.NotEmpty(context.GetPropsToUpdate());

        context.ResetUpdateProps();

        Assert.Empty(context.GetPropsToUpdate());
    }

    [Fact]
    public void ResetUpdateProps_Does_Not_Reset_Property_Values()
    {
        var context = new InternalFunctionContext();

        context
            .SetProperty(c => c.Status, TickerStatus.Done)
            .SetProperty(c => c.ElapsedTime, 250L);

        context.ResetUpdateProps();

        Assert.Equal(TickerStatus.Done, context.Status);
        Assert.Equal(250L, context.ElapsedTime);
        Assert.Empty(context.GetPropsToUpdate());
    }

    [Fact]
    public void SetProperty_Reinitializes_Tracking_Set_When_Null()
    {
        var context = new InternalFunctionContext();

        // Simulate a null ParametersToUpdate set to verify the null-coalescing assignment.
        var parametersProperty = typeof(InternalFunctionContext)
            .GetProperty("ParametersToUpdate", BindingFlags.Instance | BindingFlags.Public);

        Assert.NotNull(parametersProperty);
        parametersProperty!.SetValue(context, null);

        context.SetProperty(c => c.Status, TickerStatus.InProgress);

        var updated = context.GetPropsToUpdate();
        Assert.NotNull(updated);
        Assert.Contains(nameof(InternalFunctionContext.Status), updated);
    }

    [Fact]
    public void SetProperty_Allows_Multiple_Updates_To_Same_Property()
    {
        var context = new InternalFunctionContext();

        context
            .SetProperty(c => c.Status, TickerStatus.InProgress)
            .SetProperty(c => c.Status, TickerStatus.Failed);

        Assert.Equal(TickerStatus.Failed, context.Status);

        var updated = context.GetPropsToUpdate();
        Assert.Contains(nameof(InternalFunctionContext.Status), updated);
        Assert.Equal(1, updated.Count);
    }

    [Fact]
    public void SetProperty_Throws_For_NonProperty_Expression()
    {
        var context = new InternalFunctionContext();

        var ex = Assert.Throws<ArgumentException>(() => context.SetProperty(c => c.ElapsedTime + 1, 10L));

        Assert.Contains("Expression must point to a property", ex.Message);
    }

    [Fact]
    public void TimeTickerChildren_Defaults_To_Empty_List_And_Can_Add()
    {
        var context = new InternalFunctionContext();

        Assert.NotNull(context.TimeTickerChildren);
        Assert.Empty(context.TimeTickerChildren);

        var child = new InternalFunctionContext
        {
            TickerId = Guid.NewGuid(),
            ParentId = Guid.NewGuid(),
            FunctionName = "ChildFunction"
        };

        context.TimeTickerChildren.Add(child);

        Assert.Single(context.TimeTickerChildren);
        Assert.Equal("ChildFunction", context.TimeTickerChildren.Single().FunctionName);
    }

    [Fact]
    public void CachedDelegate_And_Priority_Can_Be_Assigned()
    {
        var context = new InternalFunctionContext();
        TickerFunctionDelegate handler = (_, _, _) => default!;

        context.CachedDelegate = handler;
        context.CachedPriority = TickerTaskPriority.High;

        Assert.Same(handler, context.CachedDelegate);
        Assert.Equal(TickerTaskPriority.High, context.CachedPriority);
    }

    [Fact]
    public void SetProperty_Supports_Array_And_String_Properties()
    {
        var context = new InternalFunctionContext();
        var intervals = new[] { 1, 5, 10 };
        const string exceptionDetails = "Something went wrong";

        context
            .SetProperty(c => c.RetryIntervals, intervals)
            .SetProperty(c => c.ExceptionDetails, exceptionDetails);

        Assert.Same(intervals, context.RetryIntervals);
        Assert.Equal(exceptionDetails, context.ExceptionDetails);

        var updated = context.GetPropsToUpdate();
        Assert.Contains(nameof(InternalFunctionContext.RetryIntervals), updated);
        Assert.Contains(nameof(InternalFunctionContext.ExceptionDetails), updated);
    }

    // The setters SharpTicker uses on each run do what SetProperty does: the same value, and the same name recorded for the
    // store's write. A setter that sets the value but records no name, or another name, leaves the field out of the write.
    public static TheoryData<string> InternalSetters =>
    [
        nameof(InternalFunctionContext.Status), nameof(InternalFunctionContext.ExecutedAt), nameof(InternalFunctionContext.ElapsedTime),
        nameof(InternalFunctionContext.ExceptionDetails), nameof(InternalFunctionContext.RetryCount),
        nameof(InternalFunctionContext.ReleaseLock), nameof(InternalFunctionContext.ExecutionTime)
    ];

    [Theory]
    [MemberData(nameof(InternalSetters))]
    public void An_Internal_Setter_Sets_And_Records_What_SetProperty_Does(string property)
    {
        var at = new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc);
        var bySetProperty = new InternalFunctionContext();
        var bySetter = new InternalFunctionContext();
        switch (property)
        {
            case nameof(InternalFunctionContext.Status):
                bySetProperty.SetProperty(c => c.Status, TickerStatus.Failed);
                bySetter.SetStatus(TickerStatus.Failed);
                break;
            case nameof(InternalFunctionContext.ExecutedAt):
                bySetProperty.SetProperty(c => c.ExecutedAt, at);
                bySetter.SetExecutedAt(at);
                break;
            case nameof(InternalFunctionContext.ElapsedTime):
                bySetProperty.SetProperty(c => c.ElapsedTime, 42L);
                bySetter.SetElapsedTime(42L);
                break;
            case nameof(InternalFunctionContext.ExceptionDetails):
                bySetProperty.SetProperty(c => c.ExceptionDetails, "boom");
                bySetter.SetExceptionDetails("boom");
                break;
            case nameof(InternalFunctionContext.RetryCount):
                bySetProperty.SetProperty(c => c.RetryCount, 3);
                bySetter.SetRetryCount(3);
                break;
            case nameof(InternalFunctionContext.ReleaseLock):
                bySetProperty.SetProperty(c => c.ReleaseLock, true);
                bySetter.SetReleaseLock(true);
                break;
            case nameof(InternalFunctionContext.ExecutionTime):
                bySetProperty.SetProperty(c => c.ExecutionTime, at);
                bySetter.SetExecutionTime(at);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(property), property, null);
        }

        Assert.Equal([property], bySetter.GetPropsToUpdate());
        Assert.Equal(bySetProperty.GetPropsToUpdate(), bySetter.GetPropsToUpdate());
        var prop = typeof(InternalFunctionContext).GetProperty(property)!;
        Assert.Equal(prop.GetValue(bySetProperty), prop.GetValue(bySetter));
        Assert.NotEqual(prop.GetValue(new InternalFunctionContext()), prop.GetValue(bySetter));
    }

    [Fact]
    public void An_Internal_Setter_Starts_The_Tracking_Set_When_It_Is_Null()
    {
        var context = new InternalFunctionContext { ParametersToUpdate = null! };

        context.SetStatus(TickerStatus.Done);

        Assert.Equal([nameof(InternalFunctionContext.Status)], context.GetPropsToUpdate());
    }
}
