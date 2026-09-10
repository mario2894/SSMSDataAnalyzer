using System;
using System.Collections.Generic;
using System.Text;

namespace SsmsDataAnalyzer.Core.Pivot
{
    /// <summary>
    /// Pure formatting: header texts + rows of already-rendered cell strings -&gt; a GitHub/
    /// Jira/Confluence-style Markdown table (docs/pivot-plan.md §4 item 14). No knowledge of
    /// PivotResult/PivotRowItem -- the caller decides which columns/rows are currently visible
    /// (filters, toggles) and which header text to use (item 11's "Header:" chooser), and
    /// passes exactly that. Never touches the clipboard itself.
    /// </summary>
    public static class PivotMarkdown
    {
        /// <summary>Builds one Markdown table. <paramref name="headers"/> is the header row;
        /// each entry of <paramref name="rows"/> must have the same number of cells as
        /// <paramref name="headers"/>.Count. A null header or cell is treated as empty.</summary>
        public static string Build(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
        {
            if (headers == null) throw new ArgumentNullException(nameof(headers));
            if (rows == null) throw new ArgumentNullException(nameof(rows));

            var sb = new StringBuilder();

            AppendRow(sb, headers);

            var separator = new string[headers.Count];
            for (var i = 0; i < separator.Length; i++)
                separator[i] = "---";
            AppendRow(sb, separator);

            foreach (var row in rows)
                AppendRow(sb, row);

            return sb.ToString();
        }

        private static void AppendRow(StringBuilder sb, IReadOnlyList<string> cells)
        {
            sb.Append("|");
            for (var i = 0; i < cells.Count; i++)
            {
                sb.Append(' ');
                sb.Append(EscapeCell(cells[i]));
                sb.Append(" |");
            }
            sb.Append('\n');
        }

        /// <summary>'|' -&gt; '\|' (would otherwise end the cell early); CR/LF -&gt; a single
        /// space (Markdown table rows can't span lines). Null/empty -&gt; empty string.</summary>
        private static string EscapeCell(string cell)
        {
            if (string.IsNullOrEmpty(cell)) return string.Empty;

            var sb = new StringBuilder(cell.Length);
            for (var i = 0; i < cell.Length; i++)
            {
                var ch = cell[i];
                if (ch == '|')
                {
                    sb.Append("\\|");
                }
                else if (ch == '\r' || ch == '\n')
                {
                    // A lone CR/LF or a CRLF pair both collapse to one space -- otherwise a
                    // Windows-style CRLF line break would leave a double space in the cell.
                    if (ch == '\r' && i + 1 < cell.Length && cell[i + 1] == '\n') i++;
                    sb.Append(' ');
                }
                else
                {
                    sb.Append(ch);
                }
            }
            return sb.ToString();
        }
    }
}
