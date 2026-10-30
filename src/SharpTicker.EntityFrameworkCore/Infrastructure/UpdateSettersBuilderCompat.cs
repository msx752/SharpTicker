#if !NET10_0_OR_GREATER
using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace SharpTicker.EntityFrameworkCore.Infrastructure
{
    /// <summary>
    /// Stand-in for EF Core 10's <c>UpdateSettersBuilder&lt;TSource&gt;</c> when building against EF Core 8/9.
    /// Collects <c>SetProperty</c> calls and turns them into the <see cref="SetPropertyCalls{TSource}"/>
    /// expression tree that the older <c>ExecuteUpdateAsync</c> overload expects, so the persistence
    /// code can be written once against the EF Core 10 shape.
    /// </summary>
    internal sealed class UpdateSettersBuilder<TSource>
    {
        private static readonly MethodInfo SetPropertyWithValue = FindSetProperty(valueIsExpression: false);
        private static readonly MethodInfo SetPropertyWithExpression = FindSetProperty(valueIsExpression: true);

        private readonly ParameterExpression _calls = Expression.Parameter(typeof(SetPropertyCalls<TSource>), "calls");
        private Expression _body;

        internal UpdateSettersBuilder() => _body = _calls;

        public UpdateSettersBuilder<TSource> SetProperty<TProperty>(
            Expression<Func<TSource, TProperty>> propertyExpression,
            TProperty valueExpression)
        {
            // Read the value through a member access on a constant (like a compiler closure)
            // so EF Core sends it as a query parameter instead of inlining a literal
            var value = Expression.Property(
                Expression.Constant(new ValueHolder<TProperty>(valueExpression)),
                nameof(ValueHolder<TProperty>.Value));

            _body = Expression.Call(_body, SetPropertyWithValue.MakeGenericMethod(typeof(TProperty)), propertyExpression, value);
            return this;
        }

        public UpdateSettersBuilder<TSource> SetProperty<TProperty>(
            Expression<Func<TSource, TProperty>> propertyExpression,
            Expression<Func<TSource, TProperty>> valueExpression)
        {
            _body = Expression.Call(_body, SetPropertyWithExpression.MakeGenericMethod(typeof(TProperty)), propertyExpression, valueExpression);
            return this;
        }

        internal Expression<Func<SetPropertyCalls<TSource>, SetPropertyCalls<TSource>>> Build()
            => Expression.Lambda<Func<SetPropertyCalls<TSource>, SetPropertyCalls<TSource>>>(_body, _calls);

        // SetPropertyCalls<TSource> has exactly two SetProperty overloads: (Func<TSource, TProperty>, TProperty)
        // and (Func<TSource, TProperty>, Func<TSource, TProperty>); the second parameter tells them apart
        private static MethodInfo FindSetProperty(bool valueIsExpression)
            => typeof(SetPropertyCalls<TSource>).GetMethods()
                .Single(m => m.Name == nameof(SetPropertyCalls<TSource>.SetProperty)
                             && m.GetParameters()[1].ParameterType.IsGenericType == valueIsExpression);

        private sealed class ValueHolder<TValue>
        {
            public ValueHolder(TValue value) => Value = value;

            public TValue Value { get; }
        }
    }

    internal static class UpdateSettersBuilderExtensions
    {
        /// <summary>
        /// EF Core 10-style <c>ExecuteUpdateAsync(Action&lt;UpdateSettersBuilder&lt;TSource&gt;&gt;)</c> for EF Core 8/9.
        /// Declared in this namespace so it is picked over EF Core's own overload by the persistence code.
        /// </summary>
        public static Task<int> ExecuteUpdateAsync<TSource>(
            this IQueryable<TSource> source,
            Action<UpdateSettersBuilder<TSource>> setPropertyCalls,
            CancellationToken cancellationToken = default)
        {
            var builder = new UpdateSettersBuilder<TSource>();
            setPropertyCalls(builder);
#if NET9_0_OR_GREATER
            return EntityFrameworkQueryableExtensions.ExecuteUpdateAsync(source, builder.Build(), cancellationToken);
#else
            // EF Core 8 ships ExecuteUpdate in the relational package; EF Core 9 moved it to core
            return RelationalQueryableExtensions.ExecuteUpdateAsync(source, builder.Build(), cancellationToken);
#endif
        }
    }
}
#endif
