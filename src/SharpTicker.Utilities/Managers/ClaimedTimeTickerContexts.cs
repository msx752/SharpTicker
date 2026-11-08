using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SharpTicker.Utilities.Entities;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Utilities.Managers
{
    /// <summary>
    /// Builds the contexts of time tickers a claim has taken to run them now (run-now, and a delayed child handed off when
    /// its parent ends), with the function references cached.
    /// </summary>
    internal static class ClaimedTimeTickerContexts
    {
        // Each acquired row carries the marker its claim wrote; only the row itself was claimed, not its children.
        internal static InternalFunctionContext[] Build(IEnumerable<TimeTickerEntity> tickers)
        {
            var contexts = tickers.Select(ticker =>
            {
                var context = Build(ticker, isChild: false);
                context.ClaimMarker = ticker.UpdatedAt;
                return context;
            }).ToArray();
            CacheFunctionReferences(contexts.AsSpan());
            return contexts;
        }

        private static InternalFunctionContext Build(TimeTickerEntity ticker, bool isChild)
        {
            return new InternalFunctionContext
            {
                FunctionName = ticker.Function,
                TickerId = ticker.Id,
                Type = TickerType.TimeTicker,
                Retries = ticker.Retries,
                RetryIntervals = ticker.RetryIntervals,
                ParentId = ticker.ParentId,
                ExecutionTime = ticker.ExecutionTime ?? DateTime.UtcNow,
                RunCondition = ticker.RunCondition ?? RunCondition.OnAnyCompletedStatus,
                IsDelayed = isChild && ticker.ExecutionTime.HasValue,
                TimeTickerChildren = ticker.Children.Select(child => Build(child, isChild: true)).ToList()
            };
        }

        internal static void CacheFunctionReferences(Span<InternalFunctionContext> functions)
        {
            for (var i = 0; i < functions.Length; i++)
            {
                ref var context = ref functions[i];
                if (TickerFunctionProvider.TickerFunctions.TryGetValue(context.FunctionName, out var tickerItem))
                {
                    context.CachedDelegate = tickerItem.Delegate;
                    context.CachedPriority = tickerItem.Priority;
                    context.CachedMaxConcurrency = tickerItem.MaxConcurrency;
                }

                if (context.TimeTickerChildren is { Count: > 0 })
                {
                    var childrenSpan = CollectionsMarshal.AsSpan(context.TimeTickerChildren);
                    CacheFunctionReferences(childrenSpan);
                }
            }
        }
    }
}
