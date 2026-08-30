using System;
using System.Globalization;
using System.Text;
using SsmsDataAnalyzer.Core.Model;

namespace SsmsDataAnalyzer.Core.Export
{
    /// <summary>Renders a TableProfile as a GitHub-flavoured Markdown report.</summary>
    public static class MarkdownExporter
    {
        public static string Export(TableProfile profile)
        {
            if (profile == null) throw new ArgumentNullException("profile");
            var sb = new StringBuilder();

            sb.Append("# Data profile — ").AppendLine(profile.Table == null ? "(unknown)" : profile.Table.QualifiedName);
            sb.AppendLine();

            if (profile.Table != null)
            {
                sb.Append("- **Server / database:** `").Append(profile.Table.Server).Append("` / `")
                  .Append(profile.Table.Database).AppendLine("`");
            }
            sb.Append("- **Rows:** ").Append(ProfileFormat.Number(profile.TotalRows))
              .Append(" (estimate ").Append(ProfileFormat.Number(profile.EstimatedRows)).AppendLine(")");
            sb.Append("- **DateCreated column:** ")
              .AppendLine(profile.DateCreatedColumn == null ? "_n/a_" : "`" + profile.DateCreatedColumn + "`");
            sb.Append("- **Sampled:** ").AppendLine(profile.WasSampled ? "yes" : "no");
            sb.Append("- **Elapsed:** ")
              .AppendLine(profile.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s");
            sb.AppendLine();

            sb.AppendLine("| Column | Type | Collation | References | Attributes | Filled | Fill % | Blank | Distinct | Distinct % | Last fill | Min | Max | Avg bytes | Flags | Note |");
            sb.AppendLine("|---|---|---|---|---|---:|---:|---:|---:|---:|---|---|---|---:|---|---|");

            foreach (var c in profile.Columns)
            {
                sb.Append("| ").Append(Escape(c.Meta.Name))
                  .Append(" | ").Append(Escape(ProfileFormat.TypeDisplay(c.Meta)))
                  .Append(" | ").Append(Escape(c.Meta.Collation))
                  .Append(" | ").Append(Escape(ProfileFormat.ForeignKey(c.Meta)))
                  .Append(" | ").Append(Escape(ProfileFormat.Attributes(c.Meta)))
                  .Append(" | ").Append(ProfileFormat.Number(c.FilledCount))
                  .Append(" | ").Append(ProfileFormat.Percent(c.FillRatio))
                  .Append(" | ").Append(ProfileFormat.Number(c.BlankCount))
                  .Append(" | ").Append(ProfileFormat.Number(c.DistinctCount))
                  .Append(" | ").Append(ProfileFormat.Percent(c.DistinctRatio))
                  .Append(" | ").Append(ProfileFormat.Date(c.LastFillDate))
                  .Append(" | ").Append(Escape(ProfileFormat.Value(c.MinValue)))
                  .Append(" | ").Append(Escape(ProfileFormat.Value(c.MaxValue)))
                  .Append(" | ").Append(ProfileFormat.Bytes(c.AvgByteLength))
                  .Append(" | ").Append(ProfileFormat.Flags(c.Flags))
                  .Append(" | ").Append(Escape(c.SkipReason))
                  .AppendLine(" |");
            }

            if (profile.Warnings != null && profile.Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Warnings");
                sb.AppendLine();
                foreach (var w in profile.Warnings) sb.Append("- ").AppendLine(w);
            }

            return sb.ToString();
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("|", "\\|");
        }
    }
}
