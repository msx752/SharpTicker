using System;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using SharpTicker.Utilities;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Models;

namespace SharpTicker.MongoDB.Infrastructure
{
    /// <summary>
    /// Builds Mongo UpdateDefinition from InternalFunctionContext. Mirrors the EF
    /// EfUpdateExtensions.UpdateTimeTicker / UpdateCronTickerOccurrence helpers
    /// — same conditional-property-write pattern, just emitting $set operations.
    /// </summary>
    internal static class MongoUpdateBuilders
    {
        public static UpdateDefinition<TTimeTicker> BuildTimeTickerUpdate<TTimeTicker>(
            InternalFunctionContext ctx, DateTime updatedAt)
            where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
        {
            var props = ctx.GetPropsToUpdate();
            var defs = Builders<TTimeTicker>.Update;
            UpdateDefinition<TTimeTicker> u = null;

            if (props.Contains(nameof(InternalFunctionContext.Status)) && ctx.Status != TickerStatus.Skipped)
            {
                u = Combine(u, defs.Set(x => x.Status, ctx.Status));
            }
            else if (props.Contains(nameof(InternalFunctionContext.Status)))
            {
                u = Combine(u, defs.Set(x => x.Status, ctx.Status));
                u = Combine(u, defs.Set(x => x.SkippedReason, ctx.ExceptionDetails));
            }

            if (props.Contains(nameof(InternalFunctionContext.ExecutedAt)))
                u = Combine(u, defs.Set(x => x.ExecutedAt, ctx.ExecutedAt));

            if (props.Contains(nameof(InternalFunctionContext.ExceptionDetails)) && ctx.Status != TickerStatus.Skipped)
                u = Combine(u, defs.Set(x => x.ExceptionMessage, ctx.ExceptionDetails));

            if (props.Contains(nameof(InternalFunctionContext.ElapsedTime)))
                u = Combine(u, defs.Set(x => x.ElapsedTime, ctx.ElapsedTime));

            if (props.Contains(nameof(InternalFunctionContext.RetryCount)))
                u = Combine(u, defs.Set(x => x.RetryCount, ctx.RetryCount));

            if (props.Contains(nameof(InternalFunctionContext.ReleaseLock)))
            {
                u = Combine(u, defs.Set(x => x.LockHolder, (string)null));
                u = Combine(u, defs.Set(x => x.LockedAt, (DateTime?)null));
            }

            u = Combine(u, defs.Set(x => x.UpdatedAt, updatedAt));
            return u;
        }

        public static UpdateDefinition<CronTickerOccurrenceEntity<TCronTicker>> BuildCronOccurrenceUpdate<TCronTicker>(
            InternalFunctionContext ctx, DateTime updatedAt)
            where TCronTicker : CronTickerEntity, new()
        {
            var props = ctx.GetPropsToUpdate();
            var defs = Builders<CronTickerOccurrenceEntity<TCronTicker>>.Update;
            UpdateDefinition<CronTickerOccurrenceEntity<TCronTicker>> u = null;

            if (props.Contains(nameof(InternalFunctionContext.Status)) && ctx.Status != TickerStatus.Skipped)
            {
                u = Combine(u, defs.Set(x => x.Status, ctx.Status));
            }
            else if (props.Contains(nameof(InternalFunctionContext.Status)))
            {
                u = Combine(u, defs.Set(x => x.Status, ctx.Status));
                u = Combine(u, defs.Set(x => x.SkippedReason, ctx.ExceptionDetails));
            }

            if (props.Contains(nameof(InternalFunctionContext.ExecutedAt)))
                u = Combine(u, defs.Set(x => x.ExecutedAt, ctx.ExecutedAt));

            if (props.Contains(nameof(InternalFunctionContext.ExceptionDetails)) && ctx.Status != TickerStatus.Skipped)
                u = Combine(u, defs.Set(x => x.ExceptionMessage, ctx.ExceptionDetails));

            if (props.Contains(nameof(InternalFunctionContext.ElapsedTime)))
                u = Combine(u, defs.Set(x => x.ElapsedTime, ctx.ElapsedTime));

            if (props.Contains(nameof(InternalFunctionContext.RetryCount)))
                u = Combine(u, defs.Set(x => x.RetryCount, ctx.RetryCount));

            if (props.Contains(nameof(InternalFunctionContext.ReleaseLock)))
            {
                u = Combine(u, defs.Set(x => x.LockHolder, (string)null));
                u = Combine(u, defs.Set(x => x.LockedAt, (DateTime?)null));
            }

            if (props.Contains(nameof(InternalFunctionContext.ExecutionTime)))
                u = Combine(u, defs.Set(x => x.ExecutionTime, ctx.ExecutionTime));

            u = Combine(u, defs.Set(x => x.UpdatedAt, updatedAt));
            return u;
        }

