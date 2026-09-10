using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Smo.RegSvrEnum;
using Microsoft.VisualStudio.Shell;
using SsmsDataAnalyzer.Core.Pivot;
using SsmsDataAnalyzer.Vsix.ObjectExplorer;
using SsmsDataAnalyzer.Vsix.Options;
using SsmsDataAnalyzer.Vsix.Pivot;
using Task = System.Threading.Tasks.Task;

namespace SsmsDataAnalyzer.Vsix.Peek
{
    /// <summary>
    /// "Peek source for this value" / pivot's "Peek source…" — the shared, non-UI engine both
    /// <see cref="SsmsDataAnalyzer.Vsix.ResultsGrid.ResultsGridPeekCommand"/> and PivotView's
    /// cell context menu go through, so the query/format/display steps exist exactly once.
    ///
    /// Runs the caller-supplied, already-built, bounded read-only SELECT (Go to source's own
    /// GeneratedSql — this never builds SQL itself), reads at most
    /// <see cref="OptionsAccessor.GetPivotRowLimit"/> rows off the UI thread, formats every
    /// value with Core's pure <see cref="PeekValueFormatter"/>, and shows the result by
    /// reusing PivotView/PivotViewModel inside the shared, single-instance
    /// <see cref="PeekToolWindow"/> — a peeked record is exactly a one-range "pivot".
    /// </summary>
    internal static class PeekRunner
    {
        internal sealed class Request
        {
            /// <summary>The bounded, read-only SELECT to run — built by
            /// ResultsGridGoToSourceResolver/PivotFkLinkMap, never here.</summary>
            public string Sql;
            public string TargetConnectionString;
            /// <summary>"[schema].[table]" of the referenced row — used for both the window
            /// caption and the banner. Contains no cell value.</summary>
            public string TargetQualifiedName;
            /// <summary>The filter column/value, e.g. "[Id] = 8" — used for both the window
            /// caption and the banner. CAN contain a cell value: never pass this to
            /// OeDiagnostics, status-bar display only (the same rule Go to source's own
            /// StatusMessage already follows).</summary>
            public string FilterDescription;
            /// <summary>The source editor's own UIConnectionInfo, reused (same login) so FK
            /// link icons work inside the peek window too (chained navigation). Null = the
            /// peek still works, just with no FK icons.</summary>
            public UIConnectionInfo ChainConnection;
        }

        /// <summary>
        /// Runs the peek and shows/re-targets the shared tool window. Returns null on success;
        /// otherwise the failure text for the CALLER's status bar — callers prefix their own
        /// "Peek source: " (matching how Go to source's command classes build their own status
        /// text rather than a shared helper doing it), and must not log a returned message
        /// that could echo <see cref="Request.FilterDescription"/>.
        /// </summary>
        internal static async Task<string> RunAsync(AsyncPackage package, Request request, CancellationToken cancellationToken)
        {
            try
            {
                var columnNames = new List<string>();
                var rows = new List<string[]>();
                int limit = OptionsAccessor.GetPivotRowLimit();
                bool truncated = false;

                using (var connection = new SqlConnection(request.TargetConnectionString))
                {
                    await connection.OpenAsync(cancellationToken).ConfigureAwait(true);

                    using (var command = new SqlCommand(request.Sql, connection) { CommandTimeout = 30 })
                    using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(true))
                    {
                        for (int i = 0; i < reader.FieldCount; i++)
                            columnNames.Add(reader.GetName(i));

                        while (rows.Count < limit && await reader.ReadAsync(cancellationToken).ConfigureAwait(true))
                        {
                            var row = new string[reader.FieldCount];
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                bool isNull = await reader.IsDBNullAsync(i, cancellationToken).ConfigureAwait(true);
                                row[i] = PeekValueFormatter.ToDisplayText(isNull ? null : reader.GetValue(i));
                            }
                            rows.Add(row);
                        }

                        // One more read (never materialized into `rows`) to know whether more
                        // rows existed than the limit allowed, for the banner's " — first N
                        // shown" suffix — same shape as PivotBuilder's own Truncated signal.
                        if (rows.Count == limit)
                            truncated = await reader.ReadAsync(cancellationToken).ConfigureAwait(true);
                    }
                }

                var ranges = rows.Count > 0
                    ? new[] { new RowRange(0, rows.Count - 1) }
                    : Array.Empty<RowRange>();

                var result = PivotBuilder.Build(columnNames, ranges, limit, (row, ordinal) => rows[(int)row][ordinal - 1]);

                string banner = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} row{1} from {2} where {3}{4}",
                    rows.Count,
                    rows.Count == 1 ? string.Empty : "s",
                    request.TargetQualifiedName,
                    request.FilterDescription,
                    truncated ? string.Format(CultureInfo.InvariantCulture, " — first {0} shown", limit) : string.Empty);

                string caption = "Peek: " + request.TargetQualifiedName + " where " + request.FilterDescription;

                // FK link chaining (design §3): the SAME UIConnectionInfo (same login), just
                // re-pointed at the peeked row's own database and query — lets a link inside
                // the peeked record itself be peeked/gone-to again.
                PivotFkLinks fkLinks = null;
                if (request.ChainConnection != null)
                {
                    string targetDatabase = TryGetInitialCatalog(request.TargetConnectionString);
                    fkLinks = new PivotFkLinks(package, request.ChainConnection, targetDatabase, request.Sql, columnNames);
                }

                await package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

                var pane = await package.ShowToolWindowAsync(
                    typeof(PeekToolWindow), 0, create: true, cancellationToken: cancellationToken) as PeekToolWindow;

                if (pane == null)
                {
                    OeDiagnostics.Error("'Peek source for this value' could not create/show its tool window.");
                    return "could not create the peek window.";
                }

                pane.Bind(result, fkLinks, banner, caption);
                return null;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                // Unlike Go to source, Peek EXECUTES the query itself, so a SqlException here can
                // quote data ("Conversion failed when converting the varchar value '…'"). Log
                // only the exception type and SQL error number; the full message goes to the
                // caller's status bar, never to the ActivityLog.
                string kind = ex is SqlException sqlEx
                    ? "SqlException " + sqlEx.Number.ToString(CultureInfo.InvariantCulture)
                    : ex.GetType().Name;
                OeDiagnostics.Error("'Peek source for this value' failed (" + kind + ").");
                return ex.Message;
            }
        }

        /// <summary>Reads back the database name from a connection string we already hold in
        /// memory (built moments earlier by GridConnectionInfo/BuildConnectionStringForDatabase)
        /// — never a new credential extraction, just the InitialCatalog field of a string this
        /// method already has.</summary>
        private static string TryGetInitialCatalog(string connectionString)
        {
            try
            {
                return new SqlConnectionStringBuilder(connectionString).InitialCatalog;
            }
            catch
            {
                return null;
            }
        }
    }
}
