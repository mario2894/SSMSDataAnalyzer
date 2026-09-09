using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SsmsDataAnalyzer.Vsix.QueryEditor
{
    /// <summary>
    /// Turns clipboard text into a T-SQL <c>IN (...)</c> list. Pure text in, pure text out —
    /// no VS, no clipboard, no editor — so every rule below is testable headlessly, which is
    /// where this project has consistently caught its real bugs.
    ///
    /// Values are split, trimmed, de-duplicated and emitted in first-seen order.
    /// De-duplication is ORDINAL, not case-insensitive: 'ABC' and 'abc' are different literals,
    /// and under a case-sensitive collation they select different rows — collapsing them would
    /// silently change what the query returns. The caller is told how many duplicates were
    /// removed, so the change is never invisible.
    /// </summary>
    internal static class SqlInListFormatter
    {
        internal sealed class Result
        {
            public bool Success;
            /// <summary>The formatted list, when <see cref="Success"/>.</summary>
            public string Text;
            public int ValueCount;
            /// <summary>How many duplicate values were removed. Reported to the user rather
            /// than dropped quietly.</summary>
            public int DuplicatesRemoved;
            /// <summary>Why nothing was produced. Always set when <see cref="Success"/> is false.</summary>
            public string Error;
        }

        // Newlines and tabs only. Commas are NOT separators: a comma is perfectly ordinary
        // inside a real value ("Smith, John"), and splitting on it would quietly corrupt such
        // a paste. Tabs are included because a column copied out of Excel arrives tab- and
        // newline-delimited.
        private static readonly char[] Separators = { '\r', '\n', '\t' };

        public static Result Format(string clipboardText, bool numeric)
        {
            if (string.IsNullOrWhiteSpace(clipboardText))
            {
                return Fail("the clipboard is empty (or holds no text).");
            }

            List<string> cleaned = clipboardText
                .Split(Separators, StringSplitOptions.RemoveEmptyEntries)
                .Select(Clean)
                .Where(v => v.Length > 0)
                .ToList();

            if (cleaned.Count == 0)
            {
                return Fail("the clipboard held no non-empty values.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var values = new List<string>(cleaned.Count);
            foreach (string v in cleaned)
            {
                if (seen.Add(v)) values.Add(v);
            }
            int duplicatesRemoved = cleaned.Count - values.Count;

            Result result = numeric ? FormatNumeric(values) : FormatStrings(values);
            result.DuplicatesRemoved = duplicatesRemoved;
            return result;
        }

        /// <summary>
        /// Trims, then removes ONE matching pair of surrounding quotes. Someone pasting a
        /// column that is already quoted ('abc' or "abc") wants those values, not values whose
        /// text includes quote characters — and re-quoting would produce ''abc''.
        /// </summary>
        private static string Clean(string raw)
        {
            string v = raw.Trim();
            if (v.Length >= 2 &&
                ((v[0] == '\'' && v[v.Length - 1] == '\'') ||
                 (v[0] == '"' && v[v.Length - 1] == '"')))
            {
                v = v.Substring(1, v.Length - 2).Trim();
            }
            return v;
        }

        private static Result FormatNumeric(List<string> values)
        {
            // Validate every value before emitting anything. A non-numeric value in an
            // unquoted IN list produces SQL that either fails to parse or — worse, if it
            // happens to match a column name — silently means something else entirely.
            var bad = new List<string>();
            foreach (string v in values)
            {
                if (!IsNumericLiteral(v))
                {
                    if (bad.Count < 3) bad.Add(v);
                }
            }

            if (bad.Count > 0)
            {
                int badTotal = values.Count(v => !IsNumericLiteral(v));
                string examples = string.Join(", ", bad.Select(b => "'" + b + "'"));
                return Fail(badTotal == 1
                    ? $"{examples} isn't a number. Use \"Paste as SQL IN (...)\" for text values."
                    : $"{badTotal} of {values.Count} values aren't numbers (e.g. {examples}). Use \"Paste as SQL IN (...)\" for text values.");
            }

            return Build(values, quote: false, unicode: false);
        }

        /// <summary>
        /// Invariant-culture only, deliberately. Parsing under the current culture would accept
        /// "1.234" as 1234 in some locales — the exact class of bug that turned "1234.5600"
        /// into 12345600 in v0.8.0's display-text work. What the user sees pasted must be what
        /// SQL Server will read.
        /// </summary>
        private static bool IsNumericLiteral(string v)
        {
            const NumberStyles Styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
            return decimal.TryParse(v, Styles, CultureInfo.InvariantCulture, out _);
        }

        private static Result FormatStrings(List<string> values)
        {
            // N-prefix the whole list if ANY value is non-ASCII, so e.g. Croatian text
            // (č, ć, š, ž, đ) survives. Applied uniformly rather than per value: a list where
            // some literals are N'' and others aren't reads like a mistake.
            bool unicode = values.Any(v => v.Any(c => c > 127));
            return Build(values, quote: true, unicode: unicode);
        }

        private static Result Build(List<string> values, bool quote, bool unicode)
        {
            var sb = new StringBuilder();
            sb.AppendLine("(");
            for (int i = 0; i < values.Count; i++)
            {
                string v = values[i];
                if (quote)
                {
                    // Double any embedded single quote — O'Brien must become 'O''Brien'.
                    sb.Append(unicode ? "N'" : "'").Append(v.Replace("'", "''")).Append('\'');
                }
                else
                {
                    sb.Append(v);
                }

                if (i < values.Count - 1) sb.Append(',');
                sb.AppendLine();
            }
            sb.Append(')');

            return new Result { Success = true, Text = sb.ToString(), ValueCount = values.Count };
        }

        private static Result Fail(string reason) =>
            new Result { Success = false, Error = "Paste as SQL IN: " + reason };
    }
}
