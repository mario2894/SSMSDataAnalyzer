using System;
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

        public static async Task<Result> ResolveAsync(Request request, int describeTimeoutSeconds, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.EditorText))
                return Decline("Go to source: no query text available.");

            DescribedColumn described;
            using (var describeConn = new SqlConnection(request.EditorConnectionString))
            {
                await describeConn.OpenAsync(cancellationToken).ConfigureAwait(true);

                // Gate 3: error rows (e.g. 11525 for temp tables in a multi-statement batch)
                // come back as ROWS, not exceptions. Checked on the UNFILTERED result set —
                // an error row's is_hidden comes back NULL (verified live), so filtering by
                // is_hidden = 0 first (as the raw SQL sketch would) can silently hide the very
                // row this gate exists to catch. See DescribeFirstResultSetService's doc comment.
                var allRows = await DescribeFirstResultSetService.DescribeAsync(
                    describeConn, request.EditorText, describeTimeoutSeconds, cancellationToken).ConfigureAwait(true);

                if (allRows.Any(r => r.ErrorNumber != null))
                    return Decline("Go to source: couldn't determine the source query — declined (a selection may have run, or GO split the batch).");

                // NOW it's safe to drop the DM's browse-info rows (is_hidden = 1) — ordinals
                // 1..N of what's left align 1:1 with the grid's data columns (docs section 6.3).
                var rows = allRows.Where(r => r.IsHidden == false).ToList();

                // Gate 4: shape must match exactly, or the described text is not the text that
                // produced this grid.
                if (rows.Count != request.NumberOfDataColumns)
                    return Decline("Go to source: result shape does not match what's on screen — declined rather than risk the wrong table.");

                described = rows.FirstOrDefault(r => r.Ordinal == request.GridColumnOrdinal);
                if (described == null)
                    return Decline("Go to source: could not match this column to the described query.");

                // Gate 5: the described name must match the grid's own column name at this
                // ordinal — the safety net for "wrong text described."
                if (!NamesMatch(described.Name, request.GridColumnName))
                    return Decline("Go to source: column names did not match — declined rather than risk the wrong table.");
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
                return new Result
                {
                    Success = true,
                    StatusMessage = $"Resolved to {columnMeta.ReferencedQualifiedName}.",
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
    }
}
