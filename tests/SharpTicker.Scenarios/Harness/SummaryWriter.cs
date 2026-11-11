using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace SharpTicker.Scenarios.Harness;

/// <summary>What one scenario environment saw, written as <c>run.json</c> into its log folder when it is disposed.</summary>
public sealed class RunRecord
{
    public const string FileName = "run.json";

    /// <summary>The test method that started the environment ("Class.Method"); empty when it could not be found.</summary>
    public string Test { get; init; } = "";
    public string Scenario { get; init; } = "";
    public string Store { get; init; } = "";
    /// <summary>The commit the nodes were built from (SCENARIO_SOURCE_REF); null for the working tree.</summary>
    public string? SourceRef { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset EndedAt { get; init; }
    public int Jobs { get; init; }
    public int Starts { get; init; }
    public int Ends { get; init; }
    public string LogFolder { get; init; } = "";
}

/// <summary>One test result read from a TRX file.</summary>
public sealed record TestResult(string Name, string Outcome, TimeSpan Duration, string? Message,
    DateTimeOffset? StartTime = null, DateTimeOffset? EndTime = null)
{
    /// <summary>"Class.Method" without the namespace and the arguments, as <see cref="RunRecord.Test"/> holds it.</summary>
    public string Method
    {
        get
        {
            var name = Name.Split('(')[0];
            var parts = name.Split('.');
            return parts.Length >= 2 ? $"{parts[^2]}.{parts[^1]}" : name;
        }
    }

    /// <summary>The store argument ("store: Redis"), or null for a test without one.</summary>
    public string? Store
    {
        get
        {
            const string key = "store: ";
            var i = Name.IndexOf(key, StringComparison.Ordinal);
            if (i < 0)
                return null;
            var rest = Name[(i + key.Length)..];
            return new string(rest.TakeWhile(char.IsLetterOrDigit).ToArray());
        }
    }
}

/// <summary>
/// Writes <c>artifacts/scenarios/summary.md</c> after a run: each test's result from the TRX file, joined with the
/// <see cref="RunRecord"/>s of the environments it started (by test method and store).
/// </summary>
public static class SummaryWriter
{
    private static readonly XNamespace Trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public static IReadOnlyList<TestResult> ReadTrx(string xml)
    {
        var doc = XDocument.Parse(xml);
        return doc.Descendants(Trx + "UnitTestResult")
            .Select(r => new TestResult(
                (string?)r.Attribute("testName") ?? "",
                (string?)r.Attribute("outcome") ?? "",
                TimeSpan.TryParse((string?)r.Attribute("duration"), CultureInfo.InvariantCulture, out var d) ? d : TimeSpan.Zero,
                r.Element(Trx + "Output")?.Element(Trx + "ErrorInfo")?.Element(Trx + "Message")?.Value,
                Time(r.Attribute("startTime")),
                Time(r.Attribute("endTime"))))
            .ToList();
    }

    public static IReadOnlyList<RunRecord> ReadRecords(string artifactsFolder)
        => Directory.Exists(artifactsFolder)
            ? Directory.EnumerateFiles(artifactsFolder, RunRecord.FileName, SearchOption.AllDirectories)
                .Select(f => JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(f)))
                .OfType<RunRecord>()
                .ToList()
            : [];

    /// <summary>The report: one row per test, failed first; each row lists its environments' counts and log folders.</summary>
    public static string Build(IReadOnlyList<TestResult> results, IReadOnlyList<RunRecord> records, string? sourceRef)
    {
        // Only the records of this run's code: the working tree, or the one pre-fix commit.
        var mine = records.Where(r => r.SourceRef == sourceRef).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("# Scenario run summary");
        sb.AppendLine();
        sb.AppendLine($"Code: {(sourceRef == null ? "working tree" : $"pre-fix ref `{sourceRef}`")}. " +
                      $"Tests: {results.Count}, passed {results.Count(r => r.Outcome == "Passed")}, " +
                      $"failed {results.Count(r => r.Outcome == "Failed")}, other {results.Count(r => r.Outcome is not ("Passed" or "Failed"))}.");
        sb.AppendLine();
        sb.AppendLine("| Result | Test | Store | Duration | Jobs | Starts | Ends | Log folder | Message |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var result in results.OrderBy(r => r.Outcome == "Failed" ? 0 : 1).ThenBy(r => r.Name, StringComparer.Ordinal))
        {
            var envs = mine.Where(r => r.Test == result.Method && (result.Store == null || r.Store == result.Store)).ToList();
            // Several cases of one theory on one store (other arguments): keep the environments that ran during this case.
            if (envs.Count > 1 && result.StartTime is { } from && result.EndTime is { } until)
                envs = envs.Where(e => e.StartedAt >= from && e.EndedAt <= until).ToList();
            var message = result.Message?.Split('\n')[0].Trim() ?? "";
            sb.AppendLine(string.Join(" | ",
                "| " + result.Outcome,
                Cell(result.Method + Arguments(result.Name)),
                result.Store ?? "",
                $"{result.Duration.TotalSeconds:0} s",
                envs.Count == 0 ? "" : envs.Sum(e => e.Jobs).ToString(CultureInfo.InvariantCulture),
                envs.Count == 0 ? "" : envs.Sum(e => e.Starts).ToString(CultureInfo.InvariantCulture),
                envs.Count == 0 ? "" : envs.Sum(e => e.Ends).ToString(CultureInfo.InvariantCulture),
                Cell(string.Join(", ", envs.Select(e => e.LogFolder).Distinct())),
                Cell(message) + " |"));
        }

        return sb.ToString();
    }

    /// <summary>Reads the TRX file and the run records under the artifacts folder and writes summary.md there.</summary>
    public static string Write(string trxPath, string artifactsFolder, string? sourceRef)
    {
        var path = Path.Combine(artifactsFolder, "summary.md");
        File.WriteAllText(path, Build(ReadTrx(File.ReadAllText(trxPath)), ReadRecords(artifactsFolder), sourceRef));
        return path;
    }

    private static DateTimeOffset? Time(XAttribute? attribute)
        => DateTimeOffset.TryParse((string?)attribute, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    private static string Arguments(string name) => name.IndexOf('(') is var i and >= 0 ? name[i..] : "";

    private static string Cell(string text) => text.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");
}
