using System;
using System.Data.SqlTypes;
using System.Globalization;
using SsmsDataAnalyzer.Core.Model;

namespace SsmsDataAnalyzer.Vsix.GoToSource
{
    /// <summary>
    /// CONTRACT.md Amendment 14/15/16: builds the SQL literal for a "Go to source for this
    /// value" WHERE clause from a column's real value (tool window: MinValue/MaxValue; results
    /// grid: the clicked cell).
    ///
    /// Two rules, both binding, from Agent A's review:
    /// - <see cref="ColumnProfile.MinValue"/>/<see cref="ColumnProfile.MaxValue"/> are
    ///   <c>object</c> holding the RAW provider type (int, DateTime, Guid, byte[], ...), not
    ///   strings — this switches on the runtime type, it never parses text.
    /// - Core's <c>ProfileFormat.Value</c> is display-only and LOSSY (truncates strings at
    ///   60 chars, hex-clips byte[] at 16 bytes). It must never be used to build a SQL
    ///   literal — that would silently produce a wrong (truncated) filter. This class reads
    ///   only the raw value, never anything already formatted for display.
    ///
    /// If a type cannot be safely rendered as a literal, <see cref="TryFormat"/> returns
    /// false — CONTRACT.md is explicit that withholding the value jump beats guessing.
    ///
    /// v0.5.2 field report: the results-grid path (<c>IGridResultSet.GetCellData</c>, IL-
    /// verified against <c>QEStorageViewOnReader</c>) surfaces PROVIDER-SPECIFIC
    /// <see cref="System.Data.SqlTypes"/> structs for ordinary cells — <c>SqlInt32</c>, not
    /// <c>System.Int32</c> — which this switch had no case for, so it correctly (if
    /// unhelpfully) refused every one of them. Every <c>System.Data.SqlTypes</c> struct
    /// implements <see cref="INullable"/>; the fix lives here, once, rather than at every call
    /// site: unwrap to the underlying CLR value and re-dispatch through the same switch below.
    /// </summary>
    internal static class SqlLiteralFormatter
    {
        /// <summary>
        /// True if <paramref name="value"/> represents "no value" — plain null, DBNull, or a
        /// <see cref="System.Data.SqlTypes"/> struct whose <c>IsNull</c> is true (a null
        /// <c>SqlInt32</c> is a real, non-null .NET object — <c>value == null</c> and
        /// <c>value is DBNull</c> both miss it, which is exactly how "is NULL" got
        /// misreported as "unsupported type" before this fix). Callers use this for their own
        /// NULL-vs-unsupported-type message, so the null-detection logic lives in this one
        /// place rather than being duplicated (and re-drifted) at every call site.
        /// </summary>
        public static bool IsEffectivelyNull(object value)
        {
            if (value == null || value is DBNull) return true;
            if (value is INullable nullable) return nullable.IsNull;
            return false;
        }

