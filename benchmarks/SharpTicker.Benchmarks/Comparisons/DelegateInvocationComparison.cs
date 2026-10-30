using System.Collections.Frozen;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using SharpTicker.Utilities;

namespace SharpTicker.Benchmarks.Comparisons;

/// <summary>
/// Compares function dispatch mechanisms:
/// - SharpTicker: pre-compiled delegate via FrozenDictionary (source-generated at build time)
/// - Reflection: MethodInfo.Invoke (traditional approach used by older schedulers)
/// - Compiled delegate from MethodInfo (middle ground)
///
/// This isolates the per-invocation cost of finding and calling a job method.
///
/// Results Overview (Apple M4 Pro, .NET 10.0):
/// ┌──────────────────────────────────┬───────────┬───────┬──────────────┐
/// │ Method                           │ Time      │ Alloc │ vs SharpTicker   │
/// ├──────────────────────────────────┼───────────┼───────┼──────────────┤
/// │ SharpTicker: Lookup + invoke         │ 1.38 ns   │ 0 B   │ 1x (baseline)│
/// │ SharpTicker: Invoke cached delegate  │ ~0 ns     │ 0 B   │ -            │
/// │ Compiled: CreateDelegate+invoke  │ ~0 ns     │ 0 B   │ -            │
/// │ Reflection: MethodInfo.Invoke    │ 14.6 ns   │ 64 B  │ 10.6x slower │
/// └──────────────────────────────────┴───────────┴───────┴──────────────┘
/// Winner: SharpTicker — 10.6x faster than reflection, zero allocations.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.HostProcess)]
public class DelegateInvocationComparison
{
    private TickerFunctionDelegate _sharptickerDelegate = null!;
    private FrozenDictionary<string, TickerFunctionDelegate> _sharptickerRegistry = null!;
    private MethodInfo _reflectionMethod = null!;
    private object _reflectionTarget = null!;
    private Func<string, int, Task> _compiledDelegate = null!;

    private const string FunctionKey = "MyApp.Jobs.ProcessOrder";

    [GlobalSetup]
    public void Setup()
    {
        // SharpTicker: source-generated delegate in FrozenDictionary
        _sharptickerDelegate = (_, _, _) => Task.CompletedTask;
        var dict = new Dictionary<string, TickerFunctionDelegate>
        {
            [FunctionKey] = _sharptickerDelegate,
            ["MyApp.Jobs.SendEmail"] = (_, _, _) => Task.CompletedTask,
            ["MyApp.Jobs.GenerateReport"] = (_, _, _) => Task.CompletedTask,
        };
        _sharptickerRegistry = dict.ToFrozenDictionary();

        // Reflection: traditional approach
        _reflectionTarget = new SampleJobClass();
        _reflectionMethod = typeof(SampleJobClass).GetMethod(nameof(SampleJobClass.ProcessOrder))!;

        // Compiled delegate: middle ground
        _compiledDelegate = _reflectionMethod.CreateDelegate<Func<string, int, Task>>(_reflectionTarget);
    }

    // ── SharpTicker: lookup + invoke pre-compiled delegate ──

    [Benchmark(Baseline = true, Description = "SharpTicker: Lookup + invoke delegate")]
    public Task SharpTicker_LookupAndInvoke()
    {
        _sharptickerRegistry.TryGetValue(FunctionKey, out var del);
        return del!(CancellationToken.None, null!, null!);
    }

    // ── SharpTicker: invoke cached delegate (no lookup) ──

    [Benchmark(Description = "SharpTicker: Invoke cached delegate")]
    public Task SharpTicker_InvokeCached() =>
        _sharptickerDelegate(CancellationToken.None, null!, null!);

    // ── Reflection: MethodInfo.Invoke ──

    [Benchmark(Description = "Reflection: MethodInfo.Invoke")]
    public object? Reflection_Invoke() =>
        _reflectionMethod.Invoke(_reflectionTarget, ["order-123", 1]);

    // ── Compiled delegate from reflection ──

    [Benchmark(Description = "Compiled: CreateDelegate + invoke")]
    public Task Compiled_Invoke() =>
        _compiledDelegate("order-123", 1);

    public class SampleJobClass
    {
        public Task ProcessOrder(string orderId, int priority) => Task.CompletedTask;
    }
}
