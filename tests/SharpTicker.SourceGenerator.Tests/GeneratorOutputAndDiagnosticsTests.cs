namespace SharpTicker.SourceGenerator.Tests;

/// <summary>
/// What the generator produces for [TickerFunction] methods, and which build errors it reports for the mistakes an
/// application can make. A build error here stops the application from compiling, so the exact rules matter.
/// </summary>
public class GeneratorOutputAndDiagnosticsTests
{
    private static string Jobs(string members, string classModifiers = "public", string usings = "")
        => $@"
using System.Threading;
using System.Threading.Tasks;
using SharpTicker.Utilities.Base;
{usings}
namespace TestApp
{{
    public class Order {{ public string Id {{ get; set; }} }}

    {classModifiers} class Jobs
    {{
{members}
    }}
}}";

    private const string Context = "TickerFunctionContext c";

    #region Generated registration

    [Fact]
    public void AFunction_IsRegisteredWithItsNameCronPriorityAndConcurrency()
    {
        var result = GeneratorHarness.Run(Jobs(@"
        [TickerFunction(""Nightly"", ""0 0 2 * * *"", 3, 2)]
        public Task Nightly(TickerFunctionContext c, CancellationToken ct) => Task.CompletedTask;"));

        Assert.Empty(result.Ids);
        Assert.Contains(@"[""Nightly""] = (""0 0 2 * * *"", (global::SharpTicker.Utilities.Enums.TickerTaskPriority)3", result.Factory);
        Assert.Contains("Nightly(context, cancellationToken)", result.Factory);
        Assert.Contains("), 2),", result.Factory);
    }

    [Fact]
    public void TheGeneratedCode_CompilesWithoutErrors()
    {
        var result = GeneratorHarness.Run(Jobs(@"
        [TickerFunction(""Plain"")]
        public Task Plain() => Task.CompletedTask;
        [TickerFunction(""WithContext"", ""0 0 * * * *"")]
        public Task WithContext(TickerFunctionContext c, CancellationToken ct) => Task.CompletedTask;
        [TickerFunction(""Typed"")]
        public Task Typed(TickerFunctionContext<Order> c, CancellationToken ct) => Task.CompletedTask;"));

        Assert.Empty(GeneratorHarness.CompileErrors(result));
    }

    [Fact]
    public void AFunctionWithATypedContext_RegistersItsRequestType()
    {
        var result = GeneratorHarness.Run(Jobs(@"
        [TickerFunction(""Typed"")]
        public Task Typed(TickerFunctionContext<Order> c) => Task.CompletedTask;"));

        Assert.Contains(@"[""Typed""] = (typeof(global::TestApp.Order).FullName, typeof(global::TestApp.Order))", result.Factory);
        Assert.Contains("ToGenericContextAsync<global::TestApp.Order>", result.Factory);
    }

    [Fact]
    public void AFunctionWithoutACron_IsRegisteredWithAnEmptyOne_AndNormalPriorityDefaults()
    {
        var result = GeneratorHarness.Run(Jobs(@"
        [TickerFunction(""NoCron"")]
        public Task NoCron() => Task.CompletedTask;"));

        Assert.Contains(@"[""NoCron""] = (string.Empty, (global::SharpTicker.Utilities.Enums.TickerTaskPriority)0", result.Factory);
    }

    [Fact]
    public void AFiveFieldCron_IsStoredWithTheSecondsField()
    {
        var result = GeneratorHarness.Run(Jobs(@"
        [TickerFunction(""Hourly"", ""30 2 * * *"")]
        public Task Hourly() => Task.CompletedTask;"));

        Assert.Empty(result.Ids);
        Assert.Contains(@"""0 30 2 * * *""", result.Factory);
    }

    [Fact]
    public void ACronPlaceholderForConfiguration_IsPassedThroughUnchanged()
    {
        var result = GeneratorHarness.Run(Jobs(@"
        [TickerFunction(""Configured"", ""%Jobs:Cleanup%"")]
        public Task Configured() => Task.CompletedTask;"));

        Assert.Empty(result.Ids);
        Assert.Contains(@"""%Jobs:Cleanup%""", result.Factory);
    }

    [Fact]
    public void ConstructorServices_AreResolvedFromTheContainer()
    {
        var result = GeneratorHarness.Run(Jobs(@"
        public Jobs(System.IDisposable dependency) { }
        [TickerFunction(""Needs"")]
        public Task Needs() => Task.CompletedTask;"));

        Assert.Contains("GetService<global::System.IDisposable>", result.Factory);
        Assert.Contains("new global::TestApp.Jobs(dependency)", result.Factory);
    }

    [Fact]
    public void SeveralFunctionsOfOneClass_ShareOneFactoryMethod()
    {
        var result = GeneratorHarness.Run(Jobs(@"
        [TickerFunction(""A"")]
        public Task A() => Task.CompletedTask;
        [TickerFunction(""B"")]
        public Task B() => Task.CompletedTask;"));

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result.Factory, @"private static global::TestApp\.Jobs Create_"));
    }

    [Fact]
    public void AnInternalClassWithAnInternalMethod_IsAccepted()
    {
        var result = GeneratorHarness.Run(Jobs(@"
        [TickerFunction(""Internal"")]
        internal Task Internal() => Task.CompletedTask;", classModifiers: "internal"));

        Assert.Empty(result.Ids);
        Assert.Contains(@"[""Internal""]", result.Factory);
    }

    #endregion

    #region Build errors

    [Fact]
    public void ACronThatCannotBeParsed_IsAnError()
    {
        var result = GeneratorHarness.Run(Jobs(@"
        [TickerFunction(""Bad"", ""not a cron"")]
        public Task Bad() => Task.CompletedTask;"));

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("TQ003", diagnostic.Id);
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("not a cron", diagnostic.GetMessage());
    }

    [Fact]
    public void ACronWithAValueOutOfRange_IsAnError()
    {
        var result = GeneratorHarness.Run(Jobs(@"
        [TickerFunction(""Bad"", ""0 0 24 * * *"")]
        public Task Bad() => Task.CompletedTask;"));

        Assert.Equal(new[] { "TQ003" }, result.Ids);
    }

    [Fact]
    public void AnEmptyFunctionName_IsAnError()
    {
        var result = GeneratorHarness.Run(Jobs(@"
        [TickerFunction("""")]
        public Task Nameless() => Task.CompletedTask;"));

        Assert.Contains("TQ004", result.Ids);
    }

    [Fact]
    public void TwoFunctionsWithTheSameName_AreAnError()
    {
        var result = GeneratorHarness.Run(Jobs(@"
        [TickerFunction(""Same"")]
        public Task One() => Task.CompletedTask;
        [TickerFunction(""Same"")]
        public Task Two() => Task.CompletedTask;"));

        Assert.Contains("TQ005", result.Ids);
        Assert.Contains("Same", result.Diagnostics.First(d => d.Id == "TQ005").GetMessage());
    }

    [Fact]
    public void ThePrivateMethod_IsAnError()
    {
        var result = GeneratorHarness.Run(Jobs(@"
        [TickerFunction(""Hidden"")]
        private Task Hidden() => Task.CompletedTask;"));

        Assert.Contains("TQ002", result.Ids);
    }

    [Fact]
    public void AMethodWithoutAModifier_IsAnError()
    {
        var result = GeneratorHarness.Run(Jobs(@"
        [TickerFunction(""Hidden"")]
        Task Hidden() => Task.CompletedTask;"));

        Assert.Contains("TQ002", result.Ids);
    }

    [Fact]
    public void AnAbstractClass_IsAnError()
    {
        var result = GeneratorHarness.Run(Jobs(@"
        [TickerFunction(""Abstract"")]
        public Task Abstract() => Task.CompletedTask;", classModifiers: "public abstract"));

        Assert.Contains("TQ007", result.Ids);
    }

    [Fact]
    public void ANestedClass_IsAnError()
    {
        var result = GeneratorHarness.Run(@"
using System.Threading.Tasks;
using SharpTicker.Utilities.Base;
namespace TestApp
{
    public class Outer
    {
        public class Inner
        {
            [TickerFunction(""Nested"")]
            public Task Nested() => Task.CompletedTask;
        }
    }
}");

        Assert.Contains("TQ008", result.Ids);
    }

    [Fact]
    public void AParameterThatIsNotAContextOrACancellationToken_IsAnError()
    {
        var result = GeneratorHarness.Run(Jobs(@"
        [TickerFunction(""Param"")]
        public Task Param(string orderId) => Task.CompletedTask;"));

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Id == "TQ009");
        Assert.Contains("orderId", diagnostic.GetMessage());
    }

    [Fact]
    public void ANegativeMaxConcurrency_IsAnError()
    {
        var result = GeneratorHarness.Run(Jobs(@"
        [TickerFunction(""Limited"", null, 0, -1)]
        public Task Limited() => Task.CompletedTask;"));

        Assert.Contains("TQ011", result.Ids);
    }

    [Fact]
    public void AZeroMaxConcurrency_MeansUnlimited_AndIsAccepted()
    {
        var result = GeneratorHarness.Run(Jobs(@"
        [TickerFunction(""Unlimited"", null, 0, 0)]
        public Task Unlimited() => Task.CompletedTask;"));

        Assert.Empty(result.Ids);
    }

    #endregion

    #region Methods that are not ticker functions

    [Fact]
    public void AMethodWithoutTheAttribute_IsLeftAlone()
    {
        var result = GeneratorHarness.Run(Jobs(@"
        public Task NotAJob(string anything) => Task.CompletedTask;"));

        Assert.Empty(result.Ids);
        Assert.Empty(result.Sources);
    }

    #endregion
}
