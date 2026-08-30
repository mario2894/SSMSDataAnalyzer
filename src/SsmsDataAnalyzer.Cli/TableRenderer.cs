using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SsmsDataAnalyzer.Core.Export;
using SsmsDataAnalyzer.Core.Model;

namespace SsmsDataAnalyzer.Cli
{
    /// <summary>Fixed-width console grid, one row per column.</summary>
    internal static class TableRenderer
    {
        private sealed class Col
        {
            public string Header;
            public bool Right;
            public Func<ColumnProfile, string> Get;
        }

        private static readonly Col CollationCol =
            new Col { Header = "COLLATION", Get = c => c.Meta.Collation ?? string.Empty };

        private static readonly Col ReferencesCol =
            new Col { Header = "REFERENCES", Get = c => ProfileFormat.ForeignKey(c.Meta) };

        private static readonly Col[] Cols =
        {
            new Col { Header = "COLUMN",    Get = c => c.Meta.Name },
            new Col { Header = "TYPE",      Get = c => ProfileFormat.TypeDisplay(c.Meta) },
            new Col { Header = "ATTRS",     Get = c => ProfileFormat.Attributes(c.Meta) },
            new Col { Header = "FILLED",    Right = true, Get = c => ProfileFormat.Number(c.FilledCount) },
            new Col { Header = "FILL%",     Right = true, Get = c => ProfileFormat.Percent(c.FillRatio) },
            new Col { Header = "BLANK",     Right = true, Get = c => ProfileFormat.Number(c.BlankCount) },
            new Col { Header = "DISTINCT",  Right = true, Get = c => ProfileFormat.Number(c.DistinctCount) },
            new Col { Header = "DIST%",     Right = true, Get = c => ProfileFormat.Percent(c.DistinctRatio) },
            new Col { Header = "LAST FILL", Get = c => ProfileFormat.Date(c.LastFillDate) },
            new Col { Header = "MIN",       Get = c => Clip(ProfileFormat.Value(c.MinValue), 22) },
            new Col { Header = "MAX",       Get = c => Clip(ProfileFormat.Value(c.MaxValue), 22) },
            new Col { Header = "AVGLEN",    Right = true, Get = c => ProfileFormat.Bytes(c.AvgByteLength) },
            new Col { Header = "FLAGS",     Get = c => ProfileFormat.Flags(c.Flags) }
        };

        public static string Render(TableProfile p)
        {
            var sb = new StringBuilder();

            sb.AppendLine();
            sb.Append("Table       : ").AppendLine(p.Table.QualifiedName
                + "  (" + p.Table.Server + " / " + p.Table.Database + ")");
            sb.Append("Rows        : ").Append(ProfileFormat.Number(p.TotalRows))
              .Append("   estimate ").AppendLine(ProfileFormat.Number(p.EstimatedRows));
            sb.Append("DateCreated : ").AppendLine(p.DateCreatedColumn ?? "n/a");
            sb.Append("Sampled     : ").AppendLine(p.WasSampled ? "yes" : "no");
            sb.Append("Elapsed     : ").AppendLine(p.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s");
            sb.AppendLine();

            // Collation only earns a column when something in this table actually has one —
            // an all-numeric table would otherwise carry a permanently empty column.
            var cols = new List<Col>(Cols);
            bool anyCollation = false;
            foreach (var c in p.Columns)
                if (!string.IsNullOrEmpty(c.Meta.Collation)) { anyCollation = true; break; }
            if (anyCollation) cols.Insert(2, CollationCol);   // straight after TYPE

            // Same rule for foreign keys: only spend grid width when the table actually has one.
            bool anyFk = false;
            foreach (var c in p.Columns)
                if (c.Meta.IsForeignKey) { anyFk = true; break; }
            if (anyFk) cols.Insert(anyCollation ? 3 : 2, ReferencesCol);

            var rows = new List<string[]>();
            foreach (var c in p.Columns)
            {
                var row = new string[cols.Count];
                for (int i = 0; i < cols.Count; i++) row[i] = cols[i].Get(c) ?? string.Empty;
                rows.Add(row);
            }

            var widths = new int[cols.Count];
            for (int i = 0; i < cols.Count; i++)
            {
                widths[i] = cols[i].Header.Length;
                foreach (var row in rows) if (row[i].Length > widths[i]) widths[i] = row[i].Length;
            }

            for (int i = 0; i < cols.Count; i++)
            {
                if (i > 0) sb.Append("  ");
                sb.Append(Pad(cols[i].Header, widths[i], cols[i].Right));
            }
            sb.AppendLine();

            for (int i = 0; i < cols.Count; i++)
            {
                if (i > 0) sb.Append("  ");
                sb.Append(new string('-', widths[i]));
            }
            sb.AppendLine();

            foreach (var row in rows)
            {
                for (int i = 0; i < cols.Count; i++)
                {
                    if (i > 0) sb.Append("  ");
                    sb.Append(Pad(row[i], widths[i], cols[i].Right));
                }
                sb.AppendLine();
            }

            var notes = new List<string>();
            foreach (var c in p.Columns)
                if (!string.IsNullOrEmpty(c.SkipReason)) notes.Add("  " + c.Meta.Name + ": " + c.SkipReason);

            if (notes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Skipped:");
                foreach (var n in notes) sb.AppendLine(n);
            }

            if (p.Warnings != null && p.Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Warnings:");
                foreach (var w in p.Warnings) sb.Append("  ! ").AppendLine(w);
            }

            return sb.ToString();
        }

        private static string Pad(string s, int width, bool right)
        {
            if (s.Length >= width) return s;
            return right ? s.PadLeft(width) : s.PadRight(width);
        }

        private static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max - 1) + "…";
        }
    }
}
