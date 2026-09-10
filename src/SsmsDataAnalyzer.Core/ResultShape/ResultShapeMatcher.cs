using System;
using System.Collections.Generic;
using System.Linq;

namespace SsmsDataAnalyzer.Core.ResultShape
{
    /// <summary>A shape-matching batch: its 0-based index in the split query text and its
    /// described rows, is_hidden-filtered and ordinal-ordered.</summary>
    public sealed class MatchedBatch
    {
        internal MatchedBatch(int batchIndex, IReadOnlyList<DescribedColumn> rows)
        {
            BatchIndex = batchIndex;
            Rows = rows;
        }

        public int BatchIndex { get; }
        public IReadOnlyList<DescribedColumn> Rows { get; }
    }

    /// <summary>Outcome of matching every described batch against the grid's full shape
    /// (CONTRACT.md Amendment 17 gates 3, 4, 5). Either <see cref="IsMatch"/> with at least one
    /// <see cref="Matches"/>, or a decline with a never-blank <see cref="DeclineMessage"/>.</summary>
    public sealed class ShapeMatch
    {
        private ShapeMatch() { }

        public bool IsMatch { get; private set; }
        /// <summary>Non-null exactly when <see cref="IsMatch"/> is false.</summary>
        public string DeclineMessage { get; private set; }
        /// <summary>Every batch whose described shape matched the whole grid. Empty on decline.</summary>
        public IReadOnlyList<MatchedBatch> Matches { get; private set; } = new MatchedBatch[0];
        /// <summary>Full, unfiltered describe dumps for every mismatching batch, produced only
        /// when nothing matched — the caller writes them to its diagnostic log. Contain column
        /// names and metadata only, never cell values.</summary>
        public IReadOnlyList<string> DiagnosticDumps { get; private set; } = new string[0];

        public static ShapeMatch Declined(string message) =>
            new ShapeMatch { IsMatch = false, DeclineMessage = message ?? throw new ArgumentNullException(nameof(message)) };

        internal static ShapeMatch Matched(IReadOnlyList<MatchedBatch> matches) =>
            new ShapeMatch { IsMatch = true, Matches = matches };

        internal static ShapeMatch Declined(string message, IReadOnlyList<string> dumps) =>
            new ShapeMatch { IsMatch = false, DeclineMessage = message, DiagnosticDumps = dumps };
    }

    /// <summary>Outcome of the cross-batch agreement check for ONE grid column.</summary>
    public sealed class ColumnSourceResolution
    {
        private ColumnSourceResolution() { }

        public bool Succeeded { get; private set; }
        /// <summary>Non-null exactly when <see cref="Succeeded"/> is false.</summary>
        public string DeclineMessage { get; private set; }
        /// <summary>The first matching batch's row for this ordinal; its SourceTable is never
        /// null on success. Null on decline.</summary>
        public DescribedColumn Described { get; private set; }
        /// <summary>How many shape-matching batches agreed (>= 1 on success).</summary>
        public int MatchCount { get; private set; }

        internal static ColumnSourceResolution Decline(string message) =>
            new ColumnSourceResolution { DeclineMessage = message };

        internal static ColumnSourceResolution Success(DescribedColumn described, int matchCount) =>
            new ColumnSourceResolution { Succeeded = true, Described = described, MatchCount = matchCount };
    }

