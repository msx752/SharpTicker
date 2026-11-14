using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>
/// Probes on a real Redis: each one drives provider instances the way the scheduler loops do and asserts what
/// happens TODAY, bugs included, so they pass. When a fix closes a finding its probe is deleted and a regression
/// test that asserts the corrected behaviour stays.
/// </summary>
[Collection(RedisTestFixture.CollectionName)]
public class RedisDoubleRunProbeTests(RedisTestFixture fixture) : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    // Seven fractional digits with no trailing zero, so the stored JSON text equals DateTime.ToString("O") and the
    // Lua compare-and-set can match. Probes that are not about that compare use it to stay clear of it.
    private static readonly DateTime SeedUpdatedAt = T0.AddHours(-1).AddTicks(1234567);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    private static TimeTickerEntity NewTimeTicker(DateTime? updatedAt = null, DateTime? executionTime = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Function = "probe-fn",
            ExecutionTime = executionTime ?? T0,
            Status = TickerStatus.Idle,
            CreatedAt = T0.AddHours(-2),
            UpdatedAt = updatedAt ?? SeedUpdatedAt,
            Request = []
        };

    private async Task<string?> RawJson(Guid id)
        => (string?)await fixture.Database.StringGetAsync(SharpTicker.Caching.StackExchangeRedis.Helpers.RedisKeyBuilder.TimeTickerKey(id));

    private async Task<TickerStatus> RawStatus(Guid id)
    {
        using var document = System.Text.Json.JsonDocument.Parse((await RawJson(id))!);
        return (TickerStatus)document.RootElement.GetProperty("Status").GetInt32();
    }
}
