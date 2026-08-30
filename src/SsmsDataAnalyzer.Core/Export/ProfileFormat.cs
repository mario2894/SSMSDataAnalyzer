using System;
using System.Globalization;
using SsmsDataAnalyzer.Core.Model;

namespace SsmsDataAnalyzer.Core.Export
{
    /// <summary>Shared value formatting so Markdown, CSV and the CLI grid agree.</summary>
    public static class ProfileFormat
    {
        public const int MaxValueChars = 60;

        public static string Number(long? value)
        {
            return value.HasValue ? value.Value.ToString("N0", CultureInfo.InvariantCulture) : string.Empty;
        }

        public static string Percent(double? ratio)
        {
            return ratio.HasValue ? (ratio.Value * 100.0).ToString("0.0", CultureInfo.InvariantCulture) + "%" : string.Empty;
        }

        public static string Bytes(double? value)
        {
            return value.HasValue ? value.Value.ToString("0.#", CultureInfo.InvariantCulture) : string.Empty;
        }

        public static string Date(DateTime? value)
        {
            return value.HasValue ? value.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : string.Empty;
        }

        public static string Value(object value)
        {
            if (value == null || value == DBNull.Value) return string.Empty;

            string text;
            if (value is byte[])
            {
                var bytes = (byte[])value;
                text = "0x" + BitConverter.ToString(bytes, 0, Math.Min(bytes.Length, 16)).Replace("-", string.Empty);
            }
            else if (value is DateTime)
            {
                text = ((DateTime)value).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            else if (value is IFormattable)
            {
                text = ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);
            }
            else
            {
                text = value.ToString();
            }

            text = text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            if (text.Length > MaxValueChars) text = text.Substring(0, MaxValueChars - 1) + "…";
            return text;
        }

        /// <summary>"varchar(50)", "nvarchar(max)", "int".</summary>
        public static string TypeDisplay(ColumnMeta meta)
        {
            switch ((meta.TypeName ?? string.Empty).ToLowerInvariant())
            {
                case "char":
                case "varchar":
                case "binary":
                case "varbinary":
                case "nchar":
                case "nvarchar":
                    return meta.MaxLength == -1
                        ? meta.TypeName + "(max)"
                        : meta.TypeName + "(" + meta.CharLength.ToString(CultureInfo.InvariantCulture) + ")";
                default:
                    return meta.TypeName;
            }
        }

        /// <summary>
        /// The FK target in bracket-doubled form — the same text a generated "go to source"
        /// query would use, so a referenced table containing periods stays unambiguous.
        /// Returns "(ambiguous)" when a relationship exists but no single target resolved,
        /// and empty when the column is not a foreign key.
        /// </summary>
        public static string ForeignKey(ColumnMeta meta)
        {
            if (meta == null || !meta.IsForeignKey) return string.Empty;

            // Several FKs on one column: no navigable table at all.
            if (meta.ReferencedTable == null)
                return string.Format(CultureInfo.InvariantCulture,
                    "(ambiguous: {0} FKs)", meta.ForeignKeyCount);

            // Composite FK: the table is navigable, the column deliberately is not.
            if (meta.ReferencedColumn == null)
                return meta.ReferencedQualifiedName + " (composite key)";

            return meta.ReferencedQualifiedName + "." + Sql.SqlIdentifier.Bracket(meta.ReferencedColumn);
        }

        public static string Flags(ColumnFlag flags)
        {
            if (flags == ColumnFlag.None) return string.Empty;
            var parts = new System.Collections.Generic.List<string>();
            if ((flags & ColumnFlag.Dead) != 0) parts.Add("DEAD");
            if ((flags & ColumnFlag.Constant) != 0) parts.Add("CONSTANT");
            if ((flags & ColumnFlag.Unique) != 0) parts.Add("UNIQUE");
            if ((flags & ColumnFlag.Sparse) != 0) parts.Add("SPARSE");
            return string.Join(" ", parts.ToArray());
        }

        public static string Attributes(ColumnMeta meta)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (meta.IsPrimaryKey) parts.Add("PK");
            if (meta.IsIdentity) parts.Add("IDENTITY");
            if (meta.IsComputed) parts.Add("COMPUTED");
            parts.Add(meta.IsNullable ? "NULL" : "NOT NULL");
            return string.Join(" ", parts.ToArray());
        }
    }
}
