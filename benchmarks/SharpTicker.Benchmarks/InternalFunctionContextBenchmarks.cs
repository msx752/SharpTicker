using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Benchmarks;

/// <summary>
/// Cost of InternalFunctionContext.SetProperty against doing the same work by hand.
/// SetProperty assigns through a compiled setter (cached per property name) and records the property name in
/// ParametersToUpdate; the baselines do the same assignment and the same ParametersToUpdate.Add directly. Each SetProperty
/// call site also builds its expression tree on every call and boxes value types, which is part of what is measured.
/// One context is reused across invocations: assigning the same values and adding the same names again is idempotent.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class InternalFunctionContextBenchmarks
{
    private InternalFunctionContext _context = null!;
    private readonly DateTime _executedAt = new(2026, 3, 16, 12, 0, 0, DateTimeKind.Utc);

    [GlobalSetup]
    public void Setup()
    {
        _context = new InternalFunctionContext
        {
            FunctionName = "TestFunction",
            TickerId = Guid.NewGuid(),
            Type = TickerType.TimeTicker,
            Status = TickerStatus.Queued
        };
    }

    // ── 1 update ──

    [Benchmark(Baseline = true, Description = "1 update: direct + ParametersToUpdate.Add")]
    [BenchmarkCategory("1 update")]
    public InternalFunctionContext Direct_1()
    {
        _context.Status = TickerStatus.Done;
        _context.ParametersToUpdate.Add(nameof(InternalFunctionContext.Status));
        return _context;
    }

    [Benchmark(Description = "1 update: SetProperty")]
    [BenchmarkCategory("1 update")]
    public InternalFunctionContext SetProperty_1() =>
        _context.SetProperty(x => x.Status, TickerStatus.Done);

    // ── 3 updates ──

    [Benchmark(Baseline = true, Description = "3 updates: direct + ParametersToUpdate.Add")]
    [BenchmarkCategory("3 updates")]
    public InternalFunctionContext Direct_3()
    {
        _context.Status = TickerStatus.InProgress;
        _context.ParametersToUpdate.Add(nameof(InternalFunctionContext.Status));
        _context.ElapsedTime = 1234;
        _context.ParametersToUpdate.Add(nameof(InternalFunctionContext.ElapsedTime));
        _context.ExceptionDetails = null!;
        _context.ParametersToUpdate.Add(nameof(InternalFunctionContext.ExceptionDetails));
        return _context;
    }

    [Benchmark(Description = "3 updates: SetProperty")]
    [BenchmarkCategory("3 updates")]
    public InternalFunctionContext SetProperty_3() =>
        _context
            .SetProperty(x => x.Status, TickerStatus.InProgress)
            .SetProperty(x => x.ElapsedTime, 1234)
            .SetProperty(x => x.ExceptionDetails, null!);

    // ── 5 updates ──

    [Benchmark(Baseline = true, Description = "5 updates: direct + ParametersToUpdate.Add")]
    [BenchmarkCategory("5 updates")]
    public InternalFunctionContext Direct_5()
    {
        _context.Status = TickerStatus.Done;
        _context.ParametersToUpdate.Add(nameof(InternalFunctionContext.Status));
        _context.ElapsedTime = 5000;
        _context.ParametersToUpdate.Add(nameof(InternalFunctionContext.ElapsedTime));
        _context.RetryCount = 2;
        _context.ParametersToUpdate.Add(nameof(InternalFunctionContext.RetryCount));
        _context.ExceptionDetails = "timeout";
        _context.ParametersToUpdate.Add(nameof(InternalFunctionContext.ExceptionDetails));
        _context.ExecutedAt = _executedAt;
        _context.ParametersToUpdate.Add(nameof(InternalFunctionContext.ExecutedAt));
        return _context;
    }

    [Benchmark(Description = "5 updates: SetProperty")]
    [BenchmarkCategory("5 updates")]
    public InternalFunctionContext SetProperty_5() =>
        _context
            .SetProperty(x => x.Status, TickerStatus.Done)
            .SetProperty(x => x.ElapsedTime, 5000)
            .SetProperty(x => x.RetryCount, 2)
            .SetProperty(x => x.ExceptionDetails, "timeout")
            .SetProperty(x => x.ExecutedAt, _executedAt);
}
