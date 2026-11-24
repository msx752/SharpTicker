using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpTicker.DependencyInjection;
using SharpTicker.Provider;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Interfaces.Managers;

namespace SharpTicker.Tests;

internal sealed record TypedReadRequest(string Name, int Value, List<string> Tags);

[JsonSerializable(typeof(TypedReadRequest))]
internal sealed partial class TypedReadRequestJsonContext : JsonSerializerContext;

/// <summary>
/// A job that takes a typed request, as an application uses it: <c>AddSharpTicker</c> with the request options under test,
/// a function mapped with <c>MapTicker&lt;TRequest&gt;</c>, the request written with <c>TickerHelper.CreateTickerRequest</c>,
/// the ticker added through <see cref="ITimeTickerManager{TTimeTicker}"/>, and the scheduler running it on the in-memory
/// store. Two jobs run per host, so the second read uses what the first one resolved. The test fails when a job does not
/// get its request back as written, the exception handler is called, or anything is logged at Warning or above.
/// </summary>
[Collection(SharedStaticStateCollection.Name)]
public class TypedRequestReadEndToEndTests : IAsyncLifetime
{
    // A function with no cron: it stays in the process-wide registry after the test, and no later host seeds or runs it.
    private const string Function = "TypedRequestRead.Job";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private sealed class RecordingExceptionHandler : ITickerExceptionHandler
    {
        public static ConcurrentQueue<string> Calls { get; } = new();

        public Task HandleExceptionAsync(Exception exception, Guid tickerId, TickerType tickerType)
        {
            Calls.Enqueue($"exception {tickerId}: {exception}");
            return Task.CompletedTask;
        }

        public Task HandleCanceledExceptionAsync(Exception exception, Guid tickerId, TickerType tickerType)
        {
            Calls.Enqueue($"cancellation {tickerId}: {exception}");
            return Task.CompletedTask;
        }
    }

    private readonly CapturedLogs _logs = new();
    private readonly JsonSerializerOptions _originalOptions = TickerHelper.RequestJsonSerializerOptions;
    private readonly bool _originalGZip = TickerHelper.UseGZipCompression;
    // Static: the function stays in the process-wide registry, and a later MapTicker of the same name keeps the first handler.
    private static readonly ConcurrentDictionary<Guid, TypedReadRequest?> Received = new();
    private WebApplication? _app;

    public async Task InitializeAsync()
    {
        RecordingExceptionHandler.Calls.Clear();
        Received.Clear();
        await ClearStoreAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        await ClearStoreAsync();
        TickerHelper.RequestJsonSerializerOptions = _originalOptions;
        TickerHelper.UseGZipCompression = _originalGZip;
    }

    private static async Task ClearStoreAsync()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var store = new TickerInMemoryPersistenceProvider<TimeTickerEntity, CronTickerEntity>(services);
        var times = await store.GetTimeTickers(null, default);
        await store.RemoveTimeTickers(times.Select(t => t.Id).ToArray());
    }

    private string Report()
        => $"handler: {string.Join("\n", RecordingExceptionHandler.Calls)}\nlog:\n{string.Join("\n", _logs.Entries.Select(e => $"{e.Level} {e.Category}: {e.Message}"))}";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AJobWithATypedRequest_GetsItsRequestAsWritten(bool sourceGeneratedContext, bool gzip)
    {
        new ServiceCollection().MapTicker<TypedReadRequest>(Function, (context, _) =>
        {
            Received[context.Id] = context.Request;
            return Task.CompletedTask;
        });

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(_logs);
        builder.Services.AddSharpTicker(o =>
        {
            o.SetExceptionHandler<RecordingExceptionHandler>();
            if (sourceGeneratedContext)
                o.WithJsonContext(TypedReadRequestJsonContext.Default);
            if (gzip)
                o.UseGZipCompression();
        });
        _app = builder.Build();
        _app.UseSharpTicker();
        await _app.StartAsync();

        var manager = _app.Services.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
        var sent = new Dictionary<Guid, TypedReadRequest>();
        for (var i = 0; i < 2; i++)
        {
            var request = new TypedReadRequest($"job {i}", 40 + i, ["a", $"tag {i}"]);
            var added = await manager.AddAsync(new TimeTickerEntity
            {
                Function = Function,
                ExecutionTime = DateTime.UtcNow.AddSeconds(1),
                Request = TickerHelper.CreateTickerRequest(request)
            });
            Assert.True(added.IsSucceeded, added.Exception?.Message);
            sent[added.Result.Id] = request;
        }

        var store = _app.Services.GetRequiredService<ITickerPersistenceProvider<TimeTickerEntity, CronTickerEntity>>();
        var deadline = DateTime.UtcNow + Limit;
        while (true)
        {
            var tickers = (await store.GetTimeTickers(null, default)).Where(t => sent.ContainsKey(t.Id)).ToArray();
            Assert.Equal(sent.Count, tickers.Length);
            if (tickers.All(t => t.Status is TickerStatus.Done or TickerStatus.DueDone))
                break;
            Assert.True(DateTime.UtcNow < deadline,
                $"not within {Limit}: both tickers ran and ended (status {string.Join(", ", tickers.Select(t => t.Status))})\n{Report()}");
            await Task.Delay(100);
        }

        foreach (var (id, request) in sent)
        {
            Assert.True(Received.TryGetValue(id, out var got), $"the job of {id} did not run\n{Report()}");
            Assert.NotNull(got);
            Assert.Equal(request.Name, got.Name);
            Assert.Equal(request.Value, got.Value);
            Assert.Equal(request.Tags, got.Tags);
        }
        Assert.True(RecordingExceptionHandler.Calls.IsEmpty, Report());
        Assert.DoesNotContain(_logs.Entries, e => e.Level >= LogLevel.Warning);
    }
}
