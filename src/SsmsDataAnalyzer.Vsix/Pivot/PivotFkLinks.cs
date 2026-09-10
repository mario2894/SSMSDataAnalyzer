using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.Management.Smo.RegSvrEnum;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using SsmsDataAnalyzer.Vsix.GoToSource;
using SsmsDataAnalyzer.Vsix.ObjectExplorer;
using SsmsDataAnalyzer.Vsix.ResultsGrid;
using Task = System.Threading.Tasks.Task;

namespace SsmsDataAnalyzer.Vsix.Pivot
{
    /// <summary>
    /// docs/pivot-plan.md item 13 / §12: the non-UI engine behind the pivot's FK link icons.
    /// Created synchronously by PivotRowsCommand at snapshot time with everything Go to source
    /// would read at click time — the query text (selection or whole buffer), the editor's own
    /// <see cref="UIConnectionInfo"/> and its current database, and the grid's column names —
    /// so it keeps working after the source tab is closed or re-run. No database access until
    /// <see cref="ResolveAsync"/>.
    ///
    /// CONTRACT.md Amendment 13: the UIConnectionInfo object is held in memory only. No
    /// password or connection string is extracted into a field, stored, logged or shown;
    /// connection strings are built on demand (resolve / go) and dropped immediately, exactly
    /// as Go to source does per click.
    ///
    /// This type (and <see cref="PivotFkLinkMap"/>) references no results-grid type
    /// (GridControl / SqlScriptEditorControl), so view-model code may hold it freely.
    /// </summary>
    internal sealed class PivotFkLinks
    {
        /// <summary>Same describe timeout the results-grid Go to source command uses.</summary>
        internal const int DescribeTimeoutSeconds = 15;

        private readonly AsyncPackage _package;
        private readonly UIConnectionInfo _connection;
        private readonly string _capturedDatabase;
        private readonly string _queryText;
        private readonly string[] _gridColumnNames;

        /// <param name="package">For the status bar and JoinableTaskFactory.</param>
        /// <param name="connection">The source editor's own UIConnectionInfo; null when no
        /// editor/connection was found (resolution then declines with Go to source's wording).</param>
        /// <param name="capturedDatabase">The editor's current database at snapshot time
        /// (<c>AdvancedOptions["DATABASE"]</c>); may be null.</param>
        /// <param name="queryText">Selection-or-full-text exactly as Go to source reads it; may be null.</param>
        /// <param name="gridColumnNames">Header text of grid columns 1..N (index 0 = column 1).</param>
        internal PivotFkLinks(AsyncPackage package, UIConnectionInfo connection, string capturedDatabase, string queryText, IReadOnlyList<string> gridColumnNames)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            if (gridColumnNames == null) throw new ArgumentNullException(nameof(gridColumnNames));
            _connection = connection;
            _capturedDatabase = capturedDatabase;
            _queryText = queryText;
            _gridColumnNames = new string[gridColumnNames.Count];
            for (int i = 0; i < _gridColumnNames.Length; i++) _gridColumnNames[i] = gridColumnNames[i];
        }

        /// <summary>
        /// Describes the captured query (one call per GO batch) and resolves every grid column's
        /// FK target. Call once per pivot, from any thread (typically the UI thread; the I/O is
        /// async). Never throws for a resolution failure — that comes back as
        /// <see cref="PivotFkLinkMap.DeclineReason"/>. Throws <see cref="OperationCanceledException"/>
        /// only when <paramref name="cancellationToken"/> fires.
        /// </summary>
        public async Task<PivotFkLinkMap> ResolveAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Same order and wording as ResultsGridSourceCommand.ExecuteAsync.
                if (_connection == null)
                    return PivotFkLinkMap.Declined(this, "Go to source: no connection available for this editor.");

                if (!GridConnectionInfo.TryBuild(_connection, _capturedDatabase, out var editorConnectionString))
                    return PivotFkLinkMap.Declined(this, "Go to source: could not determine this editor's connection (Entra/token-based sign-ins aren't supported here — see docs/oe-api.md).");