        /// <summary>
        /// Formats <paramref name="value"/> (a column's real value) as a T-SQL literal
        /// suitable for a WHERE clause. <paramref name="sourceColumnType"/> is the PROFILED/
        /// SOURCE column's <see cref="ColumnMeta"/> (not the referenced column's) — the value
        /// was read from it, and per the FK constraint the referenced column must accept the
        /// same literal shape, so its type name (e.g. distinguishing nvarchar from varchar for
        /// the N-prefix) is what decides formatting.
        /// </summary>
        public static bool TryFormat(object value, ColumnMeta sourceColumnType, out string literal)
        {
            literal = null;

            if (IsEffectivelyNull(value)) return false;

            if (value is INullable)
            {
                switch (value)
                {
                    // Reference types backed by streams — decline explicitly rather than
                    // unwrap, consistent with how byte[]/xml are already treated below.
                    case SqlXml _:
                    case SqlBytes _:
                    case SqlChars _:
                        return false;

                    // SqlDecimal holds up to 38 digits; C# decimal holds 28–29 — .Value THROWS
                    // OverflowException outside decimal's range (verified empirically). Its
                    // ToString() is confirmed culture-invariant (period decimal separator
                    // regardless of thread culture, e.g. de-DE) and covers the full 38-digit
                    // range, so use it directly instead of unwrapping.
                    case SqlDecimal sqlDecimal:
                        literal = sqlDecimal.ToString();
                        return true;

                    // SqlMoney's range fits comfortably inside decimal (.Value never overflows
                    // here), but SqlMoney.ToString() is CULTURE-DEPENDENT — verified to render
                    // "1234,56" under de-DE, which is not valid T-SQL. Unwrap to decimal and
                    // format invariant ourselves; never call SqlMoney.ToString().
                    case SqlMoney sqlMoney:
                        literal = sqlMoney.Value.ToString(CultureInfo.InvariantCulture);
                        return true;

                    // SqlDateTime.ToString() is likewise culture-dependent (verified:
                    // "07.03.2026 13:45:09" under de-DE — not valid T-SQL). Unwrap to DateTime
                    // and let the existing DateTime case below apply OUR OWN ISO-8601
                    // formatting; SqlDateTime's narrower range/precision is not a formatting
                    // concern once unwrapped (DateTime covers it fully).
                    case SqlDateTime sqlDateTime:
                        return TryFormat(sqlDateTime.Value, sourceColumnType, out literal);

                    case SqlBoolean sqlBoolean: return TryFormat(sqlBoolean.Value, sourceColumnType, out literal);
                    case SqlByte sqlByte: return TryFormat(sqlByte.Value, sourceColumnType, out literal);
                    case SqlInt16 sqlInt16: return TryFormat(sqlInt16.Value, sourceColumnType, out literal);
                    case SqlInt32 sqlInt32: return TryFormat(sqlInt32.Value, sourceColumnType, out literal);
                    case SqlInt64 sqlInt64: return TryFormat(sqlInt64.Value, sourceColumnType, out literal);
                    case SqlSingle sqlSingle: return TryFormat(sqlSingle.Value, sourceColumnType, out literal);
                    case SqlDouble sqlDouble: return TryFormat(sqlDouble.Value, sourceColumnType, out literal);
                    case SqlString sqlString: return TryFormat(sqlString.Value, sourceColumnType, out literal);
                    case SqlGuid sqlGuid: return TryFormat(sqlGuid.Value, sourceColumnType, out literal);
                    case SqlBinary sqlBinary: return TryFormat(sqlBinary.Value, sourceColumnType, out literal);

                    default:
                        // Unrecognized INullable (a future SqlTypes addition, or a 3rd-party
                        // provider type) — withhold rather than guess at an unwrap shape.
                        return false;
                }
            }

            switch (value)
            {
                case string s:
                    literal = (IsUnicodeStringType(sourceColumnType) ? "N'" : "'") + s.Replace("'", "''") + "'";
                    return true;

                case bool b:
                    literal = b ? "1" : "0";
                    return true;

                case byte[] bytes:
                    // Full, lossless hex literal — never the display formatter's 16-byte-clipped preview.
                    literal = "0x" + BitConverter.ToString(bytes).Replace("-", "");
                    return true;

                case Guid g:
                    // uniqueidentifier: quoted string form.
                    literal = "'" + g.ToString() + "'";
                    return true;

                case DateTimeOffset dto:
                    // Unambiguous ISO-8601 with offset, regardless of session DATEFORMAT/LANGUAGE.
                    literal = "'" + dto.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture) + "'";
                    return true;

                case DateTime dt:
                    literal = "'" + dt.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) + "'";
                    return true;

                case TimeSpan ts:
                    // SQL Server 'time' maps to TimeSpan in ADO.NET.
                    literal = "'" + ts.ToString("hh\\:mm\\:ss\\.fffffff", CultureInfo.InvariantCulture) + "'";
                    return true;

                case byte n8: literal = n8.ToString(CultureInfo.InvariantCulture); return true;
                case sbyte n8s: literal = n8s.ToString(CultureInfo.InvariantCulture); return true;
                case short n16: literal = n16.ToString(CultureInfo.InvariantCulture); return true;
                case ushort n16u: literal = n16u.ToString(CultureInfo.InvariantCulture); return true;
                case int n32: literal = n32.ToString(CultureInfo.InvariantCulture); return true;
                case uint n32u: literal = n32u.ToString(CultureInfo.InvariantCulture); return true;
                case long n64: literal = n64.ToString(CultureInfo.InvariantCulture); return true;
                case ulong n64u: literal = n64u.ToString(CultureInfo.InvariantCulture); return true;
                case decimal dec: literal = dec.ToString(CultureInfo.InvariantCulture); return true;
                case float f: literal = f.ToString("R", CultureInfo.InvariantCulture); return true;
                case double d: literal = d.ToString("R", CultureInfo.InvariantCulture); return true;

                default:
                    // Unknown / exotic provider type (e.g. a CLR UDT, SqlGeography, ...):
                    // withhold rather than guess at a literal shape that might not round-trip.
                    return false;
            }
        }

        /// <summary>nvarchar/nchar/ntext/sysname are Unicode and need the N-prefix; char/varchar/text do not.</summary>
        private static bool IsUnicodeStringType(ColumnMeta meta)
        {
            var typeName = meta?.TypeName;
            if (string.IsNullOrEmpty(typeName)) return true; // unknown -> safer to over-prefix than mangle Unicode data

            return typeName.StartsWith("n", StringComparison.OrdinalIgnoreCase)
                || string.Equals(typeName, "sysname", StringComparison.OrdinalIgnoreCase);
        }
    }
}
