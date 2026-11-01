using System;
using System.Linq.Expressions;
using System.Security.Claims;
using SharpTicker.Utilities.Entities;

namespace SharpTicker.Dashboard;

/// <summary>
/// Decides which tickers a signed-in dashboard user may see and act on. It is evaluated for every request, and the
/// predicates it returns are applied inside the queries, so paging, totals, charts and counts only contain what the
/// user may see. A ticker the user may not see answers as if it did not exist.
/// </summary>
/// <remarks>
/// Register it with <see cref="DashboardOptionsBuilder.WithVisibilityRule{TRule}"/>. It needs
/// <see cref="DashboardOptionsBuilder.WithHostAuthentication"/>, because it needs the user's <see cref="ClaimsPrincipal"/>.
/// The rule is built from the stored fields of a ticker (<c>Function</c>, <c>Description</c> and so on), so it needs no
/// schema change. Predicates must be expressions the store can translate: on EF Core and MongoDB they run in the database,
/// on Redis and in memory they run over the loaded tickers. The live notifications (a cron added or updated, a time
/// ticker updated) are also sent only to the users who may see the ticker; there the predicate is run in memory on the
/// ticker, so it should give the same answer in memory as in the store (for example, compare text with an explicit
/// ordinal comparison). The scheduler controls (stop, start, restart) are not tied to a ticker and are not limited by the rule.
/// </remarks>
public interface ITickerVisibilityRule
{
    /// <summary>The time tickers the user may see, or <c>null</c> to allow all of them.</summary>
    Expression<Func<TimeTickerEntity, bool>>? TimeTickers(ClaimsPrincipal user);

    /// <summary>The cron tickers the user may see, or <c>null</c> to allow all of them.</summary>
    Expression<Func<CronTickerEntity, bool>>? CronTickers(ClaimsPrincipal user);

    /// <summary>Whether the user may see, add and update tickers of this function.</summary>
    bool CanSeeFunction(ClaimsPrincipal user, string functionName);
}
