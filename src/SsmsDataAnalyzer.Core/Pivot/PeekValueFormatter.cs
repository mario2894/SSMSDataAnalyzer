using System;
using System.Globalization;
using System.Text;

namespace SsmsDataAnalyzer.Core.Pivot
{
    /// <summary>
    /// "Peek source for this value" — pure formatter that turns one raw ADO.NET column value
    /// (from a live SqlDataReader) into the same kind of display text PivotBuilder/PivotView
    /// already expect ("NULL" for DBNull/null, etc. — see PivotBuilder.NullDisplayText).
    /// References no VS/SSMS/ADO type beyond System, so it's exercised directly by a plain
    /// unit test with no database or UI involved.
    /// </summary>
    public static class PeekValueFormatter
    {
        /// <summary>byte[] values are truncated after this many bytes (hex-encoded) — a peek
        /// window is not the place to dump a multi-KB varbinary/image column in full.</summary>
        internal const int MaxBinaryBytes = 64;

        public static string ToDisplayText(object value)
        {
            if (value == null || value is DBNull) return "NULL";

            // Order matters: DateTime/DateTimeOffset/TimeSpan/bool/Guid are all IFormattable
            // too, but each needs its own format string (or, for bool, no numeric formatting
            // at all) rather than the generic ToString(null, InvariantCulture) fallback below.
            switch (value)
            {
                case DateTime dt:
                    return dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                case DateTimeOffset dto:
                    return dto.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture);
                case TimeSpan ts:
                    return ts.ToString("c", CultureInfo.InvariantCulture);
                case bool b:
                    return b ? "1" : "0";
                case byte[] bytes:
                    return FormatBytes(bytes);
                case Guid g:
                    return g.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant();
                case IFormattable formattable:
                    return formattable.ToString(null, CultureInfo.InvariantCulture);
                default:
                    return value.ToString();
            }
        }

        private static string FormatBytes(byte[] bytes)
        {
            if (bytes.Length == 0) return "0x";

            var count = Math.Min(bytes.Length, MaxBinaryBytes);
            var sb = new StringBuilder(2 + count * 2 + 1);
            sb.Append("0x");
            for (var i = 0; i < count; i++)
                sb.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            if (bytes.Length > MaxBinaryBytes) sb.Append('…');
            return sb.ToString();
        }
    }
}
