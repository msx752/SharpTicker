using System.Text.Json;
using SharpTicker.Caching.StackExchangeRedis;
using SharpTicker.Dashboard.Infrastructure;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Interfaces;

namespace SharpTicker.Tests;

public sealed class TrimSafeSerializationTests
{
    [Fact]
    public void DashboardContext_HasTypeInfo_For_String()
    {
        Assert.NotNull(DashboardJsonSerializerContext.Default.String);
    }

    [Fact]
    public void DashboardContext_HasTypeInfo_For_NullableDateTime()
    {
        Assert.NotNull(DashboardJsonSerializerContext.Default.NullableDateTime);
    }

    [Fact]
    public void DashboardContext_HasTypeInfo_For_Boolean()
    {
        Assert.NotNull(DashboardJsonSerializerContext.Default.Boolean);
    }

    [Fact]
    public void DashboardContext_HasTypeInfo_For_CronTickerEntity()
    {
        Assert.NotNull(DashboardJsonSerializerContext.Default.CronTickerEntity);
    }

    [Fact]
    public void DashboardContext_HasTypeInfo_For_TimeTickerEntity()
    {
        Assert.NotNull(DashboardJsonSerializerContext.Default.TimeTickerEntity);
    }

    [Fact]
    public void DashboardContext_HasTypeInfo_For_CronTickerOccurrenceEntityCronTickerEntity()
    {
        // Property name for CronTickerOccurrenceEntity<CronTickerEntity> as emitted by the source generator.
        Assert.NotNull(DashboardJsonSerializerContext.Default.CronTickerOccurrenceEntityCronTickerEntity);
    }

    [Fact]
    public void DashboardContext_HasTypeInfo_For_CronOccurrenceUpdateNotification()
    {
        Assert.NotNull(DashboardJsonSerializerContext.Default.CronOccurrenceUpdateNotification);
    }

    [Fact]
    public void SerializeToElement_String_ProducesCorrectJson()
    {
        var element = JsonSerializer.SerializeToElement("hello", DashboardJsonSerializerContext.Default.String);

        Assert.Equal(JsonValueKind.String, element.ValueKind);
        Assert.Equal("hello", element.GetString());
    }

    [Fact]
    public void SerializeToElement_NullableDateTime_WithValue_ProducesCorrectJson()
    {
        DateTime? value = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        var element = JsonSerializer.SerializeToElement(value, DashboardJsonSerializerContext.Default.NullableDateTime);

        Assert.Equal(JsonValueKind.String, element.ValueKind);
        Assert.False(string.IsNullOrEmpty(element.GetString()));
    }

    [Fact]
    public void SerializeToElement_NullableDateTime_Null_ProducesJsonNull()
    {
        DateTime? value = null;

        var element = JsonSerializer.SerializeToElement(value, DashboardJsonSerializerContext.Default.NullableDateTime);

        Assert.Equal(JsonValueKind.Null, element.ValueKind);
    }

    [Fact]
    public void SerializeToElement_CronTickerEntity_ProducesExpectedProperties()
    {
        var id = Guid.NewGuid();
        var entity = new CronTickerEntity
        {
            Id = id,
            Expression = "0 * * * *",
            IsEnabled = true,
            Retries = 3
        };

        var element = JsonSerializer.SerializeToElement(entity, DashboardJsonSerializerContext.Default.CronTickerEntity);

        Assert.Equal(JsonValueKind.Object, element.ValueKind);

        Assert.True(element.TryGetProperty("id", out var idProp));
        Assert.Equal(id.ToString(), idProp.GetString());

        Assert.True(element.TryGetProperty("expression", out var exprProp));
        Assert.Equal("0 * * * *", exprProp.GetString());

        Assert.True(element.TryGetProperty("isEnabled", out var enabledProp));
        Assert.True(enabledProp.GetBoolean());

        Assert.True(element.TryGetProperty("retries", out var retriesProp));
        Assert.Equal(3, retriesProp.GetInt32());
    }

