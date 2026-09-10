using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SsmsDataAnalyzer.Core.Metadata;
using SsmsDataAnalyzer.Core.Model;
using SsmsDataAnalyzer.Core.ResultShape;
using SsmsDataAnalyzer.Core.Sql;
using SsmsDataAnalyzer.Vsix.GoToSource;
using SsmsDataAnalyzer.Vsix.ObjectExplorer;

namespace SsmsDataAnalyzer.Vsix.ResultsGrid
{
    /// <summary>
    /// CONTRACT.md Amendment 16/17's precondition check plus the FK jump, pulled out of
    /// <see cref="ResultsGridSourceCommand"/> so it can be exercised directly (against a real
    /// connection and real query text) without any WinForms/GridControl involved. This file
    /// deliberately references no VS/SSMS UI type.
    ///
    /// docs/pivot-plan.md item 13 split it into stages so the pivot's FK link icons resolve
    /// EVERY grid column from one describe call per batch, while single-cell Go to source runs
    /// the very same stages for its one column:
    /// <list type="number">
    /// <item><see cref="DescribeAndMatchAsync"/> — split into GO batches, describe each,
    /// match the grid's full shape (pure part: Core's <see cref="ResultShapeMatcher.Match"/>).</item>
    /// <item><see cref="ResultShapeMatcher.ResolveColumn"/> — cross-batch agreement for one
    /// ordinal (pure, Core).</item>
    /// <item><see cref="CheckForeignKey"/> — the column's declared FK via Core's SchemaReader.</item>
    /// <item><see cref="TryBuildJump"/> — cell display text → literal → SELECT.</item>
    /// </list>
    /// <see cref="ResolveAsync"/> (single cell) and <see cref="ResolveAllColumnsAsync"/> (every
    /// column, no cell) are the two compositions. Gates 1+2 (which grid) are the caller's
    /// responsibility since they are about the live UI, not the query text.
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
            /// <summary>"[schema].[table]" of the referenced row (== ColumnMeta.ReferencedQualifiedName)
            /// — added for "Peek source for this value" (a tool-window caption needs it; the
            /// status-bar StatusMessage text above is unchanged). Null on decline.</summary>
            public string TargetQualifiedName;
        }

        /// <summary>One grid column's outcome in <see cref="ResolveAllColumnsAsync"/>. Holds
        /// metadata only — never a cell value, never a connection string.</summary>
        public sealed class ColumnLink
        {
            /// <summary>1-based grid column.</summary>
            public int GridOrdinal;
            public string GridColumnName;
            /// <summary>True only for a base column with exactly one single-column declared FK
            /// that every shape-matching batch agrees on.</summary>
            public bool IsLink;
            /// <summary>When not a link: exactly the decline Go to source would show for a
            /// click on this column (any non-NULL, formattable cell). Null when a link.</summary>
            public string DeclineMessage;
            /// <summary>The agreed described row (type/max_length/source database). Null when
            /// the agreement step itself declined.</summary>
            public DescribedColumn Described;
            /// <summary>The source column's metadata carrying the FK target. Non-null when a link.</summary>
            public ColumnMeta ForeignKeyColumn;
            /// <summary>How many shape-matching batches agreed.</summary>
            public int MatchCount;
            /// <summary>"[schema].[table].[column]" of the referenced column when a link, else null.</summary>
            public string TargetText;
        }

        /// <summary>Result of <see cref="ResolveAllColumnsAsync"/>: either a whole-grid decline
        /// (<see cref="DeclineMessage"/> set, <see cref="Columns"/> empty) or one
        /// <see cref="ColumnLink"/> per grid column (index = ordinal - 1).</summary>
        public sealed class ColumnLinkMap
        {
            public string DeclineMessage;
            public IReadOnlyList<ColumnLink> Columns = new ColumnLink[0];
        }

        internal const string NoQueryTextMessage = "Go to source: no query text available.";
        internal const string CouldNotBuildTargetConnectionMessage = "Go to source: could not build a connection for the source table's database.";

        public static async Task<Result> ResolveAsync(Request request, int describeTimeoutSeconds, CancellationToken cancellationToken)
        {
            var shape = await DescribeAndMatchAsync(
                request.EditorConnectionString, request.EditorText, request.NumberOfDataColumns,
                request.GridColumnNames, request.GridColumnOrdinal, request.GridColumnName,
                describeTimeoutSeconds, cancellationToken).ConfigureAwait(true);

            if (!shape.IsMatch)
                return Decline(shape.DeclineMessage);

            var column = ResultShapeMatcher.ResolveColumn(shape, request.GridColumnOrdinal, request.GridColumnName);
            if (!column.Succeeded)
                return Decline(column.DeclineMessage);

            var described = column.Described;
            var tableRef = SourceTableRef(described);

            var targetConnectionString = request.BuildConnectionStringForDatabase(described.SourceDatabase);
            if (targetConnectionString == null)
                return Decline(CouldNotBuildTargetConnectionMessage);

            using (var targetConn = new SqlConnection(targetConnectionString))
            {
                await targetConn.OpenAsync(cancellationToken).ConfigureAwait(true);

                // Same Core FK-detection path the tool window's ColumnMeta already uses — not
                // a second implementation.
                var schema = await new SchemaReader().ReadAsync(targetConn, tableRef, new ProfileOptions(), cancellationToken).ConfigureAwait(true);

                string fkDecline = CheckForeignKey(schema.Columns, described, tableRef, out var columnMeta);
                if (fkDecline != null)
                    return Decline(fkDecline);

                // v0.8.0: CellValue is the grid's DISPLAY TEXT (IGridStorage.GetCellDataAsString)
                // — see docs/newer-grid-api.md. TryBuildJump both parses it back to a literal
                // AND is where a NULL-vs-literal-"NULL" cell gets declined.
                if (!TryBuildJump(columnMeta, described, request.GridColumnName, request.CellValue as string, column.MatchCount, out var sql, out var statusMessage))
                    return Decline(statusMessage);

                return new Result
                {
                    Success = true,
                    StatusMessage = statusMessage,
                    GeneratedSql = sql,
                    TargetConnectionString = targetConnectionString,
                    TargetQualifiedName = columnMeta.ReferencedQualifiedName
                };
            }
        }

        /// <summary>
        /// Stage 1: describe every GO-separated batch of <paramref name="editorText"/> (one
        /// describe call per batch, one connection) and match each against the grid's full
        /// shape. Logs the full describe dumps when nothing matched (same as before the split).
        /// </summary>
        internal static async Task<ShapeMatch> DescribeAndMatchAsync(
            string editorConnectionString, string editorText, int numberOfDataColumns,
            IReadOnlyList<string> gridColumnNames, int clickedOrdinal, string clickedColumnName,
            int describeTimeoutSeconds, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(editorText))
                return ShapeMatch.Declined(NoQueryTextMessage);

            // v0.7.4: SSMS executes whichever batch (GO-separated) the grid actually came
            // from — often NOT the whole editor text ("USE db / GO / SELECT ..."). See
            // TSqlBatchSplitter's doc comment for why this is a real lexer.
            var batches = TSqlBatchSplitter.Split(editorText)
                .Select(b => b.Trim())
                .Where(b => b.Length > 0)
                .ToList();

            if (batches.Count == 0)
                return ShapeMatch.Declined(NoQueryTextMessage);

            var described = new List<IReadOnlyList<DescribedColumn>>(batches.Count);
            using (var describeConn = new SqlConnection(editorConnectionString))
            {
                await describeConn.OpenAsync(cancellationToken).ConfigureAwait(true);

                for (int b = 0; b < batches.Count; b++)
                {
                    // UNFILTERED rows — gate 3 (error rows) must see is_hidden = NULL rows.
                    described.Add(await DescribeFirstResultSetService.DescribeAsync(
                        describeConn, batches[b], describeTimeoutSeconds, cancellationToken).ConfigureAwait(true));
                }
            }

            var match = ResultShapeMatcher.Match(described, numberOfDataColumns, gridColumnNames, clickedOrdinal, clickedColumnName);

            // v0.7.5: the full dump stays one ActivityLog away even when the status bar
            // message can't carry all of it. Column names/metadata only, never cell values.
            foreach (var dump in match.DiagnosticDumps)
                OeDiagnostics.Warn(dump);

            return match;
        }

        /// <summary>
        /// docs/pivot-plan.md item 13: resolve EVERY grid column at once — one describe call
        /// per batch, then one SchemaReader read per distinct source table (grouped ordinally,
        /// so two tables differing only by case in a case-sensitive database are never
        /// conflated). Per column, the outcome is exactly what single-cell Go to source decides
        /// for that column before it looks at the cell value.
        /// </summary>
        /// <param name="gridColumnNames">Every grid column's header text; its length is the
        /// grid's data-column count. Must not be null.</param>
        /// <param name="buildConnectionStringForDatabase">Same contract as
        /// <see cref="Request.BuildConnectionStringForDatabase"/>. Connection strings are used
        /// immediately and never stored in the result.</param>
        /// <exception cref="OperationCanceledException">When <paramref name="cancellationToken"/> fires.</exception>
        internal static async Task<ColumnLinkMap> ResolveAllColumnsAsync(
            string editorConnectionString, string editorText, IReadOnlyList<string> gridColumnNames,
            Func<string, string> buildConnectionStringForDatabase,
            int describeTimeoutSeconds, CancellationToken cancellationToken)
        {
            if (gridColumnNames == null) throw new ArgumentNullException(nameof(gridColumnNames));
            if (buildConnectionStringForDatabase == null) throw new ArgumentNullException(nameof(buildConnectionStringForDatabase));

            int columnCount = gridColumnNames.Count;
            var shape = await DescribeAndMatchAsync(
                editorConnectionString, editorText, columnCount, gridColumnNames, 0, null,
                describeTimeoutSeconds, cancellationToken).ConfigureAwait(true);

            if (!shape.IsMatch)
                return new ColumnLinkMap { DeclineMessage = shape.DeclineMessage };

            var columns = new ColumnLink[columnCount];
            for (int i = 0; i < columnCount; i++)
            {
                int ordinal = i + 1;
                var link = new ColumnLink { GridOrdinal = ordinal, GridColumnName = gridColumnNames[i] };
                var resolution = ResultShapeMatcher.ResolveColumn(shape, ordinal, link.GridColumnName);
                if (resolution.Succeeded)
                {
                    link.Described = resolution.Described;
                    link.MatchCount = resolution.MatchCount;
                }
                else
                {
                    link.DeclineMessage = resolution.DeclineMessage;
                }
                columns[i] = link;
            }

            // Group the agreed columns by source database, then by table — ORDINAL keys.
            var byDatabase = columns
                .Where(c => c.Described != null)
                .GroupBy(c => c.Described.SourceDatabase ?? string.Empty, StringComparer.Ordinal);

            foreach (var dbGroup in byDatabase)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Same argument Go to source passes (the DM's source_database, possibly null).
                string sourceDatabase = dbGroup.First().Described.SourceDatabase;
                string targetConnectionString = buildConnectionStringForDatabase(sourceDatabase);
                if (targetConnectionString == null)
                {
                    foreach (var c in dbGroup) c.DeclineMessage = CouldNotBuildTargetConnectionMessage;
                    continue;
                }

                try
                {
                    using (var targetConn = new SqlConnection(targetConnectionString))
                    {
                        await targetConn.OpenAsync(cancellationToken).ConfigureAwait(true);

                        var byTable = dbGroup.GroupBy(
                            c => SourceTableRef(c.Described).QualifiedName, StringComparer.Ordinal);

                        foreach (var tableGroup in byTable)
                        {
                            var tableRef = SourceTableRef(tableGroup.First().Described);
                            try
                            {
                                var schema = await new SchemaReader().ReadAsync(targetConn, tableRef, new ProfileOptions(), cancellationToken).ConfigureAwait(true);
                                foreach (var c in tableGroup)
                                {
                                    string fkDecline = CheckForeignKey(schema.Columns, c.Described, tableRef, out var columnMeta);
                                    if (fkDecline != null)
                                    {
                                        c.DeclineMessage = fkDecline;
                                        continue;
                                    }
                                    c.IsLink = true;
                                    c.ForeignKeyColumn = columnMeta;
                                    c.TargetText = columnMeta.ReferencedQualifiedName + "." + SqlIdentifier.Bracket(columnMeta.ReferencedColumn);
                                }
                            }
                            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && !(ex is OperationCanceledException))
                            {
                                // One unreadable table (e.g. no VIEW DEFINITION) must not take
                                // down the links of every other column. Go to source would have
                                // shown "Go to source: " + ex.Message for a click here.
                                OeDiagnostics.Error("Pivot FK links: reading " + tableRef.QualifiedName + " failed", ex);
                                foreach (var c in tableGroup) c.DeclineMessage = "Go to source: " + ex.Message;
                            }
                        }
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested && !(ex is OperationCanceledException))
                {
                    OeDiagnostics.Error("Pivot FK links: connecting to a source database failed", ex);
                    foreach (var c in dbGroup.Where(c => !c.IsLink && c.DeclineMessage == null))
                        c.DeclineMessage = "Go to source: " + ex.Message;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new ColumnLinkMap { Columns = columns };
        }

        /// <summary>Stage 3 check: the described base column must exist on its table and carry
        /// exactly one single-column declared FK. Returns the decline message, or null with
        /// <paramref name="columnMeta"/> set.</summary>
        internal static string CheckForeignKey(IEnumerable<ColumnMeta> tableColumns, DescribedColumn described, TableRef tableRef, out ColumnMeta columnMeta)
        {
            columnMeta = (tableColumns ?? Enumerable.Empty<ColumnMeta>()).FirstOrDefault(c =>
                string.Equals(c.Name, described.SourceColumn, StringComparison.OrdinalIgnoreCase));

            if (columnMeta == null)
                return $"Go to source: could not find column '{described.SourceColumn}' on {tableRef.QualifiedName}.";

            // Never gate on IsForeignKey alone (CONTRACT.md Amendment 15): a composite or
            // multi-FK column both set it true but leave ReferencedTable/-Column null.
            if (columnMeta.ReferencedTable == null)
                return $"Go to source: '{columnMeta.Name}' on {tableRef.QualifiedName} is not a (single-resolvable) foreign key.";

            if (columnMeta.ReferencedColumn == null)
                return $"Go to source: '{columnMeta.Name}' is part of a composite foreign key — can't resolve a single-value filter.";

            return null;
        }

        /// <summary>True when <paramref name="cellDisplayText"/> can be turned into a literal for
        /// this column's described type (non-NULL, not float/binary/MAX, parses). CPU only.</summary>
        internal static bool CanFormatCell(DescribedColumn described, string cellDisplayText) =>
            described != null &&
            SqlLiteralFormatter.TryFormatDisplayText(cellDisplayText, described.SystemTypeName, described.MaxLength, out _, out _);

        /// <summary>Stage 4: literal + SELECT + the success status text. On false,
        /// <paramref name="statusMessage"/> is the decline (which can quote the display text —
        /// SqlLiteralFormatter's own wording — so callers must not log it).</summary>
        internal static bool TryBuildJump(ColumnMeta columnMeta, DescribedColumn described, string gridColumnName, string cellDisplayText, int matchCount, out string sql, out string statusMessage)
        {
            sql = null;
            if (!SqlLiteralFormatter.TryFormatDisplayText(cellDisplayText, described.SystemTypeName, described.MaxLength, out var literal, out var declineReason))
            {
                statusMessage = $"Go to source: [{gridColumnName}] {declineReason}.";
                return false;
            }

            sql = $"SELECT * FROM {columnMeta.ReferencedQualifiedName} WHERE {SqlIdentifier.Bracket(columnMeta.ReferencedColumn)} = {literal};";

            // Lead's explicit ask: make the multi-batch agreement visible, not magic.
            string matchNote = matchCount > 1
                ? $" (resolved via {matchCount} matching batches, all agreeing on this source)"
                : "";

            statusMessage = $"Resolved to {columnMeta.ReferencedQualifiedName}.{matchNote}";
            return true;
        }

        private static TableRef SourceTableRef(DescribedColumn described) =>
            new TableRef { Schema = described.SourceSchema ?? "dbo", Name = described.SourceTable };

        private static Result Decline(string reason) => new Result { Success = false, StatusMessage = reason };

        /// <summary>Both NULL/"(No column name)" count as a match (docs section 6.4).</summary>
        internal static bool NamesMatch(string describedName, string gridColumnName) =>
            ResultShapeMatcher.NamesMatch(describedName, gridColumnName);
    }
}
