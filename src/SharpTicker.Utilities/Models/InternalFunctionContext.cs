using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Serialization;
using SharpTicker.Utilities.Enums;

namespace SharpTicker.Utilities.Models
{
    public class InternalFunctionContext
    {
        // Compiled setter cache to avoid reflection on every SetProperty call
        private static readonly ConcurrentDictionary<string, (Action<InternalFunctionContext, object> Setter, string Name)> SetterCache = new();

        public HashSet<string> ParametersToUpdate { get; set; } = [];
        // Cached function delegate and priority for performance optimization
        // Eliminates dictionary lookups during execution
        [JsonIgnore]
        public TickerFunctionDelegate CachedDelegate { get; set; }
        public TickerTaskPriority CachedPriority { get; set; }
        public int CachedMaxConcurrency { get; set; }
        public string FunctionName { get; set; }
        public Guid TickerId { get; set; }
        public Guid? ParentId { get; set; }
        public TickerType Type { get; set; }
        public int Retries { get; set; }
        public int RetryCount { get; set; }
        public TickerStatus Status { get; set; }
        public long ElapsedTime { get; set; }
        public string ExceptionDetails { get; set; }
        public DateTime ExecutedAt { get; set; }
        public int[] RetryIntervals { get; set; }
        public bool ReleaseLock { get; set; }
        public DateTime ExecutionTime { get; set; }
        public RunCondition RunCondition { get; set; }
        public List<InternalFunctionContext> TimeTickerChildren { get; set; } = [];

        /// <summary>
        /// The marker the claim that handed this job out wrote to its row (the fallback, run-now), or null when the job was
        /// not handed out by such a claim (a child, which runs inside its parent). A job that never starts is put back with it.
        /// </summary>
        internal DateTime? ClaimMarker { get; set; }

        /// <summary>
        /// The marker that fences the writes of this run (see <see cref="Interfaces.IFencedRunWriter"/>): the claim marker of a
        /// claimed job, or the start marker of an inline chain child. Null when the run is not fenced: the job was not
        /// claimed, its start could not be recorded, or the store does not fence.
        /// </summary>
        internal DateTime? RunMarker { get; set; }

        /// <summary>
        /// True for a chain child (or grandchild) that has an execution time of its own. Such a child is a row of its own: its
        /// parent's run never runs it, the store hands it out once the parent has ended with a status its
        /// <see cref="RunCondition"/> accepts and its time has come (see <see cref="ChainRunGate"/>).
        /// </summary>
        internal bool IsDelayed { get; set; }

        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(InternalFunctionContext))]
        public InternalFunctionContext SetProperty<T>(Expression<Func<InternalFunctionContext, T>> property, T value)
        {
            ParametersToUpdate ??= [];

            if (property.Body is not MemberExpression { Member: PropertyInfo prop })
                throw new ArgumentException("Expression must point to a property", nameof(property));

            var cached = SetterCache.GetOrAdd(prop.Name, _ =>
            {
                var instance = Expression.Parameter(typeof(InternalFunctionContext), "obj");
                var val = Expression.Parameter(typeof(object), "val");
                var assign = Expression.Assign(
                    Expression.Property(instance, prop),
                    Expression.Convert(val, prop.PropertyType));
                var lambda = Expression.Lambda<Action<InternalFunctionContext, object>>(assign, instance, val);
                return (lambda.Compile(), prop.Name);
            });

            cached.Setter(this, value);
            ParametersToUpdate.Add(cached.Name);

            return this;
        }

        public InternalFunctionContext ResetUpdateProps()
        {
             ParametersToUpdate.Clear();
             return this;
        }

        public HashSet<string> GetPropsToUpdate()
            => ParametersToUpdate;
    }
}
