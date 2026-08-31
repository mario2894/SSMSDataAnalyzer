using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SsmsDataAnalyzer.Core.Metadata;
using SsmsDataAnalyzer.Core.Model;
using SsmsDataAnalyzer.Core.Sql;
using SsmsDataAnalyzer.Vsix.GoToSource;

namespace SsmsDataAnalyzer.Vsix.ResultsGrid
{
    /// <summary>
    /// CONTRACT.md Amendment 16's five-gate precondition check plus the FK jump, pulled out of
    /// <see cref="ResultsGridSourceCommand"/> so it can be exercised directly (against a real
    /// connection and real query text) without any WinForms/GridControl involved — those are
    /// the parts that genuinely cannot be driven headlessly (docs/resultsgrid-api.md's own
    /// object graph is all WinForms controls), unlike this resolution logic, which is plain
    /// data in, plain data out and is exactly the part most likely to jump to the wrong table
    /// if it has a bug. Gates 1+2 (grid index 0 / single grid in tab) are the caller's
    /// responsibility (<see cref="GridClickCapture"/>) since they are inherently about the
    /// live UI, not the query text.
    /// </summary>
    internal static class ResultsGridGoToSourceResolver
    {
        public sealed class Request
        {
            /// <summary>Connection string for the editor's OWN current database — used only
            /// to run the describe call, per docs section 6.2 ("the connection's database context").</summary>
            public string EditorConnectionString;
            public string EditorText;
            /// <summary>GRID column index (1..N) — the same convention as GetCellData/column_ordinal.</summary>
            public int GridColumnOrdinal;
            public string GridColumnName;
            public object CellValue;
            public int NumberOfDataColumns;
            /// <summary>Every data column's name, in grid order (index 0 = grid column 1,
            /// same as <see cref="DescribedColumn.Ordinal"/> - 1) — v0.7.4, needed to
            /// full-shape-match a candidate batch against the WHOLE grid, not just the
            /// clicked column. May be null (falls back to matching only the clicked column,
            /// same as before v0.7.4) if a caller doesn't have it.</summary>
            public string[] GridColumnNames;
            /// <summary>Builds a connection string for a possibly-different database (a
            /// describe result's source_database) — normally <see cref="GridConnectionInfo.TryBuild"/>
            /// bound to the same UIConnectionInfo as <see cref="EditorConnectionString"/>.</summary>
            public Func<string, string> BuildConnectionStringForDatabase;
        }

        public sealed class Result
        {
            public bool Success;
            /// <summary>Always set — the reason for a decline, or the confirmation for a success. Never blank.</summary>
            public string StatusMessage;
            public string GeneratedSql;
            public string TargetConnectionString;
        }

        /// <summary>One candidate batch's describe outcome, kept only for building a specific
        /// decline message — see <see cref="ResolveAsync"/>.</summary>
        private sealed class BatchOutcome
        {
            public int BatchIndex; // 0-based, for messaging shown as 1-based
            public List<DescribedColumn> Rows; // is_hidden-filtered, ordinal-ordered
            public int MismatchOrdinal = -1; // first ordinal (1-based) where names differ, or -1 if none
            public string MismatchDescribedName;
            public string MismatchGridName;
        }

        public static async Task<Result> ResolveAsync(Request request, int describeTimeoutSeconds, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.EditorText))
                return Decline("Go to source: no query text available.");

            // v0.7.4: SSMS executes whichever batch (GO-separated) the grid actually came
            // from — often NOT the whole editor text (e.g. "USE db / GO / SELECT ..." is
            // extremely common, and describing the whole buffer describes USE's own empty
            // result, not the SELECT that produced this grid). See TSqlBatchSplitter's doc
            // comment for why this is a real lexer, not a naive split on the substring "GO".
            var batches = TSqlBatchSplitter.Split(request.EditorText)
                .Select(b => b.Trim())
                .Where(b => b.Length > 0)
                .ToList();

            if (batches.Count == 0)
                return Decline("Go to source: no query text available.");

