using System;
using System.ComponentModel.Design;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.Management.UI.VSIntegration.Editors;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using SsmsDataAnalyzer.Vsix.GoToSource;
using SsmsDataAnalyzer.Vsix.ObjectExplorer;
using Task = System.Threading.Tasks.Task;

namespace SsmsDataAnalyzer.Vsix.ResultsGrid
{
    /// <summary>
    /// CONTRACT.md Amendment 16 — "Go to source for this value" on SSMS's own query results
    /// grid (docs/resultsgrid-api.md, Agent C's second spike). Opposite mechanism from the
    /// Object Explorer menu: this one IS a real VS ctmenu, wired the same way as
    /// AnalyzeDataCommand, just parented (in the .vsct only) to SSMS's own results-grid group.
    ///
    /// BeforeQueryStatus does ONLY the cheap, local checks (capture the clicked cell, gates 1+2
    /// — see GridClickCapture) — never a database round trip, per the doc's risk #11. The
    /// expensive part (sys.dm_exec_describe_first_result_set, gates 3+4+5, the FK lookup) runs
    /// once, in Invoke, against the CACHED cell from BeforeQueryStatus (never re-HitTest in
    /// Invoke — by then the mouse has moved to the menu item).
    ///
    /// Reuses v0.4.x wholesale rather than re-implementing it: SqlLiteralFormatter (never
    /// ProfileFormat.Value), SqlIdentifier.Bracket, QueryWindowAccessor (the same
    /// ServiceCache.ScriptFactory path DataAnalyzerPackage already wires for the tool window),
    /// and Core's SchemaReader for the FK metadata lookup — the identical query that already
    /// populates ColumnMeta.ReferencedTable/ReferencedColumn/ReferencedQualifiedName.
    /// </summary>
    internal sealed class ResultsGridSourceCommand
    {
        private readonly AsyncPackage _package;
        private ClickedGridCell _cached;

        private ResultsGridSourceCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            if (commandService == null) throw new ArgumentNullException(nameof(commandService));

            var commandId = new CommandID(PackageGuids.CommandSetGuid, PackageIds.GoToSourceForValueCommandId);
            var command = new OleMenuCommand(Execute, commandId);
            command.BeforeQueryStatus += OnBeforeQueryStatus;
            // Starts invisible: only BeforeQueryStatus (a right-click actually landing on a
            // results-grid cell) ever makes it visible. Never guessed-visible.
            command.Visible = false;
            commandService.AddCommand(command);
        }

