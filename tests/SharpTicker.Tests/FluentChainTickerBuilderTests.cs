using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Managers;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// The fluent builder for a chain of time tickers: one parent, up to five children, each with up to five
/// grandchildren, every slot usable once. The five slots at each level are separate methods with separate return
/// types, so the slot tests call them by name.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class FluentChainTickerBuilderTests
{
    private static readonly string[] Ordinals = { "First", "Second", "Third", "Fourth", "Fifth" };

    private sealed record Order(string OrderId, decimal Amount);

    private static FluentChainTickerBuilder<TimeTickerEntity> Begin(Action<ParentBuilder<TimeTickerEntity>>? configure = null)
        => FluentChainTickerBuilder<TimeTickerEntity>.BeginWith(configure ?? (_ => { }));

    /// <summary>Calls WithFirstChild .. WithFifthChild on the root builder, or the same slot on a child builder.</summary>
    private static object Call(object builder, string method, object configure)
    {
        var target = builder.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance)
                     ?? throw new MissingMethodException(builder.GetType().Name, method);
        try
        {
            return target.Invoke(builder, new[] { configure })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static object AddChild(object builder, int slot, Action<ChildBuilder<TimeTickerEntity>>? configure = null)
        => Call(builder, $"With{Ordinals[slot]}Child", configure ?? (_ => { }));

    private static object AddGrandChild(object childBuilder, int slot, Action<GrandChildBuilder<TimeTickerEntity>>? configure = null)
        => Call(childBuilder, $"With{Ordinals[slot]}GrandChild", configure ?? (_ => { }));

    public static IEnumerable<object[]> Slots() => Enumerable.Range(0, 5).Select(slot => new object[] { slot });

    public static IEnumerable<object[]> ChildAndGrandChildSlots() =>
        from child in Enumerable.Range(0, 5)
        from grandChild in Enumerable.Range(0, 5)
        select new object[] { child, grandChild };

    #region Parent

    [Fact]
    public void BeginWith_CreatesAParentWithAnIdAndNoChildren()
    {
        var parent = Begin().Build();

        Assert.NotEqual(Guid.Empty, parent.Id);
        Assert.Null(parent.ParentId);
        Assert.Empty(parent.Children);
        Assert.InRange(parent.CreatedAt, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
        // Two separate clock reads, so they can differ by a few ticks, more on a slow machine.
        Assert.InRange(parent.UpdatedAt, parent.CreatedAt, parent.CreatedAt.AddSeconds(5));
    }

    [Fact]
    public void BeginWith_GivesEveryChainItsOwnIds()
    {
        Assert.NotEqual(Begin().Build().Id, Begin().Build().Id);
    }

    [Fact]
    public void ParentBuilder_SetsFunctionDescriptionAndExecutionTime()
    {
        var at = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var parent = Begin(p => p.SetFunction("Import").SetDescription("nightly import").SetExecutionTime(at)).Build();

        Assert.Equal("Import", parent.Function);
        Assert.Equal("nightly import", parent.Description);
        Assert.Equal(at, parent.ExecutionTime);
    }

    [Fact]
    public void ParentBuilder_SetsRetriesWithTheirIntervals()
    {
        var parent = Begin(p => p.SetRetries(3, 10, 30, 60)).Build();

        Assert.Equal(3, parent.Retries);
        Assert.Equal(new[] { 10, 30, 60 }, parent.RetryIntervals);
    }

    [Fact]
    public void ParentBuilder_SetRetriesWithoutIntervals_LeavesAnEmptyList()
    {
        var parent = Begin(p => p.SetRetries(2)).Build();

        Assert.Equal(2, parent.Retries);
        Assert.Empty(parent.RetryIntervals);
    }

    [Fact]
    public void ParentBuilder_SetRequest_StoresTheSameBytesAsTheRequestHelper()
    {
        var order = new Order("A-1", 10.50m);

        var parent = Begin(p => p.SetRequest(order)).Build();

        Assert.Equal(TickerHelper.CreateTickerRequest(order), parent.Request);
        Assert.Equal(order, TickerHelper.ReadTickerRequest<Order>(parent.Request));
    }

    #endregion

    #region Children

    [Theory]
    [MemberData(nameof(Slots))]
    public void EachChildSlot_AddsAChildThatPointsAtTheParent(int slot)
    {
        var root = Begin(p => p.SetFunction("Root"));

        AddChild(root, slot, c => c.SetFunction($"Child{slot}"));

        var parent = root.Build();
        var child = Assert.Single(parent.Children);
        Assert.Equal($"Child{slot}", child.Function);
        Assert.Equal(parent.Id, child.ParentId);
        Assert.NotEqual(parent.Id, child.Id);
        Assert.Empty(child.Children);
    }

    [Fact]
    public void ChildBuilder_SetsFunctionDescriptionRunConditionAndExecutionTime()
    {
        var at = new DateTime(2031, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        var root = Begin();

        AddChild(root, 0, c => c.SetFunction("Notify").SetDescription("tell people").SetRunCondition(RunCondition.OnSuccess).SetExecutionTime(at));

        var child = root.Build().Children.Single();
        Assert.Equal("Notify", child.Function);
        Assert.Equal("tell people", child.Description);
        Assert.Equal(RunCondition.OnSuccess, child.RunCondition);
        Assert.Equal(at, child.ExecutionTime);
    }

    [Fact]
    public void ChildBuilder_SetsRetriesAndRequest()
    {
        var root = Begin();
        var order = new Order("B-2", 5m);

        AddChild(root, 0, c => c.SetRetries(1, 5).SetRequest(order));

        var child = root.Build().Children.Single();
        Assert.Equal(1, child.Retries);
        Assert.Equal(new[] { 5 }, child.RetryIntervals);
        Assert.Equal(order, TickerHelper.ReadTickerRequest<Order>(child.Request));
    }

    [Fact]
    public void Children_KeepTheOrderTheyWereConfiguredIn_AndSlotsMaySkip()
    {
        var root = Begin();

        var afterThird = AddChild(root, 2, c => c.SetFunction("third"));
        var afterFirst = AddChild(afterThird, 3, c => c.SetFunction("fourth"));

        Assert.Equal(new[] { "third", "fourth" }, root.Build().Children.Select(c => c.Function));
        Assert.NotNull(afterFirst);
    }

    [Theory]
    [MemberData(nameof(Slots))]
    public void AChildSlotCanOnlyBeUsedOnce(int slot)
    {
        var root = Begin();
        AddChild(root, slot);

        var error = Assert.Throws<InvalidOperationException>(() => AddChild(root, slot));

        Assert.Equal($"{Ordinals[slot]} child has already been configured", error.Message);
        Assert.Single(root.Build().Children);
    }

    #endregion

    #region Grandchildren

    [Theory]
    [MemberData(nameof(ChildAndGrandChildSlots))]
    public void EachGrandChildSlotOfEachChild_AddsAGrandChildThatPointsAtItsChild(int childSlot, int grandChildSlot)
    {
        var root = Begin();
        var child = AddChild(root, childSlot);

        AddGrandChild(child, grandChildSlot, g => g.SetFunction("Grand"));

        var parent = root.Build();
        var childEntity = Assert.Single(parent.Children);
        var grandChild = Assert.Single(childEntity.Children);
        Assert.Equal("Grand", grandChild.Function);
        Assert.Equal(childEntity.Id, grandChild.ParentId);
        Assert.NotEqual(parent.Id, grandChild.ParentId);
        Assert.Empty(grandChild.Children);
    }

    [Fact]
    public void GrandChildBuilder_SetsEverythingAChildCanExceptTheRunCondition()
    {
        var at = new DateTime(2032, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var root = Begin();
        var order = new Order("C-3", 1m);

        AddGrandChild(AddChild(root, 0), 0, g => g
            .SetFunction("Cleanup").SetDescription("tidy").SetExecutionTime(at).SetRetries(4, 1, 2).SetRequest(order));

        var grandChild = root.Build().Children.Single().Children.Single();
        Assert.Equal("Cleanup", grandChild.Function);
        Assert.Equal("tidy", grandChild.Description);
        Assert.Equal(at, grandChild.ExecutionTime);
        Assert.Equal(4, grandChild.Retries);
        Assert.Equal(new[] { 1, 2 }, grandChild.RetryIntervals);
        Assert.Equal(order, TickerHelper.ReadTickerRequest<Order>(grandChild.Request));
    }

    [Theory]
    [MemberData(nameof(ChildAndGrandChildSlots))]
    public void AGrandChildSlotCanOnlyBeUsedOnce(int childSlot, int grandChildSlot)
    {
        var root = Begin();
        var child = AddChild(root, childSlot);
        AddGrandChild(child, grandChildSlot);

        var error = Assert.Throws<InvalidOperationException>(() => AddGrandChild(child, grandChildSlot));

        Assert.Equal(
            $"{Ordinals[grandChildSlot]} grandchild of {Ordinals[childSlot].ToLowerInvariant()} child has already been configured",
            error.Message);
    }

    [Fact]
    public void TheSameGrandChildSlotCanBeUsedUnderDifferentChildren()
    {
        var root = Begin();
        var first = AddChild(root, 0);
        var second = AddChild(first, 1);

        AddGrandChild(first, 0);
        AddGrandChild(second, 0);

        var children = root.Build().Children;
        Assert.All(children, child => Assert.Single(child.Children));
    }

    #endregion

    #region The whole tree

    [Fact]
    public void AFullTree_HasThirtyOneTickers_WithUniqueIds_AndEveryParentIdPointingAtItsParent()
    {
        var root = Begin(p => p.SetFunction("Root"));
        object builder = root;
        for (var childSlot = 0; childSlot < 5; childSlot++)
        {
            builder = AddChild(builder, childSlot, c => c.SetFunction("Child"));
            for (var grandChildSlot = 0; grandChildSlot < 5; grandChildSlot++)
                builder = AddGrandChild(builder, grandChildSlot, g => g.SetFunction("Grand"));
        }

        var parent = root.Build();
        var all = new List<TimeTickerEntity> { parent };
        all.AddRange(parent.Children);
        all.AddRange(parent.Children.SelectMany(c => c.Children));

        Assert.Equal(31, all.Count);
        Assert.Equal(31, all.Select(t => t.Id).Distinct().Count());
        Assert.Equal(5, parent.Children.Count);
        Assert.All(parent.Children, child =>
        {
            Assert.Equal(parent.Id, child.ParentId);
            Assert.Equal(5, child.Children.Count);
            Assert.All(child.Children, grand => Assert.Equal(child.Id, grand.ParentId));
        });
    }

    #endregion

    #region Build and conversion

    [Fact]
    public void Build_ReturnsTheSameParentEveryTime()
    {
        var root = Begin();

        Assert.Same(root.Build(), root.Build());
    }

    [Fact]
    public void TheBuilderConvertsToTheTickerAtEveryLevel()
    {
        var root = Begin(p => p.SetFunction("Root"));
        var parent = root.Build();

        TimeTickerEntity fromRoot = root;
        var childBuilder = root.WithFirstChild(c => c.SetFunction("C"));
        TimeTickerEntity fromChild = childBuilder;
        var withGrandChild = childBuilder.WithFirstGrandChild(g => g.SetFunction("G"));
        TimeTickerEntity fromGrandChild = withGrandChild;

        Assert.Same(parent, fromRoot);
        Assert.Same(parent, fromChild);
        Assert.Same(parent, fromGrandChild);
    }

    [Fact]
    public void ChildBuildersContinueWithTheLaterSlots_AndBuildTheSameChain()
    {
        var chain = Begin(p => p.SetFunction("Root"))
            .WithFirstChild(c => c.SetFunction("A"))
            .WithSecondChild(c => c.SetFunction("B"))
            .WithThirdChild(c => c.SetFunction("C"))
            .WithFourthChild(c => c.SetFunction("D"))
            .WithFifthChild(c => c.SetFunction("E"))
            .Build();

        Assert.Equal(new[] { "A", "B", "C", "D", "E" }, chain.Children.Select(c => c.Function));
    }

    #endregion
}