                var map = await ResultsGridGoToSourceResolver.ResolveAllColumnsAsync(
                    editorConnectionString, _queryText, _gridColumnNames, BuildConnectionStringForDatabase,
                    DescribeTimeoutSeconds, cancellationToken).ConfigureAwait(true);

                return map.DeclineMessage != null
                    ? PivotFkLinkMap.Declined(this, map.DeclineMessage)
                    : PivotFkLinkMap.Resolved(this, map.Columns);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Includes an OperationCanceledException NOT caused by our token (e.g. a
                // driver-internal cancel): that is a failure to report, not a cancellation.
                OeDiagnostics.Error("Pivot FK links: resolution failed", ex);
                return PivotFkLinkMap.Declined(this, "Go to source: " + ex.Message);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // SqlClient can surface a cancel as SqlException; normalize it.
                throw new OperationCanceledException(cancellationToken);
            }
        }

        /// <summary>Connection string for a describe result's source_database, built on demand
        /// and never stored. A null/empty source database means the captured editor database
        /// (not whatever the source tab has switched to since).</summary>
        internal string BuildConnectionStringForDatabase(string database) =>
            GridConnectionInfo.TryBuild(_connection, string.IsNullOrEmpty(database) ? _capturedDatabase : database, out var cs)
                ? cs
                : null;

        internal UIConnectionInfo Connection => _connection;

        /// <summary>Status bar + (optionally) ActivityLog, on the UI thread. Never throws.</summary>
        internal async Task ShowStatusAsync(string message, bool log)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (log) OeDiagnostics.Info(message);
            try
            {
                var statusBar = ((IServiceProvider)_package).GetService(typeof(SVsStatusbar)) as IVsStatusbar;
                statusBar?.SetText(message);
            }
            catch (Exception ex)
            {
                OeDiagnostics.Error("Pivot FK links: could not set the status bar text", ex);
            }
        }

        internal JoinableTaskFactory JoinableTaskFactory => _package.JoinableTaskFactory;
    }

    /// <summary>
    /// Immutable outcome of <see cref="PivotFkLinks.ResolveAsync"/>. Grid ordinals are the
    /// 1-based <c>PivotColumn.GridOrdinal</c> values. All query methods are cheap, synchronous
    /// and safe to call from bindings; unknown ordinals simply answer "no link".
    /// </summary>
    internal sealed class PivotFkLinkMap
    {
        private readonly PivotFkLinks _owner;
        private readonly IReadOnlyList<ResultsGridGoToSourceResolver.ColumnLink> _columns;

        private PivotFkLinkMap(PivotFkLinks owner, string declineReason, IReadOnlyList<ResultsGridGoToSourceResolver.ColumnLink> columns)
        {
            _owner = owner;
            DeclineReason = declineReason;
            _columns = columns ?? new ResultsGridGoToSourceResolver.ColumnLink[0];
            int count = 0;
            foreach (var c in _columns) if (c.IsLink) count++;
            LinkColumnCount = count;
        }

        internal static PivotFkLinkMap Declined(PivotFkLinks owner, string reason) => new PivotFkLinkMap(owner, reason, null);
        internal static PivotFkLinkMap Resolved(PivotFkLinks owner, IReadOnlyList<ResultsGridGoToSourceResolver.ColumnLink> columns) => new PivotFkLinkMap(owner, null, columns);

        /// <summary>Whole-pivot failure, in Go to source's wording (starts with "Go to source: ").
        /// Null when resolution succeeded — even if no column turned out to be a link. Contains
        /// no cell values.</summary>
        public string DeclineReason { get; }

        /// <summary>Number of link columns (0 on decline).</summary>
        public int LinkColumnCount { get; }

        /// <summary>True when this grid column is a single-column declared FK that resolved
        /// unambiguously. Drives the 🔗 marker on the column-name cell.</summary>
        public bool IsLinkColumn(int gridOrdinal) => Get(gridOrdinal)?.IsLink == true;

        /// <summary>Referenced column as "[schema].[table].[column]" for a link column, else null.</summary>
        public string GetTargetText(int gridOrdinal)
        {
            var c = Get(gridOrdinal);
            return c != null && c.IsLink ? c.TargetText : null;
        }

        /// <summary>Why this column is not a link (Go to source's wording), or null for a link
        /// column / unknown ordinal / whole-pivot decline. Metadata only.</summary>
        public string GetColumnDeclineReason(int gridOrdinal) => Get(gridOrdinal)?.DeclineMessage;

        /// <summary>Show the icon for this cell? True only for a link column whose display text is
        /// not "NULL" and parses back to a literal under SqlLiteralFormatter's rules (float,
        /// real, binary, MAX and unparseable text all answer false). CPU only, no I/O.</summary>
        public bool CanGo(int gridOrdinal, string cellDisplayText)
        {
            var c = Get(gridOrdinal);
            return c != null && c.IsLink && ResultsGridGoToSourceResolver.CanFormatCell(c.Described, cellDisplayText);
        }

        /// <summary>
        /// Go to source for this cell: open a new connected query window with
        /// <c>SELECT * FROM target WHERE [col] = literal;</c> (auto-executed per the existing
        /// option), reporting success/decline/failure on the status bar exactly as the
        /// results-grid command does. Never throws. No cancellation, like Go to source.
        /// </summary>
        public async Task GoAsync(int gridOrdinal, string cellDisplayText)
        {
            try
            {
                if (DeclineReason != null) { await _owner.ShowStatusAsync(DeclineReason, log: true); return; }

                var c = Get(gridOrdinal);
                if (c == null) { await _owner.ShowStatusAsync("Go to source: could not match this column to the described query.", log: true); return; }
                if (!c.IsLink) { await _owner.ShowStatusAsync(c.DeclineMessage, log: true); return; }

                // Same order as ResolveAsync(Request): target connection, then the literal.
                string targetConnectionString = _owner.BuildConnectionStringForDatabase(c.Described.SourceDatabase);
                if (targetConnectionString == null)
                {
                    await _owner.ShowStatusAsync(ResultsGridGoToSourceResolver.CouldNotBuildTargetConnectionMessage, log: true);
                    return;
                }

                if (!ResultsGridGoToSourceResolver.TryBuildJump(c.ForeignKeyColumn, c.Described, c.GridColumnName, cellDisplayText, c.MatchCount, out var sql, out var statusMessage))
                {
                    // The formatter's decline can quote the display text: status bar only, never logged.
                    await _owner.ShowStatusAsync(statusMessage, log: false);
                    return;
                }

                var openResult = await QueryWindowAccessor.TryOpenAsync(sql, targetConnectionString, _owner.Connection).ConfigureAwait(true);
                await _owner.ShowStatusAsync(openResult.Success
                    ? statusMessage
                    : $"Go to source: could not open a query window: {openResult.Reason}", log: true);
            }
            catch (Exception ex)
            {
                OeDiagnostics.Error("Pivot FK link 'Go to source' failed", ex);
                await _owner.ShowStatusAsync("Go to source: " + ex.Message, log: true);
            }
        }

        /// <summary>Fire-and-forget <see cref="GoAsync"/> for click handlers (UI thread).</summary>
        public void BeginGo(int gridOrdinal, string cellDisplayText)
        {
            _owner.JoinableTaskFactory.RunAsync(() => GoAsync(gridOrdinal, cellDisplayText))
                .FileAndForget("SsmsDataAnalyzer/Pivot/FkLinks/Go");
        }

        private ResultsGridGoToSourceResolver.ColumnLink Get(int gridOrdinal) =>
            gridOrdinal >= 1 && gridOrdinal <= _columns.Count ? _columns[gridOrdinal - 1] : null;
    }
}