        public static ResultsGridSourceCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null)
            {
                OeDiagnostics.Error("Results-grid 'Go to source' command could not be registered: no IMenuCommandService.");
                return;
            }
            Instance = new ResultsGridSourceCommand(package, commandService);
        }

        /// <summary>
        /// Cheap, local, no DB round trip (docs/resultsgrid-api.md risk #11). Must never throw
        /// past this method — an unhandled exception here only drops our own item (VS isolates
        /// command targets, unlike the Object Explorer IWinformsMenuHandler case), but there is
        /// no reason to risk it.
        /// </summary>
        private void OnBeforeQueryStatus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var command = (OleMenuCommand)sender;
            try
            {
                if (GridClickCapture.TryCapture(out var cell, out _))
                {
                    _cached = cell;
                    command.Visible = true;

                    // Cheap, local "offered implies works" narrowing (lead's field report —
                    // an all-NULL FK column offered the item, then refused at Invoke with a
                    // generic message): a NULL cell can NEVER produce a value filter no matter
                    // what the DM resolves it to, and we already have the value for free from
                    // TryCapture, so this one case is worth pre-checking even though the full
                    // FK-resolution can't be (that needs the DM round trip, deliberately
                    // deferred to Invoke — docs/resultsgrid-api.md risk #11). Non-FK columns
                    // and computed expressions still get offered and then decline at Invoke,
                    // with a specific reason each time — narrowing those further would mean
                    // running the describe on every right-click, which the doc explicitly
                    // warns against.
                    if (SqlLiteralFormatter.IsEffectivelyNull(cell.Value))
                    {
                        command.Enabled = false;
                        command.Text = $"Go to source for this value ([{cell.ColumnName}] is NULL)";
                    }
                    else
                    {
                        command.Enabled = true;
                        command.Text = "Go to source for this value";
                    }
                }
                else
                {
                    _cached = null;
                    command.Visible = false;
                }
            }
            catch (Exception ex)
            {
                OeDiagnostics.Error("Results-grid 'Go to source' BeforeQueryStatus failed", ex);
                _cached = null;
                command.Visible = false;
            }
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var cell = _cached;
            if (cell == null) return;

            _package.JoinableTaskFactory.RunAsync(() => ExecuteAsync(cell))
                .FileAndForget("SsmsDataAnalyzer/ResultsGrid/GoToSource");
        }

        /// <summary>
        /// Turns the captured cell into a <see cref="ResultsGridGoToSourceResolver.Request"/>
        /// and acts on the result. The five-gate precondition check and the FK jump itself
        /// live in <see cref="ResultsGridGoToSourceResolver"/> — pulled out specifically so it
        /// can be exercised in a harness without any WinForms/GridControl involved. Every
        /// decline path and every exception reports through <see cref="ShowStatusAsync"/> —
        /// there is no tool window here to report to, and the tool-window "Go to source" bug
        /// (a click that silently did nothing) is exactly the failure mode to not repeat.
        /// </summary>
        private async Task ExecuteAsync(ClickedGridCell cell)
        {
            try
            {
                var ci = cell.Editor?.Connection;
                if (ci == null) { await ShowStatusAsync("Go to source: no connection available for this editor."); return; }

                string tsql = GetSelectionOrFullText(cell.Editor);

                if (!GridConnectionInfo.TryBuild(ci, null, out var editorConnectionString))
                {
                    await ShowStatusAsync("Go to source: could not determine this editor's connection (Entra/token-based sign-ins aren't supported here — see docs/oe-api.md).");
                    return;
                }

                var request = new ResultsGridGoToSourceResolver.Request
                {
                    EditorConnectionString = editorConnectionString,
                    EditorText = tsql,
                    GridColumnOrdinal = cell.GridCol,
                    GridColumnNames = cell.AllColumnNames,
                    GridColumnName = cell.ColumnName,
                    CellValue = cell.Value,
                    NumberOfDataColumns = cell.NumberOfDataColumns,
                    BuildConnectionStringForDatabase = db =>
                        GridConnectionInfo.TryBuild(ci, db, out var cs) ? cs : null
                };

                var result = await ResultsGridGoToSourceResolver.ResolveAsync(request, 15, CancellationToken.None).ConfigureAwait(true);

                if (!result.Success)
                {
                    await ShowStatusAsync(result.StatusMessage);
                    return;
                }

                // v0.6.1 fix: pass the CLICKED editor's own, already-working UIConnectionInfo
                // through so the new window is connected by copying it (ServerType,
                // credentials, everything) rather than us hand-building one — see
                // QueryWindowAccessor.TryOpenAsync's doc comment for why that was leaving new
                // windows "Disconnected."
                var openResult = await QueryWindowAccessor.TryOpenAsync(result.GeneratedSql, result.TargetConnectionString, ci).ConfigureAwait(true);
                await ShowStatusAsync(openResult.Success
                    ? result.StatusMessage
                    : $"Go to source: could not open a query window: {openResult.Reason}");
            }
            catch (Exception ex)
            {
                OeDiagnostics.Error("Results-grid 'Go to source' failed", ex);
                await ShowStatusAsync("Go to source: " + ex.Message);
            }
        }

        /// <summary>
        /// There is no tool window here — the VS status bar is the SSMS-wide equivalent of
        /// ProfileViewModel.StatusMessage, and every outcome (success, decline, exception) goes
        /// through it so nothing about this command can ever look like a click that did
        /// nothing.
        /// </summary>
        /// <summary>
        /// v0.7.4 field report ("USE db / GO / SELECT ..." almost always declined): SSMS runs
        /// only the SELECTED text when there is a selection, not the whole editor buffer —
        /// describing the whole buffer when the user had selected (or was about to run) just
        /// one statement out of several is exactly why gate 4/5 kept declining.
        ///
        /// Decompilation of SQLEditors.dll (spikes/OeProbe) found the fix does not need to be
        /// built by hand: ScriptEditorControl (SqlScriptEditorControl's base) has an INTERNAL
        /// <c>GetCurrentlySelectedText()</c> that is a one-line forward to
        /// ShellCodeWindowControl.SelectedText, whose IL already implements exactly "selection
        /// if non-empty, else the ENTIRE buffer text" — i.e. SSMS's own selection-or-full-text
        /// fallback, not something this project needs to reimplement or get subtly wrong.
        /// Reached via reflection (established pattern — see DataAnalyzerPackage's OnExecScript
        /// call) since it's internal; on any failure this degrades to the old always-whole-text
        /// behaviour rather than failing the command outright — worse selection fidelity, not
        /// a broken feature, if a future SSMS servicing update renames or removes it.
        /// </summary>
        private static string GetSelectionOrFullText(SqlScriptEditorControl editor)
        {
            try
            {
                var method = typeof(ScriptEditorControl).GetMethod("GetCurrentlySelectedText", BindingFlags.NonPublic | BindingFlags.Instance);
                if (method != null && method.Invoke(editor, null) is string text && !string.IsNullOrEmpty(text))
                    return text;
            }
            catch (Exception ex)
            {
                OeDiagnostics.Warn("Results-grid 'Go to source': could not read the editor's current selection, falling back to the whole document — " + ex.Message);
            }
            return editor.EditorText;
        }

        private async Task ShowStatusAsync(string message)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            OeDiagnostics.Info(message);
            try
            {
                var statusBar = ((IServiceProvider)_package).GetService(typeof(SVsStatusbar)) as IVsStatusbar;
                statusBar?.SetText(message);
            }
            catch (Exception ex)
            {
                // Best-effort UI feedback only; the real failure (if any) is already logged
                // above via OeDiagnostics — losing the status-bar echo must never mask it.
                OeDiagnostics.Error("Results-grid 'Go to source': could not set the status bar text", ex);
            }
        }
    }
}