    /// <summary>
    /// The pure (no connection, no SSMS types) half of the results-grid "Go to source"
    /// resolution, extracted from SsmsDataAnalyzer.Vsix.ResultsGrid.ResultsGridGoToSourceResolver
    /// (docs/pivot-plan.md item 13) so that one describe pass can resolve EVERY grid ordinal
    /// (pivot FK link icons) while single-cell Go to source keeps using the identical logic.
    /// Every decline message here is the exact text Go to source produced before the split —
    /// tests pin them.
    ///
    /// Step 1, <see cref="Match"/>: which GO-separated batches produced a result of exactly
    /// the grid's shape (gates 3/4/5, per CONTRACT.md Amendment 17). Step 2,
    /// <see cref="ResolveColumn"/>: do all those batches agree on one column's base source.
    /// </summary>
    public static class ResultShapeMatcher
    {
        /// <param name="describedBatches">One entry per non-empty batch, in batch order: the
        /// UNFILTERED rows sys.dm_exec_describe_first_result_set returned for it.</param>
        /// <param name="numberOfDataColumns">Grid data-column count (gutter excluded).</param>
        /// <param name="gridColumnNames">Every grid column's header text, index 0 = grid
        /// column 1. May be null (degraded caller: only the clicked column is name-checked).</param>
        /// <param name="clickedOrdinal">1-based grid column used only when
        /// <paramref name="gridColumnNames"/> is null (or shorter than the grid). Pass 0 when
        /// resolving all columns with a full name list.</param>
        /// <param name="clickedColumnName">Header text of <paramref name="clickedOrdinal"/>.</param>
        public static ShapeMatch Match(
            IReadOnlyList<IReadOnlyList<DescribedColumn>> describedBatches,
            int numberOfDataColumns,
            IReadOnlyList<string> gridColumnNames,
            int clickedOrdinal,
            string clickedColumnName)
        {
            if (describedBatches == null) throw new ArgumentNullException(nameof(describedBatches));

            int batchCount = describedBatches.Count;
            var fullMatches = new List<(int BatchIndex, List<DescribedColumn> Rows)>();
            var nameMismatches = new List<(int BatchIndex, List<DescribedColumn> Rows, int MismatchOrdinal, string DescribedName, string GridName)>();
            var countMismatches = new List<(int BatchIndex, IReadOnlyList<DescribedColumn> AllRows, List<DescribedColumn> FilteredRows)>();
            int erroredCount = 0;
            DescribedColumn firstError = null;

            for (int b = 0; b < batchCount; b++)
            {
                var allRows = describedBatches[b] ?? new DescribedColumn[0];

                // Gate 3: error rows come back as ROWS; checked on the UNFILTERED rows (an
                // error row's is_hidden is NULL, so filtering first would hide it).
                var errorRow = allRows.FirstOrDefault(r => r.ErrorNumber != null);
                if (errorRow != null)
                {
                    erroredCount++;
                    if (firstError == null) firstError = errorRow;
                    continue;
                }

                // Drop browse-info rows (is_hidden = 1); what remains aligns 1:1 with the grid.
                var rows = allRows.Where(r => r.IsHidden == false).OrderBy(r => r.Ordinal).ToList();

                // Gate 4: exact column count.
                if (rows.Count != numberOfDataColumns) { countMismatches.Add((b, allRows, rows)); continue; }

                // Gate 5: every column's name (or only the clicked one for a degraded caller).
                int mismatchOrdinal = -1;
                string mismatchDescribed = null, mismatchGrid = null;
                for (int ord = 1; ord <= numberOfDataColumns; ord++)
                {
                    bool haveGridNameHere = gridColumnNames != null || ord == clickedOrdinal;
                    if (!haveGridNameHere) continue;

                    var row = rows.FirstOrDefault(r => r.Ordinal == ord);
                    string gridName = gridColumnNames != null && ord - 1 < gridColumnNames.Count
                        ? gridColumnNames[ord - 1]
                        : clickedColumnName;

                    if (row == null || !NamesMatch(row.Name, gridName))
                    {
                        if (mismatchOrdinal == -1)
                        {
                            mismatchOrdinal = ord;
                            mismatchDescribed = row?.Name;
                            mismatchGrid = gridName;
                        }
                    }
                }

                if (mismatchOrdinal == -1) fullMatches.Add((b, rows));
                else nameMismatches.Add((b, rows, mismatchOrdinal, mismatchDescribed, mismatchGrid));
            }

            if (fullMatches.Count > 0)
                return ShapeMatch.Matched(fullMatches.Select(m => new MatchedBatch(m.BatchIndex, m.Rows)).ToList());

            var dumps = new List<string>();
            foreach (var cm in countMismatches) dumps.Add(BatchDump("count mismatch", cm.BatchIndex, batchCount, cm.AllRows));
            foreach (var nm in nameMismatches) dumps.Add(BatchDump("name mismatch", nm.BatchIndex, batchCount, nm.Rows));

            if (nameMismatches.Count == 1 && countMismatches.Count == 0)
            {
                var nm = nameMismatches[0];
                string batchNote = batchCount > 1 ? $" (batch {nm.BatchIndex + 1} of {batchCount})" : "";
                return ShapeMatch.Declined($"Go to source: column {nm.MismatchOrdinal} is named '{Describe(nm.DescribedName)}' in the query but '{Describe(nm.GridName)}' on screen{batchNote} — declined rather than risk the wrong table.", dumps);
            }

            if (countMismatches.Count == 1 && nameMismatches.Count == 0)
            {
                var cm = countMismatches[0];
                string batchNote = batchCount > 1 ? $" (batch {cm.BatchIndex + 1} of {batchCount})" : "";
                string divergenceNote = "";
                var divergence = FindFirstDivergence(cm.FilteredRows, gridColumnNames, numberOfDataColumns);
                if (divergence.HasValue)
                {
                    divergenceNote = $" First divergence at column {divergence.Value.Ordinal}: described '{Describe(divergence.Value.DescribedName)}', grid '{Describe(divergence.Value.GridName)}'.";
                }
                return ShapeMatch.Declined($"Go to source: the query describes {cm.FilteredRows.Count} column(s) but the grid shows {numberOfDataColumns}{batchNote} — declined rather than risk the wrong table.{divergenceNote}", dumps);
            }

            var parts = new List<string>();
            if (countMismatches.Count > 0) parts.Add($"{countMismatches.Count} had a different column count");
            if (nameMismatches.Count > 0) parts.Add($"{nameMismatches.Count} had different column names");
            if (erroredCount > 0) parts.Add($"{erroredCount} errored — {DescribeError(firstError)}");
            string detail = parts.Count > 0 ? $" ({string.Join(", ", parts)})" : "";
            return ShapeMatch.Declined($"Go to source: the query text has {batchCount} batch(es) and none produced a result matching this grid's {numberOfDataColumns} columns{detail} — declined rather than risk the wrong table.", dumps);
        }

