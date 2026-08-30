using System;
using System.Globalization;
using System.Text;
using SsmsDataAnalyzer.Core.Model;

namespace SsmsDataAnalyzer.Core.Export
{
    /// <summary>Renders a TableProfile as RFC 4180 CSV.</summary>
    public static class CsvExporter
    {
        private static readonly string[] Header =
        {
            "Column","Type","Collation","MaxLength","Nullable","Identity","PrimaryKey","Computed","LeadingIndex",
            "TotalRows","Filled","FillPercent","Blank","Distinct","DistinctPercent",
            "LastFill","Min","Max","AvgByteLength","Flags","SkipReason",
            "IsForeignKey","ForeignKeyCount","ReferencedSchema","ReferencedTable","ReferencedColumn","ForeignKeyName"
        };

        public static string Export(TableProfile profile)
        {
            if (profile == null) throw new ArgumentNullException("profile");
            var sb = new StringBuilder();

            sb.AppendLine(string.Join(",", Header));

            foreach (var c in profile.Columns)
            {
                var fields = new[]
                {
                    c.Meta.Name,
                    ProfileFormat.TypeDisplay(c.Meta),
                    c.Meta.Collation,
                    c.Meta.MaxLength.ToString(CultureInfo.InvariantCulture),
                    Bool(c.Meta.IsNullable),
                    Bool(c.Meta.IsIdentity),
                    Bool(c.Meta.IsPrimaryKey),
                    Bool(c.Meta.IsComputed),
                    c.Meta.LeadingIndexName,
                    profile.TotalRows.ToString(CultureInfo.InvariantCulture),
                    Raw(c.FilledCount),
                    RawRatio(c.FillRatio),
                    Raw(c.BlankCount),
                    Raw(c.DistinctCount),
                    RawRatio(c.DistinctRatio),
                    ProfileFormat.Date(c.LastFillDate),
                    ProfileFormat.Value(c.MinValue),
                    ProfileFormat.Value(c.MaxValue),
                    c.AvgByteLength.HasValue ? c.AvgByteLength.Value.ToString("0.####", CultureInfo.InvariantCulture) : string.Empty,
                    ProfileFormat.Flags(c.Flags),
                    c.SkipReason,
                    Bool(c.Meta.IsForeignKey),
                    c.Meta.ForeignKeyCount.ToString(CultureInfo.InvariantCulture),
                    c.Meta.ReferencedSchema,
                    c.Meta.ReferencedTable,
                    c.Meta.ReferencedColumn,
                    c.Meta.ForeignKeyName
                };

                for (int i = 0; i < fields.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Quote(fields[i]));
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static string Bool(bool value) { return value ? "1" : "0"; }

        private static string Raw(long? value)
        {
            return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
        }

        private static string RawRatio(double? value)
        {
            return value.HasValue ? (value.Value * 100.0).ToString("0.####", CultureInfo.InvariantCulture) : string.Empty;
        }

        private static string Quote(string s)
        {
            if (s == null) return string.Empty;
            bool needsQuotes = s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
            if (!needsQuotes) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
