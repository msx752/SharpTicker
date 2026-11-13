using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SharpTicker.Utilities.Enums;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Utilities
{
    public static class TickerCancellationTokenManager
    {
        private static readonly ConcurrentDictionary<Guid, TickerCancellationTokenDetails>  TickerCancellationTokens = new();
        private static readonly ConcurrentDictionary<Guid, ConcurrentHashSet<Guid>> ParentIdIndex = new();

        // Returns the entry of this run. When another run of the same id is still registered, that one stays the registered
        // entry and the returned one is not in the registry.
        internal static TickerCancellationTokenDetails AddTickerCancellationToken(CancellationTokenSource cancellationSource, InternalFunctionContext context, bool isDue, object owner = null,
            bool startsWithParent = false)
        {
            var details = new TickerCancellationTokenDetails
            {
                FunctionName = context.FunctionName,
                Type = context.Type,
                CancellationSource = cancellationSource,
                IsDue = isDue,
                ParentId = context.ParentId ?? Guid.Empty,
                Owner = owner,
                RunMarker = context.RunMarker,
                StartsWithParent = startsWithParent
            };
            
            TickerCancellationTokens.TryAdd(context.TickerId, details);
            
            // Add to parent index for fast lookup if parentId exists
            if (context.ParentId.HasValue && context.ParentId.Value != Guid.Empty)
            {
                ParentIdIndex.AddOrUpdate(context.ParentId.Value,
                    key => { var set = new ConcurrentHashSet<Guid>(); set.Add(context.TickerId); return set; },
                    (key, existing) => { existing.Add(context.TickerId); return existing; });
            }

            return details;
        }

        /// <summary>
        /// Asks the jobs <paramref name="owner"/> is running to stop because the node's fence was raised (see
        /// <see cref="NodeFence"/>), and marks each of those runs as fenced before its token is cancelled, so the run can
        /// tell this stop from a cancellation someone asked for. The entries stay registered until their runs end: the stop
        /// of the host still waits for them. Left alone: a run whose writes are not fenced by a marker (nothing could put
        /// its row back), and a chain child that started with its parent (put back, it would be skipped, not run again).
        /// A cancellation callback of a job that throws does not stop the others; <paramref name="onCancelFailure"/> gets
        /// the job's id and the exception. Returns how many runs were asked to stop.
        /// The cancel runs what each job hung on its token on the caller's thread, and that can be the rest of the job's run:
        /// the caller should not be a thread that must not be held up.
        /// </summary>
        internal static int CancelForFence(object owner, Action<Guid, Exception> onCancelFailure = null)
        {
            var asked = 0;
            foreach (var entry in TickerCancellationTokens)
            {
                var details = entry.Value;
                if (!ReferenceEquals(details.Owner, owner) || details.RunMarker is null || details.StartsWithParent)
                    continue;

                details.Fenced = true;
                try
                {
                    details.CancellationSource?.Cancel();
                    asked++;
                }
                catch (ObjectDisposedException)
                {
                    // The run ended meanwhile.
                }
                catch (Exception ex)
                {
                    // A callback the job registered on its token threw; the token is cancelled all the same.
                    asked++;
                    onCancelFailure?.Invoke(entry.Key, ex);
                }
            }

            return asked;
        }
        
        /// <summary>
        /// The jobs of one owner that are running now. This registry is shared by the whole process, so a host asks for the jobs it
        /// started and does not wait for those of another host that lives in the same process. A job is in the list from the moment
        /// it starts until its result has been written, so an empty list means every job has recorded how it ended.
        /// </summary>
        internal static RunningJob[] GetRunningJobs(object owner)
            => TickerCancellationTokens
                .Where(x => ReferenceEquals(x.Value.Owner, owner))
                .Select(x => new RunningJob(x.Key, x.Value.FunctionName, x.Value.Type, x.Value.ParentId, x.Value.RunMarker))
                .ToArray();

        internal static bool RemoveTickerCancellationToken(Guid tickerId)
        {
            var removed = TickerCancellationTokens.TryRemove(tickerId, out var details);
            
            if (removed && details != null)
            {
                // CRITICAL: Dispose CancellationTokenSource to prevent memory leak
                try
                {
                    details.CancellationSource?.Dispose();
                }
                catch
                {
                    // Ignore disposal errors
                }
                
                // Remove from parent index if it exists
                if (details.ParentId != Guid.Empty)
                {
                    RemoveFromParentIndex(details.ParentId, tickerId);
                }
            }

            return removed;
        }

        internal static void CleanUpTickerCancellationTokens()
        {
            // CRITICAL: Must dispose all CancellationTokenSources before clearing to prevent memory leaks
            foreach (var kvp in TickerCancellationTokens)
            {
                try
                {
                    kvp.Value.CancellationSource?.Dispose();
                }
                catch
                {
                    // Ignore disposal errors during cleanup
                }
            }
            
            TickerCancellationTokens.Clear();
            
            // Dispose all ConcurrentHashSet instances
            foreach (var kvp in ParentIdIndex)
            {
                try
                {
                    kvp.Value?.Dispose();
                }
                catch
                {
                    // Ignore disposal errors during cleanup
                }
            }
            
            ParentIdIndex.Clear();
        }

        public static bool RequestTickerCancellationById(Guid tickerId)
        {
            // Cancel while the entry is still tracked so IsParentRunning remains accurate
            if (!TickerCancellationTokens.TryGetValue(tickerId, out var details))
                return false;

            // Signal cancellation while the entry is still tracked
            try
            {
                details.CancellationSource?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed by another thread - safe to ignore
            }

            // Now remove and dispose
            if (TickerCancellationTokens.TryRemove(tickerId, out var removed))
            {
                try
                {
                    removed.CancellationSource?.Dispose();
                }
                catch
                {
                    // Ignore disposal errors
                }

                if (removed.ParentId != Guid.Empty)
                {
                    RemoveFromParentIndex(removed.ParentId, tickerId);
                }
            }

            return true;
        }
        
        /// <summary>
        /// Atomically removes a ticker from the parent index, cleaning up the set if empty.
        /// Uses TryRemove with value comparison to avoid TOCTOU races.
        /// </summary>
        private static void RemoveFromParentIndex(Guid parentId, Guid tickerId)
        {
            if (!ParentIdIndex.TryGetValue(parentId, out var set))
                return;

            set.Remove(tickerId);

            // Only remove the set from the dictionary if it's still empty.
            // Use the ICollection<KVP> remove overload for atomic check-and-remove.
            if (set.IsEmpty)
            {
                ((ICollection<KeyValuePair<Guid, ConcurrentHashSet<Guid>>>)ParentIdIndex)
                    .Remove(new KeyValuePair<Guid, ConcurrentHashSet<Guid>>(parentId, set));
            }
        }

        /// <summary>
        /// Fast O(1) lookup to check if any tickers are running for a given parent ID.
        /// This replaces the expensive LINQ Any() operation with a direct dictionary lookup.
        /// </summary>
        /// <param name="parentId">The parent ID to check</param>
        /// <returns>True if any tickers are running for this parent ID</returns>
        public static bool IsParentRunning(Guid parentId)
        {
            return ParentIdIndex.ContainsKey(parentId);
        }
        
        /// <summary>
        /// Checks if any OTHER tickers (excluding the current one) are running for a given parent ID.
        /// Used to prevent false positives when checking if a sibling occurrence is already running.
        /// </summary>
        /// <param name="parentId">The parent ID to check</param>
        /// <param name="excludeTickerId">The ticker ID to exclude from the check (usually the current ticker)</param>
        /// <returns>True if any other tickers are running for this parent ID</returns>
        public static bool IsParentRunningExcludingSelf(Guid parentId, Guid excludeTickerId)
        {
            if (!ParentIdIndex.TryGetValue(parentId, out var tickerSet))
                return false;
            
            return tickerSet.HasOtherItemsBesides(excludeTickerId);
        }
    }

    public class TickerCancellationTokenDetails 
    {
        public string FunctionName { get; set; }
        public TickerType Type { get; set; }
        public bool IsDue { get; set; }
        public CancellationTokenSource CancellationSource { get; set; }
        public Guid ParentId { get; set; }
        // Who started the job, a host; not part of the public surface.
        internal object Owner { get; set; }
        // The marker that fences the run's writes (InternalFunctionContext.RunMarker), for the stop's write of a job still running.
        internal DateTime? RunMarker { get; set; }
        // A chain child without a time of its own that started together with its parent (RunCondition.InProgress).
        internal bool StartsWithParent { get; set; }
        // Set by CancelForFence before the token is cancelled, and never cleared: this run was stopped by the node's fence.
        internal bool Fenced
        {
            get => Volatile.Read(ref _fenced);
            set => Volatile.Write(ref _fenced, value);
        }
        private bool _fenced;
    }
    
    /// <summary>
    /// Thread-safe HashSet implementation for concurrent operations
    /// </summary>
    public class ConcurrentHashSet<T> : IDisposable
    {
        private readonly HashSet<T> _set = new();
        private readonly ReaderWriterLockSlim _lock = new();
        
        public bool Add(T item)
        {
            _lock.EnterWriteLock();
            try { return _set.Add(item); }
            finally { _lock.ExitWriteLock(); }
        }
        
        public bool Remove(T item)
        {
            _lock.EnterWriteLock();
            try { return _set.Remove(item); }
            finally { _lock.ExitWriteLock(); }
        }
        
        public bool IsEmpty
        {
            get
            {
                _lock.EnterReadLock();
                try { return _set.Count == 0; }
                finally { _lock.ExitReadLock(); }
            }
        }
        
        /// <summary>
        /// Checks if there are any items in the set other than the specified excluded item.
        /// </summary>
        /// <param name="excludeItem">The item to exclude from the check</param>
        /// <returns>True if there are other items besides the excluded one</returns>
        public bool HasOtherItemsBesides(T excludeItem)
        {
            _lock.EnterReadLock();
            try
            {
                if (_set.Count == 0)
                    return false;
                    
                if (_set.Count == 1)
                    return !_set.Contains(excludeItem);
                    
                // Multiple items - at least one must be different from excludeItem
                return true;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
        
        public void Dispose()
        {
            _lock?.Dispose();
        }
    }

    /// <summary>A job that is running, as <see cref="TickerCancellationTokenManager.GetRunningJobs"/> reports it.</summary>
    internal readonly struct RunningJob
    {
        public RunningJob(Guid tickerId, string functionName, TickerType type, Guid parentId, DateTime? runMarker = null)
        {
            RunMarker = runMarker;
            TickerId = tickerId;
            FunctionName = functionName;
            Type = type;
            ParentId = parentId;
        }

        public Guid TickerId { get; }
        public string FunctionName { get; }
        public TickerType Type { get; }
        public Guid ParentId { get; }
        /// <summary>The marker that fences the run's writes, or null when the run is not fenced.</summary>
        public DateTime? RunMarker { get; }
    }
}