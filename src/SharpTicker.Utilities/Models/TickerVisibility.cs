using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using SharpTicker.Utilities.Entities;

namespace SharpTicker.Utilities.Models
{
    /// <summary>
    /// What one user may see, as the predicates the queries add. A null predicate allows everything.
    /// </summary>
    internal sealed class TickerVisibility<TTimeTicker, TCronTicker>
        where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
        where TCronTicker : CronTickerEntity, new()
    {
        public Expression<Func<TTimeTicker, bool>> TimeTickers { get; }
        public Expression<Func<TCronTicker, bool>> CronTickers { get; }

        public TickerVisibility(Expression<Func<TTimeTicker, bool>> timeTickers, Expression<Func<TCronTicker, bool>> cronTickers)
        {
            TimeTickers = timeTickers;
            CronTickers = cronTickers;
        }
    }

    internal static class PredicateExtensions
    {
        /// <summary>Both predicates must hold. Either can be null, which means no condition.</summary>
        public static Expression<Func<T, bool>> And<T>(this Expression<Func<T, bool>> first, Expression<Func<T, bool>> second)
        {
            if (first == null) return second;
            if (second == null) return first;

            var parameter = Expression.Parameter(typeof(T), first.Parameters[0].Name);
            var left = new ParameterSwap(first.Parameters[0], parameter).Visit(first.Body);
            var right = new ParameterSwap(second.Parameters[0], parameter).Visit(second.Body);
            return Expression.Lambda<Func<T, bool>>(Expression.AndAlso(left, right), parameter);
        }

        private sealed class ParameterSwap : ExpressionVisitor
        {
            private readonly ParameterExpression _from;
            private readonly ParameterExpression _to;

            public ParameterSwap(ParameterExpression from, ParameterExpression to)
            {
                _from = from;
                _to = to;
            }

            protected override Expression VisitParameter(ParameterExpression node) => node == _from ? _to : base.VisitParameter(node);
        }
    }
}
