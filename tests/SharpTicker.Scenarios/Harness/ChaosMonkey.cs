namespace SharpTicker.Scenarios.Harness;

/// <summary>
/// Injects random faults one at a time: a graceful stop and start, a kill and start, a pause, a store cut, a latency toxic.
/// The random sequence comes from a seed (SCENARIO_SEED to repeat a run); the seed and every fault are written to
/// faults.txt in the scenario's log folder.
/// </summary>
public sealed class ChaosMonkey
{
    private readonly ScenarioEnvironment _env;
    private readonly Random _random;
    private readonly List<FaultEvent> _faults = new();
    private readonly string _file;

    public ChaosMonkey(ScenarioEnvironment env)
    {
        _env = env;
        Seed = int.TryParse(Environment.GetEnvironmentVariable("SCENARIO_SEED"), out var seed) ? seed : Random.Shared.Next();
        _random = new Random(Seed);
        _file = Path.Combine(env.LogFolder, "faults.txt");
        File.WriteAllText(_file, $"seed {Seed} (repeat with SCENARIO_SEED={Seed}){Environment.NewLine}");
    }

    public int Seed { get; }

    public IReadOnlyList<FaultEvent> Faults
    {
        get { lock (_faults) return _faults.ToList(); }
    }

    /// <summary>Nodes that are not to be faulted (e.g. the one the test reads from).</summary>
    public HashSet<int> Spared { get; } = new();

    /// <summary>
    /// Runs <paramref name="work"/> while injecting faults for <paramref name="duration"/>. If the work throws, the faults
    /// stop and the monkey is awaited before the exception goes on, so no fault runs against a disposed environment.
    /// </summary>
    public async Task<T> RunWhileAsync<T>(TimeSpan duration, Func<Task<T>> work)
    {
        using var stop = new CancellationTokenSource();
        var chaos = RunAsync(duration, stop.Token);
        T result;
        try
        {
            result = await work();
        }
        catch
        {
            await stop.CancelAsync();
            try
            {
                await chaos;
            }
            catch (Exception e)
            {
                File.AppendAllText(_file, $"{DateTimeOffset.UtcNow:O} the monkey failed while stopping: {e.Message}{Environment.NewLine}");
            }

            throw;
        }

        await chaos;
        return result;
    }

    /// <summary>
    /// Injects faults until <paramref name="duration"/> has passed or <paramref name="stop"/> is cancelled, then leaves every
    /// node running. The token is checked only between faults, so a fault in progress always ends with its node started again.
    /// </summary>
    public async Task RunAsync(TimeSpan duration, CancellationToken stop = default)
    {
        var end = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < end && !stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(4 + _random.Next(8)), stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var targets = Enumerable.Range(0, _env.Nodes.Count).Where(i => !Spared.Contains(i)).ToList();
            var node = targets.Count > 0 ? targets[_random.Next(targets.Count)] : -1;
            var kind = node < 0 ? 3 + _random.Next(2) : _random.Next(5);
            switch (kind)
            {
                case 0:
                    Record("stop", node);
                    await _env.StopNodeAsync(node);
                    await Task.Delay(TimeSpan.FromSeconds(1 + _random.Next(4)));
                    await _env.StartNodeAsync(node);
                    break;
                case 1:
                    Record("kill", node);
                    await _env.KillNodeAsync(node);
                    await Task.Delay(TimeSpan.FromSeconds(1 + _random.Next(4)));
                    await _env.StartNodeAsync(node);
                    break;
                case 2:
                    var pause = TimeSpan.FromSeconds(2 + _random.Next(5));
                    Record("pause", node, $"{pause.TotalSeconds} s", pause);
                    await _env.PauseNodeAsync(node, pause);
                    break;
                case 3:
                    // Shorter than the Redis heartbeat TTL (5 s + 20 s): a longer cut is outside what this mix checks.
                    var cut = TimeSpan.FromSeconds(3 + _random.Next(8));
                    Record("cut", null, $"{cut.TotalSeconds} s", cut);
                    await _env.CutStoreAsync(cut);
                    break;
                default:
                    var latency = 300 + _random.Next(1200);
                    var lasts = TimeSpan.FromSeconds(4 + _random.Next(6));
                    Record("latency", null, $"{latency} ms for {lasts.TotalSeconds} s", lasts);
                    await _env.AddStoreToxicAsync("latency", new { latency }, "downstream");
                    await Task.Delay(lasts);
                    await _env.RemoveStoreToxicsAsync();
                    break;
            }
        }
    }

    private void Record(string type, int? node, string? detail = null, TimeSpan? lasts = null)
    {
        var at = DateTimeOffset.UtcNow;
        var fault = new FaultEvent(type, node is { } i ? _env.Nodes[i].Identifier : null) { At = at, Until = at + (lasts ?? TimeSpan.Zero) };
        lock (_faults)
            _faults.Add(fault);
        File.AppendAllText(_file, $"{fault.At:O} {type} {(node is { } n ? $"node-{n} {fault.NodeIdentifier}" : "store")} {detail}{Environment.NewLine}");
    }
}
