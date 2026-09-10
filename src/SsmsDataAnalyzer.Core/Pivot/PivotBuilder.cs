using System;
using System.Collections.Generic;

namespace SsmsDataAnalyzer.Core.Pivot
{
    /// <summary>
    /// Builds a <see cref="PivotResult"/> snapshot from a grid selection. This is the one
    /// place that reads grid cells, and it reads each pivoted cell exactly once -- the
    /// caller's <c>readCell</c> is typically a live SSMS grid lookup, so re-reading would be
    /// both slow and, for a re-run query, wrong.
    /// </summary>
    public static class PivotBuilder
    {
        /// <summary>What the grid shows for a real NULL -- also what a literal string 'NULL'
        /// looks like, since the portable grid API can't tell the two apart (see
        /// docs/newer-grid-api.md). AllNull/Differs inherit that ambiguity deliberately.</summary>
        public const string NullDisplayText = "NULL";

        public const int DefaultRowLimit = 100;
        public const int MinRowLimit = 1;
        public const int MaxRowLimit = 500;

        public static PivotResult Build(
            IReadOnlyList<string> columnNames,
            IEnumerable<RowRange> selection,
            int rowLimit,
            Func<long, int, string> readCell)
        {
            if (columnNames == null) throw new ArgumentNullException(nameof(columnNames));
            if (readCell == null) throw new ArgumentNullException(nameof(readCell));

            var clampedLimit = rowLimit < MinRowLimit ? MinRowLimit : rowLimit > MaxRowLimit ? MaxRowLimit : rowLimit;

            var totalSelectedRows = PivotSelection.CountDistinctRows(selection);
            var rows = PivotSelection.TakeDistinctRows(selection, clampedLimit);

            var displayNames = BuildDisplayNames(columnNames);

            var values = new string[columnNames.Count][];
            var columns = new PivotColumn[columnNames.Count];
            var differingColumnCount = 0;

            for (var c = 0; c < columnNames.Count; c++)
            {
                var gridOrdinal = c + 1;
                var columnValues = new string[rows.Count];
                for (var r = 0; r < rows.Count; r++)
                    columnValues[r] = readCell(rows[r], gridOrdinal) ?? string.Empty;

                var differs = ColumnDiffers(columnValues);
                var allNull = ColumnAllNull(columnValues);
                if (differs) differingColumnCount++;

                values[c] = columnValues;
                columns[c] = new PivotColumn(gridOrdinal, columnNames[c], displayNames[c], differs, allNull);
            }

            return new PivotResult(rows, columns, values, totalSelectedRows, differingColumnCount);
        }

        private static bool ColumnDiffers(string[] values)
        {
            if (values.Length < 2) return false;
            for (var i = 1; i < values.Length; i++)
                if (!string.Equals(values[i], values[0], StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static bool ColumnAllNull(string[] values)
        {
            if (values.Length < 1) return false;
            for (var i = 0; i < values.Length; i++)
                if (!string.Equals(values[i], NullDisplayText, StringComparison.Ordinal))
                    return false;
            return true;
        }

        /// <summary>
        /// First occurrence of a name keeps it; every later collision (whether it started as
        /// a duplicate or is itself a real column literally called "Name (2)") walks up to
        /// the next suffix that isn't already taken.
        /// </summary>
        private static string[] BuildDisplayNames(IReadOnlyList<string> columnNames)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            var result = new string[columnNames.Count];

            for (var i = 0; i < columnNames.Count; i++)
            {
                var name = columnNames[i];
                if (used.Add(name))
                {
                    result[i] = name;
                    continue;
                }

                var suffix = 2;
                string candidate;
                do
                {
                    candidate = name + " (" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
                    suffix++;
                }
                while (used.Contains(candidate));

                used.Add(candidate);
                result[i] = candidate;
            }

            return result;
        }
    }
}
