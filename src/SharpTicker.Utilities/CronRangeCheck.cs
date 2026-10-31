using System;

namespace SharpTicker.Utilities
{
    /// <summary>
    /// Finds a range that runs backwards in a cron expression, such as 22-2 or FRI-MON. The scheduler library does not
    /// wrap such a range around, it reads it as the forward range between the same values, so a job would run at
    /// times nobody asked for. New crons with such a range are refused. Crons that are already stored are still read
    /// the way they always were.
    /// </summary>
    internal static class CronRangeCheck
    {
        private static readonly string[] MonthNames =
            { "JANUARY", "FEBRUARY", "MARCH", "APRIL", "MAY", "JUNE", "JULY", "AUGUST", "SEPTEMBER", "OCTOBER", "NOVEMBER", "DECEMBER" };

        private static readonly string[] DayNames =
            { "SUNDAY", "MONDAY", "TUESDAY", "WEDNESDAY", "THURSDAY", "FRIDAY", "SATURDAY" };

        private const int MonthField = 4;
        private const int DayOfWeekField = 5;

        public static bool HasBackwardsRange(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
                return false;

            var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // A five-field expression gets its seconds field in front, so month and day of week are one place later.
            var offset = fields.Length == 5 ? 1 : 0;

            for (var i = 0; i < fields.Length; i++)
            {
                var names = i + offset == MonthField ? MonthNames : i + offset == DayOfWeekField ? DayNames : null;

                foreach (var item in fields[i].Split(','))
                {
                    var range = item;
                    var step = range.IndexOf('/');
                    if (step >= 0)
                        range = range.Substring(0, step);

                    var dash = range.IndexOf('-');
                    if (dash <= 0)
                        continue;

                    var from = ValueOf(range.Substring(0, dash), names);
                    var to = ValueOf(range.Substring(dash + 1), names);
                    if (from >= 0 && to >= 0 && from > to)
                        return true;
                }
            }

            return false;
        }

        private static int ValueOf(string text, string[] names)
        {
            if (text.Length == 0)
                return -1;

            if (char.IsDigit(text[0]))
                return int.TryParse(text, out var number) ? number : -1;

            if (names == null)
                return -1;

            for (var n = 0; n < names.Length; n++)
            {
                if (names[n].StartsWith(text, StringComparison.OrdinalIgnoreCase))
                    return names == MonthNames ? n + 1 : n;
            }

            return -1;
        }
    }
}
