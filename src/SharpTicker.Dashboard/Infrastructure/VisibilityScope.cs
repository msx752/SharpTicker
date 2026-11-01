using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Interfaces;
using SharpTicker.Utilities.Models;

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

    /// <summary>The predicates in the form the repository takes, or null when nothing is restricted.</summary>
    public TickerVisibility<TTimeTicker, TCronTicker>? Filter => TimeTickers == null && CronTickers == null
        ? null
        : new TickerVisibility<TTimeTicker, TCronTicker>(TimeTickers, CronTickers);

    /// <summary>True when a rule is registered, even if it allows this user everything.</summary>
    public bool HasRule => _rule != null;

    public bool CanSeeFunction(string functionName) => _rule == null || _rule.CanSeeFunction(_user, functionName);

    /// <summary>
    /// An id that no ticker has. A request about a ticker the user may not see runs with this id instead, so it gets
    /// exactly the answer for a ticker that does not exist.
    /// </summary>
    public static readonly Guid NoTicker = Guid.Empty;

    /// <summary>The id itself when the user may see that time ticker, otherwise <see cref="NoTicker"/>.</summary>
    public async Task<Guid> VisibleTimeTickerIdAsync(ITickerPersistenceProvider<TTimeTicker, TCronTicker> provider, Guid id, CancellationToken cancellationToken)
    {
        if (TimeTickers == null)
            return id;

        var found = await provider.GetTimeTickers(TimeTickers.And(t => t.Id == id), cancellationToken);
        return found.Length > 0 ? id : NoTicker;
    }

    /// <summary>The id itself when the user may see that cron ticker, otherwise <see cref="NoTicker"/>.</summary>
    public async Task<Guid> VisibleCronTickerIdAsync(ITickerPersistenceProvider<TTimeTicker, TCronTicker> provider, Guid id, CancellationToken cancellationToken)
    {
        if (CronTickers == null)
            return id;

        var found = await provider.GetCronTickers(CronTickers.And(c => c.Id == id), cancellationToken);
        return found.Length > 0 ? id : NoTicker;
    }

    /// <summary>The occurrence id itself when the user may see the cron it belongs to, otherwise <see cref="NoTicker"/>.</summary>
    public async Task<Guid> VisibleOccurrenceIdAsync(ITickerPersistenceProvider<TTimeTicker, TCronTicker> provider, Guid id, CancellationToken cancellationToken)
    {
        if (CronTickers == null)
            return id;

        var occurrence = (await provider.GetAllCronTickerOccurrences(o => o.Id == id, cancellationToken)).FirstOrDefault();
        if (occurrence == null)
            return id;

        return await VisibleCronTickerIdAsync(provider, occurrence.CronTickerId, cancellationToken) == NoTicker ? NoTicker : id;
    }

    /// <summary>True only when a time ticker with this id exists and the user may see it.</summary>
    public async Task<bool> CanSeeTimeTickerAsync(ITickerPersistenceProvider<TTimeTicker, TCronTicker> provider, Guid id, CancellationToken cancellationToken)
        => (await provider.GetTimeTickers(TimeTickers.And(t => t.Id == id), cancellationToken)).Length > 0;

    /// <summary>
    /// True when the ticker, or a ticker below it, names a parent that the user cannot see and that the ticker does not
    /// already have. <paramref name="storedParentId"/> is the parent the root has in the store, which stays allowed.
    /// </summary>
    public async Task<bool> NamesAHiddenParentAsync(ITickerPersistenceProvider<TTimeTicker, TCronTicker> provider, TTimeTicker ticker, Guid? storedParentId, CancellationToken cancellationToken)
    {
        if (TimeTickers == null)
            return false;

        return await NamesAHiddenParentAsync(provider, ticker, storedParentId, cancellationToken, 0);
    }

    private async Task<bool> NamesAHiddenParentAsync(ITickerPersistenceProvider<TTimeTicker, TCronTicker> provider, TTimeTicker ticker, Guid? allowedParentId, CancellationToken cancellationToken, int depth)
    {
        if (depth > 64)
            return true; // a chain this deep is not a real one

        if (ticker.ParentId is { } parentId && parentId != allowedParentId
            && await VisibleTimeTickerIdAsync(provider, parentId, cancellationToken) == NoTicker)
            return true;

        foreach (var child in ticker.Children ?? Array.Empty<TTimeTicker>())
        {
            if (await NamesAHiddenParentAsync(provider, child, ticker.Id, cancellationToken, depth + 1))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True only when an occurrence with this id exists and the user may see the cron it belongs to. Unlike
    /// <see cref="VisibleOccurrenceIdAsync"/> an id that is not an occurrence is not visible, so it can be used to
    /// decide whether an action may go ahead.
    /// </summary>
    public async Task<bool> CanSeeOccurrenceAsync(ITickerPersistenceProvider<TTimeTicker, TCronTicker> provider, Guid id, CancellationToken cancellationToken)
    {
        var occurrence = (await provider.GetAllCronTickerOccurrences(o => o.Id == id, cancellationToken)).FirstOrDefault();
        if (occurrence == null)
            return false;

        return CronTickers == null
            || await VisibleCronTickerIdAsync(provider, occurrence.CronTickerId, cancellationToken) != NoTicker;
    }

    /// <summary>Only the ids of the time tickers the user may see.</summary>
    public async Task<List<Guid>> VisibleTimeTickerIdsAsync(ITickerPersistenceProvider<TTimeTicker, TCronTicker> provider, List<Guid> ids, CancellationToken cancellationToken)
    {
        if (TimeTickers == null || ids.Count == 0)
            return ids;

        var found = await provider.GetTimeTickers(TimeTickers.And(t => ids.Contains(t.Id)), cancellationToken);
        return found.Select(t => t.Id).ToList();
    }

    /// <summary>Reads the rule registered for the dashboard and applies it to the user of this request.</summary>
    public static VisibilityScope<TTimeTicker, TCronTicker> For(HttpContext context) => For(context.RequestServices, context.User);

    /// <summary>Reads the rule registered in the given services and applies it to the given user.</summary>
    public static VisibilityScope<TTimeTicker, TCronTicker> For(IServiceProvider services, ClaimsPrincipal user)
    {
        var rule = services.GetService<ITickerVisibilityRule>();
        return rule == null ? Unrestricted : new VisibilityScope<TTimeTicker, TCronTicker>(rule, user);
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
