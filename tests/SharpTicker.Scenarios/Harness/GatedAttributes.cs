namespace SharpTicker.Scenarios.Harness;

/// <summary>A test that only runs when an environment variable is set; otherwise it shows as skipped, never as passed.</summary>
public static class Gate
{
    public const string SummaryTrx = "SCENARIO_SUMMARY_TRX";
    public const string Soak = "SCENARIO_SOAK";

    public static string? Value(string variable) => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } v ? v : null;
}

/// <summary>Runs only with SCENARIO_SUMMARY_TRX set to the TRX file of a finished run.</summary>
public sealed class SummaryFactAttribute : FactAttribute
{
    public SummaryFactAttribute()
    {
        if (Gate.Value(Gate.SummaryTrx) == null)
            Skip = $"set {Gate.SummaryTrx} to the TRX file of a finished run to write the summary";
    }
}

/// <summary>Runs only with SCENARIO_SOAK=1 (about 1.5 h per store).</summary>
public sealed class SoakTheoryAttribute : TheoryAttribute
{
    public SoakTheoryAttribute()
    {
        if (Gate.Value(Gate.Soak) != "1")
            Skip = $"set {Gate.Soak}=1 to run the soak (about 1.5 h per store)";
    }
}
