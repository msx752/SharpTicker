using System.Threading;
using System.Threading.Tasks;

namespace SharpTicker.Utilities.Interfaces;

/// <summary>
/// How many due rows the scheduler may claim at the same time on this store. Internal on purpose: only the stores shipped
/// with SharpTicker know what their database takes (SQLite, for one, allows a single writer, and a single-connection
/// setup cannot run two commands at once). A provider that does not implement it is claimed one row at a time.
/// </summary>
internal interface IClaimFanOut
{
    ValueTask<int> GetMaxConcurrentClaimsAsync(CancellationToken cancellationToken = default);
}

internal static class ClaimFanOut
{
    /// <summary>For stores that take concurrent single-row writes well.</summary>
    internal const int Default = 16;

    internal const int Sequential = 1;
}
