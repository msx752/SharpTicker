using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>True when the function has several stored crons and none of them is marked as seeded.</summary>
    internal static bool IsAmbiguous<TCronTicker>(IReadOnlyCollection<TCronTicker> rowsOfFunction)
        where TCronTicker : CronTickerEntity
        => rowsOfFunction.Count > 1 && rowsOfFunction.All(c => string.IsNullOrEmpty(c.InitIdentifier));
}
