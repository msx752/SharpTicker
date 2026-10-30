using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SharpTicker.SourceGenerator.Tests;

/// <summary>
/// Runs the source generator over a piece of application code, the way the compiler does, and returns what it
/// generated and which diagnostics it reported.
/// </summary>
internal static class GeneratorHarness
{
    private const string Stubs = @"
namespace SharpTicker.Utilities.Base
{
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public class TickerFunctionAttribute : System.Attribute
    {
        public TickerFunctionAttribute(string functionName, string cronExpression = null, int taskPriority = 0, int maxConcurrency = 0) { }
    }

    [System.AttributeUsage(System.AttributeTargets.Constructor)]
    public class SharpTickerConstructorAttribute : System.Attribute { }

    public class TickerFunctionContext { }
    public class TickerFunctionContext<T> : TickerFunctionContext { public T Request { get; } }
}

namespace SharpTicker.Utilities.Interfaces
{
    public interface ITickerFunctionBase { }

    public interface ITickerFunction : ITickerFunctionBase
    {
        System.Threading.Tasks.Task ExecuteAsync(
            SharpTicker.Utilities.Base.TickerFunctionContext context,
            System.Threading.CancellationToken cancellationToken = default);
    }

    public interface ITickerFunction<TRequest> : ITickerFunctionBase
    {
        System.Threading.Tasks.Task ExecuteAsync(
            SharpTicker.Utilities.Base.TickerFunctionContext<TRequest> context,
            System.Threading.CancellationToken cancellationToken = default);
    }
}

namespace SharpTicker.Utilities
{
    public delegate System.Threading.Tasks.Task TickerFunctionDelegate(
        System.Threading.CancellationToken cancellationToken,
        System.IServiceProvider serviceProvider,
        SharpTicker.Utilities.Base.TickerFunctionContext context);

    public static class TickerFunctionProvider
    {
        public static void RegisterFunctions(
            System.Collections.Generic.Dictionary<string, (string, SharpTicker.Utilities.Enums.TickerTaskPriority, TickerFunctionDelegate, int)> f, int c) { }
        public static void RegisterRequestType(
            System.Collections.Generic.Dictionary<string, (string, System.Type)> t, int c) { }
    }
}

namespace Microsoft.Extensions.DependencyInjection
{
    public static class ServiceProviderServiceExtensions
    {
        public static T GetService<T>(this System.IServiceProvider provider) => default;
        public static T GetRequiredService<T>(this System.IServiceProvider provider) => default;
    }
}

namespace SharpTicker.Utilities
{
    public static class TickerRequestProvider
    {
        public static System.Threading.Tasks.Task<SharpTicker.Utilities.Base.TickerFunctionContext<T>> ToGenericContextAsync<T>(
            SharpTicker.Utilities.Base.TickerFunctionContext context, System.Threading.CancellationToken cancellationToken) => null;
    }
}

namespace SharpTicker.Utilities.Enums
{
    public enum TickerTaskPriority { LongRunning = 0, Low = 1, Normal = 2, High = 3 }
}
";

    public sealed record Result(ImmutableArray<Diagnostic> Diagnostics, IReadOnlyDictionary<string, string> Sources, Compilation Output)
    {
        public IEnumerable<string> Ids => Diagnostics.Select(d => d.Id);

        public string Factory => Sources.TryGetValue("SharpTickerInstanceFactory.g.cs", out var text) ? text : string.Empty;

        public string AllSources => string.Join("\n", Sources.Values);
    }

    public static Result Run(string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var references = new List<MetadataReference>();
        var trusted = ((string?)AppDomain.CurrentDomain.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator) ?? Array.Empty<string>();
        foreach (var path in trusted)
            if (File.Exists(path))
                references.Add(MetadataReference.CreateFromFile(path));

        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { CSharpSyntaxTree.ParseText(source, parseOptions), CSharpSyntaxTree.ParseText(Stubs, parseOptions) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new SharpTickerIncrementalSourceGenerator().AsSourceGenerator() },
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        var run = driver.GetRunResult();
        var sources = run.Results
            .SelectMany(r => r.GeneratedSources)
            .ToDictionary(s => s.HintName, s => s.SourceText.ToString());

        return new Result(run.Diagnostics, sources, output);
    }

    /// <summary>Compiler errors in the application code plus everything the generator produced.</summary>
    public static IEnumerable<string> CompileErrors(Result result)
        => result.Output.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString());
}
