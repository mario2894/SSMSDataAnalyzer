using System;
using System.Globalization;

namespace SsmsDataAnalyzer.Core.Sql
{
    /// <summary>
    /// The single place identifiers become SQL text. Bracket-doubling only; no user value
    /// is ever concatenated into SQL as a literal by any other code path.
    /// </summary>
    public static class SqlIdentifier
    {
        public static string Bracket(string identifier)
        {
            if (identifier == null) throw new ArgumentNullException("identifier");
            return "[" + identifier.Replace("]", "]]") + "]";
        }

        /// <summary>Formats an integer for a query hint (hints cannot take parameters).</summary>
        public static string Int(int value, int min, int max, string what)
        {
            if (value < min || value > max)
                throw new ArgumentOutOfRangeException(what,
                    string.Format(CultureInfo.InvariantCulture,
                        "{0} must be between {1} and {2} (was {3}).", what, min, max, value));
            return value.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Formats a percentage for TABLESAMPLE, which — like the query hints — is one of the
        /// places T-SQL syntax forbids a parameter. Range-checked and re-formatted through an
        /// invariant numeric format, so the emitted text can only ever be digits and one dot.
        /// </summary>
        public static string Percent(double value, string what)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0 || value > 100)
                throw new ArgumentOutOfRangeException(what,
                    string.Format(CultureInfo.InvariantCulture,
                        "{0} must be greater than 0 and at most 100 (was {1}).", what, value));

            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }
    }
}