        /// <summary>
        /// Amendment 17's agreement rule for one grid column: every shape-matching batch must
        /// resolve <paramref name="gridOrdinal"/> to the same (database, schema, table, column),
        /// and that source must be a real base column (not a computed expression).
        /// </summary>
        /// <param name="gridColumnName">Header text, used only in decline messages.</param>
        public static ColumnSourceResolution ResolveColumn(ShapeMatch match, int gridOrdinal, string gridColumnName)
        {
            if (match == null) throw new ArgumentNullException(nameof(match));
            if (!match.IsMatch) throw new ArgumentException("ResolveColumn requires a successful ShapeMatch.", nameof(match));

            var describedPerMatch = match.Matches
                .Select(o => o.Rows.FirstOrDefault(r => r.Ordinal == gridOrdinal))
                .ToList();

            if (describedPerMatch.Any(d => d == null))
                return ColumnSourceResolution.Decline("Go to source: could not match this column to the described query.");

            var described = describedPerMatch[0];
            var distinctSources = describedPerMatch
                .GroupBy(d => (d.SourceDatabase, Schema: d.SourceSchema ?? "dbo", d.SourceTable, d.SourceColumn),
                    new SourceKeyComparer())
                .ToList();

            if (distinctSources.Count > 1)
            {
                string conflictList = string.Join(" vs. ", distinctSources.Select(g => DescribeSource(g.First())));
                return ColumnSourceResolution.Decline($"Go to source: '{gridColumnName}' does not resolve the same way across the query's matching batches — {conflictList} — declined rather than risk the wrong table.");
            }

            if (described.SourceTable == null)
                return ColumnSourceResolution.Decline($"Go to source: '{gridColumnName}' is a computed expression — it has no base table.");

            return ColumnSourceResolution.Success(described, match.Matches.Count);
        }