            var fullMatches = new List<BatchOutcome>();
            var nameMismatches = new List<BatchOutcome>();
            int erroredCount = 0, countMismatchCount = 0;

            using (var describeConn = new SqlConnection(request.EditorConnectionString))
            {
                await describeConn.OpenAsync(cancellationToken).ConfigureAwait(true);

                for (int b = 0; b < batches.Count; b++)
                {
                    // Gate 3: error rows (e.g. 11525 for temp tables in a multi-statement
                    // batch) come back as ROWS, not exceptions. Checked on the UNFILTERED
                    // result set — an error row's is_hidden comes back NULL (verified live),
                    // so filtering by is_hidden = 0 first (as the raw SQL sketch would) can
                    // silently hide the very row this gate exists to catch. See
                    // DescribeFirstResultSetService's doc comment.
                    var allRows = await DescribeFirstResultSetService.DescribeAsync(
                        describeConn, batches[b], describeTimeoutSeconds, cancellationToken).ConfigureAwait(true);

                    if (allRows.Any(r => r.ErrorNumber != null)) { erroredCount++; continue; }

                    // NOW it's safe to drop the DM's browse-info rows (is_hidden = 1) —
                    // ordinals 1..N of what's left align 1:1 with the grid's data columns
                    // (docs section 6.3). A batch with no result set at all (e.g. a bare
                    // "USE db") describes to zero rows here — that just falls through to the
                    // count-mismatch case below, which is exactly the "never matches, for
                    // free" outcome such a batch should have.
                    var rows = allRows.Where(r => r.IsHidden == false).OrderBy(r => r.Ordinal).ToList();

                    // Gate 4: shape must match exactly, or this candidate did not produce
                    // this grid.
                    if (rows.Count != request.NumberOfDataColumns) { countMismatchCount++; continue; }

                    // Gate 5, now checked across EVERY column (not just the clicked one) —
                    // a stronger identification than matching only the clicked column, per
                    // the lead's explicit direction: "if exactly one [batch] matches, use it
                    // — that's a stronger identification than we have today, not a weaker
                    // one."
                    var outcome = new BatchOutcome { BatchIndex = b, Rows = rows };
                    for (int ord = 1; ord <= request.NumberOfDataColumns; ord++)
                    {
                        // Whether we actually HAVE a grid-side name to compare at this
                        // ordinal: either the caller gave us the full grid column list, or
                        // (older/degraded caller) this is the one clicked column we always
                        // know. Ordinals we have no grid name for are simply not checked here
                        // — treating "unknown" as "expected blank" would wrongly flag a
                        // perfectly good match as a mismatch.
                        bool haveGridNameHere = request.GridColumnNames != null || ord == request.GridColumnOrdinal;
                        if (!haveGridNameHere) continue;

                        var row = rows.FirstOrDefault(r => r.Ordinal == ord);
                        string gridName = request.GridColumnNames != null && ord - 1 < request.GridColumnNames.Length
                            ? request.GridColumnNames[ord - 1]
                            : request.GridColumnName;

                        if (row == null || !NamesMatch(row.Name, gridName))
                        {
                            if (outcome.MismatchOrdinal == -1)
                            {
                                outcome.MismatchOrdinal = ord;
                                outcome.MismatchDescribedName = row?.Name;
                                outcome.MismatchGridName = gridName;
                            }
                        }
                    }

                    if (outcome.MismatchOrdinal == -1) fullMatches.Add(outcome);
                    else nameMismatches.Add(outcome);
                }
            }