    [Fact]
    public void SerializeToElement_CronOccurrenceUpdateNotification_ProducesAllSevenProperties()
    {
        var id = Guid.NewGuid();
        var cronTickerId = Guid.NewGuid();
        var executedAt = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        var dto = new SharpTicker.Dashboard.Hubs.CronOccurrenceUpdateNotification
        {
            Id = id,
            Status = TickerStatus.Done,
            CronTickerId = cronTickerId,
            ExecutedAt = executedAt,
            ElapsedTime = 1500L,
            RetryCount = 1,
            ExceptionMessage = "test-error"
        };

        var element = JsonSerializer.SerializeToElement(dto, DashboardJsonSerializerContext.Default.CronOccurrenceUpdateNotification);

        Assert.Equal(JsonValueKind.Object, element.ValueKind);

        Assert.True(element.TryGetProperty("id", out var idProp));
        Assert.Equal(id.ToString(), idProp.GetString());

        Assert.True(element.TryGetProperty("status", out _));
        Assert.True(element.TryGetProperty("cronTickerId", out var cronIdProp));
        Assert.Equal(cronTickerId.ToString(), cronIdProp.GetString());

        Assert.True(element.TryGetProperty("executedAt", out _));

        Assert.True(element.TryGetProperty("elapsedTime", out var elapsedProp));
        Assert.Equal(1500L, elapsedProp.GetInt64());

        Assert.True(element.TryGetProperty("retryCount", out var retryProp));
        Assert.Equal(1, retryProp.GetInt32());

        Assert.True(element.TryGetProperty("exceptionMessage", out var exMsgProp));
        Assert.Equal("test-error", exMsgProp.GetString());
    }

    [Fact]
    public void CronOccurrenceUpdateNotification_HasExpectedPropertyNames()
    {
        var type = typeof(SharpTicker.Dashboard.Hubs.CronOccurrenceUpdateNotification);

        Assert.NotNull(type.GetProperty("Id"));
        Assert.NotNull(type.GetProperty("Status"));
        Assert.NotNull(type.GetProperty("CronTickerId"));
        Assert.NotNull(type.GetProperty("ExecutedAt"));
        Assert.NotNull(type.GetProperty("ElapsedTime"));
        Assert.NotNull(type.GetProperty("RetryCount"));
        Assert.NotNull(type.GetProperty("ExceptionMessage"));
    }

    [Fact]
    public void CronOccurrenceUpdateNotification_SerializesWithCamelCaseNames()
    {
        // The JSON keys must be camelCase to match the anonymous type they replace.
        var dto = new SharpTicker.Dashboard.Hubs.CronOccurrenceUpdateNotification
        {
            Id = Guid.NewGuid(),
            Status = TickerStatus.Idle,
            CronTickerId = Guid.NewGuid(),
            ExecutedAt = DateTime.UtcNow,
            ElapsedTime = 100L,
            RetryCount = 0,
            ExceptionMessage = null
        };

        var element = JsonSerializer.SerializeToElement(dto, DashboardJsonSerializerContext.Default.CronOccurrenceUpdateNotification);
        var json = element.GetRawText();

        Assert.Contains("\"id\"", json);
        Assert.Contains("\"status\"", json);
        Assert.Contains("\"cronTickerId\"", json);
        Assert.Contains("\"executedAt\"", json);
        Assert.Contains("\"elapsedTime\"", json);
        Assert.Contains("\"retryCount\"", json);

        // Assert no PascalCase keys leaked through
        Assert.DoesNotContain("\"Id\"", json);
        Assert.DoesNotContain("\"Status\"", json);
        Assert.DoesNotContain("\"CronTickerId\"", json);
        Assert.DoesNotContain("\"ElapsedTime\"", json);
    }

    [Fact]
    public void UpdateNodeHeartBeatAsync_AcceptsJsonElement()
    {
        var method = typeof(ISharpTickerNotificationHubSender).GetMethod("UpdateNodeHeartBeatAsync");

        Assert.NotNull(method);

        var parameters = method!.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(JsonElement), parameters[0].ParameterType);
    }

    [Fact]
    public void UpdateHostStatus_AcceptsBool()
    {
        var method = typeof(ISharpTickerNotificationHubSender).GetMethod("UpdateHostStatus");

        Assert.NotNull(method);

        var parameters = method!.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(bool), parameters[0].ParameterType);
    }

    [Fact]
    public void RedisContext_HasTypeInfo_For_ICollectionTimeTickerEntity()
    {
        var typeInfo = RedisContextJsonSerializerContext.Default.Options.GetTypeInfo(typeof(ICollection<TimeTickerEntity>));
        Assert.NotNull(typeInfo);
    }

    [Fact]
    public void TimeTickerEntity_WithChildren_RoundTripsViaRedisContext()
    {
        // Verifies that a TimeTickerEntity with a populated Children collection survives a full
        // serialize→deserialize round-trip using the RedisContextJsonSerializerContext.
        var parent = new TimeTickerEntity { Id = Guid.NewGuid() };
        var child = new TimeTickerEntity { Id = Guid.NewGuid() };
        parent.Children = new List<TimeTickerEntity> { child };

        var json = JsonSerializer.Serialize(parent, RedisContextJsonSerializerContext.Default.TimeTickerEntity);
        var deserialized = JsonSerializer.Deserialize(json, RedisContextJsonSerializerContext.Default.TimeTickerEntity);

        Assert.NotNull(deserialized);
        Assert.Single(deserialized.Children);
        Assert.Equal(child.Id, deserialized.Children.First().Id);
    }
}
