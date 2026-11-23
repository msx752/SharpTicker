using BenchmarkDotNet.Attributes;
using SharpTicker.Utilities;

namespace SharpTicker.Benchmarks;

/// <summary>
/// Benchmarks for TickerHelper request serialization/deserialization.
/// Measures the overhead of creating and reading ticker requests, as plain UTF-8 JSON (the default) and with GZip
/// compression on (TickerHelper.UseGZipCompression).
/// </summary>
[MemoryDiagnoser]
public class RequestSerializationBenchmarks
{
    /// <summary>
    /// TickerHelper.UseGZipCompression for this run. It is a static setting: it is set in setup, before the payloads
    /// are created, and put back to the default in cleanup.
    /// </summary>
    [Params(false, true)]
    public bool GZip { get; set; }

    private byte[] _smallPayload = null!;
    private byte[] _mediumPayload = null!;
    private byte[] _largePayload = null!;

    private readonly SmallRequest _smallRequest = new() { Id = 42, Name = "test" };
    private readonly MediumRequest _mediumRequest = new()
    {
        UserId = Guid.NewGuid(),
        Email = "user@example.com",
        Tags = ["urgent", "email", "notification", "retry"],
        Metadata = new Dictionary<string, string>
        {
            ["source"] = "api",
            ["region"] = "eu-west-1",
            ["priority"] = "high"
        }
    };
    private LargeRequest _largeRequest = null!;

    [GlobalSetup]
    public void Setup()
    {
        TickerHelper.UseGZipCompression = GZip;

        _largeRequest = new LargeRequest
        {
            Items = Enumerable.Range(0, 1000).Select(i => new LargeRequest.Item
            {
                Id = i,
                Name = $"Item-{i}",
                Value = i * 1.5,
                CreatedAt = DateTime.UtcNow.AddMinutes(-i)
            }).ToList()
        };

        _smallPayload = TickerHelper.CreateTickerRequest(_smallRequest);
        _mediumPayload = TickerHelper.CreateTickerRequest(_mediumRequest);
        _largePayload = TickerHelper.CreateTickerRequest(_largeRequest);
    }

    [GlobalCleanup]
    public void Cleanup() => TickerHelper.UseGZipCompression = false;

    // ── Serialization ──

    [Benchmark(Description = "Serialize: Small (2 fields)")]
    public byte[] Serialize_Small() => TickerHelper.CreateTickerRequest(_smallRequest);

    [Benchmark(Description = "Serialize: Medium (4 fields, 2 of them collections)")]
    public byte[] Serialize_Medium() => TickerHelper.CreateTickerRequest(_mediumRequest);

    [Benchmark(Description = "Serialize: Large (1000 items)")]
    public byte[] Serialize_Large() => TickerHelper.CreateTickerRequest(_largeRequest);

    // ── Deserialization ──

    [Benchmark(Description = "Deserialize: Small")]
    public SmallRequest Deserialize_Small() => TickerHelper.ReadTickerRequest<SmallRequest>(_smallPayload);

    [Benchmark(Description = "Deserialize: Medium")]
    public MediumRequest Deserialize_Medium() => TickerHelper.ReadTickerRequest<MediumRequest>(_mediumPayload);

    [Benchmark(Description = "Deserialize: Large (1000 items)")]
    public LargeRequest Deserialize_Large() => TickerHelper.ReadTickerRequest<LargeRequest>(_largePayload);

    // ── Roundtrip ──

    [Benchmark(Description = "Roundtrip: Small")]
    public SmallRequest Roundtrip_Small()
    {
        var bytes = TickerHelper.CreateTickerRequest(_smallRequest);
        return TickerHelper.ReadTickerRequest<SmallRequest>(bytes);
    }

    // ── Request types ──

    public record SmallRequest
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
    }

    public record MediumRequest
    {
        public Guid UserId { get; init; }
        public string Email { get; init; } = "";
        public List<string> Tags { get; init; } = [];
        public Dictionary<string, string> Metadata { get; init; } = new();
    }

    public record LargeRequest
    {
        public List<Item> Items { get; init; } = [];

        public record Item
        {
            public int Id { get; init; }
            public string Name { get; init; } = "";
            public double Value { get; init; }
            public DateTime CreatedAt { get; init; }
        }
    }
}
