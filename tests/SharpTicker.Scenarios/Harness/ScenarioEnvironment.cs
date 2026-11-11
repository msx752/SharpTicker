using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.MongoDb;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Testcontainers.Toxiproxy;

namespace SharpTicker.Scenarios.Harness;

/// <summary>One node of a scenario. Started again, it gets a new container with the same node identifier.</summary>
public sealed class Node
{
    private HttpClient? _http;

    internal Node(int index, string identifier)
    {
        Index = index;
        Identifier = identifier;
        Api = new ScenarioClient(Http);
        Dashboard = new DashboardClient(Http);
    }

    public int Index { get; }
    public string Identifier { get; }
    public ScenarioClient Api { get; }
    public DashboardClient Dashboard { get; }

    /// <summary>The node's address from the test process (mapped port of the current container).</summary>
    public Uri BaseAddress => Http().BaseAddress!;

    /// <summary>How many containers this node has had (1 after the first start).</summary>
    public int Generation { get; internal set; }

    internal IContainer? Container { get; private set; }

    internal void Attach(IContainer container, bool withHttp)
    {
        Container = container;
        DisposeHttp();
        if (!withHttp)
            return;

        _http = new HttpClient
        {
            BaseAddress = new Uri($"http://{container.Hostname}:{container.GetMappedPublicPort(8080)}"),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    /// <summary>Disposes the client of the current container; a call still in flight on it fails, as on a stopped node.</summary>
    internal void DisposeHttp()
    {
        _http?.Dispose();
        _http = null;
    }

    private HttpClient Http() => _http ?? throw new InvalidOperationException($"Node {Index} has not been started.");
}

/// <summary>
/// The containers of one scenario: its own network, store, Toxiproxy in front of the store and the nodes. Nothing is
/// shared with other scenarios except the node image. Every container's output goes to its own file in
/// <see cref="LogFolder"/>; the nodes' run lines go to <see cref="Runs"/>.
/// </summary>
public sealed class ScenarioEnvironment : IScenarioState, IAsyncDisposable
{
    private const int ProxyListenPort = 8666;
    private const int HeartbeatProxyFirstPort = 8700;
    private const int ToxiproxyControlPort = 8474;

    private readonly string _scenario;
    private readonly string _runId = Guid.NewGuid().ToString("N")[..8];
    private readonly object _fileGate = new();
    private readonly List<StreamWriter> _files = new();
    private readonly List<IContainer> _allContainers = new();
    private readonly Func<int, IReadOnlyDictionary<string, string>>? _nodeEnvironment;
    private readonly HashSet<int> _ownStoreProxy;
    private readonly int _heartbeatProxies;
    private readonly List<Node> _nodes = new();

    private INetwork _network = null!;
    private IContainer _store = null!;
    private IContainer _proxy = null!;
    private HttpClient _proxyApi = null!;
    private int _toxicCount;

    private readonly string _test;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public DateTimeOffset StartedAt => _startedAt;

    private ScenarioEnvironment(string scenario, Store store, Func<int, IReadOnlyDictionary<string, string>>? nodeEnvironment, string test, int[]? ownStoreProxy, int heartbeatProxies)
    {
        _ownStoreProxy = new HashSet<int>(ownStoreProxy ?? []);
        _heartbeatProxies = heartbeatProxies;
        _scenario = scenario;
        _test = test;
        Store = store;
        _nodeEnvironment = nodeEnvironment;
        // Runs on an older commit (SCENARIO_SOURCE_REF) keep their logs apart from the working-tree runs.
        LogFolder = ScenarioImage.SourceRef == null
            ? Path.Combine(ScenarioImage.ArtifactsFolder, $"{scenario}-{store.Short()}")
            : Path.Combine(ScenarioImage.ArtifactsFolder, "at-" + string.Concat(ScenarioImage.SourceRef.Select(c => char.IsLetterOrDigit(c) ? c : '_')), $"{scenario}-{store.Short()}");
        RedisInstanceName = $"{scenario}-{_runId}:";
        DatabaseName = $"scenario_{_runId}";
    }

    public Store Store { get; }
    public IReadOnlyList<Node> Nodes => _nodes;
    public RunLog Runs { get; } = new();
    public string LogFolder { get; }
    public string RedisInstanceName { get; }
    public string DatabaseName { get; }

    /// <summary>The store's connection string from the test process (mapped port, not through the proxy).</summary>
    public string DirectStoreConnection { get; private set; } = "";

    /// <summary>
    /// The Redis that holds the node heartbeats, from the test process (not through a proxy): the heartbeat Redis when the
    /// scenario has one, else the store's connection (which holds heartbeats only when the store is Redis).
    /// </summary>
    public string DirectHeartbeatConnection { get; private set; } = "";

    /// <summary>
    /// Starts the network, the store, the proxy and the nodes. <paramref name="startedNodes"/> nodes are started now
    /// (all by default); the others wait for <see cref="StartNodeAsync"/>.
    /// <paramref name="nodeEnvironment"/> adds environment variables per node index.
    /// The nodes in <paramref name="ownStoreProxy"/> reach the store through a proxy of their own, so the store can be
    /// cut for them and not for the others (<see cref="SetStoreProxyAsync"/>); the faults without a node go to the proxy
    /// the other nodes share.
    /// With <paramref name="heartbeatRedis"/> (a store other than Redis; run with EF Core only so far) the nodes also
    /// use a second Redis for the node heartbeat, each through its own proxy (<see cref="SetHeartbeatProxyAsync"/>).
    /// </summary>
    public static async Task<ScenarioEnvironment> StartAsync(
        string scenario,
        Store store,
        int nodes,
        int? startedNodes = null,
        Func<int, IReadOnlyDictionary<string, string>>? nodeEnvironment = null,
        int[]? ownStoreProxy = null,
        bool heartbeatRedis = false)
    {
        // Before the first await: the calling test is still on the stack.
        var test = CallingTest();
        var image = await ScenarioImage.GetAsync();
        if (heartbeatRedis && store == Store.Redis)
            throw new ArgumentException("With Redis as the store the heartbeat is in the store already.", nameof(heartbeatRedis));
        var env = new ScenarioEnvironment(scenario, store, nodeEnvironment, test, ownStoreProxy, heartbeatRedis ? nodes : 0);
        try
        {
            await env.StartInfrastructureAsync();
            for (var i = 0; i < nodes; i++)
                env._nodes.Add(new Node(i, $"{scenario}-{store.Short()}-{i}-{env._runId}"));

            var toStart = startedNodes ?? nodes;
            if (toStart > 0)
            {
                // EF Core: one node creates the schema before the others start.
                await env.StartNodeAsync(0, image, null);
                await Task.WhenAll(Enumerable.Range(1, toStart - 1).Select(i => env.StartNodeAsync(i, image, null)));
            }

            return env;
        }
        catch
        {
            await env.DisposeAsync();
            throw;
        }
    }

    private async Task StartInfrastructureAsync()
    {
        if (Directory.Exists(LogFolder))
            Directory.Delete(LogFolder, recursive: true);
        Directory.CreateDirectory(LogFolder);

        _network = new NetworkBuilder().WithName($"st-{_scenario}-{Store.Short()}-{_runId}".ToLowerInvariant()).Build();
        await _network.CreateAsync();

        int storePort;
        switch (Store)
        {
            case Store.EfCorePostgres:
                var pg = new PostgreSqlBuilder("postgres:16-alpine")
                    .WithDatabase(DatabaseName).WithUsername("postgres").WithPassword("postgres")
                    .WithNetwork(_network).WithNetworkAliases("store")
                    .WithOutputConsumer(Output("store", null))
                    // The default of 100 is less than two nodes' Npgsql pools (100 each). A burst of overdue jobs then
                    // finds the server full, and the scenario fails on that limit instead of on the fault it injects.
                    .WithCommand("-c", "max_connections=300")
                    .Build();
                await StartTracked(pg);
                var maxConnections = await pg.ExecAsync(["psql", "-U", "postgres", "-tAc", "SHOW max_connections"]);
                if (maxConnections.Stdout.Trim() != "300")
                    throw new InvalidOperationException($"PostgreSQL max_connections is '{maxConnections.Stdout.Trim()}', expected 300: {maxConnections.Stderr}");
                DirectStoreConnection = pg.GetConnectionString();
                storePort = 5432;
                break;
            case Store.MongoDb:
                var mongo = new MongoDbBuilder("mongo:7")
                    .WithUsername("mongo").WithPassword("mongo")
                    .WithNetwork(_network).WithNetworkAliases("store")
                    .WithOutputConsumer(Output("store", null))
                    .Build();
                await StartTracked(mongo);
                DirectStoreConnection = mongo.GetConnectionString();
                storePort = 27017;
                break;
            case Store.Redis:
                var redis = new RedisBuilder("redis:7-alpine")
                    .WithNetwork(_network).WithNetworkAliases("store")
                    .WithOutputConsumer(Output("store", null))
                    .Build();
                await StartTracked(redis);
                DirectStoreConnection = redis.GetConnectionString();
                storePort = 6379;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(Store));
        }

        _store = _allContainers[^1];
        DirectHeartbeatConnection = DirectStoreConnection;
        if (_heartbeatProxies > 0)
        {
            var heartbeat = new RedisBuilder("redis:7-alpine")
                .WithNetwork(_network).WithNetworkAliases("heartbeat")
                .WithOutputConsumer(Output("heartbeat", null))
                .Build();
            await StartTracked(heartbeat);
            DirectHeartbeatConnection = heartbeat.GetConnectionString();
        }

        _proxy = new ToxiproxyBuilder("ghcr.io/shopify/toxiproxy:2.12.0")
            .WithNetwork(_network).WithNetworkAliases("proxy")
            .WithOutputConsumer(Output("proxy", null))
            .Build();
        await StartTracked(_proxy);
        _proxyApi = new HttpClient
        {
            BaseAddress = new Uri($"http://{_proxy.Hostname}:{_proxy.GetMappedPublicPort(ToxiproxyControlPort)}"),
        };
        // The shared proxy (null), then one for each node that has its own.
        foreach (var owner in _ownStoreProxy.Select(i => (int?)i).Prepend(null))
        {
            await Check(await _proxyApi.PostAsJsonAsync("/proxies", new
            {
                name = ProxyName(owner),
                listen = $"0.0.0.0:{ProxyPort(owner)}",
                upstream = $"store:{storePort}",
                enabled = true,
            }));
        }

        for (var i = 0; i < _heartbeatProxies; i++)
        {
            await Check(await _proxyApi.PostAsJsonAsync("/proxies", new
            {
                name = $"heartbeat-node-{i}",
                listen = $"0.0.0.0:{HeartbeatProxyFirstPort + i}",
                upstream = "heartbeat:6379",
                enabled = true,
            }));
        }
    }

    // The shared proxy, or the own proxy of a node that has one.
    private string ProxyName(int? node) => node is { } i && _ownStoreProxy.Contains(i) ? $"store-node-{i}" : "store";

    private int ProxyPort(int? node) => node is { } i && _ownStoreProxy.Contains(i) ? ProxyListenPort + 1 + i : ProxyListenPort;

    private string NodeConnection(int node)
    {
        var port = ProxyPort(node);
        return Store switch
        {
            Store.EfCorePostgres => $"Host=proxy;Port={port};Database={DatabaseName};Username=postgres;Password=postgres",
            // The driver's default server selection wait, pinned: the store-cut scenarios are timed against it.
            Store.MongoDb => $"mongodb://mongo:mongo@proxy:{port}/?authSource=admin&directConnection=true&serverSelectionTimeoutMS=30000",
            Store.Redis => $"proxy:{port},abortConnect=false",
            _ => throw new ArgumentOutOfRangeException(nameof(Store)),
        };
    }

    /// <summary>Starts node <paramref name="i"/>: its first container, or a new one with the same identifier.</summary>
    public async Task StartNodeAsync(int i) => await StartNodeAsync(i, await ScenarioImage.GetAsync(), null);

    /// <summary>
    /// Runs node <paramref name="i"/> once with extra environment variables and waits until its process exits (e.g. a
    /// node that only applies migrations). Returns the exit code.
    /// </summary>
    public async Task<int> RunNodeToExitAsync(int i, IReadOnlyDictionary<string, string> extra, TimeSpan timeout)
    {
        var container = await StartNodeAsync(i, await ScenarioImage.GetAsync(), extra, runToExit: true);
        var output = await DockerCli.RunCheckedAsync($"wait {container.Id}", timeout);
        return int.Parse(output.Trim().Split('\n')[^1].Trim());
    }

    private async Task<IContainer> StartNodeAsync(int i, string image, IReadOnlyDictionary<string, string>? extra = null, bool runToExit = false)
    {
        var node = _nodes[i];
        if (node.Container != null && await IsRunningAsync(node.Container))
            throw new InvalidOperationException($"Node {i} is still running.");

        var name = node.Generation == 0 ? $"node-{i}" : $"node-{i}-r{node.Generation}";
        var environment = new Dictionary<string, string>
        {
            ["SCENARIO_STORE"] = Store.HostValue(),
            ["SCENARIO_CONNECTION"] = NodeConnection(i),
            ["SCENARIO_DATABASE"] = DatabaseName,
            ["SCENARIO_NODE"] = node.Identifier,
            ["SCENARIO_REDIS_INSTANCE"] = RedisInstanceName,
            ["SCENARIO_CREATE_SCHEMA"] = i == 0 && node.Generation == 0 ? "1" : "0",
        };
        if (_heartbeatProxies > 0)
            environment["SCENARIO_HEARTBEAT_REDIS"] = $"proxy:{HeartbeatProxyFirstPort + i},abortConnect=false";
        if (_nodeEnvironment != null)
        {
            foreach (var (key, value) in _nodeEnvironment(i))
                environment[key] = value;
        }

        foreach (var (key, value) in extra ?? new Dictionary<string, string>())
            environment[key] = value;

        var builder = new ContainerBuilder(image)
            .WithName($"st-{_runId}-{name}")
            .WithNetwork(_network)
            .WithPortBinding(8080, true)
            .WithEnvironment(environment)
            .WithOutputConsumer(Output(name, Runs));
        if (!runToExit)
            builder = builder.WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/scenario/health")));
        var container = builder.Build();

        node.Generation++;
        using var startTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await StartTracked(container, startTimeout.Token);
        node.Attach(container, withHttp: !runToExit);
        if (!runToExit)
            await CheckClockAsync(node);
        return container;
    }

    private static readonly TimeSpan ClockTolerance = TimeSpan.FromMilliseconds(500);

    // Fault windows are taken on the test clock and compared with the run lines' node times (with 2 s of slack). That
    // holds only while the two clocks agree, which Docker Desktop's VM does not promise: a node whose clock is off by
    // more than the tolerance stops the scenario before anything is judged.
    private static async Task CheckClockAsync(Node node)
    {
        using var http = new HttpClient { BaseAddress = node.BaseAddress, Timeout = TimeSpan.FromSeconds(10) };
        var before = DateTimeOffset.UtcNow;
        var health = await http.GetFromJsonAsync<Health>("/scenario/health");
        var after = DateTimeOffset.UtcNow;
        var offset = health!.Now - (before + (after - before) / 2);
        if (offset.Duration() > ClockTolerance + (after - before) / 2)
            throw new InvalidOperationException(
                $"Node {node.Index}'s clock differs from the test clock by {offset.TotalMilliseconds:0} ms; the fault windows would be judged wrong.");
    }

    private sealed record Health(string Node, DateTimeOffset Now);

    /// <summary>A graceful stop (SIGTERM), with a 20 s stop timeout so the node's own 10 s shutdown is never cut.</summary>
    public async Task StopNodeAsync(int i) => await DockerCli.RunCheckedAsync($"stop -t 20 {Running(i).Id}", TimeSpan.FromSeconds(60));

    /// <summary>SIGKILL: the node gets no chance to clean up.</summary>
    public async Task KillNodeAsync(int i) => await DockerCli.RunCheckedAsync($"kill {Running(i).Id}");

    /// <summary>Freezes the node's processes for <paramref name="duration"/>, then lets them go on.</summary>
    public async Task PauseNodeAsync(int i, TimeSpan duration)
    {
        var id = Running(i).Id;
        await DockerCli.RunCheckedAsync($"pause {id}");
        try
        {
            await Task.Delay(duration);
        }
        finally
        {
            await DockerCli.RunCheckedAsync($"unpause {id}");
        }
    }

    /// <summary>
    /// Disables the shared store proxy (connections refused, open ones dropped) for <paramref name="duration"/>. Like the
    /// toxics, it does not reach a node that has a store proxy of its own.
    /// </summary>
    public async Task CutStoreAsync(TimeSpan duration)
    {
        await Check(await _proxyApi.PostAsJsonAsync("/proxies/store", new { enabled = false }));
        try
        {
            await Task.Delay(duration);
        }
        finally
        {
            await Check(await _proxyApi.PostAsJsonAsync("/proxies/store", new { enabled = true }));
        }
    }

    /// <summary>
    /// Disables or enables the store proxy node <paramref name="node"/> uses: its own one, or the shared one (then every
    /// node without a proxy of its own is cut with it).
    /// </summary>
    public async Task SetStoreProxyAsync(int node, bool enabled)
        => await Check(await _proxyApi.PostAsJsonAsync($"/proxies/{ProxyName(node)}", new { enabled }));

    /// <summary>Disables or enables the proxy between node <paramref name="node"/> and the heartbeat Redis.</summary>
    public async Task SetHeartbeatProxyAsync(int node, bool enabled)
        => await Check(await _proxyApi.PostAsJsonAsync($"/proxies/heartbeat-node-{node}", new { enabled }));

    /// <summary>
    /// The number of lines in all node logs so far that contain <paramref name="text"/> (e.g. "fail:", the prefix of an
    /// error-level log line). Lets a scenario check that the fault it injected was seen by a node. With
    /// <paramref name="node"/>, only that node's log is read.
    /// </summary>
    public int CountNodeLogLines(string text, int? node = null)
    {
        lock (_fileGate)
        {
            return Directory.GetFiles(LogFolder, node is { } n ? $"node-{n}.log" : "node-*.log").Sum(path =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var count = 0;
                while (reader.ReadLine() is { } line)
                {
                    if (line.Contains(text, StringComparison.Ordinal))
                        count++;
                }

                return count;
            });
        }
    }

