using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SharpTicker.Utilities.Entities;

namespace SharpTicker.Utilities;

/// <summary>
/// Which stored crons a code-defined cron (a [TickerFunction] with a cron expression) owns when the seed at startup brings
/// them to the expression in code. A function can have crons the seed made (marked by their InitIdentifier) and crons a
/// user added through the API or the dashboard (no InitIdentifier). Only the seed's own rows may be changed: a user's
/// cron of the same function keeps its schedule.
/// </summary>
internal static class SeededCronTickers
{
    internal const string InitIdentifierPrefix = "MemoryTicker_Seeded_";

    internal static string InitIdentifierFor(string function) => InitIdentifierPrefix + function;

    /// <summary>
    /// The id of the row the seed inserts for a function: always the same for one function name, on every node. Two nodes
    /// that start together and both find the cron missing then insert the same key, and the store keeps one row. The id is
    /// stored, so how it is made must never change: the first 16 bytes of the SHA-256 of the prefix and the name. Rows that
    /// exist keep the id they have; the seed finds them by function.
    /// </summary>
    internal static Guid IdFor(string function)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("SharpTicker.SeededCron:" + function));
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    /// <summary>The warning when <see cref="IsAmbiguous"/>: logged on every start until one of the crons is kept.</summary>
    internal const string AmbiguousWarning =
        "The code-defined cron of {Function} was not brought to {Expression}: the function has {Count} crons and none is marked as seeded, so none of them is changed. Keep one cron of this function (delete the others), or set its expression on the dashboard.";

    /// <summary>
    /// The rows of one function that take the code-defined expression, given all its stored rows:
    /// <list type="bullet">
    /// <item>none stored: none (the caller inserts the seeded row);</item>
    /// <item>one or more marked by the seed: all of them (two marked rows are copies of the same seeded cron, so none
    /// may keep the old schedule), and none of the unmarked ones;</item>
    /// <item>exactly one, unmarked: that one, as before. A dashboard edit clears the marker, so this is most likely the
    /// seeded cron edited there, and inserting another would run the job twice;</item>
    /// <item>several, none marked: none. They cannot be told apart, and any choice could
    /// overwrite a user's schedule; <see cref="IsAmbiguous"/> is true so the caller can say so.</item>
    /// </list>
    /// </summary>
    internal static IReadOnlyList<TCronTicker> Owned<TCronTicker>(IReadOnlyCollection<TCronTicker> rowsOfFunction)
        where TCronTicker : CronTickerEntity
    {
        var marked = rowsOfFunction.Where(c => !string.IsNullOrEmpty(c.InitIdentifier)).ToList();
        if (marked.Count > 0)
            return marked;

        return rowsOfFunction.Count == 1 ? rowsOfFunction.ToList() : [];
    }

    /// <summary>The warning when <see cref="Copies"/> were removed.</summary>
    internal const string CopiesRemovedWarning =
        "Removed {Count} copies of the code-defined cron of {Function}, with their occurrences. They were made when several nodes started at the same time; the oldest one is kept.";

    /// <summary>
    /// The rows of one function to remove because they are copies of its seeded cron: of the rows that carry the seed's
    /// marker for this function, all but the oldest. Every one of them runs the job on every tick. The order (creation
    /// time, then id) is the same on every node, so nodes that start together keep the same row. Rows without the marker
    /// (a user's cron, or a seeded cron edited on the dashboard) are never among them.
    /// </summary>
    internal static IReadOnlyList<TCronTicker> Copies<TCronTicker>(IReadOnlyCollection<TCronTicker> rowsOfFunction)
        where TCronTicker : CronTickerEntity
    {
        var seeded = rowsOfFunction
            .Where(c => string.Equals(c.InitIdentifier, InitIdentifierFor(c.Function), StringComparison.Ordinal))
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToList();

        return seeded.Count > 1 ? seeded.GetRange(1, seeded.Count - 1) : [];
    }

    /// <summary>True when the function has several stored crons and none of them is marked as seeded.</summary>
    internal static bool IsAmbiguous<TCronTicker>(IReadOnlyCollection<TCronTicker> rowsOfFunction)
        where TCronTicker : CronTickerEntity
        => rowsOfFunction.Count > 1 && rowsOfFunction.All(c => string.IsNullOrEmpty(c.InitIdentifier));
}
