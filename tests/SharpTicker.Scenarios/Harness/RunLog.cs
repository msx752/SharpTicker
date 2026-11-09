using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpTicker.Scenarios.Harness;

/// <summary>One <c>SCENARIO_RUN</c> line of a node: a job start or end.</summary>
public sealed class RunEvent
{
    [JsonPropertyName("seq")] public long Seq { get; init; }
    [JsonPropertyName("event")] public string Event { get; init; } = "";
    [JsonPropertyName("tickerId")] public Guid TickerId { get; init; }
    [JsonPropertyName("function")] public string Function { get; init; } = "";
    [JsonPropertyName("node")] public string Node { get; init; } = "";
    [JsonPropertyName("attempt")] public int Attempt { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    /// <summary>
    /// The node's own clock. It is compared with fault windows taken on the test clock only because every node's clock is
    /// checked against the test clock when it starts (ScenarioEnvironment, within 500 ms).
    /// </summary>
    [JsonPropertyName("at")] public DateTimeOffset At { get; init; }
    [JsonPropertyName("thread")] public string? Thread { get; init; }
    [JsonPropertyName("pool")] public bool? Pool { get; init; }
    [JsonPropertyName("scheduledFor")] public DateTimeOffset? ScheduledFor { get; init; }

    /// <summary>The container whose log carried the line.</summary>
    [JsonIgnore] public string Container { get; internal set; } = "";

    public bool IsStart => Event == "start";
    public bool IsEnd => Event == "end";
}

/// <summary>
/// The run events of every node of one scenario, collected live from the container logs. Thread-safe.
/// Runs are counted per ticker id and attempt, so a retry is never taken for a duplicate run.
/// </summary>
public sealed class RunLog
{
    public const string Marker = "SCENARIO_RUN ";

    private readonly object _gate = new();
    private readonly List<RunEvent> _events = new();

    public IReadOnlyList<RunEvent> Events
    {
        get { lock (_gate) return _events.ToList(); }
    }

    /// <summary>Takes one log line; lines without the marker are ignored.</summary>
    public void Add(string container, string line)
    {
        var at = line.IndexOf(Marker, StringComparison.Ordinal);
        if (at < 0)
            return;

        var evt = JsonSerializer.Deserialize<RunEvent>(line.AsSpan(at + Marker.Length).Trim())
                  ?? throw new FormatException($"Unreadable run-log line: {line}");
        evt.Container = container;
        lock (_gate)
            _events.Add(evt);
    }

    public int Starts(Guid tickerId, int? attempt = null)
        => Count(e => e.IsStart && e.TickerId == tickerId && (attempt == null || e.Attempt == attempt));

    public int Ends(Guid tickerId)
        => Count(e => e.IsEnd && e.TickerId == tickerId);

    public IReadOnlyList<RunEvent> For(Guid tickerId)
    {
        lock (_gate)
            return _events.Where(e => e.TickerId == tickerId).ToList();
    }

    private int Count(Func<RunEvent, bool> match)
    {
        lock (_gate)
            return _events.Count(match);
    }
}