        /// <summary>Both NULL/"(No column name)" count as a match (docs section 6.4).</summary>
        public static bool NamesMatch(string describedName, string gridColumnName)
        {
            bool describedEmpty = string.IsNullOrEmpty(describedName) || describedName == "(No column name)";
            bool gridEmpty = string.IsNullOrEmpty(gridColumnName) || gridColumnName == "(No column name)";
            if (describedEmpty && gridEmpty) return true;
            return string.Equals(describedName, gridColumnName, StringComparison.Ordinal);
        }

        private static string Describe(string name) => string.IsNullOrEmpty(name) ? "(No column name)" : name;

        private static (int Ordinal, string DescribedName, string GridName)? FindFirstDivergence(
            List<DescribedColumn> describedRows, IReadOnlyList<string> gridColumnNames, int gridColumnCount)
        {
            if (gridColumnNames == null) return null;

            int maxOrdinal = Math.Max(describedRows.Count == 0 ? 0 : describedRows.Max(r => r.Ordinal), gridColumnCount);
            for (int ord = 1; ord <= maxOrdinal; ord++)
            {
                string describedName = describedRows.FirstOrDefault(r => r.Ordinal == ord)?.Name;
                string gridName = ord - 1 < gridColumnNames.Count ? gridColumnNames[ord - 1] : null;
                if (!NamesMatch(describedName, gridName))
                    return (ord, describedName, gridName);
            }
            return null;
        }

        /// <summary>"SQL Server error 11525: The metadata could not be determined because …",
        /// trimmed. The real error replaces the old generic guess ("e.g. a selection or a later
        /// batch's temp table"), which left a field report undiagnosable.</summary>
        private static string DescribeError(DescribedColumn errorRow)
        {
            const int MaxMessageLength = 200;
            string text = "SQL Server error " + errorRow.ErrorNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string message = errorRow.ErrorMessage?.Trim();
            if (!string.IsNullOrEmpty(message))
            {
                if (message.Length > MaxMessageLength) message = message.Substring(0, MaxMessageLength) + "…";
                text += ": " + message;
            }
            return text;
        }

        private static string BatchDump(string reason, int batchIndex, int totalBatches, IReadOnlyList<DescribedColumn> allRows)
        {
            var lines = allRows.Select(r =>
                $"  ordinal={r.Ordinal} name='{r.Name ?? "<null>"}' is_hidden={(r.IsHidden.HasValue ? r.IsHidden.Value.ToString() : "NULL")} error_number={(r.ErrorNumber.HasValue ? r.ErrorNumber.Value.ToString() : "NULL")}");
            return $"Go to source ({reason}) — full describe dump for batch {batchIndex + 1} of {totalBatches}:\n{string.Join("\n", lines)}";
        }

        private static string DescribeSource(DescribedColumn c) =>
            c.SourceTable == null
                ? "a computed expression with no base table"
                : $"{(c.SourceDatabase != null ? c.SourceDatabase + "." : "")}{c.SourceSchema ?? "dbo"}.{c.SourceTable}.{c.SourceColumn}";

        /// <summary>Case-insensitive identity of "the same underlying column" across batches —
        /// unchanged from the pre-split resolver.</summary>
        private sealed class SourceKeyComparer : IEqualityComparer<(string SourceDatabase, string Schema, string SourceTable, string SourceColumn)>
        {
            public bool Equals((string SourceDatabase, string Schema, string SourceTable, string SourceColumn) a,
                                (string SourceDatabase, string Schema, string SourceTable, string SourceColumn) b) =>
                string.Equals(a.SourceDatabase, b.SourceDatabase, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.Schema, b.Schema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.SourceTable, b.SourceTable, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.SourceColumn, b.SourceColumn, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode((string SourceDatabase, string Schema, string SourceTable, string SourceColumn) k) =>
                StringComparer.OrdinalIgnoreCase.GetHashCode(k.SourceTable ?? "") ^
                StringComparer.OrdinalIgnoreCase.GetHashCode(k.SourceColumn ?? "");
        }
    }
}
