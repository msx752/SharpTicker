using System;
using System.Collections.Generic;

namespace SharpTicker.SourceGenerator.Validation
{
    public static class CronValidator
    {
        // Format with seconds (6 parts only): seconds, minutes, hours, day, month, day-of-week
        private static readonly int[] MinValues = { 0, 0, 0, 1, 1, 0 };
        private static readonly int[] MaxValues = { 59, 59, 23, 31, 12, 6 };

        // Month (field 5) and day-of-week (field 6) accept names in any case. Like the scheduler, any start of the
        // full name counts (MON, Mo, MONDAY, SEP, SEPT, SEPTEMBER).
        private static readonly string[] MonthNames =
            { "JANUARY", "FEBRUARY", "MARCH", "APRIL", "MAY", "JUNE", "JULY", "AUGUST", "SEPTEMBER", "OCTOBER", "NOVEMBER", "DECEMBER" };
        private static readonly string[] DayNames =
            { "SUNDAY", "MONDAY", "TUESDAY", "WEDNESDAY", "THURSDAY", "FRIDAY", "SATURDAY" };
        
        // Performance constants
        private const int RequiredPartsCount = 6;

        /// <summary>
        /// If expression has 5 parts (standard cron), prepends "0 " to add seconds.
        /// Returns the expression as-is if already 6 parts.
        /// </summary>
        public static string NormalizeToSixPart(string expression)
        {
            if (string.IsNullOrEmpty(expression)) return expression;
            var trimmed = expression.Trim();
            // Count fields the way the scheduler does: runs of spaces separate fields, leading and trailing spaces do not.
            return SplitFields(trimmed).Length == 5 ? "0 " + trimmed : trimmed;
        }

        private static string[] SplitFields(string expression)
            => expression.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        public static bool IsValidCronExpression(string expression)
        {
            if (string.IsNullOrEmpty(expression)) return false;

            // Auto-normalize 5-part to 6-part
            var normalized = NormalizeToSixPart(expression);

            // Anything but exactly six fields is invalid, including extra fields after the sixth.
            var parts = SplitFields(normalized);
            if (parts.Length != RequiredPartsCount)
                return false;

            // Validate each part with corresponding min/max values
            for (int i = 0; i < RequiredPartsCount; i++)
            {
                if (!ValidatePart(parts[i], MinValues[i], MaxValues[i], NamesFor(i)))
                    return false;
            }
            
            return true;
        }

        private static string[] NamesFor(int fieldIndex)
            => fieldIndex == 4 ? MonthNames : fieldIndex == 5 ? DayNames : null;

        private static bool ValidatePart(string part, int min, int max, string[] names)
        {
            // Fast path for wildcard
            if (part.Length == 1 && part[0] == '*') 
                return true; // wildcard

            var span = part.AsSpan();
            int i = 0;

            // A list is one or more items separated by commas. An empty item, such as a trailing comma, is invalid.
            while (true)
            {
                int num1;
                int num2 = -1;
                int step = 1;
                var wildcard = false;

                // first value or '*'
                if (i < span.Length && span[i] == '*')
                {
                    wildcard = true;
                    num1 = min;
                    num2 = max;
                    i++;
                }
                else
                {
                    num1 = ReadValue(span, ref i, names);
                    if (num1 == -1) return false; // Neither a number nor a known name
                }

                // optional range "-" (a wildcard cannot start a range)
                if (i < span.Length && span[i] == '-')
                {
                    if (wildcard) return false;
                    i++;
                    num2 = ReadValue(span, ref i, names);
                    if (num2 == -1) return false; // Neither a number nor a known name
                }

                // optional step "/"
                if (i < span.Length && span[i] == '/')
                {
                    i++;
                    if (i < span.Length && span[i] >= '0' && span[i] <= '9')
                    {
                        step = ReadNumber(span, ref i);
                        if (step == -1) return false; // Invalid number
                    }
                    else
                        return false;
                }

                if (num2 == -1) 
                    num2 = num1;

                // validate bounds. A range has to run forward; a backwards range is not supported. A step larger
                // than the field is accepted like the scheduler does; a step of zero is not.
                if (num1 < min || num1 > max || num2 < min || num2 > max || num1 > num2 || step < 1)
                    return false;

                if (i >= span.Length)
                    return true;

                if (span[i] != ',')
                    return false;

                i++; // the comma; another item has to follow
            }
        }

        /// <summary>
        /// Reads a number, or a name when the field has names. Returns -1 when there is neither.
        /// </summary>
        private static int ReadValue(ReadOnlySpan<char> span, ref int index, string[] names)
        {
            if (index >= span.Length)
                return -1;

            if (span[index] >= '0' && span[index] <= '9')
                return ReadNumber(span, ref index);

            if (names == null)
                return -1;

            var start = index;
            while (index < span.Length && ((span[index] >= 'A' && span[index] <= 'Z') || (span[index] >= 'a' && span[index] <= 'z')))
                index++;

            var word = span.Slice(start, index - start).ToString();
            if (word.Length == 0)
                return -1;

            for (int n = 0; n < names.Length; n++)
            {
                if (names[n].StartsWith(word, StringComparison.OrdinalIgnoreCase))
                    return names == MonthNames ? n + 1 : n;
            }

            return -1;
        }

        /// <summary>
        /// Efficiently reads a number from a ReadOnlySpan with bounds checking.
        /// Optimized to avoid char.IsDigit() calls by using direct ASCII range checks.
        /// </summary>
        private static int ReadNumber(ReadOnlySpan<char> span, ref int index)
        {
            int num = 0;
            var startIndex = index;
            
            // Use direct ASCII range check instead of char.IsDigit() for better performance
            while (index < span.Length && span[index] >= '0' && span[index] <= '9')
            {
                var digit = span[index] - '0';
                
                // Overflow protection for cron values (max expected is 59)
                if (num > (int.MaxValue - digit) / 10)
                    break;
                    
                num = num * 10 + digit;
                index++;
            }
            
            // Return -1 if no digits were read (invalid number)
            return index > startIndex ? num : -1;
        }
    }
}