            DescribedColumn described;
            if (fullMatches.Count == 0)
            {
                if (nameMismatches.Count == 1)
                {
                    // Exactly one candidate had the right COLUMN COUNT but a name
                    // disagreement — specific and actionable, same principle as the SqlInt32
                    // fix: name what was actually seen instead of a generic "doesn't match."
                    var nm = nameMismatches[0];
                    string batchNote = batches.Count > 1 ? $" (batch {nm.BatchIndex + 1} of {batches.Count})" : "";
                    return Decline($"Go to source: column {nm.MismatchOrdinal} is named '{Describe(nm.MismatchDescribedName)}' in the query but '{Describe(nm.MismatchGridName)}' on screen{batchNote} — declined rather than risk the wrong table.");
                }

                var parts = new List<string>();
                if (countMismatchCount > 0) parts.Add($"{countMismatchCount} had a different column count");
                if (nameMismatches.Count > 0) parts.Add($"{nameMismatches.Count} had different column names");
                if (erroredCount > 0) parts.Add($"{erroredCount} errored (e.g. a selection or a later batch's temp table)");
                string detail = parts.Count > 0 ? $" ({string.Join(", ", parts)})" : "";
                return Decline($"Go to source: the query text has {batches.Count} batch(es) and none produced a result matching this grid's {request.NumberOfDataColumns} columns{detail} — declined rather than risk the wrong table.");
            }

            // v0.7.4 amendment (lead's ruling, supersedes the "exactly one matching batch"
            // rule and Amendment 16's original gates 1+2 together): the question that
            // actually matters is not "which single batch produced this grid" but "do ALL
            // shape-matching candidates agree on where this column comes from." Two
            // near-identical SELECTs (e.g. differing only in a WHERE value) describe to the
            // same source table/column for the same clicked ordinal — requiring exactly one
            // matching batch was refusing a case that was never actually ambiguous. If they
            // DISAGREE on the source, that is the real wrong-table risk, caught directly here
            // instead of by a same-tab-grid-count proxy.
            var describedPerMatch = fullMatches
                .Select(o => o.Rows.FirstOrDefault(r => r.Ordinal == request.GridColumnOrdinal))
                .ToList();

            if (describedPerMatch.Any(d => d == null))
                return Decline("Go to source: could not match this column to the described query.");

            described = describedPerMatch[0];
            var distinctSources = describedPerMatch
                .GroupBy(d => (d.SourceDatabase, Schema: d.SourceSchema ?? "dbo", d.SourceTable, d.SourceColumn),
                    new SourceKeyComparer())
                .ToList();

            if (distinctSources.Count > 1)
            {
                string conflictList = string.Join(" vs. ", distinctSources.Select(g => DescribeSource(g.First())));
                return Decline($"Go to source: '{request.GridColumnName}' does not resolve the same way across the query's matching batches — {conflictList} — declined rather than risk the wrong table.");
            }

            if (described.SourceTable == null)
                return Decline($"Go to source: '{request.GridColumnName}' is a computed expression — it has no base table.");

            var tableRef = new TableRef { Schema = described.SourceSchema ?? "dbo", Name = described.SourceTable };

            var targetConnectionString = request.BuildConnectionStringForDatabase(described.SourceDatabase);
            if (targetConnectionString == null)
                return Decline("Go to source: could not build a connection for the source table's database.");

