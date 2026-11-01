using System;
using System.Linq.Expressions;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SharpTicker.Utilities.Entities;

namespace SharpTicker.Dashboard.Infrastructure;

/// <summary>
/// What the signed-in user of one request may see: the result of the registered <see cref="ITickerVisibilityRule"/>
/// for that user, in the form the queries use. Without a rule nothing is restricted and no predicate is added, so a
/// dashboard without a rule does what it always did.
/// </summary>
internal sealed class VisibilityScope<TTimeTicker, TCronTicker>
    where TTimeTicker : TimeTickerEntity<TTimeTicker>, new()
    where TCronTicker : CronTickerEntity, new()
{
    private readonly ITickerVisibilityRule? _rule;
    private readonly ClaimsPrincipal _user;

    /// <summary>The scope that restricts nothing.</summary>
    public static readonly VisibilityScope<TTimeTicker, TCronTicker> Unrestricted = new(null, new ClaimsPrincipal());

    private VisibilityScope(ITickerVisibilityRule? rule, ClaimsPrincipal user)
    {
        _rule = rule;
        _user = user;
        TimeTickers = rule == null ? null : PredicateRebinder.To<TimeTickerEntity, TTimeTicker>(rule.TimeTickers(user));
        CronTickers = rule == null ? null : PredicateRebinder.To<CronTickerEntity, TCronTicker>(rule.CronTickers(user));
    }

    /// <summary>The time tickers the user may see, or null for all of them.</summary>
    public Expression<Func<TTimeTicker, bool>>? TimeTickers { get; }

    /// <summary>The cron tickers the user may see, or null for all of them.</summary>
    public Expression<Func<TCronTicker, bool>>? CronTickers { get; }

    /// <summary>True when a rule is registered, even if it allows this user everything.</summary>
    public bool HasRule => _rule != null;

    public bool CanSeeFunction(string functionName) => _rule == null || _rule.CanSeeFunction(_user, functionName);

    /// <summary>Reads the rule registered for the dashboard and applies it to the user of this request.</summary>
    public static VisibilityScope<TTimeTicker, TCronTicker> For(HttpContext context)
    {
        var rule = context.RequestServices.GetService<ITickerVisibilityRule>();
        return rule == null ? Unrestricted : new VisibilityScope<TTimeTicker, TCronTicker>(rule, context.User);
    }
}

/// <summary>
/// Turns a predicate written for the plain entity types of <see cref="ITickerVisibilityRule"/> into one for the entity
/// type the store uses, which can be an application's own type. It stays an expression tree, so the store can still
/// translate it. Members are matched by name, so the predicate should use stored fields such as Function and Description.
/// </summary>
internal static class PredicateRebinder
{
    public static Expression<Func<TTo, bool>>? To<TFrom, TTo>(Expression<Func<TFrom, bool>>? predicate)
    {
        if (predicate == null)
            return null;

        var parameter = Expression.Parameter(typeof(TTo), predicate.Parameters[0].Name);
        var body = new MemberRebinder(predicate.Parameters[0], parameter).Visit(predicate.Body);
        return Expression.Lambda<Func<TTo, bool>>(body!, parameter);
    }

    private sealed class MemberRebinder : ExpressionVisitor
    {
        private readonly ParameterExpression _from;
        private readonly ParameterExpression _to;

        public MemberRebinder(ParameterExpression from, ParameterExpression to)
        {
            _from = from;
            _to = to;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression != _from)
                return base.VisitMember(node);

            var member = _to.Type.GetProperty(node.Member.Name)
                ?? throw new InvalidOperationException(
                    $"The visibility rule uses {node.Member.Name}, which the ticker type {_to.Type.Name} does not have. " +
                    "A rule can only use fields that every ticker type has, such as Function and Description.");
            return Expression.MakeMemberAccess(_to, member);
        }

        protected override Expression VisitParameter(ParameterExpression node)
            => node == _from
                ? throw new InvalidOperationException("The visibility rule uses the ticker itself, not one of its fields. A rule can only compare fields such as Function and Description.")
                : base.VisitParameter(node);
    }
}