    /// <summary>Adds a Toxiproxy toxic (e.g. "timeout", "latency", "reset_peer") to the store proxy.</summary>
    public async Task AddStoreToxicAsync(string type, object attributes, string stream = "downstream", double toxicity = 1.0)
    {
        var name = $"{type}-{Interlocked.Increment(ref _toxicCount)}";
        await Check(await _proxyApi.PostAsJsonAsync("/proxies/store/toxics", new { name, type, stream, toxicity, attributes }));
    }

    public async Task RemoveStoreToxicsAsync()
    {
        var toxics = await _proxyApi.GetFromJsonAsync<List<ToxicName>>("/proxies/store/toxics") ?? new();
        foreach (var toxic in toxics)
            await Check(await _proxyApi.DeleteAsync($"/proxies/store/toxics/{toxic.Name}"));
    }

    /// <summary>Waits until <paramref name="condition"/> holds; on timeout names the state, the containers and the log folder.</summary>
    public Task UntilAsync(Func<Task<bool>> condition, Func<string> describe, TimeSpan? timeout = null)
        => Eventually.UntilAsync(condition, timeout ?? Eventually.DefaultTimeout, describe, this);

    public async Task<string> DescribeContainersAsync()
    {
        if (_allContainers.Count == 0)
            return "(none)";
        var ids = string.Join(' ', _allContainers.Select(c => c.Id));
        var (code, output) = await DockerCli.RunAsync($"inspect -f \"{{{{.Name}}}}: {{{{.State.Status}}}} (exit {{{{.State.ExitCode}}}}, restarts {{{{.RestartCount}}}})\" {ids}");
        return code == 0
            ? string.Join("; ", output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.TrimStart('/')))
            : output.Trim();
    }

    /// <summary>The docker state of a node's current container and its restart count.</summary>
    public async Task<(string Status, int RestartCount)> NodeStateAsync(int i)
    {
        var output = await DockerCli.RunCheckedAsync($"inspect -f \"{{{{.State.Status}}}} {{{{.RestartCount}}}}\" {_nodes[i].Container!.Id}");
        var parts = output.Trim().Split(' ');
        return (parts[0], int.Parse(parts[1]));
    }

    public async ValueTask DisposeAsync()
    {
        WriteRunRecord();
        foreach (var container in Enumerable.Reverse(_allContainers))
        {
            try
            {
                await container.DisposeAsync();
            }
            catch
            {
                // Best effort; the Testcontainers resource reaper removes anything left behind.
            }
        }

        if (_network != null)
        {
            try
            {
                await _network.DisposeAsync();
            }
            catch
            {
                // Same as above.
            }
        }

        foreach (var node in _nodes)
            node.DisposeHttp();
        _proxyApi?.Dispose();
        lock (_fileGate)
        {
            foreach (var file in _files)
                file.Dispose();
        }
    }

    /// <summary>
    /// How many time tickers the store holds in <paramref name="status"/>, read straight from the store (not through the
    /// proxy, so a toxic does not slow it, and not through a node).
    /// </summary>
    public async Task<int> CountTimeTickersAsync(TickerStatus status)
    {
        switch (Store)
        {
            case Store.EfCorePostgres:
            {
                await using var db = new Npgsql.NpgsqlConnection(DirectStoreConnection);
                await db.OpenAsync();
                await using var count = new Npgsql.NpgsqlCommand("""SELECT count(*) FROM ticker."TimeTickers" WHERE "Status" = @status""", db);
                count.Parameters.AddWithValue("status", (int)status);
                return Convert.ToInt32(await count.ExecuteScalarAsync());
            }
            case Store.MongoDb:
            {
                using var client = new MongoDB.Driver.MongoClient(DirectStoreConnection);
                var collection = client.GetDatabase(DatabaseName)
                    .GetCollection<MongoDB.Bson.BsonDocument>("ticker_TimeTickers");
                // The status as a number or as its name, whichever the store writes.
                var filter = MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.In("Status",
                    new MongoDB.Bson.BsonValue[] { (int)status, status.ToString() });
                return (int)await collection.CountDocumentsAsync(filter);
            }
            default:
            {
                await using var redis = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(DirectStoreConnection);
                var snapshot = await RedisIndexCheck.ReadAsync(redis);
                return snapshot.TimeTickers.Values.Count(r => r.Status == status);
            }
        }
    }

    /// <summary>The node's current container and its memory use in bytes (docker stats).</summary>
    public async Task<(string Container, long Bytes)> NodeMemoryAsync(int i)
    {
        var id = Running(i).Id;
        var usage = await DockerCli.RunCheckedAsync($"stats --no-stream --format \"{{{{.MemUsage}}}}\" {id}", TimeSpan.FromSeconds(30));
        return (id, ParseMemory(usage.Split('/')[0]));
    }

    /// <summary>"123.4MiB" (docker stats) in bytes.</summary>
    internal static long ParseMemory(string text)
    {
        text = text.Trim();
        var units = new (string Suffix, double Factor)[] { ("GiB", 1L << 30), ("MiB", 1L << 20), ("KiB", 1L << 10), ("GB", 1e9), ("MB", 1e6), ("kB", 1e3), ("B", 1) };
        foreach (var (suffix, factor) in units)
        {
            if (text.EndsWith(suffix, StringComparison.Ordinal))
                return (long)(double.Parse(text[..^suffix.Length], System.Globalization.CultureInfo.InvariantCulture) * factor);
        }

        throw new FormatException($"memory usage not understood: '{text}'");
    }

    // For the run summary (SummaryWriter); a failure here must not hide the scenario's own result.
    private void WriteRunRecord()
    {
        try
        {
            var events = Runs.Events;
            var record = new RunRecord
            {
                Test = _test,
                Scenario = _scenario,
                Store = Store.ToString(),
                SourceRef = ScenarioImage.SourceRef,
                StartedAt = _startedAt,
                EndedAt = DateTimeOffset.UtcNow,
                Jobs = events.Select(e => e.TickerId).Distinct().Count(),
                Starts = events.Count(e => e.IsStart),
                Ends = events.Count(e => e.IsEnd),
                LogFolder = Path.GetRelativePath(ScenarioImage.ArtifactsFolder, LogFolder),
            };
            File.WriteAllText(Path.Combine(LogFolder, RunRecord.FileName), JsonSerializer.Serialize(record));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"run record for {LogFolder} not written: {e.Message}");
        }
    }

    /// <summary>"Class.Method" of the [Fact]/[Theory] method on the stack (an async test shows as its state machine).</summary>
    internal static string CallingTest()
    {
        foreach (var frame in new StackTrace().GetFrames())
        {
            var method = frame.GetMethod();
            if (method?.DeclaringType is not { } type)
                continue;
            if (method.Name == "MoveNext" && type.DeclaringType is { } outer && type.Name.StartsWith('<') && type.Name.IndexOf('>') is var end and > 1)
            {
                var name = type.Name[1..end];
                method = outer.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == name);
                type = outer;
            }

            if (method?.GetCustomAttribute<FactAttribute>() != null)
                return $"{type.Name}.{method.Name}";
        }

        return "";
    }

    private IContainer Running(int i) => _nodes[i].Container ?? throw new InvalidOperationException($"Node {i} has not been started.");

    private static async Task<bool> IsRunningAsync(IContainer container)
    {
        var (code, output) = await DockerCli.RunAsync($"inspect -f \"{{{{.State.Running}}}}\" {container.Id}");
        return code == 0 && output.Trim() == "true";
    }

    private async Task StartTracked(IContainer container, CancellationToken ct = default)
    {
        _allContainers.Add(container);
        await container.StartAsync(ct);
    }

    private DotNet.Testcontainers.Configurations.IOutputConsumer Output(string name, RunLog? runLog)
    {
        var file = new StreamWriter(Path.Combine(LogFolder, $"{name}.log"), append: false);
        lock (_fileGate)
            _files.Add(file);
        return Consume.RedirectStdoutAndStderrToStream(
            new LogSink(name, file, _fileGate, runLog),
            new LogSink(name, file, _fileGate, null));
    }

    private static async Task Check(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Toxiproxy {response.RequestMessage?.RequestUri} -> {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private sealed class ToxicName
    {
        public string Name { get; set; } = "";
    }
}