            using (var targetConn = new SqlConnection(targetConnectionString))
            {
                await targetConn.OpenAsync(cancellationToken).ConfigureAwait(true);

                // Same Core FK-detection path the tool window's ColumnMeta already uses — not
                // a second implementation.
                var schema = await new SchemaReader().ReadAsync(targetConn, tableRef, new ProfileOptions(), cancellationToken).ConfigureAwait(true);

                var columnMeta = schema.Columns.FirstOrDefault(c =>
                    string.Equals(c.Name, described.SourceColumn, StringComparison.OrdinalIgnoreCase));

                if (columnMeta == null)
                    return Decline($"Go to source: could not find column '{described.SourceColumn}' on {tableRef.QualifiedName}.");

                // Never gate on IsForeignKey alone (CONTRACT.md Amendment 15): a composite or
                // multi-FK column both set it true but leave ReferencedTable/-Column null.
                if (columnMeta.ReferencedTable == null)
                    return Decline($"Go to source: '{columnMeta.Name}' on {tableRef.QualifiedName} is not a (single-resolvable) foreign key.");

                if (columnMeta.ReferencedColumn == null)
                    return Decline($"Go to source: '{columnMeta.Name}' is part of a composite foreign key — can't resolve a single-value filter.");

                // IL-verified (QEStorageViewOnReader.GetCellData, SQLEditors.dll): for an
                // ordinary column this is the real typed value from the underlying
                // StorageDataReader.GetValue() — NOT the grid's display text (that is
                // GetCellDataAsString's job, a separate method) — but it is a PROVIDER-
                // SPECIFIC System.Data.SqlTypes struct (e.g. SqlInt32), not the plain CLR
                // type the tool window's Min/Max are. A NULL cell comes back as a plain C#
                // null for an ordinary reader-backed grid, but a SqlTypes struct can ALSO be
                // "null" via its own IsNull — value == null/DBNull alone would miss that (a
                // null SqlInt32 is a real, non-null object), which is exactly how a NULL
                // FundID cell was misreported as "unsupported type SqlInt32" before this fix.
                // IsEffectivelyNull is the one place both kinds of "no value" are recognized.
                if (SqlLiteralFormatter.IsEffectivelyNull(request.CellValue))
                    return Decline($"Go to source: [{request.GridColumnName}] is NULL — there's no value to filter by.");

                if (!SqlLiteralFormatter.TryFormat(request.CellValue, columnMeta, out var literal))
                    return Decline($"Go to source: [{request.GridColumnName}] has type {request.CellValue.GetType().Name} which can't be rendered as a SQL literal.");

                var sql = $"SELECT * FROM {columnMeta.ReferencedQualifiedName} WHERE {SqlIdentifier.Bracket(columnMeta.ReferencedColumn)} = {literal};";

                // Lead's explicit ask: make the multi-batch agreement visible, not magic.
                string matchNote = fullMatches.Count > 1
                    ? $" (resolved via {fullMatches.Count} matching batches, all agreeing on this source)"
                    : "";

                return new Result
                {
                    Success = true,
                    StatusMessage = $"Resolved to {columnMeta.ReferencedQualifiedName}.{matchNote}",
                    GeneratedSql = sql,
                    TargetConnectionString = targetConnectionString
                };
            }
        }

        private static Result Decline(string reason) => new Result { Success = false, StatusMessage = reason };

        /// <summary>Both NULL/"(No column name)" count as a match (docs section 6.4).</summary>
        internal static bool NamesMatch(string describedName, string gridColumnName)
        {
            bool describedEmpty = string.IsNullOrEmpty(describedName) || describedName == "(No column name)";
            bool gridEmpty = string.IsNullOrEmpty(gridColumnName) || gridColumnName == "(No column name)";
            if (describedEmpty && gridEmpty) return true;
            return string.Equals(describedName, gridColumnName, StringComparison.Ordinal);
        }

        /// <summary>For decline messages only — renders a null/blank column name the same
        /// human-readable way SSMS itself shows an unnamed column.</summary>
        private static string Describe(string name) => string.IsNullOrEmpty(name) ? "(No column name)" : name;

        /// <summary>For decline/conflict messages only — human-readable "where does this
        /// column actually come from," used to name a disagreement between batches
        /// concretely rather than just refusing.</summary>
        private static string DescribeSource(DescribedColumn c) =>
            c.SourceTable == null
                ? "a computed expression with no base table"
                : $"{(c.SourceDatabase != null ? c.SourceDatabase + "." : "")}{c.SourceSchema ?? "dbo"}.{c.SourceTable}.{c.SourceColumn}";

        /// <summary>Identifies "the same underlying column" across independently-described
        /// batches (database/schema/table/column, case-insensitive — SQL Server identifiers
        /// describing the literal same object off the same server should never legitimately
        /// differ only in case). Deliberately NOT the same comparison as
        /// <see cref="NamesMatch"/>, which is ordinal because it's checking a DISPLAYED label
        /// against what SSMS itself shows, not two descriptions of one real object.</summary>
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
