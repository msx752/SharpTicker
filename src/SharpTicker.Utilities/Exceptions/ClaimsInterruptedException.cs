using System;
using SharpTicker.Utilities.Models;

namespace SharpTicker.Utilities.Exceptions;

/// <summary>
/// A call that claims rows one by one failed after it had claimed some. Those rows are InProgress for this node; they are
/// handed back with the failure so the caller runs them (or puts them back) instead of leaving them InProgress.
/// </summary>
internal sealed class ClaimsInterruptedException(InternalFunctionContext[] claimed, Exception failure)
    : Exception("The claim call failed after it had claimed some rows; they are handed back with the failure.", failure)
{
    /// <summary>The rows claimed before the failure, each with the marker its claim wrote.</summary>
    internal InternalFunctionContext[] Claimed { get; } = claimed;
}
