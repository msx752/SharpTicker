using SharpTicker.Scenarios.Harness;
using Xunit.Abstractions;

namespace SharpTicker.Scenarios;

/// <summary>
/// Writes artifacts/scenarios/summary.md from a finished run. Step 1: dotnet test ... --logger "trx;LogFileName=scenarios.trx"
/// --results-directory artifacts/scenarios. Step 2: SCENARIO_SUMMARY_TRX=artifacts/scenarios/scenarios.trx dotnet test ...
/// --no-build --filter "Category=Summary" (with the same SCENARIO_SOURCE_REF as step 1, if any).
/// </summary>
public class SummaryReport(ITestOutputHelper output)
{
    [SummaryFact]
    [Trait("Category", "Summary")]
    public void WriteTheSummary()
    {
        var trx = Path.GetFullPath(Gate.Value(Gate.SummaryTrx)!, ScenarioImage.RepoRoot);
        Assert.True(File.Exists(trx), $"no TRX file at {trx}");

        var path = SummaryWriter.Write(trx, ScenarioImage.ArtifactsFolder, ScenarioImage.SourceRef);
        output.WriteLine($"summary written to {path}");
    }
}
