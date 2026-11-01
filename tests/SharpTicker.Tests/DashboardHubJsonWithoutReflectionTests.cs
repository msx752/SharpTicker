using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.SignalR;
using SharpTicker.Dashboard.DependencyInjection;
using SharpTicker.Dashboard.Hubs;
using SharpTicker.Dashboard.Infrastructure;
using Xunit;

namespace SharpTicker.Tests;

/// <summary>
/// In an application with reflection-based serialization turned off, SignalR cannot write any hub message unless the
/// JSON protocol has a resolver: the write fails and the connection is dropped, so the dashboard gets no live update.
/// The dashboard adds its source-generated context for that case only, and never touches an application that has
/// reflection on (see <see cref="SerializerGlobalPollutionTests"/>).
/// </summary>
public sealed class DashboardHubJsonWithoutReflectionTests
{
    [Fact]
    public void WithReflectionOn_TheApplicationsHubJsonOptionsAreLeftAlone()
    {
        var options = new JsonHubProtocolOptions();
        var before = options.PayloadSerializerOptions.TypeInfoResolverChain.Count;

        ServiceCollectionExtensions.AddHubJsonContextWhenReflectionIsOff(options, reflectionIsEnabled: true);

        Assert.Equal(before, options.PayloadSerializerOptions.TypeInfoResolverChain.Count);
        Assert.DoesNotContain(options.PayloadSerializerOptions.TypeInfoResolverChain,
            r => ReferenceEquals(r, DashboardJsonSerializerContext.Default));
    }

    [Fact]
    public void WithReflectionOff_TheContextIsAddedAfterTheResolversTheApplicationRegistered()
    {
        var options = new JsonHubProtocolOptions();
        var hostResolver = new DefaultJsonTypeInfoResolver();
        options.PayloadSerializerOptions.TypeInfoResolverChain.Add(hostResolver);

        ServiceCollectionExtensions.AddHubJsonContextWhenReflectionIsOff(options, reflectionIsEnabled: false);

        var chain = options.PayloadSerializerOptions.TypeInfoResolverChain;
        Assert.Same(hostResolver, chain[chain.Count - 2]);
        Assert.Same(DashboardJsonSerializerContext.Default, chain[chain.Count - 1]);
    }

    [Fact]
    public void TheContextDescribesEveryArgumentTheHubSends()
    {
        // The options of an application without reflection: only the dashboard's context to go by.
        var options = new JsonSerializerOptions { TypeInfoResolver = DashboardJsonSerializerContext.Default };

        foreach (var type in new[] { typeof(Guid), typeof(string), typeof(JsonElement), typeof(HubConnectionStatus) })
            Assert.NotNull(options.GetTypeInfo(type));
    }

    [Fact]
    public void TheConnectionStatusIsWrittenInCamelCase()
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = DashboardJsonSerializerContext.Default };
        var status = new HubConnectionStatus("c1", true, "mustafa", new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc));

        // SignalR writes an argument by its run-time type.
        var json = JsonSerializer.Serialize(status, status.GetType(), options);

        Assert.Contains("\"connectionId\":\"c1\"", json);
        Assert.Contains("\"authenticated\":true", json);
        Assert.Contains("\"username\":\"mustafa\"", json);
        Assert.Contains("\"timestamp\"", json);
    }
}
