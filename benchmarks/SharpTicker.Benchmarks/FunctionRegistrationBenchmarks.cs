using System.Collections.Frozen;
using BenchmarkDotNet.Attributes;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Enums;

namespace SharpTicker.Benchmarks;

/// <summary>
/// Cost of building a function registry of the same shape as TickerFunctionProvider.TickerFunctions:
/// ToFrozenDictionary against copying into a plain Dictionary. This measures the collection types only, not the
/// whole registration that TickerFunctionProvider does at startup.
/// </summary>
[MemoryDiagnoser]
public class FunctionRegistrationBenchmarks
{
    private Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)> _functions = null!;

    [Params(10, 50, 200)]
    public int FunctionCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        TickerFunctionDelegate noopDelegate = (_, _, _) => Task.CompletedTask;

        _functions = new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)>();
        for (int i = 0; i < FunctionCount; i++)
        {
            _functions[$"MyApp.Jobs.Function_{i}"] = ($"*/5 * * * * *", TickerTaskPriority.Normal, noopDelegate, 0);
        }
    }

    [Benchmark(Description = "Build FrozenDictionary from registrations")]
    public FrozenDictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)> BuildFrozenDictionary() =>
        _functions.ToFrozenDictionary();

    [Benchmark(Baseline = true, Description = "Build Dictionary (baseline)")]
    public Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate, int)> BuildDictionary() =>
        new(_functions);
}
