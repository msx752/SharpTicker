#nullable disable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using StackExchange.Redis;
using SharpTicker.Utilities.Enums;

namespace SharpTicker.Caching.StackExchangeRedis.Helpers;

internal static class RedisKeyBuilder
{
    internal const string Prefix = "tq";
    internal const string TimeTickerIdsKey = $"{Prefix}:tt:ids";
    internal const string TimeTickerPendingKey = $"{Prefix}:tt:pending";
    internal const string CronIdsKey = $"{Prefix}:cron:ids";
    internal const string CronOccurrenceIdsKey = $"{Prefix}:co:ids";
    internal const string CronOccurrencePendingKey = $"{Prefix}:co:pending";

    internal static string TimeTickerKey(Guid id) => $"{Prefix}:tt:{id}";
    internal static string CronKey(Guid id) => $"{Prefix}:cron:{id}";
    internal static string CronOccurrenceKey(Guid id) => $"{Prefix}:co:{id}";
    internal static string CronOccurrencesByCronKey(Guid cronId) => $"{Prefix}:cron:{cronId}:occurrences";

    // One occurrence id per (cron, slot): every node derives the same id, so SET NX on its key admits one writer.
    // Fixed big-endian layout (id bytes + ticks) so nodes on any TFM or CPU agree; DateTime.Kind is not part of it.
    internal static Guid SlotId(Guid cronTickerId, DateTime executionTime)
    {
        Span<byte> input = stackalloc byte[24];
        cronTickerId.TryWriteBytes(input, bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(input[16..], executionTime.Ticks);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        return new Guid(hash[..16], bigEndian: true);
    }

    internal static double ToScore(DateTime utc) => utc.ToUniversalTime().Ticks;

    internal static bool CanAcquire(TickerStatus status, string currentHolder, string lockHolder)
    {
        return status is TickerStatus.Idle or TickerStatus.Queued &&
               (string.IsNullOrEmpty(currentHolder) || string.Equals(currentHolder, lockHolder, StringComparison.Ordinal));
    }

    internal static Guid[] ParseGuidSet(RedisValue[] members)
    {
        var result = new List<Guid>(members.Length);
        foreach (var member in members)
        {
            if (Guid.TryParse(member.ToString(), out var id))
                result.Add(id);
        }
        return result.ToArray();
    }
}