        public static FilterDefinition<TTimeTicker> CanAcquireTimeTicker<TTimeTicker>(string lockHolder)
            where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
        {
            var fb = Builders<TTimeTicker>.Filter;
            var statusOk = fb.In(x => x.Status, new[] { TickerStatus.Idle, TickerStatus.Queued });
            var ownerOrFree = fb.Or(
                fb.Eq(x => x.LockHolder, lockHolder),
                fb.Eq(x => x.LockedAt, (DateTime?)null));
            return fb.And(statusOk, ownerOrFree);
        }

        public static FilterDefinition<CronTickerOccurrenceEntity<TCronTicker>> CanAcquireCronOccurrence<TCronTicker>(string lockHolder)
            where TCronTicker : CronTickerEntity, new()
        {
            var fb = Builders<CronTickerOccurrenceEntity<TCronTicker>>.Filter;
            var statusOk = fb.In(x => x.Status, new[] { TickerStatus.Idle, TickerStatus.Queued });
            var ownerOrFree = fb.Or(
                fb.Eq(x => x.LockHolder, lockHolder),
                fb.Eq(x => x.LockedAt, (DateTime?)null));
            return fb.And(statusOk, ownerOrFree);
        }

        // The fenced claim: the row must still be Queued and held by this node. Internal on purpose, no public surface.
        public static FilterDefinition<TTimeTicker> QueuedByTimeTicker<TTimeTicker>(string lockHolder)
            where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
        {
            var fb = Builders<TTimeTicker>.Filter;
            return fb.And(fb.Eq(x => x.Status, TickerStatus.Queued), fb.Eq(x => x.LockHolder, lockHolder));
        }

        public static FilterDefinition<CronTickerOccurrenceEntity<TCronTicker>> QueuedByCronOccurrence<TCronTicker>(string lockHolder)
            where TCronTicker : CronTickerEntity, new()
        {
            var fb = Builders<CronTickerOccurrenceEntity<TCronTicker>>.Filter;
            return fb.And(fb.Eq(x => x.Status, TickerStatus.Queued), fb.Eq(x => x.LockHolder, lockHolder));
        }

        /// <summary>
        /// The edit of a running time ticker (a full-entity update of an InProgress row): a $set of every field of
        /// <paramref name="body"/> but the run's state, which the run keeps (see <see cref="RunStateOnEdit"/>). The run state is
        /// its status, lock, run marker (UpdatedAt) and the fields the run writes.
        /// </summary>
        public static UpdateDefinition<TTimeTicker> BuildEditOfRunningTimeTicker<TTimeTicker>(TTimeTicker body, IBsonSerializer<TTimeTicker> serializer)
            where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
        {
            var fields = Serialize(body, serializer);
            fields.Remove("_id");
            foreach (var element in RunStateElements<TTimeTicker>.Names)
                fields.Remove(element);
            return new BsonDocumentUpdateDefinition<TTimeTicker>(new BsonDocument("$set", fields));
        }

        /// <summary>A copy of a time ticker's stored fields (its navigation properties are not mapped, so not copied).</summary>
        public static TTimeTicker CopyOf<TTimeTicker>(TTimeTicker ticker, IBsonSerializer<TTimeTicker> serializer)
            where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
        {
            var document = Serialize(ticker, serializer);
            using var reader = new BsonDocumentReader(document);
            return serializer.Deserialize(BsonDeserializationContext.CreateRoot(reader));
        }

        private static BsonDocument Serialize<T>(T value, IBsonSerializer<T> serializer)
        {
            var document = new BsonDocument();
            using (var writer = new BsonDocumentWriter(document))
                serializer.Serialize(BsonSerializationContext.CreateRoot(writer), value);
            return document;
        }

        // The element names of the run state of a time ticker, from its class map.
        private static class RunStateElements<TTimeTicker>
            where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
        {
            private static readonly string[] Members =
            {
                nameof(TimeTickerEntity.Status), nameof(TimeTickerEntity.LockHolder), nameof(TimeTickerEntity.LockedAt),
                nameof(TimeTickerEntity.UpdatedAt), nameof(TimeTickerEntity.RetryCount), nameof(TimeTickerEntity.ExecutedAt),
                nameof(TimeTickerEntity.ElapsedTime), nameof(TimeTickerEntity.ExceptionMessage), nameof(TimeTickerEntity.SkippedReason)
            };

            // Built on first use: the class map is registered before the first write.
            private static readonly Lazy<string[]> Lazy = new(() =>
            {
                var classMap = BsonClassMap.LookupClassMap(typeof(TTimeTicker));
                return Members
                    .Select(member => classMap.AllMemberMaps.FirstOrDefault(m => m.MemberName == member)?.ElementName
                                      ?? throw new InvalidOperationException(
                                          $"The MongoDB class map of {typeof(TTimeTicker).FullName} does not map {member}, which SharpTicker needs to keep the run of an edited job."))
                    .ToArray();
            });

            public static string[] Names => Lazy.Value;
        }

        private static UpdateDefinition<T> Combine<T>(UpdateDefinition<T> a, UpdateDefinition<T> b)
            => a is null ? b : Builders<T>.Update.Combine(a, b);
    }
}
