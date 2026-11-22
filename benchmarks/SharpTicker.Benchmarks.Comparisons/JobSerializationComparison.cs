using BenchmarkDotNet.Attributes;
using Hangfire.Common;
using SharpTicker.Utilities;

namespace SharpTicker.Benchmarks.Comparisons;

/// <summary>
/// Serializing and reading the data a job carries, with each library's own serializer for it:
/// - SharpTicker: TickerHelper.CreateTickerRequest / ReadTickerRequest, the path a request takes into and out of the store,
///   once as plain UTF-8 JSON (the default) and once with GZip on. TickerHelper.UseGZipCompression is a static setting; each
///   SharpTicker row sets it to its own value first (one static write).
/// - Hangfire: SerializationHelper.Serialize / Deserialize with SerializationOption.User (Newtonsoft.Json), its serializer for
///   user data such as job arguments.
/// Quartz has no row: its RAMJobStore keeps the JobDataMap objects without serializing them, and the serializers of its
///   persistent store come in separate packages.
/// </summary>
[MemoryDiagnoser]
public class JobSerializationComparison
{
    private SampleRequest _small = null!;
    private SampleRequest _medium = null!;

    private byte[] _sharpTickerSmall = null!;
    private byte[] _sharpTickerMedium = null!;
    private byte[] _sharpTickerSmallGzip = null!;
    private byte[] _sharpTickerMediumGzip = null!;
    private string _hangfireSmall = null!;
    private string _hangfireMedium = null!;

    [GlobalSetup]
    public void Setup()
    {
        _small = new SampleRequest
        {
            Id = Guid.NewGuid(),
            Name = "Process Order",
            Amount = 99.95m,
            Tags = ["urgent", "retail"]
        };

        _medium = new SampleRequest
        {
            Id = Guid.NewGuid(),
            Name = "Generate Monthly Report with Extended Analytics Dashboard",
            Amount = 1_234_567.89m,
            Tags = Enumerable.Range(0, 50).Select(i => $"tag-{i}").ToArray(),
            Metadata = Enumerable.Range(0, 20).ToDictionary(i => $"key-{i}", i => $"value-{i}-{Guid.NewGuid()}")
        };

        TickerHelper.UseGZipCompression = false;
        _sharpTickerSmall = TickerHelper.CreateTickerRequest(_small);
        _sharpTickerMedium = TickerHelper.CreateTickerRequest(_medium);

        TickerHelper.UseGZipCompression = true;
        _sharpTickerSmallGzip = TickerHelper.CreateTickerRequest(_small);
        _sharpTickerMediumGzip = TickerHelper.CreateTickerRequest(_medium);
        TickerHelper.UseGZipCompression = false;

        _hangfireSmall = SerializationHelper.Serialize(_small, SerializationOption.User);
        _hangfireMedium = SerializationHelper.Serialize(_medium, SerializationOption.User);
    }

    [GlobalCleanup]
    public void Cleanup() => TickerHelper.UseGZipCompression = false;

    // ── Serialize: small ──

    [Benchmark(Baseline = true, Description = "SharpTicker: CreateTickerRequest, small")]
    public byte[] SharpTicker_Serialize_Small()
    {
        TickerHelper.UseGZipCompression = false;
        return TickerHelper.CreateTickerRequest(_small);
    }

    [Benchmark(Description = "SharpTicker: CreateTickerRequest + GZip, small")]
    public byte[] SharpTicker_SerializeGzip_Small()
    {
        TickerHelper.UseGZipCompression = true;
        return TickerHelper.CreateTickerRequest(_small);
    }

    [Benchmark(Description = "Hangfire: SerializationHelper.Serialize (User), small")]
    public string Hangfire_Serialize_Small() =>
        SerializationHelper.Serialize(_small, SerializationOption.User);

    // ── Serialize: medium ──

    [Benchmark(Description = "SharpTicker: CreateTickerRequest, medium")]
    public byte[] SharpTicker_Serialize_Medium()
    {
        TickerHelper.UseGZipCompression = false;
        return TickerHelper.CreateTickerRequest(_medium);
    }

    [Benchmark(Description = "SharpTicker: CreateTickerRequest + GZip, medium")]
    public byte[] SharpTicker_SerializeGzip_Medium()
    {
        TickerHelper.UseGZipCompression = true;
        return TickerHelper.CreateTickerRequest(_medium);
    }

    [Benchmark(Description = "Hangfire: SerializationHelper.Serialize (User), medium")]
    public string Hangfire_Serialize_Medium() =>
        SerializationHelper.Serialize(_medium, SerializationOption.User);

    // ── Read: small ──

    [Benchmark(Description = "SharpTicker: ReadTickerRequest, small")]
    public SampleRequest SharpTicker_Read_Small()
    {
        TickerHelper.UseGZipCompression = false;
        return TickerHelper.ReadTickerRequest<SampleRequest>(_sharpTickerSmall);
    }

    [Benchmark(Description = "SharpTicker: ReadTickerRequest + GZip, small")]
    public SampleRequest SharpTicker_ReadGzip_Small()
    {
        TickerHelper.UseGZipCompression = true;
        return TickerHelper.ReadTickerRequest<SampleRequest>(_sharpTickerSmallGzip);
    }

    [Benchmark(Description = "Hangfire: SerializationHelper.Deserialize (User), small")]
    public SampleRequest Hangfire_Read_Small() =>
        SerializationHelper.Deserialize<SampleRequest>(_hangfireSmall, SerializationOption.User);

    // ── Read: medium ──

    [Benchmark(Description = "SharpTicker: ReadTickerRequest, medium")]
    public SampleRequest SharpTicker_Read_Medium()
    {
        TickerHelper.UseGZipCompression = false;
        return TickerHelper.ReadTickerRequest<SampleRequest>(_sharpTickerMedium);
    }

    [Benchmark(Description = "SharpTicker: ReadTickerRequest + GZip, medium")]
    public SampleRequest SharpTicker_ReadGzip_Medium()
    {
        TickerHelper.UseGZipCompression = true;
        return TickerHelper.ReadTickerRequest<SampleRequest>(_sharpTickerMediumGzip);
    }

    [Benchmark(Description = "Hangfire: SerializationHelper.Deserialize (User), medium")]
    public SampleRequest Hangfire_Read_Medium() =>
        SerializationHelper.Deserialize<SampleRequest>(_hangfireMedium, SerializationOption.User);

    public class SampleRequest
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public decimal Amount { get; set; }
        public string[] Tags { get; set; } = [];
        public Dictionary<string, string>? Metadata { get; set; }
    }
}
