#nullable disable
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization.Metadata;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Entities.BaseEntity;
using SharpTicker.Utilities.Enums;

namespace SharpTicker.Caching.StackExchangeRedis.Helpers;

/// <summary>
/// The ticker entities fill some of their members themselves; those have an internal setter and [JsonInclude]. A
/// source-generated context an application writes for ticker types of its own is generated in the application's assembly,
/// which cannot reach those setters, and the contract it produces throws for each of these members when a record is
/// read. The getters are public and work. This gives the members their setters on such a contract, without reflection
/// over them. A member added to the entities with an internal setter and [JsonInclude] has to be added here.
/// </summary>
internal static class EntityMembers
{
    // By the name the member has in the stored record, which is its own name: the store sets no naming policy.
    private static readonly Dictionary<string, Action<object, object>> OfEveryTicker = new(StringComparer.Ordinal)
    {
        [nameof(BaseTickerEntity.InitIdentifier)] = (o, v) => ((BaseTickerEntity)o).InitIdentifier = (string)v,
        [nameof(BaseTickerEntity.CreatedAt)] = (o, v) => ((BaseTickerEntity)o).CreatedAt = (DateTime)v,
        [nameof(BaseTickerEntity.UpdatedAt)] = (o, v) => ((BaseTickerEntity)o).UpdatedAt = (DateTime)v
    };

    private static class OfTimeTicker<TTimeTicker> where TTimeTicker : TimeTickerEntity<TTimeTicker>
    {
        internal static readonly Dictionary<string, Action<object, object>> Members = new(StringComparer.Ordinal)
        {
            [nameof(TimeTickerEntity.Status)] = (o, v) => ((TimeTickerEntity<TTimeTicker>)o).Status = (TickerStatus)v,
            [nameof(TimeTickerEntity.LockHolder)] = (o, v) => ((TimeTickerEntity<TTimeTicker>)o).LockHolder = (string)v,
            [nameof(TimeTickerEntity.LockedAt)] = (o, v) => ((TimeTickerEntity<TTimeTicker>)o).LockedAt = (DateTime?)v,
            [nameof(TimeTickerEntity.ExecutedAt)] = (o, v) => ((TimeTickerEntity<TTimeTicker>)o).ExecutedAt = (DateTime?)v,
            [nameof(TimeTickerEntity.ExceptionMessage)] = (o, v) => ((TimeTickerEntity<TTimeTicker>)o).ExceptionMessage = (string)v,
            [nameof(TimeTickerEntity.SkippedReason)] = (o, v) => ((TimeTickerEntity<TTimeTicker>)o).SkippedReason = (string)v,
            [nameof(TimeTickerEntity.ElapsedTime)] = (o, v) => ((TimeTickerEntity<TTimeTicker>)o).ElapsedTime = (long)v,
            [nameof(TimeTickerEntity.RetryCount)] = (o, v) => ((TimeTickerEntity<TTimeTicker>)o).RetryCount = (int)v,
            [nameof(TimeTickerEntity.ParentId)] = (o, v) => ((TimeTickerEntity<TTimeTicker>)o).ParentId = (Guid?)v
        };
    }

    /// <summary>
    /// The resolver with the setters of the entities' own members set on every contract it produces for a ticker type.
    /// </summary>
    internal static IJsonTypeInfoResolver WithEntityMembers<TTimeTicker>(this IJsonTypeInfoResolver resolver)
        where TTimeTicker : TimeTickerEntity<TTimeTicker>
        => resolver.WithAddedModifier(static info =>
        {
            if (info.Kind != JsonTypeInfoKind.Object || !typeof(BaseTickerEntity).IsAssignableFrom(info.Type))
                return;

            var isTimeTicker = typeof(TimeTickerEntity<TTimeTicker>).IsAssignableFrom(info.Type);
            foreach (var property in info.Properties)
            {
                if (OfEveryTicker.TryGetValue(property.Name, out var set)
                    || (isTimeTicker && OfTimeTicker<TTimeTicker>.Members.TryGetValue(property.Name, out set)))
                    property.Set = set;
            }
        });
}
