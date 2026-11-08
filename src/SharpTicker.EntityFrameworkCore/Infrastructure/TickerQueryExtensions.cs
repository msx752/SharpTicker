using System;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;

namespace SharpTicker.EntityFrameworkCore.Infrastructure;


public static class TickerQueryExtensions
{
    public static IQueryable<TTimeTicker> WhereCanAcquire<TTimeTicker>(this IQueryable<TTimeTicker> q, string lockHolder) where TTimeTicker : TimeTickerEntity<TTimeTicker>
    {
        Expression<Func<TTimeTicker, bool>> pred = e =>
            ((e.Status == TickerStatus.Idle || e.Status == TickerStatus.Queued) && e.LockHolder == lockHolder) || 
            ((e.Status == TickerStatus.Idle || e.Status == TickerStatus.Queued) && e.LockedAt == null);
           
        return q.Where(pred);
    }
    
    public static IQueryable<CronTickerOccurrenceEntity<TCronTicker>> WhereCanAcquire<TCronTicker>(this IQueryable<CronTickerOccurrenceEntity<TCronTicker>> q, string lockHolder) where TCronTicker : CronTickerEntity
    {
        return q.Where(e =>
                ((e.Status == TickerStatus.Idle || e.Status == TickerStatus.Queued) && e.LockHolder == lockHolder) || 
                ((e.Status == TickerStatus.Idle || e.Status == TickerStatus.Queued) && e.LockedAt == null)
            );
    }

    // A row of a chain (a delayed child: a child with an execution time of its own) may be handed out only once its
    // parent's status lets it run. The same table as ChainRunGate, written out so EF Core can translate it (a join on the
    // parent's primary key); the store tests check it against ChainRunGate for every condition and status.
    internal static IQueryable<TTimeTicker> WhereChainGateOpen<TTimeTicker>(this IQueryable<TTimeTicker> q) where TTimeTicker : TimeTickerEntity<TTimeTicker>
    {
        var allowed = ParentStatusAllows<TTimeTicker>();
        var e = allowed.Parameters[0];
        var noParent = Expression.Equal(Expression.Property(e, nameof(TimeTickerEntity<TTimeTicker>.ParentId)), Expression.Constant(null, typeof(Guid?)));
        return q.Where(Expression.Lambda<Func<TTimeTicker, bool>>(Expression.OrElse(noParent, allowed.Body), e));
    }

    // A delayed child whose parent has ended with an outcome it does not run on: it never will, as a finished status does
    // not change.
    internal static IQueryable<TTimeTicker> WhereParentEndedWithoutMatch<TTimeTicker>(this IQueryable<TTimeTicker> q) where TTimeTicker : TimeTickerEntity<TTimeTicker>
    {
        var allowed = ParentStatusAllows<TTimeTicker>();
        return q
            .Where(e => e.ParentId != null
                        && (e.Parent.Status == TickerStatus.Done || e.Parent.Status == TickerStatus.DueDone || e.Parent.Status == TickerStatus.Failed
                            || e.Parent.Status == TickerStatus.Cancelled || e.Parent.Status == TickerStatus.Skipped))
            .Where(Expression.Lambda<Func<TTimeTicker, bool>>(Expression.Not(allowed.Body), allowed.Parameters[0]));
    }

    // Whether the parent's status lets the row run, by the row's RunCondition (null = OnAnyCompletedStatus).
    private static Expression<Func<TTimeTicker, bool>> ParentStatusAllows<TTimeTicker>() where TTimeTicker : TimeTickerEntity<TTimeTicker>
        => e => (
            ((e.RunCondition == null || e.RunCondition == RunCondition.OnAnyCompletedStatus)
                && (e.Parent.Status == TickerStatus.Done || e.Parent.Status == TickerStatus.DueDone || e.Parent.Status == TickerStatus.Failed || e.Parent.Status == TickerStatus.Cancelled))
            || (e.RunCondition == RunCondition.OnSuccess
                && (e.Parent.Status == TickerStatus.Done || e.Parent.Status == TickerStatus.DueDone))
            || (e.RunCondition == RunCondition.OnFailure
                && e.Parent.Status == TickerStatus.Failed)
            || (e.RunCondition == RunCondition.OnCancelled
                && e.Parent.Status == TickerStatus.Cancelled)
            || (e.RunCondition == RunCondition.OnFailureOrCancelled
                && (e.Parent.Status == TickerStatus.Failed || e.Parent.Status == TickerStatus.Cancelled))
            || (e.RunCondition == RunCondition.InProgress
                && (e.Parent.Status == TickerStatus.InProgress || e.Parent.Status == TickerStatus.Done || e.Parent.Status == TickerStatus.DueDone || e.Parent.Status == TickerStatus.Failed || e.Parent.Status == TickerStatus.Cancelled)));

    // The fenced claim: the row must still be Queued and held by this node. Internal on purpose, no public surface.
    internal static IQueryable<TTimeTicker> WhereQueuedBy<TTimeTicker>(this IQueryable<TTimeTicker> q, string lockHolder) where TTimeTicker : TimeTickerEntity<TTimeTicker>
    {
        return q.Where(e => e.Status == TickerStatus.Queued && e.LockHolder == lockHolder);
    }

    internal static IQueryable<CronTickerOccurrenceEntity<TCronTicker>> WhereQueuedBy<TCronTicker>(this IQueryable<CronTickerOccurrenceEntity<TCronTicker>> q, string lockHolder) where TCronTicker : CronTickerEntity
    {
        return q.Where(e => e.Status == TickerStatus.Queued && e.LockHolder == lockHolder);
    }
}
